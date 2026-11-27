"""Isolated databases, workbooks and installs for packaged PLC HMI checks."""

from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
from time import monotonic, sleep, time_ns
from zipfile import ZIP_DEFLATED, ZipFile

from release_candidate_install import prepare_install


PROBE = "PlcHmi.History.Smoke"
SCHEMA = "prism_fixture"
RESTORED = "prism_fixture_restored"
OPCUA_NAMESPACE = "urn:prism:plc:opcua-test"
STOP = "UnitMode[0].Action[3]"
WORKBOOK = "Config/PLC/CRT_HMI_PLC变量绑定配置.xlsx"
SOFTWARE_SETTINGS = "Config/Company.Core.Config.ConfigKey.SystemConfig.json"
HISTORY_DUMP = "history.sql"
DESKTOP_BACKUP_FILES = {WORKBOOK, SOFTWARE_SETTINGS, HISTORY_DUMP}


def create_desktop_backup(archive, installation, snapshot):
    contents = {name: (installation / name).read_bytes() for name in (WORKBOOK, SOFTWARE_SETTINGS)}
    contents[HISTORY_DUMP] = snapshot
    manifest = {name: hashlib.sha256(data).hexdigest() for name, data in contents.items()}
    with ZipFile(archive, "x", ZIP_DEFLATED) as bundle:
        for name, data in contents.items():
            bundle.writestr(name, data)
        bundle.writestr("sha256.json", json.dumps(manifest, ensure_ascii=False, sort_keys=True))
    return manifest


def read_desktop_backup(archive, manifest):
    with ZipFile(archive) as bundle:
        members = bundle.namelist()
        if (manifest.keys() != DESKTOP_BACKUP_FILES or len(members) != len(DESKTOP_BACKUP_FILES) + 1 or
                set(members) != DESKTOP_BACKUP_FILES | {"sha256.json"}):
            raise ValueError("The PLC desktop backup inventory changed.")
        if json.loads(bundle.read("sha256.json")) != manifest:
            raise ValueError("The PLC desktop backup manifest changed.")
        backup = {name: bundle.read(name) for name in manifest}
    for name, data in backup.items():
        if hashlib.sha256(data).hexdigest() != manifest[name]:
            raise ValueError(f"PLC desktop backup hash mismatch: {name}")
    return backup


def loopback_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as available:
        available.bind(("127.0.0.1", 0))
        return available.getsockname()[1]


@contextmanager
def mysql_fixture(bin_directory, work, evidence, env):
    server = (bin_directory / "mysqld.exe").resolve(strict=True)
    for client in ("mysql.exe", "mysqladmin.exe", "mysqldump.exe"):
        if not (bin_directory / client).is_file():
            raise ValueError(f"The MySQL bin directory must contain {client}.")
    data = work / "mysql-data"
    log = evidence / "mysql-server.log"
    common = [str(server), "--no-defaults", f"--datadir={data}", "--console"]
    hidden = subprocess.CREATE_NO_WINDOW
    with (evidence / "mysql-initialize.log").open("wb") as output:
        subprocess.run([*common, "--initialize-insecure"], cwd=work, env=env, stdout=output,
                       stderr=subprocess.STDOUT, check=True, timeout=60, creationflags=hidden)

    port = loopback_port()
    with log.open("wb") as output:
        process = subprocess.Popen([*common, "--bind-address=127.0.0.1", f"--port={port}", "--mysqlx=OFF"],
                                   cwd=work, env=env, stdout=output, stderr=subprocess.STDOUT, creationflags=hidden)
    ready = False

    def client(name, *arguments):
        return [str(bin_directory / f"{name}.exe"), "--no-defaults", "--protocol=TCP", "--host=127.0.0.1", f"--port={port}",
                "--user=root", "--skip-password", *arguments]

    try:
        deadline = monotonic() + 45
        while process.poll() is None and monotonic() < deadline:
            log_text = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
            if any("ready for connections" in line and f"port: {port} " in line for line in log_text.splitlines()):
                ready = True
                break
            sleep(0.15)
        if not ready or process.poll() is not None:
            raise RuntimeError("The isolated MySQL server did not start. See mysql-server.log.")
        yield port, client
    finally:
        if ready and process.poll() is None:
            try:
                subprocess.run(client("mysqladmin", "shutdown"), cwd=work, env=env, stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL, timeout=10, creationflags=hidden)
                process.wait(timeout=10)
            except (OSError, subprocess.TimeoutExpired):
                pass
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)


