"""Launch probes using verified, disposable copies of release packages."""

import shutil
from zipfile import ZipFile


def prepare_install(archive, destination, work, binaries, verify_package, *, application, probe):
    snapshot = work / f"{destination.name}.zip"
    shutil.copyfile(archive, snapshot)
    verified = verify_package(snapshot, application)
    with ZipFile(snapshot) as bundle:
        bundle.extractall(destination)
    for extension in ("dll", "deps.json", "runtimeconfig.json"):
        name = f"{probe}.{extension}"
        shutil.copyfile(binaries / name, destination / name)
    return verified["sha256"]
