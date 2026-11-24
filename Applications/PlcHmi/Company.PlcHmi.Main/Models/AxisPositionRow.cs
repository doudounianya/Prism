/*
 * 文件职责与维护说明：
 * 轴绝对/相对定位表的一行：组合位置索引、Excel 配置名称、运动参数、耗时及运行/到位字段。
 * 各字段仍保留原绑定，行模型只组织显示，不自行建立 PLC 连接。
 */
namespace Company.PlcHmi.Main.Models;

/// <summary>缺少某一单元格的绑定时保持空白，不补造 PLC 变量。</summary>
public sealed record AxisPositionRow(int Index, PlcDisplayField? Position, PlcDisplayField? Speed,
    PlcDisplayField? Acceleration, PlcDisplayField? Deceleration, PlcDisplayField? Dwell)
{
    public PlcDisplayField? NameField { get; init; }
    public PlcDisplayField? RunState { get; init; }
    public PlcDisplayField? DoneState { get; init; }
    public string ConfiguredName { get; init; } = "";
    public bool IsRelative { get; init; }
    public string Name => string.IsNullOrWhiteSpace(ConfiguredName) ? $"{(IsRelative ? "相对" : "绝对")}位置{Index}" : ConfiguredName;
}
