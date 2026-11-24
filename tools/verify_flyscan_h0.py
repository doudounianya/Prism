"""Offline IMC30G-E inventory and H0 trace checks. No SDK or device calls."""

import argparse
from bisect import bisect_left
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import sys


ROOT = Path(__file__).resolve().parents[1]
AXES = ("Front.X", "Front.Y", "Front.R1", "Front.R2")
EXPORTS = {
    "identity": ("IMC_GetHwSysPara", "IMC_GetCardResource", "IMC_GetAxBondCfg"),
    "positionCompare": (
        "IMC_CompareSrcConfig", "IMC_CompareOutputConfig", "IMC_SetMultiDimensComparePara",
        "IMC_GetMultiDimensComparePara", "IMC_SetMultiDimensCompareData",
        "IMC_StartCompareOut", "IMC_GetCompareStatus", "IMC_StopCompareOut",
    ),
    "electronicGear": (
        "IMC_GearSetParam", "IMC_GearGetParam", "IMC_GearStart", "IMC_GearGetStatus",
        "IMC_GearStop", "IMC_GearDestroy",
    ),
}
REQUIRED_CASES = ("forward", "reverse", "rejectX", "rejectY", "rejectR1", "gearExit")
LIMIT_KEYS = ("positionTolerance", "gearPositionDeg", "gearSpeedDegPerSec", "pulseWidthUs", "maxSampleGapMs")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def numbers(row, size):
    return isinstance(row, list) and len(row) == size and all(finite(value) for value in row)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def profile_summary(profile):
    axes = profile["axes"]
    numbers_by_bus = {bus: sorted(axis["hardwareAxis"] for axis in axes if axis["bus"] == bus)
                      for bus in ("Localbus", "EtherCAT")}
    require(len(axes) == 13 and len({axis["axis"] for axis in axes}) == 13 and
            numbers_by_bus == {"Localbus": list(range(4)), "EtherCAT": list(range(4, 13))},
            "The profile must map axes 0–3 to Localbus and 4–12 to EtherCAT.")
    front = {axis["axis"]: (axis["hardwareAxis"], axis["unit"]) for axis in axes}
    require([front.get(axis) for axis in AXES] == [(0, "Millimetre"), (1, "Millimetre"), (2, "Degree"), (3, "Degree")],
            "Incorrect X/Y/R1/R2 axis order or engineering units.")
    coordinates = [row for row in profile["coordinates"] if row["coordinateId"] == "Front.Scan"]
    require(len(coordinates) == 1 and coordinates[0]["coordinateEnabled"] is True and
            coordinates[0]["axes"] == list(AXES[:3]), "Front.Scan must interpolate X/Y/R1 only.")
    relations = [row for row in profile["synchronizations"] if row["enabled"]]
    require(len(relations) == 1 and tuple(relations[0][key] for key in
            ("masterAxis", "slaveAxis", "ratio", "mode", "masterSource")) ==
            ("Front.R1", "Front.R2", 1, "ElectronicGear", "CommandPosition"),
            "The enabled follower relation must be R1 to R2, 1:1, from command position.")
    return {"axisCount": 13, "hardwareAxes": numbers_by_bus, "coordinate": "Front.Scan",
            "masterSource": relations[0]["masterSource"], "followerRatio": 1}


