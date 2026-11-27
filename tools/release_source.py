"""Copy and fingerprint the inputs shared by release packaging and offline verification."""

import hashlib
import shutil


RELEASE_DIRECTORIES = ("Company.Shell", "Applications", "Framework", "Hardware", "examples")
VERIFICATION_DIRECTORIES = ("tests", "tools")
SOURCE_FILES = ("PrismFramework.sln", "Directory.Build.props", "Directory.Build.targets", "NuGet.Config", "global.json")
SOURCE_EXCLUSIONS = shutil.ignore_patterns(
    "bin", "obj", ".git", ".vs", "__pycache__", "ConfigurationProfiles", "Database.local.json",
    "maintenance-locks.json", "launchSettings.json", "*.user", "*.sqlite", "*.sqlite-wal", "*.sqlite-shm",
)


def copy_source(root, destination):
    destination.mkdir()
    for name in (*RELEASE_DIRECTORIES, *VERIFICATION_DIRECTORIES):
        shutil.copytree(root / name, destination / name, ignore=SOURCE_EXCLUSIONS)
    for name in SOURCE_FILES:
        if (root / name).is_file():
            shutil.copyfile(root / name, destination / name)


def source_fingerprint(source, *, release_only=False):
    digest = hashlib.sha256()
    release_entries = {*RELEASE_DIRECTORIES, *SOURCE_FILES}
    count = 0
    for path in sorted(source.rglob("*")):
        relative = path.relative_to(source)
        if not path.is_file() or (release_only and relative.parts[0] not in release_entries):
            continue
        digest.update(relative.as_posix().encode("utf-8") + b"\0")
        with path.open("rb") as stream:
            digest.update(hashlib.file_digest(stream, "sha256").digest())
        count += 1
    return {"files": count, "sha256": digest.hexdigest()}