def worksheet_rows(sheet, column):
    header = next(row for row in sheet.iter_rows(min_row=1, max_row=8) if {"启用", column} <= {cell.value for cell in row})
    columns = {cell.value: cell.column for cell in header}
    return columns, range(header[0].row + 1, sheet.max_row + 1)


def set_cells(sheet, row, columns, values):
    for name, value in values.items():
        sheet.cell(row, columns[name]).value = value


def set_loopback_points(book, parameter_points=(), *, mode_operations=False, extra_io_group=False, shared_io=None):
    points = {f"UnitMode[0].{label}": (node, None, "Int16", "R", "只读")
              for label, node in (("Mode", "i_Mode"), ("Status", "i_Status"))}
    action_node = "Fixture.System.st_UnitMode[0].b_Action"
    for bank, count in (("Action", 9), ("Signal", 6)):
        node = action_node if bank == "Action" else "b_Signal"
        points.update({f"UnitMode[0].{bank}[{n}]": (node, n, "Bool", "R", "只读") for n in range(count)})
    for bank in ("DI", "DO"):
        count = len(shared_io[bank.title() + "Before"]) if shared_io is not None else 16
        points.update({f"IO.{bank}[0].{n:02d}": (bank, n, "Bool", "R", "只读") for n in range(count)})
    if extra_io_group:
        points.update({f"IO.DI[1].{n:02d}": ("DI1", n, "Bool", "R", "只读") for n in range(16)})
    points[STOP] = (action_node, 3, "Bool", "RW", "握手请求")

    selected = set()
    selected_sheets = {"设备状态_OpcUa", "IO_OpcUa"}
    if parameter_points or mode_operations:
        points["UnitMode[0].Request.Debug"] = (action_node, 6, "Bool", "W", "保持请求")
    if mode_operations:
        points["UnitMode[0].Request.Initialize"] = (action_node, 7, "Bool", "W", "握手请求")
        points["UnitMode[0].Request.Maintenance"] = (action_node, 8, "Bool", "W", "保持请求")
    if parameter_points:
        selected_sheets.update(("轴_OpcUa", "气缸_OpcUa"))
        for point in parameter_points:
            key, data_type = point["Key"], point["Type"]
            write_mode = point.get("WriteMode")
            access = "W" if point.get("WriteOnly") else "RW" if write_mode else "R"
            points[key] = ("Fixture." + key, None, data_type, access, write_mode or "只读")
            if command := point.get("Command"):
                points[command] = ("Fixture." + key, None, data_type, "W", "握手请求")

    for sheet in book.worksheets:
        if not sheet.title.endswith(("_OpcUa", "_Modbus")):
            continue
        columns, rows = worksheet_rows(sheet, "HMI变量标识")
        for row in rows:
            key = sheet.cell(row, columns["HMI变量标识"]).value
            if not key:
                continue
            device = sheet.cell(row, columns["PLC编号"]).value
            active = sheet.title in selected_sheets and device == "PLC01" and key in points
            sheet.cell(row, columns["启用"]).value = "是" if active else "否"
            if not active:
                continue
            if key in selected:
                raise ValueError(f"Duplicate OPC UA fixture point: {key}")
            selected.add(key)
            node, index, data_type, access, write_mode = points[key]
            set_cells(sheet, row, columns, {"命名空间URI": OPCUA_NAMESPACE, "节点标识": "s=" + node,
                                           "OPC UA数组索引": index, "数据类型": data_type, "比例": 1, "偏移": 0,
                                           "布尔取反": "否", "访问权限": access,
                                           "采集组": "PAGE.Active" if "R" in access else None, "写入方式": write_mode})
    if selected != points.keys():
        raise ValueError(f"Missing OPC UA fixture points: {', '.join(sorted(points.keys() - selected))}")