def sdk_inventory(path):
    data = path.read_bytes()

    def read(offset, layout):
        return struct.unpack_from("<" + layout, data, offset)

    require(data[:2] == b"MZ", f"{path.name}: invalid DOS header.")
    pe, = read(0x3C, "I")
    require(data[pe:pe + 4] == b"PE\x00\x00", f"{path.name}: invalid PE signature.")
    machine, section_count, _, _, _, optional_size, _ = read(pe + 4, "HHIIIHH")
    optional = pe + 24
    require(machine == 0x8664 and read(optional, "H")[0] == 0x20B and optional_size >= 120,
            f"{path.name}: an AMD64 PE32+ SDK DLL is required.")
    export_rva, export_size = read(optional + 112, "II")
    require(export_size >= 40, f"{path.name}: missing export directory.")
    sections = [read(optional + optional_size + i * 40 + 12, "III") for i in range(section_count)]

    def location(rva, size):
        for start, raw_size, file_offset in sections:
            offset = rva - start
            if 0 <= offset and offset + size <= raw_size and file_offset + offset + size <= len(data):
                return file_offset + offset, file_offset + raw_size
        raise ValueError(f"{path.name}: export points outside file-backed sections.")

    offset, _ = location(export_rva, 40)
    name_count, _, name_table, _ = read(offset + 24, "IIII")
    require(0 < name_count <= 100_000, f"{path.name}: invalid export count.")
    name_offset, _ = location(name_table, 4 * name_count)
    names = set()
    for index in range(name_count):
        name_rva, = read(name_offset + 4 * index, "I")
        start, end = location(name_rva, 1)
        stop = data.find(b"\x00", start, min(end, start + 512))
        require(stop > start, f"{path.name}: invalid export name.")
        names.add(data[start:stop].decode("ascii"))
    return {"file": str(path), "sha256": hashlib.sha256(data).hexdigest(), "architecture": "AMD64",
            "exports": name_count, "missing": {group: sorted(set(expected) - names)
                                               for group, expected in EXPORTS.items()}}


def checked_limits(limits):
    require(all(key in limits for key in LIMIT_KEYS), "Missing process limits.")
    require(numbers(limits["positionTolerance"], 3) and all(x > 0 for x in limits["positionTolerance"]),
            "Record positive X, Y and R1 tolerances (mm, mm, deg).")
    require(numbers(limits["pulseWidthUs"], 2) and 0 < limits["pulseWidthUs"][0] <= limits["pulseWidthUs"][1],
            "Record a confirmed acceptable pulse-width range in microseconds.")
    for key in ("gearPositionDeg", "gearSpeedDegPerSec", "maxSampleGapMs"):
        require(finite(limits[key]) and limits[key] > 0, f"Missing or invalid limit: {key}.")
    return limits


def check_artifacts(artifacts, folder):
    required = {"hardware", "encoders", "trigger", "gear"}
    require(isinstance(artifacts, dict) and required <= set(artifacts),
            "Attach independent hardware, encoder, trigger and gear records.")
    paths = set()
    for role in sorted(required):
        row = artifacts[role]
        filename = Path(row["file"])
        path = (folder / filename).resolve()
        require(not filename.is_absolute() and path.is_relative_to(folder) and path.is_file(),
                f"{role}: use an existing evidence file within the bench record directory.")
        require(path not in paths, "Specify separate raw evidence files for each channel group.")
        paths.add(path)
        with path.open("rb") as stream:
            require(hashlib.file_digest(stream, "sha256").hexdigest() == row["sha256"],
                    f"{role}: evidence checksum mismatch.")


def check_setup(bench, sdk):
    device = bench["device"]
    for key in ("model", "firmware", "terminalModel", "terminalRevision"):
        require(isinstance(device[key], str) and device[key].strip(), f"Confirm the installed {key}.")
    require(device["model"].upper().startswith("IMC30G-E") and device["terminalModel"].upper().startswith("IMC30-6G"),
            "This check applies to IMC30G-E with IMC30-6G.")
    require(type(device["axisCapacity"]) is int and device["axisCapacity"] >= 13 and device["terminalOnline"] is True,
            "The installed control card must have capacity for 13 axes and a confirmed online terminal.")
    require(re.fullmatch(r"[0-9a-f]{64}", device["sdkSha256"]) and
            any(item["sha256"] == device["sdkSha256"] and not any(item["missing"].values()) for item in sdk),
            "The installed SDK hash must match an inventoried DLL with all required exports.")
    compare = bench["compare"]
    require(compare["port"] in (4, 5) and compare["dimensionReadback"] == 3 and
            compare["outputType"] in ("PWM", "CMP", "EX-O") and
            isinstance(compare["physicalOutput"], str) and compare["physicalOutput"].strip() and
            compare["outputExclusive"] is True,
            "Confirm the 3D logical port, dimensional readback and exclusive physical output.")
    sources = compare["sources"]
    require([row["axis"] for row in sources] == list(AXES[:3]) and
            all(type(row["terminalChannel"]) is int and 0 <= row["terminalChannel"] <= 5 and
                row["source"] in ("ExternalEncoder", "InternalCounter") for row in sources) and
            len({row["terminalChannel"] for row in sources}) == 3,
            "Record three distinct, ordered Localbus channels and their position sources.")
    gear = bench["gear"]
    require((gear["masterAxis"], gear["followerAxis"], gear["source"], gear["ratioInDegrees"]) ==
            (2, 3, "CommandPosition", 1) and gear["followedFeedback"] is True and
            gear["releasedFeedback"] is True,
            "Confirm R1/R2 gear establishment, parameters and release readback.")


