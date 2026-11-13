using Company.Application.Main.Models;
using Company.Application.Share.PLC.Configuration;
using Company.Core.Ioc;
using System.Text.RegularExpressions;

namespace Company.Application.Main.Services.PLC;

/// <summary>
/// 按设备、逻辑上下文、配方、阶段和参数类别构建工艺页面目录。
/// 识别规则基于 HMI 逻辑标识及元数据，配置不足的字段保留明确不可采集原因。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class ProcessBindingCatalog(IPlcBindingConfigProvider provider)
{
    public PlcBindingConfiguration? Configuration => provider.Current;
    public string Stamp => Configuration is { } c ? c.Version + ":" + c.FileSha256 : "";
    /// <summary>
    /// 列出包含工艺参数定义的设备，显示名为空时使用设备标识。
    /// </summary>
    public IReadOnlyList<ComponentOption> Devices => Configuration?.Devices
        .Where(d => Configuration.Variables.Any(v => v.DeviceId == d.Id && v.Protocol == d.Protocol && v.Category == "工艺参数"))
        .Select(d => new ComponentOption(d.Id, string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name)).ToArray() ?? [];
    /// <summary>
    /// 读取所选设备协议；设备不存在时返回 Unknown 供页面拒绝旧选择。
    /// </summary>
    public PlcProtocol Protocol(string? device) => Configuration?.Devices.FirstOrDefault(d => d.Id == device)?.Protocol ?? PlcProtocol.Unknown;
    /// <summary>
    /// 提取可识别逻辑上下文并去重排序，不把无法识别对象混入目录。
    /// </summary>
    public IReadOnlyList<string> Contexts(string? device) => Bindings(device).Select(v => Context(v.LogicalId))
        .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
    /// <summary>
    /// 在选中逻辑上下文内，从合法阶段字段提取配方编号。
    /// </summary>
    public IReadOnlyList<string> Recipes(string? device, string? context) => Stages(device, context)
        .Select(v => Meta(v, "配方编号")).Distinct().Order(StringComparer.Ordinal).ToArray();
    /// <summary>
    /// 在指定配方下提取配置中出现的阶段，沿用定义顺序。
    /// </summary>
    public IReadOnlyList<string> Phases(string? device, string? context, string? recipe) => Stages(device, context)
        .Where(v => Meta(v, "配方编号") == recipe).Select(v => Meta(v, "阶段")).Distinct().ToArray();
    /// <summary>
    /// 在指定配方与阶段下提取参数类别，作为页面末级筛选条件。
    /// </summary>
    public IReadOnlyList<string> Categories(string? device, string? context, string? recipe, string? phase) => Stages(device, context)
        .Where(v => Meta(v, "配方编号") == recipe && Meta(v, "阶段") == phase)
        .Select(v => Meta(v, "参数类别")).Distinct().ToArray();
    /// <summary>
    /// 仅当设备协议仍匹配选择时投影阶段页面字段，并要求页面生命周期采集组。
    /// </summary>
    public IReadOnlyList<PlcDisplayField> PageFields(ProcessParameterSelection? selection) => selection is null
        || Protocol(selection.DeviceId) != selection.Protocol ? [] :
        Stages(selection.DeviceId, selection.Context).Where(v => Meta(v, "配方编号") == selection.Recipe
            && Meta(v, "阶段") == selection.Phase && Meta(v, "参数类别") == selection.ParameterCategory)
        .Select(v => Field(v, PlcAcquisitionLifetime.Page)).ToArray();
    // 公共字段仍单独展示；统一配置后与当前阶段一起按页面生命周期采集。
    /// <summary>
    /// 投影当前上下文中的公共字段，保留其配置采集生命周期并生成诊断。
    /// </summary>
    public IReadOnlyList<PlcDisplayField> CommonFields(string? device, string? context) => Bindings(device)
        .Where(v => Context(v.LogicalId) is { } c && c == context && IsCommon(v))
        .Select(v => Field(v, Configuration?.AcquisitionGroups.FirstOrDefault(g => g.Id == v.AcquisitionGroup)?.Lifetime
            ?? PlcAcquisitionLifetime.Page)).ToArray();
    /// <summary>
    /// 统计无法识别上下文或公共/阶段结构的绑定，提示被目录排除的配置数量。
    /// </summary>
    public string Diagnostics(string? device)
    {
        var rows = Bindings(device).ToArray();
        int unknown = rows.Count(v => Context(v.LogicalId) is null || (!IsCommon(v) && !IsStage(v)));
        return unknown == 0 ? "" : $"{unknown} 项工艺绑定的对象或配方／阶段／类别信息无法识别，已排除；请核对配置。";
    }
    /// <summary>
    /// 按页面涉及采集组汇总进入读取或定时批读说明，不发起采集。
    /// </summary>
    public string ReadMode(IReadOnlyList<PlcDisplayField> fields) => string.Join("；", fields
        .Select(f => f.Binding.AcquisitionGroup).Distinct()
        .Select(id => Configuration?.AcquisitionGroups.FirstOrDefault(g => g.Id == id))
        .OfType<PlcAcquisitionGroupDefinition>().Select(g => g.Mode == PlcAcquisitionMode.OnEnter
            ? "进入读取，可手动刷新" : g.Mode == PlcAcquisitionMode.PeriodicBatch ? $"定时批读 {g.IntervalMs} ms" : "采集方式未配置").Distinct());
    /// <summary>只识别约定的 HMI 逻辑上下文，不解析 PLC 标签或节点地址；歧义上下文拒绝合并。</summary>
    public static string? Context(string id)
    {
        const string marker = ".Recipe.";
        int at = id.IndexOf(marker, StringComparison.Ordinal);
        if (at <= 0 || id.IndexOf(marker, at + marker.Length, StringComparison.Ordinal) >= 0) return null;
        string prefix = id[..at];
        return Regex.IsMatch(prefix, @"^W[0-9]+\.P[0-9]+$") ? prefix : null;
    }
    private static string Meta(PlcVariableBinding v, string name) => v.Metadata.GetValueOrDefault(name) ?? "";
    /// <summary>
    /// 公共字段必须具有公共配方/阶段、RECIPE 构件及单层逻辑后缀，避免与阶段字段混淆。
    /// </summary>
    private static bool IsCommon(PlcVariableBinding v)
    {
        if (Meta(v, "配方编号") != "公共" || Meta(v, "阶段") != "公共" || v.ComponentId != "RECIPE") return false;
        var context = Context(v.LogicalId);
        if (context is null) return false;
        string suffix = v.LogicalId[(context.Length + ".Recipe.".Length)..];
        return suffix.Length > 0 && !suffix.Contains('.');
    }
    /// <summary>
    /// 校验配方、阶段、类别元数据与逻辑路径的一致性，拒绝空值、公共项及不匹配路径。
    /// </summary>
    private static bool IsStage(PlcVariableBinding v) => Meta(v, "配方编号") is { Length: > 0 } recipe && recipe != "公共"
        && Meta(v, "阶段") is { Length: > 0 } phase && phase != "公共" && Meta(v, "参数类别").Length > 0
        && v.ComponentId == recipe && Context(v.LogicalId) is { } context
        && v.LogicalId.StartsWith(context + ".Recipe." + recipe + "." + phase + ".", StringComparison.Ordinal)
        && v.LogicalId.Length > (context + ".Recipe." + recipe + "." + phase + ".").Length;
    /// <summary>
    /// 只读取选中设备和协议中的可读工艺参数定义；启用状态在字段诊断中继续判断。
    /// </summary>
    private IEnumerable<PlcVariableBinding> Bindings(string? device)
    {
        var config = Configuration;
        var definition = config?.Devices.FirstOrDefault(d => d.Id == device);
        return definition is null ? [] : config!.Variables.Where(v => v.Category == "工艺参数" && v.DeviceId == device
            && v.Protocol == definition.Protocol && v.Access.HasFlag(PlcAccess.Read));
    }
    /// <summary>
    /// 在设备字段中筛出当前逻辑上下文且结构合法的阶段项。
    /// </summary>
    private IEnumerable<PlcVariableBinding> Stages(string? device, string? context) => Bindings(device)
        .Where(v => Context(v.LogicalId) is { } c && c == context && IsStage(v));
    /// <summary>
    /// 核对地址、启用、逻辑键唯一性及采集组配置，按优先原因生成可显示字段。
    /// </summary>
    private PlcDisplayField Field(PlcVariableBinding v, PlcAcquisitionLifetime lifetime)
    {
        var config = Configuration!;
        var device = config.Devices.First(d => d.Id == v.DeviceId);
        // 这里只检查配置中地址是否具备基本形状，不通过网络探测节点或寄存器是否实际存在。
        bool addressed = v.Address switch
        {
            OpcUaBindingAddress a => !string.IsNullOrWhiteSpace(a.NamespaceUri) && !string.IsNullOrWhiteSpace(a.NodeIdentifier),
            ModbusBindingAddress a => !string.IsNullOrWhiteSpace(a.Area) && a.StartAddress is >= 0,
            _ => false
        };
        var group = config.AcquisitionGroups.FirstOrDefault(g => g.Id == v.AcquisitionGroup);
        // 一个字段保留一个最先适用的诊断；可见性与是否允许加入实际读取需求分别由显示模型决定。
        string reason = !v.Enabled || !device.Enabled ? addressed ? "未启用" : "未启用 · 地址未配置"
            : !addressed ? "地址未配置"
            : Bindings(v.DeviceId).Count(b => b.LogicalId == v.LogicalId) > 1 ? "变量标识重复"
            : group is null ? "采集组未配置"
            : group.Lifetime != lifetime ? lifetime == PlcAcquisitionLifetime.Page ? "采集组不是页面按需" : "公共信息未配置为后台采集"
            : group.Mode is not (PlcAcquisitionMode.OnEnter or PlcAcquisitionMode.PeriodicBatch) ? "采集方式未配置" : "";
        return new(v, reason);
    }
}
