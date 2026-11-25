import hashlib
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
import unittest
from zipfile import ZipFile


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from package_release_candidates import CANDIDATES, MANIFEST, PLC_MODULES, RUNTIME
from verify_release_candidates import verify_package, verify_packages


def make_package(folder, app, *, shared=b"shared-ui", extra=None, omit=None, tamper=False):
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


if __name__ == "__main__":
    unittest.main()