def evaluate_case(name, case, limits):
    trace = case["encoders"]
    edges = case["edges"]
    targets = case["targets"]
    require(isinstance(trace, list) and len(trace) >= 2 and all(numbers(row, 5) for row in trace),
            "Encoder samples use rows [time_ms, x_mm, y_mm, r1_deg, r2_deg].")
    gaps = [right[0] - left[0] for left, right in zip(trace, trace[1:])]
    require(all(0 < elapsed <= limits["maxSampleGapMs"] for elapsed in gaps), "Encoder time must increase without gaps.")
    require(isinstance(targets, list) and all(numbers(target, 3) for target in targets), "Invalid target coordinates.")
    times = [row[0] for row in trace]
    require(isinstance(edges, list) and all(isinstance(e, list) and len(e) == 2 and
            finite(e[0]) and times[0] <= e[0] <= times[-1] for e in edges), "Edges must share the bounded encoder timebase.")
    require(all(right[0] > left[0] for left, right in zip(edges, edges[1:])), "Edges must be strictly ordered.")

    def coordinate_at(time):
        index = bisect_left(times, time)
        if times[index] == time:
            return trace[index][1:4]
        before, after = trace[index - 1], trace[index]
        fraction = (time - before[0]) / (after[0] - before[0])
        return [before[column] + (after[column] - before[column]) * fraction for column in range(1, 4)]

    tolerance = limits["positionTolerance"]
    start = trace[0]
    master_change = trace[-1][3] - start[3]
    gear_error = max(abs((row[4] - start[4]) - (row[3] - start[3])) for row in trace)
    gear_speed_error = max(abs((right[4] - left[4]) - (right[3] - left[3])) * 1000 / elapsed
                           for left, right, elapsed in zip(trace, trace[1:], gaps))
    if name in ("forward", "reverse"):
        require(len(targets) >= 2 and len(edges) == len(targets) * 2, "Pulse count must match the ordered multi-point plan.")
        require(master_change * (1 if name == "forward" else -1) > limits["gearPositionDeg"], "Missing R1 motion in the expected direction.")
        require(all(abs(trace[-1][column] - start[column]) > tolerance[column - 1] for column in (1, 2, 3)),
                "Each of X, Y and R1 must move during the positive scan.")
        require(any(all(right[column] != left[column] for column in (1, 2, 3))
                    for left, right in zip(trace, trace[1:])), "No sampled interval shows X/Y/R1 moving together.")
        require(gear_error <= limits["gearPositionDeg"] and gear_speed_error <= limits["gearSpeedDegPerSec"],
                "R2 relative displacement or velocity exceeds its confirmed limit.")
        widths, maximum = [], [0.0, 0.0, 0.0]
        for index, target in enumerate(targets):
            rise, fall = edges[index * 2:index * 2 + 2]
            require(rise[1] == "rise" and fall[1] == "fall", "Pulse edges must alternate rising and falling.")
            width = (fall[0] - rise[0]) * 1000
            low, high = limits["pulseWidthUs"]
            require(low - 1e-8 <= width <= high + 1e-8, "Pulse width is outside the confirmed limits.")
            error = [abs(actual - expected) for actual, expected in zip(coordinate_at(rise[0]), target)]
            require(all(err <= limit for err, limit in zip(error, tolerance)),
                    f"Pulse {index + 1} misses the ordered X/Y/R1 target.")
            widths.append(width)
            maximum = [max(a, b) for a, b in zip(maximum, error)]
        return {"pulses": len(targets), "widthUs": widths, "maximumPositionError": maximum,
                "maximumGearErrorDeg": gear_error, "maximumGearSpeedErrorDegPerSec": gear_speed_error}

    require(not edges, "Unexpected trigger pulse in a rejection or release case.")
    if name == "gearExit":
        follower_travel = max(abs(row[4] - start[4]) for row in trace)
        require(not targets and abs(master_change) > limits["gearPositionDeg"] and
                follower_travel <= limits["gearPositionDeg"], "Record R1 moving and R2 stationary after gear release.")
        return {"pulses": 0, "maximumFollowerTravelDeg": follower_travel}

    require(len(targets) == 1, "A rejection case must challenge exactly one complete target.")
    target = targets[0]
    wrong = {"rejectX": 0, "rejectY": 1, "rejectR1": 2}[name]
    matching_other_axes = [row for row in trace if all(
        abs(row[i + 1] - target[i]) <= tolerance[i] for i in range(3) if i != wrong)]
    require(matching_other_axes and all(abs(row[wrong + 1] - target[wrong]) > tolerance[wrong]
                                      for row in matching_other_axes),
            "The other two coordinates must reach the target while the challenged coordinate is outside tolerance.")
    require(any(any(abs(row[i + 1] - start[i + 1]) > tolerance[i] for i in range(3) if i != wrong) for row in trace),
            "The rejection test must include observed motion.")
    return {"pulses": 0, "samplesWithOtherAxesInTolerance": len(matching_other_axes)}


