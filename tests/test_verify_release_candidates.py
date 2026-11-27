from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
from tempfile import TemporaryDirectory
import unittest
from zipfile import ZipFile


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from flyscan_recovery_fixture import RECIPE, create_backup, read_history, restore_backup
from package_release_candidates import CANDIDATES, MANIFEST, PLC_MODULES, RUNTIME
from plc_history_fixture import HISTORY_DUMP, SOFTWARE_SETTINGS, WORKBOOK, create_desktop_backup, read_desktop_backup, verify_exit_timing
from release_source import RELEASE_DIRECTORIES, VERIFICATION_DIRECTORIES, copy_source, source_fingerprint
from verify_release_candidates import (
    FULL_OFFLINE_CHECKS, WARNING_BASELINE, read_build_warnings, require_full_offline_coverage, verify_build_warnings,
    verify_candidate_source, verify_package, verify_packages,
)


def make_package(folder, app, *, shared=b"shared-ui", extra=None, omit=None, tamper=False, source_inputs=None):
    artifact = CANDIDATES[app]
    files = {f"Company.{app}.Shell.{extension}": b"fixture" for extension in ("exe", "dll", "deps.json", "runtimeconfig.json")}
    files["Company.Hmi.Presentation.dll"] = shared
    if app == "PlcHmi":
        files.update({f"Modules/{module}": b"module" for module in PLC_MODULES})
        files.update({"NLog.config": b"log", "Config/Database.example.json": b"{}"})
    else:
        files.update({"Config/FlyScan.13Axes.json": b'{"enabled": false, "allowNativeHardware": false}',
                      "Config/FlyScan.DemoRecipe.json": b"{}", "Company.Hardware.ControlCard.None.dll": b"simulation"})
    if extra:
        files.update(extra)
    if omit:
        del files[omit]
    manifest = {"application": app, "artifact": artifact, "configuration": "Release", "requires": RUNTIME,
                "sha256": {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}
    if source_inputs is not None:
        manifest["sourceInputs"] = source_inputs
    if tamper:
        files["Company.Hmi.Presentation.dll"] += b"tampered"
    path = folder / f"{artifact}.zip"
    with ZipFile(path, "w") as bundle:
        bundle.writestr(MANIFEST, json.dumps(manifest))
        for name, data in files.items():
            bundle.writestr(name, data)
    return path


class ReleaseVerificationTests(unittest.TestCase):
    def setUp(self):
        workspace = TemporaryDirectory()
        self.addCleanup(workspace.cleanup)
        self.root = Path(workspace.name)

    def test_two_matching_packages(self):
        make_package(self.root, "PlcHmi")
        make_package(self.root, "FlyScan")
        results = []
        verify_packages(self.root, results)
        self.assertEqual([result["status"] for result in results], ["passed", "passed", "passed"])

    def test_plc_desktop_backup_restores_exact_inputs_and_rejects_changed_members(self):
        files = {WORKBOOK: b"loopback workbook", SOFTWARE_SETTINGS: b'{"SoftwareConfig":{"Name":"original"}}',
                 HISTORY_DUMP: b"fixture database"}
        for name in (WORKBOOK, SOFTWARE_SETTINGS):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(files[name])
        archive = self.root / "desktop.zip"
        manifest = create_desktop_backup(archive, self.root, files[HISTORY_DUMP])
        (self.root / SOFTWARE_SETTINGS).write_bytes(b'{"SoftwareConfig":{"Name":"candidate"}}')
        self.assertEqual(read_desktop_backup(archive, manifest), files)

        for changes, expected in (({SOFTWARE_SETTINGS: b"changed"}, "hash mismatch"),
                                  ({"Config/Database.local.json": b"local credentials"}, "inventory changed")):
            with self.subTest(expected=expected):
                modified = self.root / "altered.zip"
                with ZipFile(modified, "w") as bundle:
                    for name, data in (files | changes).items():
                        bundle.writestr(name, data)
                    bundle.writestr("sha256.json", json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, expected):
                    read_desktop_backup(modified, manifest)

    def test_tampered_binary_fails_against_manifest(self):
        archive = make_package(self.root, "FlyScan", tamper=True)
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            verify_package(archive, "FlyScan")

    def test_archive_scope_with_valid_recomputed_hashes(self):
        cases = [
            ("FlyScan", {"runtimes/win-x64/native/IMC_API_x64.dll": b"driver"}, None, "another product"),
            ("PlcHmi", {"Config/Database.local.json": b"{}"}, None, "database example"),
            ("PlcHmi", None, "Modules/Company.PlcHmi.Login.dll", "runtime Prism modules"),
        ]
        for app, extra, omit, message in cases:
            with self.subTest(app=app, extra=extra, omit=omit):
                archive = make_package(self.root, app, extra=extra, omit=omit)
                with self.assertRaisesRegex(ValueError, message):
                    verify_package(archive, app)

    def test_invalid_or_colliding_windows_paths(self):
        for member in ("../outside.dll", "config/flyscan.13axes.json", "runtimes/NUL.dll"):
            with self.subTest(member=member):
                archive = make_package(self.root, "FlyScan", extra={member: b"duplicate or invalid"})
                with self.assertRaisesRegex(ValueError, "Windows ZIP paths"):
                    verify_package(archive, "FlyScan")

    def test_mismatched_shared_ui_fails_pair(self):
        make_package(self.root, "PlcHmi")
        make_package(self.root, "FlyScan", shared=b"another UI build")
        results = []
        verify_packages(self.root, results)
        self.assertEqual([result["status"] for result in results], ["passed", "passed", "failed"])

    def test_failed_package_blocks_pair_check(self):
        make_package(self.root, "PlcHmi")
        (self.root / "FlyScan-Simulation.zip").write_bytes(b"invalid ZIP")
        results = []
        verify_packages(self.root, results)
        self.assertEqual([result["status"] for result in results], ["passed", "failed", "notRun"])

    def test_release_fingerprint_covers_packaging_inputs_and_excludes_build_outputs(self):
        source = self.root / "repo"
        source.mkdir()
        for directory in (*RELEASE_DIRECTORIES, *VERIFICATION_DIRECTORIES):
            (source / directory).mkdir()
        (source / "Directory.Build.targets").write_bytes(b"release build input")
        assembly = source / "Applications/App.cs"
        assembly.write_bytes(b"class Original {}")
        test = source / "tests/Test.cs"
        test.write_bytes(b"test source")
        outputs = source / "Applications/bin"
        outputs.mkdir()
        (outputs / "cached.dll").write_bytes(b"old assembly")
        (source / "Applications/Database.local.json").write_bytes(b"deployment configuration")
        first = self.root / "first"
        copy_source(source, first)
        self.assertFalse((first / "Applications/bin").exists())
        self.assertFalse((first / "Applications/Database.local.json").exists())
        fingerprint = source_fingerprint(first, release_only=True)
        self.assertEqual(fingerprint["files"], 2)
        test.write_bytes(b"updated test source")
        second = self.root / "second"
        copy_source(source, second)
        self.assertNotEqual(source_fingerprint(first), source_fingerprint(second))
        self.assertEqual(fingerprint, source_fingerprint(second, release_only=True))
        assembly.write_bytes(b"class Updated {}")
        third = self.root / "third"
        copy_source(source, third)
        self.assertNotEqual(fingerprint, source_fingerprint(third, release_only=True))

    def test_package_source_gate_compares_both_packages_with_verification_inputs(self):
        expected = {"files": 42, "sha256": "a" * 64}
        outdated = {"files": 42, "sha256": "b" * 64}
        for fingerprints, mismatched in (((expected, expected), []),
                                          ((expected, outdated), ["FlyScan"]),
                                          ((None, None), ["PlcHmi", "FlyScan"]),
                                          ((outdated, outdated), ["PlcHmi", "FlyScan"])):
            with self.subTest(fingerprints=fingerprints):
                for application, fingerprint in zip(CANDIDATES, fingerprints):
                    make_package(self.root, application, source_inputs=fingerprint)
                results = []
                verify_packages(self.root, results)
                self.assertEqual([item["status"] for item in results], ["passed", "passed", "passed"])
                self.assertEqual(verify_candidate_source(results, expected), not mismatched)
                self.assertEqual(results[-1]["mismatched"], mismatched)

    def test_package_verifier_rejects_invalid_source_fingerprint(self):
        for invalid in ([], {"files": True, "sha256": "a" * 64}, {"files": 42, "sha256": "not a digest"}):
            with self.subTest(invalid=invalid):
                archive = make_package(self.root, "PlcHmi", source_inputs=invalid)
                with self.assertRaisesRegex(ValueError, "Invalid release source fingerprint"):
                    verify_package(archive, "PlcHmi")

    def test_full_offline_gate_reports_missing_and_skipped_checks(self):
        missing = "flyscan-upgrade-and-rollback"
        skipped = "plc-lifecycle"
        results = [{"check": name, "status": "notRun" if name == skipped else "passed"}
                   for name in sorted(FULL_OFFLINE_CHECKS - {missing})]
        require_full_offline_coverage(results)
        self.assertEqual(results[-1]["check"], "full-offline-coverage")
        self.assertEqual(results[-1]["status"], "failed")
        self.assertEqual(results[-1]["missing"], [missing])
        self.assertEqual(results[-1]["skipped"], [skipped])

    def test_full_offline_gate_preserves_complete_check_results(self):
        results = [{"check": name, "status": "passed"} for name in sorted(FULL_OFFLINE_CHECKS)]
        expected = results.copy()
        require_full_offline_coverage(results)
        self.assertEqual(results, expected)

    def test_build_warnings_keep_source_location_and_normalize_temporary_project(self):
        source = Path("C:/scratch/src")
        warning = (r"C:\scratch\src\App\Shell.cs(25,16): warning CS8618: Member 'Config' is required."
                   r" [C:\scratch\src\App\App_8n7wp121_wpftmp.csproj]")
        text = f"{warning}\n{warning}\n    1 个警告\n"
        self.assertEqual(read_build_warnings(text, source), {
            ("App/App_wpftmp.csproj", "App/Shell.cs(25,16)", "CS8618", "Member 'Config' is required.")
        })

    def test_missing_or_inconsistent_build_warning_total_fails(self):
        for text in ("Build output interrupted.", "    1 Warning(s)\n", "CSC : warning : Unexpected warning.\n    0 Warning(s)\n"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                read_build_warnings(text, self.root)

    def test_build_warning_gate_fails_when_a_diagnostic_changes_at_the_same_location(self):
        baseline = self.root / WARNING_BASELINE
        baseline.parent.mkdir()
        retained = "Member 'Config' is required."
        introduced = "Member 'Settings' is required."
        baseline.write_text(json.dumps({
            "g0ArchiveSha256": "fixture",
            "columns": ["project", "sourceLocation", "code", "message"],
            "warnings": [("App.csproj", "Shell.cs(25,16)", "CS8618", retained)],
        }), encoding="utf-8")
        for config, message in (("debug", retained), ("release", introduced)):
            text = f"Shell.cs(25,16): warning CS8618: {message} [App.csproj]\n    1 Warning(s)\n"
            (self.root / f"build-{config}.log").write_text(text, encoding="utf-8")
        results = []
        verify_build_warnings(results, self.root, self.root)
        self.assertEqual([(entry["status"], entry["added"]) for entry in results], [("failed", 1)])
        report = json.loads((self.root / results[0]["details"]).read_text(encoding="utf-8"))
        self.assertEqual(report["configurations"]["Debug"]["unchangedCount"], 1)
        release = report["configurations"]["Release"]
        self.assertEqual(release["g0Count"], release["currentCount"])
        self.assertEqual(release["added"][0][-1], introduced)
        self.assertEqual(release["cleared"][0][-1], retained)
        (self.root / "build-release.log").write_text("    0 Warning(s)\n", encoding="utf-8")
        results.clear()
        verify_build_warnings(results, self.root, self.root)
        self.assertEqual(results[0]["status"], "passed")

    def test_full_offline_requires_both_baselines(self):
        make_package(self.root, "PlcHmi")
        output = self.root / "report"
        command = [sys.executable, str(Path(__file__).resolve().parents[1] / "tools/verify_release_candidates.py"),
                   "--full-offline", "--packages", str(self.root), "--output", str(output),
                   "--baseline-packages", str(self.root)]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=10)
        self.assertEqual(result.returncode, 2)
        self.assertIn("--full-offline requires an existing --flyscan-baseline ZIP", result.stderr)
        self.assertFalse(output.exists())

    def test_package_only_replay_does_not_report_full_offline_pass(self):
        make_package(self.root, "PlcHmi")
        make_package(self.root, "FlyScan")
        output = self.root / "report"
        command = [sys.executable, str(Path(__file__).resolve().parents[1] / "tools/verify_release_candidates.py"),
                   "--packages-only", "--packages", str(self.root), "--output", str(output)]
        subprocess.run(command, check=True, capture_output=True, timeout=10)
        report = json.loads((output / "report.json").read_text(encoding="utf-8"))
        self.assertEqual(report["counts"], {"passed": 3, "failed": 0, "notRun": 1})
        self.assertTrue(report["passedSelectedChecks"])
        self.assertEqual(report["separateAcceptance"]["fullOffline"], "notRun")

    def test_lifecycle_checks_both_exit_limits(self):
        marker = 'EXIT_TIMING {"ConfirmToWindowCloseMs":5000,"WindowClosedUnixMs":10000}'
        self.assertEqual(verify_exit_timing([marker], 12000),
                         {"confirmToWindowClose": 5000, "windowCloseToProcessExit": 2000})
        for lines, process_exit in [
            ([marker], 12001),
            ([marker], 9999),
            ([marker.replace('5000', '5001')], 12000),
            ([], 12000),
            ([marker, marker], 12000),
        ]:
            with self.subTest(lines=lines, process_exit=process_exit), self.assertRaises(RuntimeError):
                verify_exit_timing(lines, process_exit)

    def test_flyscan_backup_preserves_sqlite_wal_and_checkpoint(self):
        source = self.root / "source"
        source.mkdir()
        checkpoint = b'{"counts":{"pieces":1}}'
        (source / "run.json").write_bytes(checkpoint)
        recipe = b'{"name":"operator recipe"}'
        (source / RECIPE).write_bytes(recipe)
        restored = self.root / "restored"
        archive = self.root / "backup.zip"
        with closing(sqlite3.connect(source / "history.sqlite")) as db:
            db.execute("PRAGMA journal_mode=WAL")
            db.executescript("CREATE TABLE pieces(id TEXT,stamp INTEGER); CREATE TABLE operations(component TEXT,stamp INTEGER,action TEXT);")
            db.execute("INSERT INTO pieces VALUES(?,?)", ("previous-piece", 123))
            db.execute("INSERT INTO operations VALUES(?,?,?)", ("previous-piece", 123, "工件完成"))
            db.commit()
            self.assertTrue((source / "history.sqlite-wal").is_file())
            manifest = create_backup(source, archive)
            restore_backup(archive, restored, manifest)
            rollback = self.root / "rollback"
            restore_backup(archive, rollback, manifest)
        self.assertEqual((restored / "run.json").read_bytes(), checkpoint)
        self.assertEqual(set(path.name for path in restored.iterdir()), set(manifest))
        self.assertEqual(read_history(restored), [("previous-piece", 123)])
        self.assertEqual((restored / RECIPE).read_bytes(), recipe)
        self.assertEqual((rollback / RECIPE).read_bytes(), recipe)
        self.assertEqual(read_history(rollback), read_history(restored))

    def test_flyscan_restore_publishes_only_a_complete_valid_backup(self):
        source = self.root / "source"
        source.mkdir()
        (source / "run.json").write_bytes(b'{"counts":{"pieces":1}}')
        (source / RECIPE).write_bytes(b'{"name":"operator recipe"}')
        with closing(sqlite3.connect(source / "history.sqlite")) as database:
            database.execute("PRAGMA user_version=1")
        valid = self.root / "valid.zip"
        expected = create_backup(source, valid)
        with ZipFile(valid) as archive:
            payload = {name: archive.read(name) for name in expected}

        failures = (
            ("hash-mismatch", "run.json", b"mismatched checkpoint", ValueError),
            ("invalid-history", "history.sqlite", b"invalid database bytes", sqlite3.DatabaseError),
        )
        for label, changed_file, content, exception in failures:
            with self.subTest(case=label):
                altered = {**payload, changed_file: content}
                manifest = expected.copy()
                if label == "invalid-history":
                    manifest[changed_file] = hashlib.sha256(content).hexdigest()
                broken = self.root / (label + ".zip")
                with ZipFile(broken, "w") as archive:
                    for name, data in altered.items():
                        archive.writestr(name, data)
                    archive.writestr("sha256.json", json.dumps(manifest))
                restored = self.root / label
                with self.assertRaises(exception):
                    restore_backup(broken, restored, manifest)
                self.assertFalse(restored.exists(), "Failed verification left a usable-looking restore directory.")
                restore_backup(valid, restored, expected)
                self.assertEqual({name: (restored / name).read_bytes() for name in expected}, payload)

        def reject_checkpoints(_):
            raise ValueError("fixture: mismatched workpiece data")

        rejected = self.root / "rejected-checkpoint"
        with self.assertRaisesRegex(ValueError, "mismatched workpiece data"):
            restore_backup(valid, rejected, expected, validate=reject_checkpoints)
        self.assertFalse(rejected.exists())


if __name__ == "__main__":
    unittest.main()
