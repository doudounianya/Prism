using Company.Application.Share.PLC.Configuration;
using Company.Core.Ioc;
using ExcelDataReader;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Company.Application.Config.Services.PLC;

/// <summary>
/// 将正式 xlsx 模板中的设备、采集组、变量和本地名称解析为只读配置快照。
/// Open XML 只负责定位 Excel 数据表范围，单元格值由 ExcelDataReader 读取；不计算公式或修改工作簿。
/// 解析时保留表名、行号和列号，格式问题以诊断返回，跨字段业务约束交给后续校验器。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcBindingExcelLoader
{
    // ExcelDataReader的默认配置会访问旧代码页；.NET需显式注册。
    static PlcBindingExcelLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    // 表名与表头采用模板中的精确文本；同一业务类别分别有 OpcUa 和 Modbus 两张变量表。
    private static readonly string[] Categories = ["IO", "轴", "气缸", "报警", "产能", "工艺参数", "设备状态"];
    private static readonly string[] CommonColumns = ["启用", "HMI变量标识", "界面名称", "构件编号", "PLC编号", "数据类型", "访问权限", "采集组", "单位", "比例", "偏移", "布尔取反", "最小值", "最大值", "写入方式", "备注"];
    private static readonly string[] UaColumns = ["命名空间URI", "节点标识", "OPC UA数组索引"];
    private static readonly string[] MbColumns = ["Modbus存储区", "Modbus起始地址(0基)", "Modbus位号", "Modbus寄存器数", "Modbus字节顺序", "Modbus字顺序"];
    private static readonly string[] DeviceColumns = ["启用", "PLC编号", "设备名称", "协议", "OPC UA端点", "安全模式", "安全策略", "凭据引用", "连接超时(ms)", "重连间隔(ms)", "Modbus IP", "Modbus端口", "Modbus Unit ID", "变量Sheet后缀", "备注"];
    private static readonly string[] GroupColumns = ["采集组", "生命周期", "启用条件", "采集方式", "周期(ms)", "失效阈值(ms)", "释放规则", "说明", "有效检查点周期(ms)"];
    // 类别专属字段在解析后作为 Metadata 保留，供报警、位置及界面显示等下游使用。
    private static readonly IReadOnlyDictionary<string, string[]> ExtraColumns = new Dictionary<string, string[]>
    {
        ["IO"] = ["信号类型", "分组号", "通道号"], ["轴"] = ["参数分类", "位置编号"],
        ["气缸"] = ["动作方向", "信号角色"],
        ["报警"] = ["报警编码", "报警等级", "触发值", "报警说明", "恢复建议", "所属工位", "所属构件"],
        ["产能"] = ["计数口径", "归零处理"], ["工艺参数"] = ["配方编号", "阶段", "参数类别"],
        ["设备状态"] = ["枚举或含义"]
    };

    /// <summary>解析所得配置和格式诊断；存在错误时仍返回模型以便继续收集语义问题。</summary>
    internal sealed record ParsedWorkbook(PlcBindingConfiguration Configuration, IReadOnlyList<PlcConfigurationDiagnostic> Diagnostics);
    // 范围坐标均使用 Excel 的 1 基行列号，LastRow 已排除数据表汇总行。
    private sealed record TableRegion(int HeaderRow, int LastRow, int FirstColumn, int LastColumn);

    /// <summary>读取一个文件快照，建立表结构、强类型配置及定位诊断；可在逐表/逐行读取时取消。</summary>
    internal ParsedWorkbook Read(string filePath, CancellationToken cancellationToken)
    {
        // 先取文件快照，解析及哈希针对同一份内容；支持用户以共享方式打开 Excel。
        byte[] bytes;
        using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var copy = new MemoryStream()) { file.CopyTo(copy); bytes = copy.ToArray(); }
        cancellationToken.ThrowIfCancellationRequested();
        using var package = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        // 先解析每张受支持表对应的唯一 Excel 数据表边界，再按边界读取值，避免表外说明混入配置。
        var regions = ReadTableRegions(package);
        var diagnostics = new List<PlcConfigurationDiagnostic>();
        var tables = new Dictionary<string, List<ConfigRow>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string version = "";
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sheet = reader.Name;
            // 记录实际工作表名称以检查必需表是否缺失；有工作表不代表其数据表和表头合格。
            seen.Add(sheet);
            regions.TryGetValue(sheet, out var region);
            var rows = new List<ConfigRow>();
            Dictionary<string, int>? columns = null;
            int number = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                number++;
                // 使用说明不要求数据表结构；约定直接从第 2 行首列提取 V主版本.次版本。
                if (sheet == "使用说明" && number == 2)
                    version = Regex.Match(Cell(reader, 0), @"\bV\d+\.\d+\b", RegexOptions.CultureInvariant).Value;
                if (region is null || number < region.HeaderRow || number > region.LastRow) continue;
                // 表头映射保留 Excel 列号；空表头和重复表头都会报告准确的行列位置。
                if (number == region.HeaderRow)
                {
                    columns = new(StringComparer.Ordinal);
                    for (int c = region.FirstColumn; c <= region.LastColumn; c++)
                    {
                        string field = Cell(reader, c - 1);
                        if (field.Length == 0 || !columns.TryAdd(field, c))
                            diagnostics.Add(new(PlcDiagnosticSeverity.Error, "HEADER", sheet, number, field, c, "数据表存在空白或重复表头。"));
                    }
                    continue;
                }
                if (columns is null) continue;
                var fields = columns.ToDictionary(c => c.Key, c => Cell(reader, c.Value - 1), StringComparer.Ordinal);
                // 表内全空行可跳过，非空行即便禁用也保留，后续按启用状态决定格式诊断的严重度。
                if (fields.Values.All(string.IsNullOrWhiteSpace)) continue;
                rows.Add(new ConfigRow(sheet, number, columns, fields, diagnostics));
            }
            if (region is not null) tables.Add(sheet, rows);
            // 按工作表类型逐列检查模板必需字段；缺少数据表时 columns 为空，也能给出结构诊断。
            if (RequiredColumns(sheet) is { } required)
            {
                foreach (string field in required)
                    if (columns is null || !columns.ContainsKey(field))
                        diagnostics.Add(new(PlcDiagnosticSeverity.Error, "MISSING_COLUMN", sheet, region?.HeaderRow ?? 0, field, null, "缺少必要数据表或字段。"));
            }
        } while (reader.NextResult());

        // 两种协议的所有正式类别表均属于模板结构；“界面名称”是可选扩展表，不在缺表清单内。
        foreach (string sheet in new[] { "使用说明", "设备连接", "采集组", "字段说明" }
            .Concat(Categories.SelectMany(c => new[] { c + "_OpcUa", c + "_Modbus" })))
            if (!seen.Contains(sheet)) diagnostics.Add(new(PlcDiagnosticSeverity.Error, "MISSING_SHEET", sheet, 0, "", null, "缺少必要工作表。"));

        List<ConfigRow> Rows(string name) => tables.GetValueOrDefault(name) ?? [];
        // 先建设备，供变量解析按设备启用状态及协议决定是否严格处理其字段格式。
        var devices = Rows("设备连接").Select(r =>
        {
            // “启用”本身不能模糊解释，始终按错误报告；禁用设备的其余格式问题降为警告。
            bool enabled = r.Boolean("启用", alwaysError: true); r.Strict = enabled;
            return new PlcDeviceDefinition
            {
                Source = r.Source, Enabled = enabled, Id = r["PLC编号"], Name = r["设备名称"],
                Protocol = r["协议"] switch { "OpcUa" => PlcProtocol.OpcUa, "ModbusTcp" => PlcProtocol.ModbusTcp, _ => PlcProtocol.Unknown },
                SheetSuffix = r["变量Sheet后缀"], OpcUaEndpoint = r["OPC UA端点"],
                SecurityMode = r["安全模式"], SecurityPolicy = r["安全策略"], CredentialReference = r["凭据引用"],
                ConnectionTimeoutMs = r.Integer("连接超时(ms)"), ReconnectIntervalMs = r.Integer("重连间隔(ms)"),
                ModbusHost = r["Modbus IP"], ModbusPort = r.Integer("Modbus端口"), ModbusUnitId = r.Integer("Modbus Unit ID"), Notes = r["备注"]
            };
        }).ToArray();
        // 采集组没有独立启用列，作为公共调度定义始终严格解析；未知枚举交给语义校验指出。
        var groups = Rows("采集组").Select(r =>
        {
            r.Strict = true;
            return new PlcAcquisitionGroupDefinition
            {
                Source = r.Source, Id = r["采集组"],
                Lifetime = r["生命周期"] switch { "后台长期" => PlcAcquisitionLifetime.Background, "页面按需" => PlcAcquisitionLifetime.Page, _ => PlcAcquisitionLifetime.Unknown },
                Mode = r["采集方式"] switch { "定时批读" => PlcAcquisitionMode.PeriodicBatch, "进入读取" => PlcAcquisitionMode.OnEnter, _ => PlcAcquisitionMode.Unknown },
                ActivationCondition = r["启用条件"], IntervalMs = r.Integer("周期(ms)"), StaleAfterMs = r.Integer("失效阈值(ms)"),
                CheckpointIntervalMs = r.Integer("有效检查点周期(ms)"), ReleaseRule = r["释放规则"], Notes = r["说明"]
            };
        }).ToArray();
        var variables = new List<PlcVariableBinding>();
        foreach (string category in Categories)
        foreach (var protocol in new[] { PlcProtocol.OpcUa, PlcProtocol.ModbusTcp })
        foreach (var r in Rows(category + (protocol == PlcProtocol.OpcUa ? "_OpcUa" : "_Modbus")))
        {
            bool enabled = r.Boolean("启用", alwaysError: true);
            // 变量及其同协议设备都启用时格式问题才是 Error；待配置映射仍保留为 Warning 供完善。
            r.Strict = enabled && devices.Any(d => d.Enabled && d.Id == r["PLC编号"] && d.Protocol == protocol);
            // 公共字段和协议地址字段形成强类型属性，其余列全部转入 Metadata 保留原模板扩展信息。
            var common = CommonColumns.Concat(protocol == PlcProtocol.OpcUa ? UaColumns : MbColumns).ToHashSet(StringComparer.Ordinal);
            variables.Add(new PlcVariableBinding
            {
                Source = r.Source, Enabled = enabled, Protocol = protocol, Category = category,
                LogicalId = r["HMI变量标识"], DisplayName = r["界面名称"], ComponentId = r["构件编号"], DeviceId = r["PLC编号"],
                Address = protocol == PlcProtocol.OpcUa
                    ? new OpcUaBindingAddress(r["命名空间URI"], r["节点标识"], r.Integer("OPC UA数组索引"))
                    : new ModbusBindingAddress(r["Modbus存储区"], r.Integer("Modbus起始地址(0基)"), r.Integer("Modbus位号"), r.Integer("Modbus寄存器数"), r["Modbus字节顺序"], r["Modbus字顺序"]),
                // 要求枚举名称精确匹配模板，拒绝数字枚举文本和大小写变体悄悄被视为合法类型。
                DataType = Enum.TryParse<PlcDataType>(r["数据类型"], ignoreCase: false, out var type) && Enum.IsDefined(type) && type.ToString() == r["数据类型"] ? type : PlcDataType.Unknown,
                Access = r["访问权限"] switch { "R" => PlcAccess.Read, "W" => PlcAccess.Write, "RW" => PlcAccess.ReadWrite, _ => PlcAccess.None },
                AcquisitionGroup = r["采集组"], Unit = r["单位"], Scale = r.Number("比例"), Offset = r.Number("偏移"),
                InvertBoolean = r.Boolean("布尔取反"), Minimum = r.Number("最小值"), Maximum = r.Number("最大值"),
                WriteMode = r["写入方式"], Notes = r["备注"],
                Metadata = new ReadOnlyDictionary<string, string>(r.Fields.Where(p => !common.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))
            });
        }
        // 可选的本地名称表与协议无关；只作为显示元数据，绝不加入PLC采集或写入需求。
        var localNames = new Dictionary<(string Device, string Component, string Kind, string Index), string>();
        foreach (var row in Rows("界面名称"))
        {
            string kind = row["位置类型"], index = row["位置编号"], component = row["构件编号"];
            bool validComponent = row["构件类型"] switch
            {
                "轴" => Regex.IsMatch(component, @"^Axis\[\d+\]$"),
                "气缸" => Regex.IsMatch(component, @"^CYL\[\d+\]$"),
                "调速电机" => Regex.IsMatch(component, @"^Motor\[\d+\]$"),
                _ => false
            };
            bool validPosition = kind == "" ? index == "" : row["构件类型"] == "轴" && (kind is "绝对" or "相对") &&
                int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= 9 && index == n.ToString(CultureInfo.InvariantCulture);
            var key = (row["PLC编号"], component, kind, index);
            if (!devices.Any(d => d.Id == key.Item1) || !validComponent || !validPosition || row["显示名称"].Length == 0 || !localNames.TryAdd(key, row["显示名称"]))
                diagnostics.Add(new(PlcDiagnosticSeverity.Error, "LOCAL_NAME", row.Source.Sheet, row.Source.Row, "显示名称", row.Source.Columns.GetValueOrDefault("显示名称"), "本地名称的设备、构件、位置编号或名称无效，或同一位置重复配置。"));
        }
        // 把有效本地名称并入每个变量的只读元数据；不改动逻辑标识、地址或采集组。
        for (int i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];
            var metadata = variable.Metadata.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (localNames.TryGetValue((variable.DeviceId, variable.ComponentId, "", ""), out var componentName)) metadata["构件名称"] = componentName;
            // 同时兼容既有参数数组路径与位置对象路径，从逻辑标识提取绝对/相对位置编号。
            var position = Regex.Match(variable.LogicalId, @"\.(?:st_Parameter\.i_(Abs|Rel)_(?:Pos|Vel|Acc|Dec)\[(\d+)\]|(Positions|RelativePositions)\[(\d+)\])");
            if (position.Success)
            {
                string kind = position.Groups[1].Value == "Rel" || position.Groups[3].Value == "RelativePositions" ? "相对" : "绝对";
                string index = position.Groups[2].Success ? position.Groups[2].Value : position.Groups[4].Value;
                if (localNames.TryGetValue((variable.DeviceId, variable.ComponentId, kind, index), out var name)) metadata["位置名称"] = name;
            }
            variables[i] = variable with { Metadata = new ReadOnlyDictionary<string, string>(metadata) };
        }
        // 字段说明中若声明了导入版本，应与使用说明的版本一致，避免混用不同模板片段。
        var versionRow = Rows("字段说明").FirstOrDefault(r => r["字段"] == "导入版本");
        if (versionRow is not null && versionRow["填写形式"] != version)
            diagnostics.Add(new(PlcDiagnosticSeverity.Error, "VERSION_MISMATCH", versionRow.Source.Sheet, versionRow.Source.Row, "填写形式", versionRow.Source.Columns.GetValueOrDefault("填写形式"), "字段说明与使用说明的版本不一致。"));
        // 哈希针对最初读取的字节快照，后续可据此识别实际加载内容；集合包装防止意外改写。
        var configuration = new PlcBindingConfiguration
        {
            Version = version, FilePath = filePath, FileSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Devices = Array.AsReadOnly(devices), Variables = variables.AsReadOnly(), AcquisitionGroups = Array.AsReadOnly(groups)
        };
        return new(configuration, diagnostics.AsReadOnly());
    }

    // 只有本方法识别的工作表需要正式 Excel 数据表；额外说明工作表不会当作绑定表解析。
    private static string[]? RequiredColumns(string sheet)
    {
        if (sheet == "设备连接") return DeviceColumns;
        if (sheet == "采集组") return GroupColumns;
        if (sheet == "字段说明") return ["字段", "填写形式", "解释"];
        if (sheet == "界面名称") return ["PLC编号", "构件类型", "构件编号", "位置类型", "位置编号", "显示名称"];
        foreach (string category in Categories)
        {
            if (sheet == category + "_OpcUa") return CommonColumns.Concat(UaColumns).Concat(ExtraColumns[category]).ToArray();
            if (sheet == category + "_Modbus") return CommonColumns.Concat(MbColumns).Concat(ExtraColumns[category]).ToArray();
        }
        return null;
    }

    // 用不随系统区域变化的格式转换数值，统一修剪首尾空白；越界和空单元格映射为空字符串。
    private static string Cell(IExcelDataReader reader, int column) => column >= reader.FieldCount ? "" :
        Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture)?.Trim() ?? "";

    /// <summary>
    /// 将一行表格连同原始坐标封装为命名字段访问，集中处理布尔值和可空数值的格式诊断。
    /// 空数值返回 null 而非 0，是否必填由语义校验决定。
    /// </summary>
    private sealed class ConfigRow
    {
        private readonly List<PlcConfigurationDiagnostic> diagnostics;
        public PlcConfigurationSource Source { get; }
        public IReadOnlyDictionary<string, string> Fields { get; }
        public bool Strict { get; set; }
        public string this[string field] => Fields.GetValueOrDefault(field) ?? "";
        public ConfigRow(string sheet, int row, Dictionary<string, int> columns, Dictionary<string, string> fields, List<PlcConfigurationDiagnostic> diagnostics)
        {
            Source = new(sheet, row, new ReadOnlyDictionary<string, int>(columns));
            Fields = new ReadOnlyDictionary<string, string>(fields); this.diagnostics = diagnostics;
        }
        // Strict 由外层根据启用状态设置；必要字段可通过 alwaysError 强制阻止带歧义的配置。
        private void Invalid(string field, string message, bool alwaysError = false) => diagnostics.Add(new(
            Strict || alwaysError ? PlcDiagnosticSeverity.Error : PlcDiagnosticSeverity.Warning,
            "FIELD_FORMAT", Source.Sheet, Source.Row, field, Source.Columns.GetValueOrDefault(field), message));
        /// <summary>只接受模板约定的“是/否”；非强制的空单元格按 false 处理。</summary>
        public bool Boolean(string field, bool alwaysError = false)
        {
            if (this[field] == "是") return true;
            if (this[field] == "否") return false;
            if (this[field] != "" || alwaysError) Invalid(field, "应填写“是”或“否”。", alwaysError);
            return false;
        }
        /// <summary>接受可表示为 Int32 的整值数字，包含 0 和指数写法；拒绝小数部分与溢出。</summary>
        public int? Integer(string field)
        {
            if (this[field] == "") return null;
            if (decimal.TryParse(this[field], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var n) && n == decimal.Truncate(n) && n is >= int.MinValue and <= int.MaxValue) return (int)n;
            Invalid(field, "应填写范围内的整数；0是有效数值。"); return null;
        }
        /// <summary>按固定区域格式读取 decimal，空单元格保留为 null；非法内容附加字段诊断。</summary>
        public decimal? Number(string field)
        {
            if (this[field] == "") return null;
            if (decimal.TryParse(this[field], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n)) return n;
            Invalid(field, "应填写decimal范围内的有限数值。"); return null;
        }
    }

    // 只读取 Open XML 的数据表边界；单元格值由 ExcelDataReader 解析。
    // 不以“遇到空行即结束”判断范围，因此表内空行和表外说明都能正确处理。
    private static Dictionary<string, TableRegion> ReadTableRegions(ZipArchive zip)
    {
        XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XDocument Xml(string entry)
        {
            using var input = zip.GetEntry(entry)?.Open() ?? throw new InvalidDataException("Excel数据表结构不完整。");
            return XDocument.Load(input);
        }
        // 用包内相对路径解析关系目标；这里只构造 URI 来规范化路径，不发起网络访问。
        string Resolve(string parent, string target) => new Uri(new Uri("https://xlsx.local/" + parent), target).AbsolutePath.TrimStart('/');
        Dictionary<string, string> Relationships(string parent)
        {
            int slash = parent.LastIndexOf('/');
            string entry = parent[..(slash + 1)] + "_rels/" + parent[(slash + 1)..] + ".rels";
            if (zip.GetEntry(entry) is null) return new();
            // 排除外部关系，仅使用 zip 内部的工作簿、工作表和数据表部件。
            return Xml(entry).Root!.Elements().Where(e => (string?)e.Attribute("TargetMode") != "External")
                .ToDictionary(e => (string)e.Attribute("Id")!, e => Resolve(parent, (string)e.Attribute("Target")!));
        }
        var workbookRels = Relationships("xl/workbook.xml");
        var result = new Dictionary<string, TableRegion>(StringComparer.Ordinal);
        foreach (var sheet in Xml("xl/workbook.xml").Descendants(m + "sheet"))
        {
            string name = (string)sheet.Attribute("name")!;
            if (RequiredColumns(name) is null) continue;
            string sheetPath = workbookRels[(string)sheet.Attribute(r + "id")!];
            var parts = Xml(sheetPath).Descendants(m + "tablePart").ToArray();
            // 正式表必须只有一个 Excel 数据表；不根据视觉上相邻的单元格猜测配置范围。
            if (parts.Length != 1) throw new InvalidDataException($"工作表“{name}”需要且仅能有一个Excel数据表。");
            var rels = Relationships(sheetPath);
            var table = Xml(rels[(string)parts[0].Attribute(r + "id")!]).Root!;
            var match = Regex.Match((string?)table.Attribute("ref") ?? "", @"^([A-Z]+)(\d+):([A-Z]+)(\d+)$");
            if (!match.Success || (string?)table.Attribute("headerRowCount") == "0") throw new InvalidDataException($"工作表“{name}”的数据表范围或表头无效。");
            int Column(string text) => text.Aggregate(0, (n, c) => checked(n * 26 + c - 'A' + 1));
            // 汇总行不属于配置记录；列字母按 26 进制转换并使用 checked 检查溢出。
            int totals = (int?)table.Attribute("totalsRowCount") ?? 0;
            result.Add(name, new(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) - totals,
                Column(match.Groups[1].Value), Column(match.Groups[3].Value)));
        }
        return result;
    }
}
