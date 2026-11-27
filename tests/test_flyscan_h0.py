import copy
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import verify_flyscan_h0 as h0


LIMITS = h0.checked_limits({
    "positionTolerance": [0.1, 0.1, 0.1],
    "gearPositionDeg": 0.02,
    "gearSpeedDegPerSec": 1,
    "pulseWidthUs": [250, 400],
    "maxSampleGapMs": 1,
})


def positive(reverse=False):
    indexes = range(4, -1, -1) if reverse else range(5)
    rows = [[tick, step, 2 * step, step, 17 + step] for tick, step in enumerate(indexes)]
    return {"targets": [rows[1][1:4], rows[3][1:4]], "encoders": rows,
            "edges": [[1, "rise"], [1.3, "fall"], [3, "rise"], [3.3, "fall"]],
            "compareStatus": [[-0.25, 4, 0, 1, 7], [2.5, 4, 0, 1, 8], [4.25, 4, 0, 0, 9]]}


def rejected(axis):
    case = positive()
    case["targets"] = [case["targets"][0]]
    for row in case["encoders"]:
        row[axis + 1] += 10
    case["edges"] = []
    case["compareStatus"] = [[-0.25, 4, 0, 1, 7], [4.25, 4, 0, 1, 7]]
    return case


def gear_exit():
    return {"targets": [], "encoders": [[0, 0, 0, 0, 17], [1, 0, 0, 1, 17]], "edges": []}


