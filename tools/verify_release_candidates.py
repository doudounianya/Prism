"""Verify existing release ZIPs and replay the offline source checks."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
from time import monotonic
from zipfile import BadZipFile, ZipFile
from zlib import error as CompressionError

from package_release_candidates import CANDIDATES, MANIFEST, ROOT, RUNTIME, check_inventory


SOURCE_DIRECTORIES = ("Applications", "Framework", "Hardware", "examples", "tests", "tools")
SOURCE_FILES = ("PrismFramework.sln", "Directory.Build.props", "Directory.Build.targets", "NuGet.Config", "global.json")
SOURCE_EXCLUSIONS = shutil.ignore_patterns(
    "bin", "obj", ".git", ".vs", "__pycache__", "ConfigurationProfiles", "Database.local.json",
    "maintenance-locks.json", "launchSettings.json", "*.user", "*.sqlite", "*.sqlite-wal", "*.sqlite-shm",
)
WINDOWS_DEVICES = {"aux", "con", "nul", "prn", "conin$", "conout$"} | {
    f"{device}{number}" for device in ("com", "lpt") for number in "123456789¹²³"
}


def unique_object(pairs):
    result = dict(pairs)
    if len(result) != len(pairs):
        raise ValueError("Duplicate key in package manifest.")
    return result


def valid_member(name):
    return not re.search(r'[\\:<>"|?*\x00-\x1f]', name) and all(
        part and not part.endswith((".", " ")) and part.split(".", 1)[0].strip().casefold() not in WINDOWS_DEVICES
        for part in name.split("/")
    )


def verify_package(archive, application):
    with archive.open("rb") as stream, ZipFile(stream) as bundle:
        names = bundle.namelist()
        if not all(valid_member(name) for name in names) or len({name.casefold() for name in names}) != len(names):
            raise ValueError("Invalid or colliding Windows ZIP paths.")
        manifest = json.loads(bundle.read(MANIFEST), object_pairs_hook=unique_object)
        if (manifest["application"], manifest["artifact"], manifest["configuration"], manifest["requires"]) != (
            application, CANDIDATES[application], "Release", RUNTIME
        ):
            raise ValueError("Package identity or runtime differs from the expected candidate.")
        files = manifest["sha256"]
        if not isinstance(files, dict) or set(files) != set(names) - {MANIFEST}:
            raise ValueError("Archive entries differ from the manifest.")
        for name, expected in files.items():
            with bundle.open(name) as member:
                if hashlib.file_digest(member, "sha256").hexdigest() != expected:
                    raise ValueError(f"SHA-256 mismatch: {name}")
        check_inventory(application, set(files), bundle.read)
        stream.seek(0)
        return {"file": archive.name, "files": len(files), "sha256": hashlib.file_digest(stream, "sha256").hexdigest(),
                "sharedUiSha256": files["Company.Hmi.Presentation.dll"]}


def verify_packages(folder, results):
    verified = []
    for app, artifact in CANDIDATES.items():
        name = f"package-{app}"
        try:
            details = verify_package(folder / f"{artifact}.zip", app)
            record(results, name, "passed", **details)
            verified.append(details)
        except (OSError, ValueError, KeyError, TypeError, RuntimeError, BadZipFile, CompressionError) as error:
            record(results, name, "failed", detail=str(error))
    if len(verified) != len(CANDIDATES):
        record(results, "shared-ui", "notRun", detail="Both ZIPs must pass their individual checks.")
    elif len({item["sharedUiSha256"] for item in verified}) == 1:
        record(results, "shared-ui", "passed", sha256=verified[0]["sharedUiSha256"])
    else:
        record(results, "shared-ui", "failed", detail="The two packages contain different shared UI assemblies.")


def record(results, name, status, **details):
    print(f"{name}: {status}", flush=True)
    results.append({"check": name, "status": status, **details})


def run_check(results, output, source, env, name, command):
    log = output / f"{name}.log"
    start = monotonic()
    try:
        with log.open("wb") as stream:
            result = subprocess.run(command, cwd=source, env=env, stdout=stream, stderr=subprocess.STDOUT)
        record(results, name, "passed" if result.returncode == 0 else "failed",
               exitCode=result.returncode, elapsedSeconds=round(monotonic() - start, 2), log=log.name)
        return result.returncode == 0
    except OSError as error:
        record(results, name, "failed", log=log.name, detail=str(error))
        return False


def copy_source(destination):
    destination.mkdir()
    for name in SOURCE_DIRECTORIES:
        shutil.copytree(ROOT / name, destination / name, ignore=SOURCE_EXCLUSIONS)
    for name in SOURCE_FILES:
        if (ROOT / name).is_file():
            shutil.copyfile(ROOT / name, destination / name)


def source_fingerprint(source):
    digest = hashlib.sha256()
    files = sorted(path for path in source.rglob("*") if path.is_file())
    for path in files:
        digest.update(path.relative_to(source).as_posix().encode("utf-8") + b"\0")
        with path.open("rb") as stream:
            digest.update(hashlib.file_digest(stream, "sha256").digest())
    return {"files": len(files), "sha256": digest.hexdigest()}


def verify_source(results, output, restore_source, desktop):
    with TemporaryDirectory(prefix="prism-verify-") as workspace:
        source = Path(workspace) / "src"
        copy_source(source)
        record(results, "source-snapshot", "passed", **source_fingerprint(source))
        env = dict(os.environ, DOTNET_CLI_HOME=workspace, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
                   MSBUILDDISABLENODEREUSE="1", PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        msbuild = ["-p:SkipPlcHmiConfigurationSeed=true"]
        if restore_source:
            msbuild.extend((f"-p:RestoreSources={restore_source}", "-p:NuGetAudit=false"))

        def run(name, command):
            return run_check(results, output, source, env, name, command)

        run("architecture", [sys.executable, "tools/verify_architecture.py", "--report", str(output / "architecture.json")])
        run("h0", [sys.executable, "tests/test_flyscan_h0.py", "-v"])
        run("package-verifier", [sys.executable, "tests/test_verify_release_candidates.py", "-v"])
        run("build-debug", ["dotnet", "build", "PrismFramework.sln", "-c", "Debug", "-m:1", *msbuild])
        release_built = run("build-release", ["dotnet", "build", "PrismFramework.sln", "-c", "Release", "-m:1", *msbuild])

        suites = {
            "Headless.Smoke": [],
            "Platform.Smoke": [str(output / "platform.json")],
            "HmiAuthorization.Smoke": [str(output / "authorization.json")],
            "Runtime.Smoke": [str(output / "runtime.json")],
            "Motion.Configuration.Smoke": ["examples/Motion/FlyScan.13Axes.json", str(output / "motion.json")],
            "FlyScan.Workflow.Smoke": [str(output / "workflow")],
            "SharedIo.Smoke": [str(output / "shared-io")],
            "SharedControlPages.Smoke": [str(output / "shared-control")],
        }
        for suite, arguments in suites.items():
            if suite.startswith("Shared") and not desktop:
                record(results, suite, "notRun", detail="Desktop probes require --desktop.")
            elif not release_built:
                record(results, suite, "notRun", detail="The Release build failed.")
            else:
                run(suite, ["dotnet", "run", "--project", f"tests/{suite}/{suite}.csproj", "-c", "Release",
                            *msbuild, "--", *arguments])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path, help="Directory containing the two existing release ZIPs.")
    parser.add_argument("--output", required=True, type=Path, help="New directory for reports and logs.")
    parser.add_argument("--restore-source", type=Path, help="Local NuGet source for offline builds.")
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--packages-only", action="store_true", help="Verify ZIPs without .NET builds or tests.")
    modes.add_argument("--desktop", action="store_true", help="Also run the two FlyScan WPF simulation probes from source.")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        parser.error("--output must refer to a new directory.")
    output.mkdir(parents=True)

    results = []
    verify_packages(args.packages.resolve(), results)
    if args.packages_only:
        record(results, "source-checks", "notRun", detail="Package-only validation selected.")
    else:
        try:
            verify_source(results, output, args.restore_source.resolve() if args.restore_source else None, args.desktop)
        except OSError as error:
            record(results, "source-workspace", "failed", detail=str(error))

    counts = {status: sum(item["status"] == status for item in results) for status in ("passed", "failed", "notRun")}
    report = {"checks": results, "counts": counts, "passedSelectedChecks": counts["failed"] == 0,
              "separateAcceptance": {"plcDesktopAndRollback": "requiresFixture", "h0HardwareBench": "pending"}}
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Selected offline checks: {counts['passed']} passed, {counts['failed']} failed, {counts['notRun']} not run.")
    return 1 if counts["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
