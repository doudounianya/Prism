"""Build separate Windows packages for PLC HMI and FlyScan simulation."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZipFile


ROOT = Path(__file__).resolve().parents[1]
CANDIDATES = {"PlcHmi": "PlcHmi-ReleaseCandidate", "FlyScan": "FlyScan-Simulation"}
MANIFEST = "package-manifest.json"
RUNTIME = ".NET 8 Windows Desktop Runtime (x64)"
PLC_MODULES = {f"Company.PlcHmi.{name}.dll" for name in (
    "Composition", "Configuration", "Login", "Main", "Menu", "Startup",
)}
SIMULATION_CONFIG = {"Config/FlyScan.13Axes.json", "Config/FlyScan.DemoRecipe.json"}


def check_inventory(app, files, read_file):
    if app not in CANDIDATES:
        raise ValueError(f"Unknown application: {app}")
    shell = f"Company.{app}.Shell"
    required = {f"{shell}.{extension}" for extension in ("exe", "dll", "deps.json", "runtimeconfig.json")}
    required.add("Company.Hmi.Presentation.dll")
    modules = {name for name in files if name.startswith("Modules/")}
    config = {name for name in files if name.startswith("Config/")}
    if app == "PlcHmi":
        required.update({"NLog.config", "Config/Database.example.json"})
        if modules != {f"Modules/{name}" for name in PLC_MODULES} or config != {"Config/Database.example.json"}:
            raise ValueError("PLC HMI must contain exactly the runtime Prism modules and the database example.")
        foreign = ("Company.FlyScan.",)
    else:
        required.add("Company.Hardware.ControlCard.None.dll")
        if modules or config != SIMULATION_CONFIG:
            raise ValueError("FlyScan simulation must contain its example configuration and no dynamic PLC HMI modules.")
        profile = json.loads(read_file("Config/FlyScan.13Axes.json"))
        if profile["allowNativeHardware"] is not False or profile["enabled"] is not False:
            raise ValueError("The packaged example must keep native hardware and physical motion disabled by default.")
        foreign = ("Company.PlcHmi.", "Company.Hardware.PLC", "Company.Hardware.ControlCard.INOVANCE", "IMC_API")
    if required - files:
        raise ValueError(f"Missing {app} files: {', '.join(sorted(required - files))}")
    foreign_prefixes = tuple(prefix.casefold() for prefix in foreign)
    if any(Path(name).name.casefold().startswith(foreign_prefixes) for name in files):
        raise ValueError(f"The {app} payload contains another product's components.")


def package(app, name, publish, output):
    files = {}
    for path in sorted(publish.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(publish)
        if relative.parts[:2] == ("Config", "PLC") and app == "PlcHmi":
            # The target device's reviewed workbook is restored separately from the application binaries.
            continue
        if relative.suffix.lower() == ".pdb":
            continue
        files[relative.as_posix()] = path
    if app == "PlcHmi":
        files["Config/Database.example.json"] = ROOT / "Framework/Company.Database.SqlSugar/Database.example.json"
    check_inventory(app, set(files), lambda relative: files[relative].read_bytes())
    manifest = {"application": app, "artifact": name, "configuration": "Release",
                "requires": RUNTIME, "sha256": {}}
    archive = output / f"{name}.zip"
    with ZipFile(archive, "x", ZIP_DEFLATED) as bundle:
        for relative, path in files.items():
            data = path.read_bytes()
            manifest["sha256"][relative] = hashlib.sha256(data).hexdigest()
            bundle.writestr(relative, data)
        bundle.writestr(MANIFEST, json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    with ZipFile(archive) as bundle:
        if bundle.testzip() is not None:
            raise ValueError(f"Corrupt archive: {archive.name}")
    print(f"{archive.name}: {len(files)} files", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New directory for the two ZIP packages.")
    parser.add_argument("--restore-source", type=Path, help="Optional local NuGet source for an offline build.")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        parser.error("--output must refer to a new directory.")
    output.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix="release-", dir=output.parent) as temporary:
        staging = Path(temporary) / "packages"
        staging.mkdir()
        for app, name in CANDIDATES.items():
            publish = Path(temporary) / app
            command = ["dotnet", "publish", "Company.Shell/Company.Shell.csproj", "-c", "Release",
                       f"-p:CompanyProduct={app}", "--self-contained", "false", "-m:1",
                       "-p:SkipPlcHmiConfigurationSeed=true", "-o", str(publish)]
            if args.restore_source:
                command.extend((f"-p:RestoreSources={args.restore_source.resolve()}", "-p:NuGetAudit=false"))
            subprocess.run(command, cwd=ROOT, check=True)
            package(app, name, publish, staging)
        staging.rename(output)


if __name__ == "__main__":
    main()