class H0Tests(unittest.TestCase):
    def test_localbus_and_ethercat_mapping(self):
        profile = h0.read_json(ROOT / "examples/Motion/FlyScan.13Axes.json")
        summary = h0.profile_summary(profile)
        self.assertEqual(summary["hardwareAxes"]["Localbus"], [0, 1, 2, 3])
        changed = copy.deepcopy(profile)
        changed["coordinates"][0]["axes"][-1] = "Front.R2"
        with self.assertRaisesRegex(ValueError, "X/Y/R1"):
            h0.profile_summary(changed)

    def test_dll_inventory_does_not_load_it(self):
        dll = ROOT / "Hardware/Company.Hardware.ControlCard.INOVANCE/dll/IMC_API_x64.dll"
        inventory = h0.sdk_inventory(dll)
        self.assertEqual(inventory["architecture"], "AMD64")
        self.assertFalse(any(inventory["missing"].values()))
        with tempfile.TemporaryDirectory() as directory:
            corrupt = Path(directory) / "malformed.dll"
            corrupt.write_bytes(b"Not a DLL")
            with self.assertRaisesRegex(ValueError, "DOS"):
                h0.sdk_inventory(corrupt)

    def test_both_directions_with_rotation_offset(self):
        self.assertEqual(h0.evaluate_case("forward", positive(), LIMITS)["pulses"], 2)
        self.assertEqual(h0.evaluate_case("reverse", positive(reverse=True), LIMITS)["maximumGearErrorDeg"], 0)

    def test_interpolates_encoder_positions_at_independent_edges(self):
        case = positive()
        case["edges"][:2] = [[1.5, "rise"], [1.8, "fall"]]
        case["targets"][0] = [1.5, 3, 1.5]
        actual = h0.evaluate_case("forward", case, LIMITS)
        self.assertEqual(actual["maximumPositionError"], [0, 0, 0])

    def test_rejects_wrong_component_independently(self):
        for axis, name in enumerate(("rejectX", "rejectY", "rejectR1")):
            with self.subTest(axis=axis):
                case = rejected(axis)
                self.assertEqual(h0.evaluate_case(name, case, LIMITS)["pulses"], 0)
                case["edges"] = positive()["edges"]
                with self.assertRaisesRegex(ValueError, "Unexpected trigger"):
                    h0.evaluate_case(name, case, LIMITS)

    def test_missed_duplicate_out_of_order_or_wide_pulse(self):
        edits = (
            (lambda case: case["edges"].pop(), "Pulse count"),
            (lambda case: case["edges"].extend([[3.5, "rise"], [3.8, "fall"]]), "Pulse count"),
            (lambda case: case["targets"].reverse(), "ordered X/Y/R1"),
            (lambda case: case["edges"][1].__setitem__(0, 1.8), "Pulse width"),
        )
        for edit, message in edits:
            with self.subTest(message=message):
                case = positive()
                edit(case)
                with self.assertRaisesRegex(ValueError, message):
                    h0.evaluate_case("forward", case, LIMITS)

    def test_rejects_overlapping_adjacent_target_windows(self):
        for offset in (0, 0.125, 0.25):
            with self.subTest(offset=offset):
                case = positive()
                case["encoders"][2] = [2, 1, 2, 1, 18]
                case["encoders"][3] = [3, 1 + offset, 2, 1, 18]
                case["targets"][1] = case["encoders"][3][1:4]
                if offset <= 0.125:
                    with self.assertRaisesRegex(ValueError, "overlapping tolerance windows"):
                        h0.evaluate_case("forward", case, LIMITS)
                else:
                    self.assertEqual(h0.evaluate_case("forward", case, LIMITS)["pulses"], 2)

    def test_rejects_negative_trace_crossing_full_target_between_samples(self):
        for axis, name in enumerate(("rejectX", "rejectY", "rejectR1")):
            with self.subTest(axis=axis):
                target = [1, 2, 1]
                challenge = target.copy()
                challenge[axis] += 10
                coordinates = [challenge, [value - 1 for value in target], [value + 1 for value in target]]
                case = {"targets": [target], "edges": [],
                        "encoders": [[index, *point, point[2] + 17] for index, point in enumerate(coordinates)]}
                with self.assertRaisesRegex(ValueError, "crosses the full target window"):
                    h0.evaluate_case(name, case, LIMITS)

    def test_timestamp_gap_or_gear_slip(self):
        case = positive()
        case["encoders"].pop(2)
        with self.assertRaisesRegex(ValueError, "without gaps"):
            h0.evaluate_case("forward", case, LIMITS)
        case = positive()
        case["encoders"][2][4] += 0.5
        with self.assertRaisesRegex(ValueError, "R2 relative"):
            h0.evaluate_case("forward", case, LIMITS)
        case = gear_exit()
        self.assertEqual(h0.evaluate_case("gearExit", case, LIMITS)["maximumFollowerTravelDeg"], 0)
        case["encoders"][1][4] += 1
        with self.assertRaisesRegex(ValueError, "R2 stationary"):
            h0.evaluate_case("gearExit", case, LIMITS)

    def test_position_window_includes_exact_boundary_touch(self):
        target = [0, 0, 0]
        tolerance = [0.125] * 3
        self.assertTrue(h0.segment_intersects_target([-0.25, 0, 0], [0, 0.25, 0], target, tolerance))
        self.assertTrue(h0.segment_intersects_target([0, 0.25, 0], [-0.25, 0, 0], target, tolerance))
        self.assertFalse(h0.segment_intersects_target([-0.25, 0, 0], [0, 0.26, 0], target, tolerance))

    def test_compare_readback_matches_independent_count(self):
        case = positive()
        self.assertEqual(h0.check_compare_status(case, 4, 2),
                         {"port": 4, "samples": 3, "countBefore": 7, "countAfter": 9})
        case["compareStatus"][-1][3:] = [1, 10]
        with self.assertRaisesRegex(ValueError, "count differs"):
            h0.check_compare_status(case, 4, 2)
        case["compareStatus"][-1][3:] = [0, 8]
        with self.assertRaisesRegex(ValueError, "stopped"):
            h0.check_compare_status(case, 4, 2)
        case["compareStatus"][-1][3:] = [1, 6]
        with self.assertRaisesRegex(ValueError, "went backwards"):
            h0.check_compare_status(case, 4, 2)

    def test_compare_readback_requires_valid_status_through_trace(self):
        for column, value, message in (
            (0, 3.9, "bracket"),
            (1, 5, "configured port"),
            (2, 123, "failed read"),
            (3, 3, "compare error"),
            (3, 5, "compare error"),
            (3, 9, "compare error"),
        ):
            with self.subTest(column=column, value=value):
                case = positive()
                case["compareStatus"][-1][column] = value
                with self.assertRaisesRegex(ValueError, message):
                    h0.check_compare_status(case, 4, 2)
        case = positive()
        case["compareStatus"][1:] = [[3, 4, 0, 0, 9], [4.25, 4, 0, 1, 9]]
        with self.assertRaisesRegex(ValueError, "restarted"):
            h0.check_compare_status(case, 4, 2)

    def test_compare_rejection_requires_running_comparator(self):
        case = rejected(0)
        self.assertEqual(h0.check_compare_status(case, 4, 0)["countAfter"], 7)
        case["compareStatus"][0][3] = 0
        with self.assertRaisesRegex(ValueError, "not running"):
            h0.check_compare_status(case, 4, 0)
        case["compareStatus"][0][3] = 1
        case["compareStatus"][-1][3] = 0
        with self.assertRaisesRegex(ValueError, "stopped"):
            h0.check_compare_status(case, 4, 0)

    def test_missing_or_synthetic_bench_cannot_clear_gate(self):
        template = ROOT / "examples/Motion/FlyScan.IMC30G.H0.example.json"
        result = subprocess.run([sys.executable, str(ROOT / "tools/verify_flyscan_h0.py"),
                                 "--bench", str(template), "--require-bench"],
                                capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertIn('"status": "pending"', result.stdout)
        with self.assertRaisesRegex(ValueError, "Synthetic"):
            h0.evaluate_bench({"kind": "synthetic"}, ROOT, [])

    def test_evidence_hash_and_candidate_review(self):
        example = h0.read_json(ROOT / "examples/Motion/FlyScan.IMC30G.H0.example.json")
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            # Unit test fixture only. These files are never exported as a bench record.
            for role, item in example["artifacts"].items():
                filename = role + ".txt"
                data = ("unit test " + role).encode()
                (folder / filename).write_bytes(data)
                item.update(file=filename, sha256=hashlib.sha256(data).hexdigest())
            example.update(kind="hardware", testedAt="2000-01-01", technician="Test", reviewer="Test reviewer",
                           limits=LIMITS, cases={"forward": positive(), "reverse": positive(True),
                           "rejectX": rejected(0), "rejectY": rejected(1), "rejectR1": rejected(2), "gearExit": gear_exit()})
            example["device"].update(model="IMC30G-E-TEST", firmware="test", terminalModel="IMC30-6G",
                                     terminalRevision="test", axisCapacity=16, sdkSha256="a"*64, terminalOnline=True)
            example["compare"].update(port=4, dimensionReadback=3, outputType="CMP", physicalOutput="test",
                                      outputExclusive=True)
            for channel, source in enumerate(example["compare"]["sources"]):
                source.update(terminalChannel=channel, source="ExternalEncoder")
            example["gear"].update(followedFeedback=True, releasedFeedback=True)
            sdk = [{"sha256": "a"*64, "missing": {}}]
            report = h0.evaluate_bench(example, folder, sdk)
            self.assertEqual(report["status"], "readyForReview")
            self.assertEqual(report["cases"]["forward"]["compareReadback"]["countAfter"], 9)
            invalid = copy.deepcopy(example)
            del invalid["cases"]["rejectX"]["compareStatus"]
            invalid["cases"]["rejectY"]["compareStatus"][0][3] = 0
            report = h0.evaluate_bench(invalid, folder, sdk)
            self.assertEqual(report["status"], "failed")
            self.assertIn("Compare readback uses rows", report["errors"]["rejectX"])
            self.assertIn("not running", report["errors"]["rejectY"])
            invalid = copy.deepcopy(example)
            invalid["cases"]["rejectX"]["encoders"].extend([[5, 0, 1, 0, 17], [6, 2, 3, 2, 19]])
            report = h0.evaluate_bench(invalid, folder, sdk)
            self.assertEqual(report["status"], "failed")
            self.assertIn("crosses the full target window", report["errors"]["rejectX"])
            example["compare"]["dimensionReadback"] = 2
            with self.assertRaisesRegex(ValueError, "3D"):
                h0.evaluate_bench(example, folder, sdk)
            example["compare"]["dimensionReadback"] = 3
            (folder / "trigger.txt").write_text("altered")
            with self.assertRaisesRegex(ValueError, "checksum"):
                h0.evaluate_bench(example, folder, sdk)
            (folder / "trigger.txt").write_bytes(b"")
            example["artifacts"]["trigger"]["sha256"] = hashlib.sha256(b"").hexdigest()
            with self.assertRaisesRegex(ValueError, "raw evidence is empty"):
                h0.evaluate_bench(example, folder, sdk)


if __name__ == "__main__":
    unittest.main()
