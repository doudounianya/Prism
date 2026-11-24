using Company.PlcHmi.Shared.PLC.Configuration;
using Company.Core.Ioc;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Company.PlcHmi.Configuration.Services.PLC;

/// <summary>
/// 对已解析的 PLC 配置执行跨表、类型、地址及业务标识校验，收集可定位到源单元格的诊断。
/// 校验不修改模型也不连接设备；启用的有效设备/变量采用严格错误级别，待配置映射通常只报告警告。
/// 版本、重复标识及公共采集组等全局约束仍保持错误级别。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcBindingValidator
{
    /// <summary>检查设备、采集组和变量的整体一致性，返回全部诊断而非遇到首错就停止。</summary>
    internal IReadOnlyList<PlcConfigurationDiagnostic> Validate(PlcBindingConfiguration config)
    {
        var results = new List<PlcConfigurationDiagnostic>();
        // 统一附加来源坐标；目标字段未出现在表头映射中时保留空列号，不伪造单元格位置。
        void Check(bool valid, PlcConfigurationSource source, string field, string code, string message, bool strict = true)
        {
            if (!valid) results.Add(new(strict ? PlcDiagnosticSeverity.Error : PlcDiagnosticSeverity.Warning,
                code, source.Sheet, source.Row, field, source.Columns.TryGetValue(field, out int column) ? column : null, message));
        }
        Check(config.Version == PlcBindingConfiguration.SupportedVersion,
            new("使用说明", 2, new Dictionary<string, int> { ["版本"] = 1 }), "版本", "VERSION",
            $"本版仅支持{PlcBindingConfiguration.SupportedVersion}配置；请使用对应版本的正式配置文件。");
        // 引用标识采用区分大小写的精确匹配，设备编号重复即使出现在禁用行也属于结构歧义。
        var deviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in config.Devices)
        {
            Check(d.Id.Length > 0, d.Source, "PLC编号", "DEVICE_ID", "PLC编号不能为空。", d.Enabled);
            if (d.Id.Length > 0) Check(deviceIds.Add(d.Id), d.Source, "PLC编号", "DUPLICATE_DEVICE", "PLC编号重复。首次位置见设备连接表。" );
            Check(d.Protocol != PlcProtocol.Unknown, d.Source, "协议", "PROTOCOL", "协议应为OpcUa或ModbusTcp。", d.Enabled);
            Check(d.SheetSuffix == (d.Protocol == PlcProtocol.OpcUa ? "_OpcUa" : "_Modbus"), d.Source, "变量Sheet后缀", "SHEET_SUFFIX", "Sheet后缀与所选协议不一致。", d.Enabled);
            Check(d.ConnectionTimeoutMs is > 0, d.Source, "连接超时(ms)", "TIMEOUT", "连接超时必须为正整数。", d.Enabled);
            Check(d.ReconnectIntervalMs is > 0, d.Source, "重连间隔(ms)", "RECONNECT", "重连间隔必须为正整数。", d.Enabled);
            // 禁用设备仍检查基本结构并保留警告；端点、安全和协议连接参数只对启用设备作强制校验。
            if (!d.Enabled) continue; // 禁用设备允许保留待确认的端点与安全参数。
            if (d.Protocol == PlcProtocol.OpcUa)
            {
                Check(Uri.TryCreate(d.OpcUaEndpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "opc.tcp" &&
                    endpoint.Host.Length > 0 && !d.OpcUaEndpoint.Contains("PLC_IP", StringComparison.OrdinalIgnoreCase),
                    d.Source, "OPC UA端点", "ENDPOINT", "请填写实际的opc.tcp端点，不能使用PLC_IP占位值。");
                Check(d.SecurityMode is "None" or "Sign" or "SignAndEncrypt", d.Source, "安全模式", "SECURITY_MODE", "安全模式应为None、Sign或SignAndEncrypt。");
                Check(d.SecurityPolicy.Length > 0 && d.SecurityPolicy != "待确认" &&
                    (d.SecurityMode == "None" ? d.SecurityPolicy == "None" : d.SecurityPolicy != "None"),
                    d.Source, "安全策略", "SECURITY_POLICY", "请明确安全策略，并使其与安全模式匹配。");
            }
            else if (d.Protocol == PlcProtocol.ModbusTcp)
            {
                Check(Uri.CheckHostName(d.ModbusHost) != UriHostNameType.Unknown && d.ModbusHost != "PLC_IP", d.Source, "Modbus IP", "MODBUS_HOST", "请填写实际IP或主机名。");
                Check(d.ModbusPort is >= 1 and <= 65535, d.Source, "Modbus端口", "MODBUS_PORT", "端口应为1～65535。");
                Check(d.ModbusUnitId is >= 0 and <= 255, d.Source, "Modbus Unit ID", "UNIT_ID", "Unit ID应为0～255。");
            }
        }
        // 采集组是全局公共定义，没有启用开关：编号、调度方式及生命周期约束一律为错误。
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in config.AcquisitionGroups)
        {
            Check(g.Id.Length > 0, g.Source, "采集组", "GROUP_ID", "采集组编号不能为空。");
            Check(groupIds.Add(g.Id), g.Source, "采集组", "DUPLICATE_GROUP", "采集组编号重复。");
            Check(g.Lifetime != PlcAcquisitionLifetime.Unknown, g.Source, "生命周期", "LIFETIME", "生命周期应为后台长期或页面按需。");
            Check(g.Mode != PlcAcquisitionMode.Unknown, g.Source, "采集方式", "ACQUISITION_MODE", "首版只支持定时批读或进入读取，订阅尚未实现。");
            // 周期采集的失效阈值不能早于一次采集周期；进入读取则不允许残留定时参数。
            if (g.Mode == PlcAcquisitionMode.PeriodicBatch)
            {
                Check(g.IntervalMs is > 0, g.Source, "周期(ms)", "INTERVAL", "定时批读周期必须为正整数。");
                Check(g.StaleAfterMs is > 0 && g.StaleAfterMs >= g.IntervalMs, g.Source, "失效阈值(ms)", "STALE", "失效阈值必须为正整数且不小于采集周期。");
            }
            else if (g.Mode == PlcAcquisitionMode.OnEnter)
                Check(g.IntervalMs is null && g.StaleAfterMs is null, g.Source, "周期(ms)", "ON_ENTER_INTERVAL", "进入读取不使用定时周期或失效阈值，请留空。");
            // 历史检查点仅属于后台周期采集，检查点周期不能短于采集周期；页面作用域无需持久化检查点。
            if (g.Lifetime == PlcAcquisitionLifetime.Background)
            {
                Check(g.Mode == PlcAcquisitionMode.PeriodicBatch, g.Source, "采集方式", "BACKGROUND_MODE", "后台长期采集必须使用定时批读。");
                Check(g.CheckpointIntervalMs is > 0 && g.CheckpointIntervalMs >= g.IntervalMs, g.Source, "有效检查点周期(ms)", "CHECKPOINT", "后台检查点周期必须为正整数且不小于采集周期。");
            }
            else Check(g.CheckpointIntervalMs is null, g.Source, "有效检查点周期(ms)", "PAGE_CHECKPOINT", "页面采集不设置持久化检查点。");
            Check(g.ActivationCondition.Length > 0, g.Source, "启用条件", "ACTIVATION", "请填写启用条件说明。");
            Check(g.ReleaseRule.Length > 0, g.Source, "释放规则", "RELEASE", "请填写释放规则说明。");
        }
        // 同协议、同设备、同逻辑变量三元组必须唯一；保存首次来源以便重复项定位修正。
        var keys = new Dictionary<(PlcProtocol, string, string), PlcConfigurationSource>();
        foreach (var v in config.Variables)
        {
            var d = config.Devices.FirstOrDefault(d => d.Id == v.DeviceId);
            // 只有变量和对应协议设备均启用才执行严格变量校验；设备引用是否存在另按变量启用状态判断。
            bool active = v.Enabled && d is { Enabled: true } && d.Protocol == v.Protocol;
            void V(bool ok, string field, string code, string message) => Check(ok, v.Source, field, code, message, active);
            V(v.LogicalId.Length > 0, "HMI变量标识", "LOGICAL_ID", "逻辑变量标识不能为空。");
            V(v.ComponentId.Length > 0, "构件编号", "COMPONENT_ID", "构件编号不能为空。");
            Check(d is not null, v.Source, "PLC编号", "DEVICE_REFERENCE", "找不到引用的设备连接。", v.Enabled);
            // 重复绑定不因禁用而放宽，以免以后启用时同一个逻辑标识对应两处地址。
            if (v.LogicalId.Length > 0 && v.DeviceId.Length > 0)
            {
                var key = (v.Protocol, v.DeviceId, v.LogicalId);
                if (keys.TryGetValue(key, out var first))
                    Check(false, v.Source, "HMI变量标识", "DUPLICATE_BINDING", $"同协议、同PLC存在重复逻辑变量；首次位于{first.Sheet}第{first.Row}行。");
                else keys.Add(key, v.Source);
            }
            V(v.DataType != PlcDataType.Unknown, "数据类型", "DATA_TYPE", "数据类型不在支持列表内，且大小写必须与模板一致。");
            V(v.Access != PlcAccess.None, "访问权限", "ACCESS", "访问权限应为R、W或RW。");
            // 可读变量必须有已定义的采集组；只写变量不应出现在读取范围内。
            if (v.Access.HasFlag(PlcAccess.Read)) V(v.AcquisitionGroup.Length > 0, "采集组", "READ_GROUP", "可读变量需要配置采集组。");
            if (v.AcquisitionGroup.Length > 0)
            {
                V(groupIds.Contains(v.AcquisitionGroup), "采集组", "GROUP_REFERENCE", "找不到引用的采集组。");
                V(v.Access.HasFlag(PlcAccess.Read), "采集组", "WRITE_ONLY_GROUP", "只写变量不应加入读取采集组。");
                var g = config.AcquisitionGroups.FirstOrDefault(g => g.Id == v.AcquisitionGroup);
                if (g is not null && v.Category is "IO" or "轴" or "气缸")
                {
                    V(g.Lifetime == PlcAcquisitionLifetime.Page, "采集组", "PAGE_ONLY", "IO、轴和气缸按页面当前对象采集，不应配置为后台长期。");
                    // PAGE.Active只共享周期；实际变量范围仍由页面作用域明确指定。
                    // 兼容既有按构件命名的配置，不放宽其跨构件校验。
                    V(g.Id == "PAGE.Active" || (v.Category == "IO" ? g.Id == "PAGE." + v.ComponentId : g.Id.StartsWith("PAGE." + v.ComponentId + ".", StringComparison.Ordinal)),
                        "采集组", "GROUP_COMPONENT", "采集组应为PAGE.Active，或属于当前IO组、轴或气缸。");
                }
            }
            // 换算边界与写权限协同校验：Bool 使用取反而不做比例换算，可写数值不能用零比例。
            V(v.Minimum is null || v.Maximum is null || v.Minimum <= v.Maximum, "最小值", "BOUNDS", "最小值不能大于最大值。");
            V(v.Scale is not null && v.Offset is not null, "比例", "SCALING", "比例和偏移应明确填写，通常为1和0。");
            V(!v.InvertBoolean || v.DataType == PlcDataType.Bool, "布尔取反", "INVERSION", "布尔取反只适用于Bool类型。");
            if (v.DataType == PlcDataType.Bool) V(v.Scale == 1 && v.Offset == 0, "比例", "BOOL_SCALING", "Bool不进行数值比例换算，应使用比例1、偏移0。");
            V(v.WriteMode is "只读" or "参数暂存" or "请求参数" or "握手请求" or "保持请求", "写入方式", "WRITE_MODE", "写入方式不在模板约定列表内。");
            V(v.Access.HasFlag(PlcAccess.Write) ? v.WriteMode != "只读" : v.WriteMode == "只读", "写入方式", "WRITE_ACCESS", "写入方式与访问权限不匹配。");
            if (v.Access.HasFlag(PlcAccess.Write)) V(v.Scale != 0, "比例", "ZERO_SCALE", "可写数值不能使用0比例。");
            // 类别专属列保存在 Metadata，以下检查把模板编号与逻辑标识、构件归属联系起来。
            string Meta(string name) => v.Metadata.GetValueOrDefault(name) ?? "";
            bool Index(string text, out int n) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n >= 0;
            if (v.Category == "IO")
            {
                string kind = Meta("信号类型");
                // 使用按位 & 让组号和通道号都完成解析；索引允许 0，数字 IO 的通道范围固定为 0～15。
                bool indexes = Index(Meta("分组号"), out int group) & Index(Meta("通道号"), out int channel);
                V(indexes && kind is "DI" or "DO" or "AI" or "AO", "分组号", "IO_INDEX", "IO类型及组号、通道号应有效，索引从0开始。");
                if (indexes)
                {
                    V(v.LogicalId == $"IO.{kind}[{group}].{channel:D2}" && v.ComponentId == $"IO.{kind}[{group}]", "HMI变量标识", "IO_ID", "IO标识、构件编号与组号／通道号不一致。");
                    if (kind is "DI" or "DO")
                    {
                        V(channel <= 15, "通道号", "IO_CHANNEL", "DI、DO通道固定为00～15。");
                        V(v.DataType == PlcDataType.Bool, "数据类型", "DIGITAL_TYPE", "DI、DO应使用Bool类型。");
                    }
                }
            }
            // 轴/气缸变量必须归属自己的构件前缀；轴位置还需与模板给出的零基位置编号一致。
            if (v.Category is "轴" or "气缸")
            {
                string prefix = v.Category == "轴" ? "Axis" : "CYL";
                V(Regex.IsMatch(v.ComponentId, $@"^{prefix}\[\d+\]$") && v.LogicalId.StartsWith(v.ComponentId + ".", StringComparison.Ordinal),
                    "构件编号", "COMPONENT_REFERENCE", "构件编号应与逻辑变量所属Axis[n]或CYL[n]一致。");
                if (v.Category == "轴" && Meta("位置编号").Length > 0)
                {
                    bool indexValid = Index(Meta("位置编号"), out int position);
                    V(indexValid, "位置编号", "POSITION_INDEX", "位置编号应为从0开始的整数。");
                    V(indexValid && (v.LogicalId == $"{v.ComponentId}.st_Parameter.i_Abs_Pos[{position}]" ||
                        v.LogicalId.StartsWith($"{v.ComponentId}.Positions[{position}].", StringComparison.Ordinal) ||
                        v.LogicalId == $"{v.ComponentId}.RelativePositions[{position}].Run"),
                        "HMI变量标识", "POSITION_REFERENCE", "位置变量标识与位置编号不一致。");
                }
            }
            // 标准 Alarm[n] 位变量限定为只读 Bool，并要求用于历史及提示的报警编码、说明。
            if (v.Category == "报警" && Regex.IsMatch(v.LogicalId, @"^Alarm\[\d+\]$"))
            {
                V(v.DataType == PlcDataType.Bool && v.Access == PlcAccess.Read, "数据类型", "ALARM_TYPE", "Alarm[n]应为只读Bool变量。");
                V(Meta("报警编码").Length > 0 && Meta("报警说明").Length > 0, "报警编码", "ALARM_METADATA", "报警需配置编码与说明。");
            }
            // OPC UA 地址使用命名空间字符串和带类型前缀的节点标识，不保存易随服务器变化的 ns 索引。
            if (v.Address is OpcUaBindingAddress ua)
            {
                // 完全未填写的禁用映射可暂留；一旦填写地址则检查其格式并按当前启用状态分级。
                if (active || ua.NamespaceUri.Length > 0 || ua.NodeIdentifier.Length > 0)
                {
                    // 命名空间保留服务器发布的原字符串，部分PLC不使用绝对URI形式。
                    V(!string.IsNullOrWhiteSpace(ua.NamespaceUri), "命名空间URI", "UA_NAMESPACE", "应填写服务器发布的非空命名空间字符串。");
                    V(Regex.IsMatch(ua.NodeIdentifier, @"^[isgb]=.+$", RegexOptions.Singleline), "节点标识", "UA_NODE", "节点标识保留i=、s=、g=或b=前缀，不混入ns=索引。");
                    if (ua.NodeIdentifier.StartsWith("i=", StringComparison.Ordinal)) V(uint.TryParse(ua.NodeIdentifier[2..], NumberStyles.None, CultureInfo.InvariantCulture, out _), "节点标识", "UA_NUMERIC_NODE", "i=节点标识必须为UInt32整数。");
                    if (ua.NodeIdentifier.StartsWith("g=", StringComparison.Ordinal)) V(Guid.TryParse(ua.NodeIdentifier[2..], out _), "节点标识", "UA_GUID_NODE", "g=节点标识必须为有效GUID。");
                }
                // null 表示未指定数组元素，0 表示第一个元素，两者不能混为同一含义。
                V(ua.ArrayIndex is null or >= 0, "OPC UA数组索引", "UA_ARRAY", "数组索引不能为负数；0是有效索引。");
            }
            // Modbus 映射按存储区分别约束位/寄存器表示、地址跨度、字节字序和可写范围。
            if (v.Address is ModbusBindingAddress mb && (active || mb.Area.Length > 0 || mb.StartAddress is not null))
            {
                V(mb.Area is "Coil" or "DiscreteInput" or "InputRegister" or "HoldingRegister", "Modbus存储区", "MODBUS_AREA", "Modbus存储区不在约定列表内。");
                V(mb.StartAddress is >= 0 and <= 65535, "Modbus起始地址(0基)", "MODBUS_ADDRESS", "起始地址应为0～65535的整数。");
                if (mb.Area is "Coil" or "DiscreteInput")
                {
                    V(v.DataType == PlcDataType.Bool && mb.Bit is null && mb.RegisterCount is null, "数据类型", "MODBUS_BIT_AREA", "线圈及离散输入应为Bool，位号和寄存器数留空。");
                }
                else if (mb.Area is "HoldingRegister" or "InputRegister")
                {
                    V(mb.RegisterCount is > 0 && mb.StartAddress + (long?)mb.RegisterCount <= 65536, "Modbus寄存器数", "MODBUS_SPAN", "寄存器数必须为正整数，且地址范围不能越界。");
                    V(mb.ByteOrder is "AB" or "BA", "Modbus字节顺序", "BYTE_ORDER", "字节顺序应为AB或BA。");
                    V(mb.WordOrder is "高字在前" or "低字在前" || (mb.RegisterCount == 1 && mb.WordOrder.Length == 0),
                        "Modbus字顺序", "WORD_ORDER", "多寄存器应明确高字在前或低字在前；单寄存器可留空。");
                    // 由数据类型计算固定寄存器宽度；未定义固定宽度的类型不在此处猜测寄存器数。
                    int? words = v.DataType switch { PlcDataType.Bool or PlcDataType.Int16 or PlcDataType.UInt16 => 1, PlcDataType.Int32 or PlcDataType.UInt32 or PlcDataType.Float => 2, PlcDataType.Int64 or PlcDataType.UInt64 or PlcDataType.Double => 4, _ => null };
                    if (words is not null) V(mb.RegisterCount == words, "Modbus寄存器数", "REGISTER_WIDTH", "寄存器数与声明的数据类型宽度不一致。");
                    V(v.DataType == PlcDataType.Bool ? mb.Bit is >= 0 and <= 15 : mb.Bit is null, "Modbus位号", "MODBUS_BIT", "寄存器Bool需要0～15位号；其他类型位号留空。");
                    V(v.DataType != PlcDataType.DateTime, "数据类型", "MODBUS_TIME_ENCODING", "Modbus时间编码尚未约定，暂不能启用DateTime映射。");
                }
                // 离散输入和输入寄存器属于只读存储区，即使模板声明 W/RW 也不可作为写目标。
                V(!v.Access.HasFlag(PlcAccess.Write) || mb.Area is "Coil" or "HoldingRegister", "访问权限", "MODBUS_READ_ONLY", "输入存储区不支持写入。");
            }
        }
        // 诊断列表只读返回给加载提供器，提供器据 Error 是否存在决定能否发布整份配置。
        return results.AsReadOnly();
    }
}
