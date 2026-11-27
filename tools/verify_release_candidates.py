"""Verify existing release ZIPs and replay the offline source checks."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
from sqlite3 import Error as SqliteError
import subprocess
import sys
from tempfile import TemporaryDirectory
from time import monotonic
from zipfile import BadZipFile, ZipFile
from zlib import error as CompressionError

from flyscan_recovery_fixture import PROBE as FLYSCAN_PROBE, run_recovery
from package_release_candidates import CANDIDATES, MANIFEST, ROOT, RUNTIME, check_inventory
from plc_history_fixture import PROBE, run_desktop_rollback, run_lifecycle, run_mode_operations, run_opcua, run_parameters, run_rollback
from release_source import copy_source, source_fingerprint


WINDOWS_DEVICES = {"aux", "con", "nul", "prn", "conin$", "conout$"} | {
    f"{device}{number}" for device in ("com", "lpt") for number in "123456789¹²³"
}
FULL_OFFLINE_CHECKS = {
    "package-PlcHmi", "package-FlyScan", "shared-ui", "package-source-inputs",
    "source-snapshot", "architecture", "h0", "package-verifier", "build-debug", "build-release", "build-warning-baseline",
    "build-flyscan-debug",
    "Headless.Smoke", "Platform.Smoke", "HmiAuthorization.Smoke", "Runtime.Smoke",
    "Motion.Configuration.Smoke", "FlyScan.Workflow.Smoke", "SharedIo.Smoke", "SharedControlPages.Smoke",
    "flyscan-package-recovery", "flyscan-upgrade-and-rollback", "build-plc-history-probe",
    "plc-history-and-rollback", "plc-desktop-and-rollback", "plc-opcua-loopback",
    "plc-parameter-permissions", "plc-mode-operations", "plc-lifecycle",
}
BUILD_CONFIGURATIONS = ("Debug", "Release")
WARNING_BASELINE = "tools/g0-build-warnings.json"
WARNING_LINE = re.compile(r"^\s*(.+?):\s*warning\s+([A-Z]+\d+)\s*:\s*(.*?)(?:\s+\[([^\]]+)\])?\s*$", re.I)
WARNING_TOTAL = re.compile(r"^\s*(\d+)\s*(?:个警告|Warning\(s\))\s*$", re.I)
WARNING_MARKER = re.compile(r":\s*warning\b", re.I)


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
        details = {"file": archive.name, "files": len(files), "sha256": hashlib.file_digest(stream, "sha256").hexdigest(),
                   "sharedUiSha256": files["Company.Hmi.Presentation.dll"]}
        if "sourceInputs" in manifest:
            source = manifest["sourceInputs"]
            if (not isinstance(source, dict) or set(source) != {"files", "sha256"} or
                type(source["files"]) is not int or source["files"] < 1 or
                not isinstance(source["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", source["sha256"])):
                raise ValueError("Invalid release source fingerprint in package manifest.")
            details["sourceInputs"] = source
        return details


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


def require_full_offline_coverage(results):
    missing = sorted(FULL_OFFLINE_CHECKS - {item["check"] for item in results})
    skipped = sorted(item["check"] for item in results if item["status"] == "notRun")
    if missing or skipped:
        record(results, "full-offline-coverage", "failed",
               detail="The complete offline suite did not run every required check.", missing=missing, skipped=skipped)


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


def read_build_warnings(log_text, source):
    root = str(source).replace("\\", "/").rstrip("/") + "/"

    def relative(text):
        return re.sub(re.escape(root), "", text.replace("\\", "/"), flags=re.I)

    warnings = set()
    totals = []
    for line in log_text.splitlines():
        if total := WARNING_TOTAL.fullmatch(line):
            totals.append(int(total[1]))
        elif warning := WARNING_LINE.fullmatch(line):
            location, code, message, project = warning.groups()
            project = re.sub(r"_[A-Za-z0-9]+_wpftmp\.csproj$", "_wpftmp.csproj", relative(project or ""), flags=re.I)
            warnings.add((project, relative(location), code.upper(), relative(message)))
        elif WARNING_MARKER.search(line):
            raise ValueError("An MSBuild warning line could not be parsed.")
    # MSBuild repeats each warning in the build summary. Keep distinct diagnostics and check the reported total.
    if totals != [len(warnings)]:
        raise ValueError(f"MSBuild warning totals {totals} do not match {len(warnings)} distinct diagnostics.")
    return warnings


def verify_build_warnings(results, output, source):
    report_path = output / "build-warning-baseline.json"
    baseline = json.loads((source / WARNING_BASELINE).read_text(encoding="utf-8"), object_pairs_hook=unique_object)
    # PLC HMI 的相同源文件现在由公共入口编译。保留归档中的原始 G0 项目路径。
    old_shell = "Applications/PlcHmi/Company.PlcHmi.Shell/Company.PlcHmi.Shell"
    renamed_projects = {f"{old_shell}{suffix}.csproj": f"Company.Shell/Company.Shell{suffix}.csproj"
                        for suffix in ("", "_wpftmp")}
    expected = {(renamed_projects.get(project, project), location, code, message)
                for project, location, code, message in baseline["warnings"]}
    configurations = {}
    for config in BUILD_CONFIGURATIONS:
        log = output / f"build-{config.lower()}.log"
        current = read_build_warnings(log.read_text(encoding="utf-8-sig"), source)
        configurations[config] = {"log": log.name, "g0Count": len(expected), "currentCount": len(current),
                                  "unchangedCount": len(expected & current), "added": sorted(current - expected),
                                  "cleared": sorted(expected - current)}
    added = sum(len(item["added"]) for item in configurations.values())
    status = "failed" if added else "passed"
    evidence = {"status": status, "g0ArchiveSha256": baseline["g0ArchiveSha256"], "columns": baseline["columns"],
                "configurations": configurations}
    report_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    record(results, "build-warning-baseline", status, details=report_path.name, added=added)


def verify_candidate_source(results, expected):
    package_results = {item["check"]: item for item in results}
    actual = {app: package_results[f"package-{app}"].get("sourceInputs") for app in CANDIDATES}
    mismatched = [app for app, fingerprint in actual.items() if fingerprint != expected]
    record(results, "package-source-inputs", "failed" if mismatched else "passed",
           expectedSourceInputs=expected, candidateSourceInputs=actual, mismatched=mismatched)
    return not mismatched


def verify_source(results, output, restore_source, desktop, full_offline, mysql_bin, plc_fixtures, flyscan_fixtures):
    with TemporaryDirectory(prefix="prism-verify-") as workspace:
        source = Path(workspace) / "src"
        copy_source(ROOT, source)
        release_inputs = source_fingerprint(source, release_only=True)
        record(results, "source-snapshot", "passed", **source_fingerprint(source), releaseInputs=release_inputs)
        if full_offline and not verify_candidate_source(results, release_inputs):
            return
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
        build_env = dict(env, DOTNET_CLI_UI_LANGUAGE="zh-CN") if full_offline else env
        build_results = {
            config: run_check(results, output, source, build_env, f"build-{config.lower()}",
                              ["dotnet", "build", "PrismFramework.sln", "-c", config, "-m:1", *msbuild])
            for config in BUILD_CONFIGURATIONS
        }
        release_built = build_results["Release"]
        run("build-flyscan-debug", ["dotnet", "build", "PrismFramework.sln", "-c", "FlyScanDebug", "-m:1", *msbuild])
        if full_offline:
            if not all(build_results.values()):
                record(results, "build-warning-baseline", "notRun", detail="A required build failed.")
            else:
                try:
                    verify_build_warnings(results, output, source)
                except (OSError, ValueError, KeyError, TypeError) as error:
                    record(results, "build-warning-baseline", "failed", detail=str(error))

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
        flyscan_probe_ready = False
        for suite, arguments in suites.items():
            if suite.startswith("Shared") and not desktop:
                record(results, suite, "notRun", detail="Desktop probes require --desktop.")
            elif not release_built:
                record(results, suite, "notRun", detail="The Release build failed.")
            else:
                product = ["-p:CompanyProduct=FlyScan"] if suite.startswith("Shared") else []
                ran = run(suite, ["dotnet", "run", "--project", f"tests/{suite}/{suite}.csproj", "-c", "Release",
                                  *msbuild, *product, "--", *arguments])
                if suite == FLYSCAN_PROBE:
                    flyscan_probe_ready = ran

        if flyscan_fixtures:
            if not desktop and release_built:
                flyscan_probe_ready = run("build-flyscan-recovery-probe", [
                    "dotnet", "build", f"tests/{FLYSCAN_PROBE}/{FLYSCAN_PROBE}.csproj", "-c", "Release", "-m:1",
                    "-p:CompanyProduct=FlyScan", *msbuild])
            for name, archive, baseline in flyscan_fixtures:
                if not flyscan_probe_ready:
                    record(results, name, "notRun", detail="The Release build or FlyScan desktop probe failed.")
                    continue
                evidence = output / name
                try:
                    verified = run_recovery(source, Path(workspace) / name, evidence, env,
                                            archive, verify_package, baseline=baseline)
                    record(results, name, "passed", details=(evidence / "report.json").relative_to(output).as_posix(),
                           assertions=sum(phase["assertions"] for phase in verified["phases"]))
                except (OSError, ValueError, KeyError, RuntimeError, BadZipFile, CompressionError, SqliteError,
                        subprocess.SubprocessError) as error:
                    record(results, name, "failed", detail=str(error), logs=evidence.relative_to(output).as_posix())

        if plc_fixtures:
            build_probe = ["dotnet", "build", f"tests/{PROBE}/{PROBE}.csproj", "-c", "Release", "-m:1", *msbuild]
            ready = release_built and run("build-plc-history-probe", build_probe)
            for name, folder, replay, arguments in plc_fixtures:
                if not ready:
                    record(results, name, "notRun", detail="The Release build or PLC desktop probe build failed.")
                    continue
                evidence = output / folder
                try:
                    verified = replay(source, Path(workspace) / folder, evidence, env, mysql_bin, *arguments, verify_package)
                    record(results, name, "passed", details=(evidence / "report.json").relative_to(output).as_posix(),
                           assertions=sum(phase["assertions"] for phase in verified["phases"]))
                except (OSError, ValueError, KeyError, StopIteration, RuntimeError, BadZipFile,
                        CompressionError, subprocess.SubprocessError) as error:
                    record(results, name, "failed", detail=str(error), logs=evidence.relative_to(output).as_posix())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path, help="Directory containing the two existing release ZIPs.")
    parser.add_argument("--output", required=True, type=Path, help="New directory for reports and logs.")
    parser.add_argument("--restore-source", type=Path, help="Local NuGet source for offline builds.")
    parser.add_argument("--baseline-packages", type=Path, help="Directory containing both previous release ZIPs for --full-offline.")
    parser.add_argument("--plc-history-baseline", type=Path, help="Trusted previous PLC HMI candidate ZIP for the history-only rollback check.")
    parser.add_argument("--plc-desktop-baseline", type=Path, help="Trusted previous PLC HMI candidate ZIP for the desktop and OPC UA rollback check.")
    parser.add_argument("--plc-opcua", action="store_true", help="Run the packaged PLC HMI mode, IO reconnect and stop handshake checks over OPC UA loopback.")
    parser.add_argument("--plc-parameters", action="store_true", help="Run the packaged PLC HMI axis and cylinder permissions checks over OPC UA loopback.")
    parser.add_argument("--plc-mode-operations", action="store_true", help="Run the packaged PLC HMI debug, initialization and personnel lock checks over OPC UA loopback.")
    parser.add_argument("--plc-lifecycle", action="store_true", help="Run PLC HMI tray, held-button disconnect, shutdown and restart checks over OPC UA loopback.")
    parser.add_argument("--flyscan-recovery", action="store_true", help="Back up and recover an interrupted job using verified FlyScan simulation installations.")
    parser.add_argument("--flyscan-baseline", type=Path, help="Trusted previous FlyScan simulation ZIP for upgrade and rollback of a backed-up job.")
    parser.add_argument("--mysql-bin", type=Path, help="MySQL Server 8 bin directory used to create the temporary PLC desktop fixture.")
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--packages-only", action="store_true", help="Verify ZIPs without .NET builds or tests.")
    modes.add_argument("--desktop", action="store_true", help="Also run the two FlyScan WPF simulation probes from source.")
    modes.add_argument("--full-offline", action="store_true", help="Require the complete Windows offline release matrix.")
    args = parser.parse_args()
    if args.baseline_packages and not args.full_offline:
        parser.error("--baseline-packages requires --full-offline.")
    if args.full_offline:
        if args.baseline_packages:
            previous_plc = args.baseline_packages / f"{CANDIDATES['PlcHmi']}.zip"
            args.plc_history_baseline = args.plc_history_baseline or previous_plc
            args.plc_desktop_baseline = args.plc_desktop_baseline or previous_plc
            args.flyscan_baseline = args.flyscan_baseline or args.baseline_packages / f"{CANDIDATES['FlyScan']}.zip"
        for option, archive in (("--plc-history-baseline", args.plc_history_baseline),
                                ("--plc-desktop-baseline", args.plc_desktop_baseline),
                                ("--flyscan-baseline", args.flyscan_baseline)):
            if archive is None or not archive.is_file():
                parser.error(f"--full-offline requires an existing {option} ZIP. Supply it directly or through --baseline-packages.")
        if args.mysql_bin is None or not args.mysql_bin.is_dir():
            parser.error("--full-offline requires an existing MySQL bin directory via --mysql-bin.")

    plc_desktop = bool(args.plc_history_baseline or args.plc_desktop_baseline or args.plc_opcua or
                       args.plc_parameters or args.plc_mode_operations or args.plc_lifecycle)
    if plc_desktop != bool(args.mysql_bin) or (plc_desktop and args.packages_only):
        parser.error("Use --mysql-bin with a PLC desktop check and source verification enabled.")
    if plc_desktop and os.name != "nt":
        parser.error("The PLC desktop fixture requires Windows.")
    verify_flyscan = args.flyscan_recovery or args.flyscan_baseline is not None
    if verify_flyscan and (os.name != "nt" or args.packages_only):
        parser.error("FlyScan recovery and rollback require Windows and source verification enabled.")
    output = args.output.resolve()
    if output.exists():
        parser.error("--output must refer to a new directory.")
    output.mkdir(parents=True)

    packages = args.packages.resolve()
    if not args.full_offline:
        return run_verification(args, output, packages)
    with TemporaryDirectory(prefix="prism-candidates-") as temporary:
        snapshot = Path(temporary)
        for name in CANDIDATES.values():
            filename = f"{name}.zip"
            archive = packages / filename
            if archive.is_file():
                shutil.copyfile(archive, snapshot / filename)
        return run_verification(args, output, snapshot)


def run_verification(args, output, packages):
    results = []
    verify_packages(packages, results)
    candidate = packages / f"{CANDIDATES['PlcHmi']}.zip"
    flyscan_archive = packages / f"{CANDIDATES['FlyScan']}.zip"
    flyscan_fixtures = []
    if args.flyscan_recovery or args.full_offline:
        flyscan_fixtures.append(("flyscan-package-recovery", flyscan_archive, None))
    if args.flyscan_baseline:
        flyscan_fixtures.append(("flyscan-upgrade-and-rollback", flyscan_archive, args.flyscan_baseline.resolve()))
    plc_fixtures = []
    if args.plc_history_baseline:
        plc_fixtures.append(("plc-history-and-rollback", "plc-history", run_rollback, (args.plc_history_baseline.resolve(), candidate)))
    if args.plc_desktop_baseline:
        plc_fixtures.append(("plc-desktop-and-rollback", "plc-desktop", run_desktop_rollback, (args.plc_desktop_baseline.resolve(), candidate)))
    if args.plc_opcua or args.full_offline:
        plc_fixtures.append(("plc-opcua-loopback", "plc-opcua", run_opcua, (candidate,)))
    if args.plc_parameters or args.full_offline:
        plc_fixtures.append(("plc-parameter-permissions", "plc-parameters", run_parameters, (candidate,)))
    if args.plc_mode_operations or args.full_offline:
        plc_fixtures.append(("plc-mode-operations", "plc-mode-operations", run_mode_operations, (candidate,)))
    if args.plc_lifecycle or args.full_offline:
        plc_fixtures.append(("plc-lifecycle", "plc-lifecycle", run_lifecycle, (candidate,)))
    if args.packages_only:
        record(results, "source-checks", "notRun", detail="Package-only validation selected.")
    else:
        try:
            verify_source(results, output, args.restore_source.resolve() if args.restore_source else None,
                          args.desktop or args.full_offline, args.full_offline,
                          args.mysql_bin.resolve() if args.mysql_bin else None, plc_fixtures, flyscan_fixtures)
        except OSError as error:
            record(results, "source-workspace", "failed", detail=str(error))

    if args.full_offline:
        require_full_offline_coverage(results)
    counts = {status: sum(item["status"] == status for item in results) for status in ("passed", "failed", "notRun")}
    status_by_check = {item["check"]: item["status"] for item in results}
    full_offline = ("failed" if counts["failed"] else "passed") if args.full_offline else "notRun"
    report = {"checks": results, "counts": counts, "passedSelectedChecks": counts["failed"] == 0,
              "separateAcceptance": {"fullOffline": full_offline,
                                     "plcDesktopAndRollback": status_by_check.get("plc-desktop-and-rollback", "notRun"), "h0HardwareBench": "pending",
                                     "plcHistoryAndRollback": status_by_check.get("plc-history-and-rollback", "notRun"),
                                     "plcOpcUaLoopback": status_by_check.get("plc-opcua-loopback", "notRun"),
                                     "plcParameterPermissions": status_by_check.get("plc-parameter-permissions", "notRun"),
                                     "plcModeOperations": status_by_check.get("plc-mode-operations", "notRun"),
                                     "plcLifecycle": status_by_check.get("plc-lifecycle", "notRun"),
                                     "flyscanPackageRecovery": status_by_check.get("flyscan-package-recovery", "notRun"),
                                     "flyscanUpgradeAndRollback": status_by_check.get("flyscan-upgrade-and-rollback", "notRun")}}
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Selected offline checks: {counts['passed']} passed, {counts['failed']} failed, {counts['notRun']} not run.")
    return 1 if counts["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
