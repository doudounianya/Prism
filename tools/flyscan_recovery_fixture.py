"""Exercise FlyScan simulation backup and recovery in verified release installs."""

from contextlib import closing, contextmanager
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
from tempfile import TemporaryDirectory
from uuid import UUID
from zipfile import ZIP_DEFLATED, ZipFile

from release_candidate_install import prepare_install


PROBE = "SharedControlPages.Smoke"
JOURNAL = "run.json"
HISTORY = "history.sqlite"
RECIPE = "operator-recipe.json"
BACKUP_FILES = {JOURNAL, HISTORY, RECIPE}


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check_sqlite(connection):
    if connection.execute("PRAGMA quick_check").fetchall() != [("ok",)]:
        raise ValueError("The SQLite backup failed its integrity check.")


@contextmanager
def checkpoint_backup_lock(source):
    try:
        lock = (source / (JOURNAL + ".lock")).open("a+b")
    except PermissionError as error:
        raise RuntimeError("The FlyScan checkpoint is held or inaccessible. Close the source application before backup.") from error
    with lock:
        # The open handle conflicts with the application's FileShare.None open on Windows.
        if os.name != "nt":
            import fcntl
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as error:
                raise RuntimeError("Close the source FlyScan application before taking its backup.") from error
        yield


def create_backup(source, archive):
    if archive.exists() or archive.is_symlink():
        raise FileExistsError(archive)
    with TemporaryDirectory(prefix=".flyscan-backup-", dir=archive.parent) as temporary:
        stage = Path(temporary)
        with checkpoint_backup_lock(source):
            for name in (JOURNAL, RECIPE):
                shutil.copyfile(source / name, stage / name)
            with closing(sqlite3.connect((source / HISTORY).as_uri() + "?mode=ro", uri=True)) as original:
                with closing(sqlite3.connect(stage / HISTORY)) as backup:
                    original.backup(backup)
                    backup.execute("PRAGMA journal_mode=DELETE")
                    check_sqlite(backup)
        manifest = {name: file_hash(stage / name) for name in sorted(BACKUP_FILES)}
        staged_archive = stage / "backup.zip"
        with ZipFile(staged_archive, "x", ZIP_DEFLATED) as bundle:
            for name in manifest:
                bundle.write(stage / name, name)
            bundle.writestr("sha256.json", json.dumps(manifest, sort_keys=True))
        if os.name == "nt":
            staged_archive.rename(archive)
        else:
            os.link(staged_archive, archive)
    return manifest


def restore_backup(archive, restored, manifest, *, validate=None):
    if restored.exists() or restored.is_symlink():
        raise FileExistsError(restored)
    with ZipFile(archive) as bundle, TemporaryDirectory(prefix=".flyscan-restore-", dir=restored.parent) as temporary:
        names = bundle.namelist()
        if (manifest.keys() != BACKUP_FILES or len(names) != len(BACKUP_FILES) + 1 or
                set(names) != BACKUP_FILES | {"sha256.json"}):
            raise ValueError("The FlyScan backup inventory check failed.")
        recorded = json.loads(bundle.read("sha256.json"))
        if recorded != manifest:
            raise ValueError("The FlyScan backup manifest changed.")
        stage = Path(temporary) / "data"
        stage.mkdir()
        for name in sorted(BACKUP_FILES):
            with bundle.open(name) as source, (stage / name).open("xb") as target:
                shutil.copyfileobj(source, target)
            if file_hash(stage / name) != recorded[name]:
                raise ValueError(f"FlyScan restore hash mismatch: {name}")
        # This archive contains a standalone database. A validation read must not create WAL sidecars.
        with closing(sqlite3.connect((stage / HISTORY).as_uri() + "?mode=ro&immutable=1", uri=True)) as database:
            check_sqlite(database)
        if validate is not None:
            validate(stage)
        stage.rename(restored)


def read_history(directory, *, standalone=False):
    mode = "?mode=ro&immutable=1" if standalone else "?mode=ro"
    with closing(sqlite3.connect((directory / HISTORY).as_uri() + mode, uri=True)) as db:
        check_sqlite(db)
        pieces = db.execute("SELECT id,stamp FROM pieces ORDER BY id").fetchall()
        completions = db.execute("SELECT component,stamp FROM operations WHERE action=? ORDER BY component", ("工件完成",)).fetchall()
        if pieces != completions:
            raise ValueError("Workpieces and completion operations must match once per workpiece.")
        return pieces


def read_checkpoint(directory):
    saved = json.loads((directory / JOURNAL).read_text(encoding="utf-8"))
    return UUID(saved["workpiece"]["id"]).hex, saved["counts"]