def extend_loopback_table(sheet, last_row):
    from openpyxl.utils.cell import get_column_letter, range_boundaries

    table, = sheet.tables.values()
    first_column, first_row, last_column, current_last = range_boundaries(table.ref)
    if last_row > current_last:
        table.ref = f"{get_column_letter(first_column)}{first_row}:{get_column_letter(last_column)}{last_row}"
        if table.autoFilter is not None:
            table.autoFilter.ref = table.ref


def add_second_loopback_device(book, port, primary_row):
    sheet = book["设备连接"]
    columns, rows = worksheet_rows(sheet, "PLC编号")
    row = next((row for row in rows if sheet.cell(row, columns["PLC编号"]).value == "PLC02"), sheet.max_row + 1)
    fields = {name: sheet.cell(primary_row, column).value for name, column in columns.items() if name}
    set_cells(sheet, row, columns, fields | {"PLC编号": "PLC02", "设备名称": "离线 IO PLC02",
                                             "OPC UA端点": f"opc.tcp://127.0.0.1:{port}/CRT"})
    extend_loopback_table(sheet, row)

    sheet = book["IO_OpcUa"]
    columns, rows = worksheet_rows(sheet, "HMI变量标识")
    second = {sheet.cell(row, columns["HMI变量标识"]).value: row for row in rows
              if sheet.cell(row, columns["PLC编号"]).value == "PLC02"}
    for row in rows:
        if (sheet.cell(row, columns["PLC编号"]).value != "PLC01" or
                sheet.cell(row, columns["启用"]).value != "是" or
                str(sheet.cell(row, columns["分组号"]).value) != "0"):
            continue
        fields = {name: sheet.cell(row, column).value for name, column in columns.items() if name}
        target = second.get(fields["HMI变量标识"], sheet.max_row + 1)
        set_cells(sheet, target, columns, fields | {"PLC编号": "PLC02"})
        extend_loopback_table(sheet, target)


def make_workbook(template, target, opcua_port=None, parameter_points=(), *,
                  mode_operations=False, extra_io_group=False, shared_io=None, second_opcua_port=None):
    try:
        from openpyxl import load_workbook
    except ImportError as error:
        raise RuntimeError("Install openpyxl in the Python environment to run the PLC desktop fixture.") from error

    book = load_workbook(template)
    try:
        sheet = book["设备连接"]
        columns, rows = worksheet_rows(sheet, "PLC编号")
        devices = [row for row in rows if sheet.cell(row, columns["PLC编号"]).value]
        if not devices:
            raise ValueError("The fixture workbook has no device rows.")
        enabled = [row for row in devices if opcua_port and sheet.cell(row, columns["PLC编号"]).value == "PLC01"]
        if opcua_port and len(enabled) != 1:
            raise ValueError("The OPC UA fixture requires one PLC01 device row in the workbook.")
        for row in devices:
            set_cells(sheet, row, columns, {"启用": "否", "OPC UA端点": "opc.tcp://127.0.0.1:1/offline",
                                           "凭据引用": None, "Modbus IP": "127.0.0.1", "Modbus端口": 1})
        if enabled:
            set_cells(sheet, enabled[0], columns, {"启用": "是", "协议": "OpcUa", "变量Sheet后缀": "_OpcUa",
                                                  "OPC UA端点": f"opc.tcp://127.0.0.1:{opcua_port}/CRT",
                                                  "安全模式": "None", "安全策略": "None"})
            set_loopback_points(book, parameter_points, mode_operations=mode_operations,
                                extra_io_group=extra_io_group, shared_io=shared_io)
            if second_opcua_port is not None:
                add_second_loopback_device(book, second_opcua_port, enabled[0])
        book.save(target)
    finally:
        book.close()


def run_rollback(source, work, evidence, env, mysql_bin, baseline, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, baseline=baseline)


def run_desktop_rollback(source, work, evidence, env, mysql_bin, baseline, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package,
                       baseline=baseline, probe="desktop-rollback")