def evaluate_bench(bench, folder, sdk):
    if bench is None:
        return {"status": "pending", "reason": "No completed hardware bench record."}
    require(isinstance(bench, dict), "Expected a hardware bench JSON object.")
    if bench.get("kind") == "template":
        return {"status": "pending", "reason": "No completed hardware bench record."}
    require(bench["kind"] == "hardware", "Synthetic recordings cannot be used as a hardware bench record.")
    require(all(isinstance(bench[key], str) and bench[key].strip() for key in ("technician", "reviewer", "testedAt")),
            "Record the bench date, technician and reviewer.")
    check_setup(bench, sdk)
    limits = checked_limits(bench["limits"])
    check_artifacts(bench["artifacts"], folder)
    cases = bench["cases"]
    require(set(cases) == set(REQUIRED_CASES), "Provide the six required comparison and gear test cases.")
    results, errors = {}, {}
    for name in REQUIRED_CASES:
        try:
            results[name] = evaluate_case(name, cases[name], limits)
        except (ValueError, KeyError, TypeError, OverflowError) as error:
            errors[name] = str(error)
    return {"status": "failed" if errors else "readyForReview", "cases": results, "errors": errors}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, default=ROOT / "examples/Motion/FlyScan.13Axes.json")
    parser.add_argument("--sdk", type=Path, action="append", help="Inspect an installed or documented x64 SDK DLL without loading it.")
    parser.add_argument("--bench", type=Path, help="Hardware bench JSON with evidence files beside it.")
    parser.add_argument("--require-bench", action="store_true", help="Return 2 when the bench record is pending.")
    parser.add_argument("--out", type=Path, help="Write a JSON report in addition to stdout.")
    args = parser.parse_args()
    files = args.sdk or [ROOT / "Hardware/Company.Hardware.ControlCard.INOVANCE/dll/IMC_API_x64.dll"]
    report = {"probeMode": "offlineFileInspection", "sdk": []}
    try:
        report["profile"] = profile_summary(read_json(args.profile))
        report["sdk"] = [sdk_inventory(path) for path in files]
        require(all(not any(item["missing"].values()) for item in report["sdk"]), "One or more inspected SDKs lack required exports.")
        report["bench"] = evaluate_bench(read_json(args.bench) if args.bench else None,
                                        args.bench.resolve().parent if args.bench else ROOT, report["sdk"])
    except (KeyError, OSError, ValueError, TypeError, OverflowError, struct.error) as error:
        report["bench"] = {"status": "failed", "reason": str(error)}
    text = json.dumps(report, indent=2)
    print(text)
    if args.out:
        args.out.write_text(text + "\n", encoding="utf-8")
    status = report["bench"]["status"]
    return 1 if status == "failed" else 2 if status == "pending" and args.require_bench else 0


if __name__ == "__main__":
    sys.exit(main())
