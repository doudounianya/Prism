using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.Core.Ioc;
using System.Globalization;

namespace Company.PlcHmi.Main.Services.PLC;

/// <summary>
/// 从当前配置投影构件设备、对象和可读页面字段，并补充本地显示名称及禁用原因。
/// 目录仅解释逻辑角色与元数据，不读取 PLC，也不重写配置地址。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class ComponentBindingCatalog(IPlcBindingConfigProvider provider)
{
    public PlcBindingConfiguration? Configuration => provider.Current;
    // 版本号与文件摘要共同形成配置戳，页面可用它判断选择和字段定义是否需要重建。
    public string Stamp => Configuration is { } c ? c.Version + ":" + c.FileSha256 : "";
    /// <summary>
    /// 列出包含指定类别绑定的设备，未命名设备回退显示其标识。
    /// </summary>
    public IReadOnlyList<ComponentOption> Devices(string category) => Configuration?.Devices
        .Where(d => Configuration.Variables.Any(v => v.DeviceId == d.Id && v.Protocol == d.Protocol && v.Category == category))
        .Select(d => new ComponentOption(d.Id, string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name)).ToArray() ?? [];
    /// <summary>
    /// 按构件标识分组，优先按首个方括号编号排序，并从元数据获取构件名称。
    /// </summary>
    public IReadOnlyList<ComponentOption> Components(string category, string? device) => Bindings(category, device)
        .Where(v => !string.IsNullOrWhiteSpace(v.ComponentId)).GroupBy(v => v.ComponentId)
        .OrderBy(g => Index(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new ComponentOption(g.Key, g.Select(v => Meta(v, "构件名称")).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? g.Key)).ToArray();
    /// <summary>
    /// 保留目标构件可读字段，包括不可采集项的配置原因，供页面展示诊断。
    /// </summary>
    public IReadOnlyList<PlcDisplayField> Fields(string category, string? device, string? component)
    {
        var config = Configuration;
        if (config is null) return [];
        // 活动集合用于标记启用状态；不能仅因为定义存在就为其建立页面采集需求。
        var active = config.ActiveVariables.ToHashSet();
        return Bindings(category, device).Where(v => v.ComponentId == component && v.Access.HasFlag(PlcAccess.Read))
            .Select(v => new PlcDisplayField(WithLocalDisplayName(v), !active.Contains(v) ? "未启用" :
                !config.AcquisitionGroups.Any(g => g.Id == v.AcquisitionGroup && g.Lifetime == PlcAcquisitionLifetime.Page)
                    ? "未配置页面采集组" : "")).ToArray();
    }
    /// <summary>
    /// 按构件名称、位置名称及已约定的参数逻辑后缀生成显示副本，原绑定保持不变。
    /// </summary>
    private static PlcVariableBinding WithLocalDisplayName(PlcVariableBinding variable)
    {
        string componentName = Meta(variable, "构件名称");
        if (componentName.Length == 0) return variable;
        string positionName = Meta(variable, "位置名称");
        string label = variable.DisplayName;
        if (positionName.Length > 0)
        {
            string suffix = variable.LogicalId;
            string parameter = suffix.EndsWith(".Speed", StringComparison.Ordinal) || suffix.Contains("_Vel[", StringComparison.Ordinal) ? "速度" :
                suffix.EndsWith(".Acceleration", StringComparison.Ordinal) || suffix.Contains("_Acc[", StringComparison.Ordinal) ? "加速度" :
                suffix.EndsWith(".Deceleration", StringComparison.Ordinal) || suffix.Contains("_Dec[", StringComparison.Ordinal) ? "减速度" :
                suffix.Contains("i_Rel_Pos[", StringComparison.Ordinal) ? "相对位置" : "绝对位置";
            label = $"{componentName} {positionName} {parameter}";
        }
        else
        {
            int separator = label.IndexOf(' ');
            label = componentName + " " + (separator >= 0 ? label[(separator + 1)..] : label);
        }
        return variable with { DisplayName = label };
    }
    /// <summary>
    /// 按类别、设备及设备协议筛选定义，避免不同协议下相同标识被混入同一构件。
    /// </summary>
    private IEnumerable<PlcVariableBinding> Bindings(string category, string? device)
    {
        var config = Configuration;
        var definition = config?.Devices.FirstOrDefault(d => d.Id == device);
        return definition is null ? [] : config!.Variables.Where(v => v.Category == category && v.DeviceId == device && v.Protocol == definition.Protocol);
    }
    /// <summary>
    /// 读取可选元数据，缺失时统一返回空文本。
    /// </summary>
    public static string Meta(PlcVariableBinding v, string name) => v.Metadata.TryGetValue(name, out var value) ? value : "";
    /// <summary>
    /// 只接受无符号格式的非负位置编号；缺失或非法编号返回 -1。
    /// </summary>
    public static int PositionIndex(PlcVariableBinding v) => int.TryParse(Meta(v, "位置编号"), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 0 ? n : -1;
    /// <summary>
    /// 提取首对方括号中的整数用于目录排序；无法识别时置于默认末尾。
    /// </summary>
    public static int Index(string id)
    {
        int start = id.IndexOf('['), end = id.IndexOf(']');
        return start >= 0 && end > start && int.TryParse(id[(start + 1)..end], out int n) ? n : int.MaxValue;
    }
}