def run_opcua(source, work, evidence, env, mysql_bin, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package)


def run_parameters(source, work, evidence, env, mysql_bin, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, probe="parameters")


def run_mode_operations(source, work, evidence, env, mysql_bin, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, probe="mode-operations")


def run_lifecycle(source, work, evidence, env, mysql_bin, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, probe="lifecycle")


def run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, *, baseline=None, probe="opcua"):
    work.mkdir()
    evidence.mkdir()
    binaries = source / "tests" / PROBE / "bin/Release/net8.0-windows"
    workbook_rel = Path(WORKBOOK)
    template = source / "Applications/PlcHmi/Company.PlcHmi.Shell" / workbook_rel
    config_backup = work / workbook_rel.name
    desktop_rollback = probe == "desktop-rollback"
    opcua_port = loopback_port() if baseline is None or desktop_rollback else None
    parameter_points = ()
    if probe in ("parameters", "mode-operations", "lifecycle") or desktop_rollback:
        parameter_points = json.loads((source / "tests" / PROBE / "ParameterPoints.json").read_text(encoding="utf-8"))
    shared_io = None
    second_opcua_port = None
    if probe == "opcua" and baseline is None:
        shared_io = json.loads((source / "tests/Fixtures/SharedIoFixture.json").read_text(encoding="utf-8"))
        second_opcua_port = loopback_port()
        while second_opcua_port == opcua_port:
            second_opcua_port = loopback_port()
    make_workbook(template, config_backup, opcua_port, parameter_points,
                  mode_operations=probe in ("mode-operations", "lifecycle") or desktop_rollback,
                  extra_io_group=desktop_rollback or shared_io is not None, shared_io=shared_io,
                  second_opcua_port=second_opcua_port)
    workbook_sha = hashlib.sha256(config_backup.read_bytes()).hexdigest()
    probe_env = {name: value for name, value in env.items() if not name.upper().startswith(("PRISM_DATABASE_", "MYSQL_"))}
    probe_env["MYSQL_TEST_LOGIN_FILE"] = str(work / "test-login.cnf")
    if opcua_port:
        probe_env["CRT_HMI_PLC_WRITES"] = "1"
    account_secret, database_secret = secrets.token_hex(16), secrets.token_hex(16)
    installations = {}
    phases = ((probe, candidate),) if baseline is None else (("baseline", baseline), ("candidate", candidate), ("rollback", baseline))
    for name, archive in phases:
        destination = work / name
        installations[name] = destination, prepare_install(archive, destination, work, binaries, verify_package,
                                                            application="PlcHmi", probe=PROBE)
    if baseline is not None and installations["baseline"][1] != installations["rollback"][1]:
        raise ValueError("The baseline ZIP changed before the rollback copy was made.")
    observations = []
    with mysql_fixture(mysql_bin, work, evidence, probe_env) as (port, mysql):
        def execute(name, sql, *args):
            with (evidence / f"{name}.log").open("wb") as log:
                subprocess.run(mysql("mysql", "--default-character-set=utf8mb4", *args), input=sql, stdout=log,
                               stderr=subprocess.STDOUT, cwd=work, env=probe_env, check=True, timeout=60)

        def create_schema(name):
            execute(name, (f"CREATE DATABASE `{name}` DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;\n"
                           f"GRANT ALL ON `{name}`.* TO 'prism_fixture'@'localhost';\n").encode())

        execute("create-fixture-user", f"CREATE USER 'prism_fixture'@'localhost' IDENTIFIED BY '{database_secret}';".encode())
        create_schema(SCHEMA)
        migration = (source / "Applications/PlcHmi/Company.PlcHmi.Persistence/Schema/001_PlcHistory.sql").read_text(encoding="utf-8-sig")
        if migration.count("USE CRT_Data;") != 1:
            raise ValueError("The production migration must explicitly select its schema once.")
        execute("schema", migration.replace("USE CRT_Data;", f"USE `{SCHEMA}`;").encode("utf-8"), SCHEMA)
        execute("seed", (source / "tests" / PROBE / "FixtureSeed.sql").read_bytes(), SCHEMA)

        def run_phase(name, schema, mode, *, reuse_install=None, backup=None):
            install, digest = installations[reuse_install or name]
            if reuse_install is None:
                workbook = install / workbook_rel
                workbook.parent.mkdir(parents=True)
                if backup is None:
                    shutil.copyfile(config_backup, workbook)
                else:
                    workbook.write_bytes(backup[WORKBOOK])
                    (install / SOFTWARE_SETTINGS).write_bytes(backup[SOFTWARE_SETTINGS])
                (install / "Config/Database.local.json").write_text(json.dumps({
                    "Provider": "MySql", "ConnectionString": f"Server=127.0.0.1;Port={port};Database={schema};User Id=prism_fixture;Password={database_secret};",
                    "CreateAccountTableIfMissing": True,
                }), encoding="utf-8")
                (install / "history-fixture.json").write_text(json.dumps({
                    "AccountSecret": account_secret, "WorkbookSha256": workbook_sha.upper(),
                    "OpcUaPort": opcua_port, "SecondOpcUaPort": second_opcua_port,
                }), encoding="utf-8")
            log = evidence / f"{name}.log"
            with log.open("wb") as stream:
                subprocess.run(["dotnet", str(install / f"{PROBE}.dll"), mode], cwd=install, env=probe_env,
                               stdout=stream, stderr=subprocess.STDOUT, check=True,
                               timeout=240 if desktop_rollback or mode in ("parameters", "mode-operations", "lifecycle") else 100)
                exited_at = time_ns() // 1_000_000
            lines = log.read_text(encoding="utf-8-sig").splitlines()
            if "PASS packaged window closed normally" not in lines:
                raise RuntimeError(f"The desktop probe did not confirm orderly exit: {log.name}")
            phase = {"phase": name, "archiveSha256": digest, "assertions": sum(line.startswith("PASS ") for line in lines),
                     "log": log.name}
            if probe == "lifecycle" or desktop_rollback:
                phase["exitTimingMs"] = verify_exit_timing(lines, exited_at)
            observations.append(phase)

        def maintenance_record(installation):
            return installations[installation][0] / "Config/maintenance-locks.json"

        def restore_maintenance_record(source, destination, *, releasing):
            content = maintenance_record(source).read_bytes()
            lock = json.loads(content)
            people = lock.get("People", [])
            if lock.get("PlcHeld") is not True or len(people) != 1 or people[0].get("Releasing") is not releasing:
                raise RuntimeError(f"The installed {source} lock is missing or in the wrong state.")
            target = maintenance_record(destination)
            target.write_bytes(content)
            if target.read_bytes() != content:
                raise RuntimeError(f"The maintenance record copy to {destination} differs from the latest source.")
            return hashlib.sha256(content).hexdigest()

        result = {"status": "passed", "phases": observations}
        if baseline is None:
            run_phase(probe, SCHEMA, probe)
            if probe == "lifecycle":
                run_phase("lifecycle-restart", SCHEMA, "lifecycle-restart", reuse_install=probe)
                run_phase("lifecycle-maintenance-restart", SCHEMA, "lifecycle-maintenance-restart", reuse_install=probe)
                lock_record = maintenance_record(probe)
                saved = json.loads(lock_record.read_text(encoding="utf-8"))
                if saved.get("PlcHeld") is not False or saved.get("People") != []:
                    raise RuntimeError("The maintenance restart did not clear the confirmed lock.")
                lock_record.rename(lock_record.with_suffix(".cleared.json"))
                lock_record.mkdir()  # Deterministic I/O failure at the installed record path.
                run_phase("lifecycle-unreadable-lock", SCHEMA, "lifecycle-unreadable-lock", reuse_install=probe)
        else:
            run_phase("baseline", SCHEMA, "desktop-seed" if desktop_rollback else "seed")
            with (evidence / "backup.log").open("wb") as log:
                snapshot = subprocess.check_output(mysql("mysqldump", "--single-transaction", "--skip-dump-date", "--no-tablespaces",
                                                         "--set-gtid-purged=OFF", SCHEMA), cwd=work, env=probe_env, stderr=log, timeout=60)
            backup = None
            if desktop_rollback:
                archive = work / "desktop-backup-fixture.zip"
                manifest = create_desktop_backup(archive, installations["baseline"][0], snapshot)
                backup = read_desktop_backup(archive, manifest)
                # The candidate reads a changed live database. A rollback must read the earlier backup instead.
                execute("candidate-production-change",
                        b"UPDATE plc_production_hour SET Quantity=23, SourceRevision=2, ContentHash=REPEAT('d',64) "
                        b"WHERE DeviceId='PLC01' AND ProductionDate='2000-01-03' AND ShiftCode='Night';", SCHEMA)
            run_phase("candidate", SCHEMA, "desktop-live" if desktop_rollback else "read", backup=backup)
            create_schema(RESTORED)
            if desktop_rollback:
                backup = read_desktop_backup(archive, manifest)
                original_name = json.loads(backup[SOFTWARE_SETTINGS])["SoftwareConfig"]["Name"]
                candidate_settings = installations["candidate"][0] / SOFTWARE_SETTINGS
                candidate_name = json.loads(candidate_settings.read_bytes())["SoftwareConfig"]["Name"]
                if not candidate_name or candidate_name == original_name:
                    raise RuntimeError("The candidate did not persist the updated software name.")
            execute("restore", backup[HISTORY_DUMP] if backup is not None else snapshot, RESTORED)
            run_phase("rollback", RESTORED, "desktop-restore" if desktop_rollback else "read", backup=backup)
            result["backupSha256"] = hashlib.sha256(snapshot).hexdigest()
            if desktop_rollback:
                result["workbookSha256"] = workbook_sha
                result["desktopBackup"] = {"file": archive.name, "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                                           "files": manifest, "backedUpSoftwareName": original_name,
                                           "candidateSoftwareName": candidate_name}
                if (installations["rollback"][0] / SOFTWARE_SETTINGS).read_bytes() != backup[SOFTWARE_SETTINGS]:
                    raise RuntimeError("Opening the restored system settings changed the backed-up file.")
                shutil.copyfile(archive, evidence / archive.name)
                run_phase("desktop-lock-seed", SCHEMA, "desktop-lock-seed", reuse_install="baseline")
                upgraded_lock = restore_maintenance_record("baseline", "candidate", releasing=False)
                run_phase("desktop-lock-upgrade", SCHEMA, "desktop-lock-upgrade", reuse_install="candidate")
                rollback_lock = restore_maintenance_record("candidate", "rollback", releasing=True)
                if rollback_lock == upgraded_lock:
                    raise RuntimeError("Rollback must receive the latest pending lock record.")
                run_phase("desktop-lock-rollback", RESTORED, "desktop-lock-rollback", reuse_install="rollback")
                cleared = json.loads(maintenance_record("rollback").read_bytes())
                if cleared.get("PlcHeld") is not False or cleared.get("People") != []:
                    raise RuntimeError("PLC stop feedback did not clear the maintenance lock in the restored installation.")
                result["maintenanceLockSha256"] = {"upgrade": upgraded_lock, "rollback": rollback_lock}
    (evidence / "report.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


def verify_exit_timing(lines, exited_at):
    markers = [line.removeprefix("EXIT_TIMING ") for line in lines if line.startswith("EXIT_TIMING ")]
    if len(markers) != 1:
        raise RuntimeError("The lifecycle probe did not record both exit timing markers.")
    timing = json.loads(markers[0])
    confirm_to_window = timing["ConfirmToWindowCloseMs"]
    window_to_exit = exited_at - timing["WindowClosedUnixMs"]
    if not 0 <= confirm_to_window <= 5000 or not 0 <= window_to_exit <= 2000:
        raise RuntimeError(f"PLC HMI exceeded the G0 exit limits: {confirm_to_window} ms to window close, {window_to_exit} ms to process exit.")
    return {"confirmToWindowClose": confirm_to_window, "windowCloseToProcessExit": window_to_exit}