def run_recovery(source, work, evidence, env, candidate, verify_package, *, baseline=None):
    work.mkdir()
    evidence.mkdir()
    binaries = source / "tests" / PROBE / "bin/Release/net8.0-windows"
    initial, recovered = (work / "initial-install", work / "restored-install")
    packages = {initial: baseline or candidate, recovered: candidate}
    rollback = work / "rollback-install"
    if baseline is not None:
        packages[rollback] = baseline
    installs = {path: prepare_install(package, path, work, binaries, verify_package, application="FlyScan", probe=PROBE)
                for path, package in packages.items()}
    if baseline is not None:
        if installs[initial] != installs[rollback] or installs[initial] == installs[recovered]:
            raise ValueError("FlyScan upgrade requires distinct baseline and candidate builds and the original baseline for rollback.")
    elif installs[initial] != installs[recovered]:
        raise ValueError("The FlyScan ZIP changed between the initial and restored installations.")

    data, restored = work / "initial-data", work / "restored-data"
    phases = []

    def run(phase, install, storage, *, label=None, command="--package-recovery"):
        log = evidence / f"{label or phase}.log"
        with log.open("wb") as stream:
            subprocess.run(["dotnet", str(install / f"{PROBE}.dll"), command, phase, str(storage)],
                           cwd=install, env=env, stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=90)
        lines = log.read_text(encoding="utf-8-sig").splitlines()
        phases.append({"phase": label or phase, "archiveSha256": installs[install], "log": log.name,
                       "assertions": sum(line.startswith("PASS ") for line in lines)})
        return lines

    run("checkpoint", initial, data)
    interrupted_id, partial = read_checkpoint(data)
    prior = read_history(data)
    if len(prior) != 1 or prior[0][0] == interrupted_id or partial != {"pieces": 1, "groups": 3, "triggers": 9}:
        raise ValueError("The source checkpoint must contain one historic piece and one partially confirmed piece.")

    # Restore from the published evidence archive so the same bytes survive the temporary installs.
    archive = evidence / "flyscan-backup-fixture.zip"
    restored_hashes = create_backup(data, archive)
    backup_hash = file_hash(archive)
    counts = {"pieces": 2, "groups": 4, "triggers": 11}

    def restore(storage):
        def validate(stage):
            if read_checkpoint(stage) != (interrupted_id, partial) or read_history(stage, standalone=True) != prior:
                raise ValueError("The restored checkpoint or SQLite history differs from the source.")
            checkpoint = json.loads((stage / JOURNAL).read_text(encoding="utf-8"))
            recipe = json.loads((stage / RECIPE).read_text(encoding="utf-8"))
            if checkpoint["workpiece"]["recipe"] != recipe:
                raise ValueError("The exported FlyScan recipe does not match the pending workpiece.")

        restore_backup(archive, storage, restored_hashes, validate=validate)

    def finish_recovery(install, storage, prefix=""):
        run("resume", install, storage, label=f"{prefix}resume")
        complete = read_history(storage)
        if (read_checkpoint(storage) != (interrupted_id, counts) or len(complete) != 2 or prior[0] not in complete or
                {piece for piece, _ in complete} != {prior[0][0], interrupted_id}):
            raise ValueError("Recovery must settle the interrupted workpiece once and preserve the existing history.")
        completed_hash = file_hash(storage / JOURNAL)
        run("reopen", install, storage, label=f"{prefix}reopen")
        if read_history(storage) != complete or file_hash(storage / JOURNAL) != completed_hash:
            raise ValueError("Reopening the recovered install changed completed work or its history.")
        if file_hash(storage / RECIPE) != restored_hashes[RECIPE]:
            raise ValueError("Recovery modified the restored operator recipe.")

    restore(restored)
    finish_recovery(recovered, restored)
    rollback_preview = {}
    reopened_history = None
    if baseline is not None:
        rollback_data = work / "rollback-data"
        restore(rollback_data)
        preview = run("rollback-preview", rollback, rollback_data)
        matches, = [json.loads(line.removeprefix("ROLLBACK_PREVIEW "))["matches"]
                    for line in preview if line.startswith("ROLLBACK_PREVIEW ")]
        if not isinstance(matches, bool) or read_checkpoint(rollback_data) != (interrupted_id, partial) or read_history(rollback_data) != prior:
            raise ValueError("Opening the rollback installation mutated pending work or completion history.")
        rollback_preview = {"originalStartupRecipeMatchesCheckpoint": matches}
        if not matches:
            # The former release loads its startup recipe from Config. Restore the user's saved copy in this isolated install.
            startup_recipe = rollback / "Config" / "FlyScan.DemoRecipe.json"
            shutil.copyfile(rollback_data / RECIPE, startup_recipe)
            rollback_preview["restoredStartupRecipeSha256"] = file_hash(startup_recipe)
        finish_recovery(rollback, rollback_data, prefix="rollback-")
    else:
        history_data = work / "history-reopen"
        run("seed", recovered, history_data, label="history-seed", command="--package-history")
        expected_history = read_history(history_data)
        run("reopen", recovered, history_data, label="history-reopen", command="--package-history")
        if len(expected_history) != 5 or read_history(history_data) != expected_history:
            raise ValueError("Reopening the FlyScan history changed the five unique completion records.")
        for filename in ("history-fixture.json", "alarm-ranking.png", "alarm-history.png", "production-trend.png", "production-daily.png"):
            shutil.copyfile(history_data / filename, evidence / filename)
        reopened_history = {"workpieces": len(expected_history), "fixture": "history-fixture.json"}
    if (file_hash(data / JOURNAL) != restored_hashes[JOURNAL] or file_hash(data / RECIPE) != restored_hashes[RECIPE] or
            read_history(data) != prior):
        raise ValueError("Recovery modified the original test installation's data.")
    if file_hash(archive) != backup_hash:
        raise ValueError("The retained FlyScan backup changed during recovery.")

    result = {"status": "passed", "phases": phases, "backupFile": archive.name, "backupSha256": backup_hash,
              "restoredFileHashes": restored_hashes,
              "interruptedWorkpiece": interrupted_id, "priorWorkpiece": prior[0][0],
              "countsBeforeRecovery": partial, "countsAfterReopen": counts}
    if rollback_preview:
        result["rollbackPreview"] = rollback_preview
    if reopened_history:
        result["reopenedHistory"] = reopened_history
    (evidence / "report.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result
