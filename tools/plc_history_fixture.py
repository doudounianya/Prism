"""Isolated databases, workbooks and installs for packaged PLC HMI checks."""

from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
from time import monotonic, sleep
from zipfile import ZipFile


PROBE = "PlcHmi.History.Smoke"
SCHEMA = "prism_fixture"
RESTORED = "prism_fixture_restored"
OPCUA_NAMESPACE = "urn:prism:plc:opcua-test"
STOP = "UnitMode[0].Action[3]"


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


def set_loopback_points(book, parameter_points=()):
    points = {f"UnitMode[0].{label}": (node, None, "Int16", "R", "只读")
              for label, node in (("Mode", "i_Mode"), ("Status", "i_Status"))}
    action_node = "Fixture.System.st_UnitMode[0].b_Action"
    for bank, count in (("Action", 9), ("Signal", 6)):
        node = action_node if bank == "Action" else "b_Signal"
        points.update({f"UnitMode[0].{bank}[{n}]": (node, n, "Bool", "R", "只读") for n in range(count)})
    for bank in ("DI", "DO"):
        points.update({f"IO.{bank}[0].{n:02d}": (bank, n, "Bool", "R", "只读") for n in range(16)})
    points[STOP] = (action_node, 3, "Bool", "RW", "握手请求")

    selected = set()
    selected_sheets = {"设备状态_OpcUa", "IO_OpcUa"}
    if parameter_points:
        selected_sheets.update(("轴_OpcUa", "气缸_OpcUa"))
        points["UnitMode[0].Request.Debug"] = (action_node, 6, "Bool", "W", "保持请求")
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


def make_workbook(template, target, opcua_port=None, parameter_points=()):
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
            set_loopback_points(book, parameter_points)
        book.save(target)
    finally:
        book.close()


def prepare_install(archive, destination, work, binaries, verify_package):
    snapshot = work / f"{destination.name}.zip"
    shutil.copyfile(archive, snapshot)
    verified = verify_package(snapshot, "PlcHmi")
    with ZipFile(snapshot) as bundle:
        bundle.extractall(destination)
    for extension in ("dll", "deps.json", "runtimeconfig.json"):
        name = f"{PROBE}.{extension}"
        shutil.copyfile(binaries / name, destination / name)
    return verified["sha256"]


def run_rollback(source, work, evidence, env, mysql_bin, baseline, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, baseline=baseline)


def run_opcua(source, work, evidence, env, mysql_bin, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package)


def run_parameters(source, work, evidence, env, mysql_bin, candidate, verify_package):
    return run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, probe="parameters")


def run_desktop(source, work, evidence, env, mysql_bin, candidate, verify_package, *, baseline=None, probe="opcua"):
    work.mkdir()
    evidence.mkdir()
    binaries = source / "tests" / PROBE / "bin/Release/net8.0-windows"
    workbook_rel = Path("Config/PLC/CRT_HMI_PLC变量绑定配置.xlsx")
    template = source / "Applications/PlcHmi/Company.PlcHmi.Shell" / workbook_rel
    config_backup = work / workbook_rel.name
    opcua_port = loopback_port() if baseline is None else None
    parameter_points = ()
    if probe == "parameters":
        parameter_points = json.loads((source / "tests" / PROBE / "ParameterPoints.json").read_text(encoding="utf-8"))
    make_workbook(template, config_backup, opcua_port, parameter_points)
    probe_env = {name: value for name, value in env.items() if not name.upper().startswith(("PRISM_DATABASE_", "MYSQL_"))}
    probe_env["MYSQL_TEST_LOGIN_FILE"] = str(work / "test-login.cnf")
    if opcua_port:
        probe_env["CRT_HMI_PLC_WRITES"] = "1"
    account_secret, database_secret = secrets.token_hex(16), secrets.token_hex(16)
    installations = {}
    phases = ((probe, candidate),) if baseline is None else (("baseline", baseline), ("candidate", candidate), ("rollback", baseline))
    for name, archive in phases:
        destination = work / name
        installations[name] = destination, prepare_install(archive, destination, work, binaries, verify_package)
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

        def run_phase(name, schema, mode):
            install, digest = installations[name]
            workbook = install / workbook_rel
            workbook.parent.mkdir(parents=True)
            shutil.copyfile(config_backup, workbook)
            (install / "Config/Database.local.json").write_text(json.dumps({
                "Provider": "MySql", "ConnectionString": f"Server=127.0.0.1;Port={port};Database={schema};User Id=prism_fixture;Password={database_secret};",
                "CreateAccountTableIfMissing": True,
            }), encoding="utf-8")
            (install / "history-fixture.json").write_text(json.dumps({
                "AccountSecret": account_secret, "WorkbookSha256": hashlib.sha256(workbook.read_bytes()).hexdigest().upper(),
                "OpcUaPort": opcua_port,
            }), encoding="utf-8")
            log = evidence / f"{name}.log"
            with log.open("wb") as stream:
                subprocess.run(["dotnet", str(install / f"{PROBE}.dll"), mode], cwd=install, env=probe_env,
                               stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=240 if mode == "parameters" else 100)
            lines = log.read_text(encoding="utf-8-sig").splitlines()
            if "PASS packaged window closed normally" not in lines:
                raise RuntimeError(f"The desktop probe did not confirm orderly exit: {log.name}")
            observations.append({"phase": name, "archiveSha256": digest, "assertions": sum(line.startswith("PASS ") for line in lines),
                                 "log": log.name})

        result = {"status": "passed", "phases": observations}
        if baseline is None:
            run_phase(probe, SCHEMA, probe)
        else:
            run_phase("baseline", SCHEMA, "seed")
            with (evidence / "backup.log").open("wb") as log:
                snapshot = subprocess.check_output(mysql("mysqldump", "--single-transaction", "--skip-dump-date", "--no-tablespaces",
                                                         "--set-gtid-purged=OFF", SCHEMA), cwd=work, env=probe_env, stderr=log, timeout=60)
            run_phase("candidate", SCHEMA, "read")
            create_schema(RESTORED)
            execute("restore", snapshot, RESTORED)
            run_phase("rollback", RESTORED, "read")
            result["backupSha256"] = hashlib.sha256(snapshot).hexdigest()
    (evidence / "report.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result
