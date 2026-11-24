/*
 * 文件职责与维护说明：
 * 构件字段的纯显示格式化：按快照质量与工程数据类型返回数值或状态说明。
 * 显示占位符不代表 PLC 中存在该数值；不可用数据不应参与操作判断。
 */
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using System.Globalization;

namespace Company.PlcHmi.Main.Presentation;

public static class ComponentValueFormatter
{
    // EngineeringValue 已由 PLC 应用层完成倍率、偏移及布尔反转，展示层不得再次换算。
    public static string Value(PlcVariableSnapshot? value) => value?.IsValid == true
        ? value.EngineeringValue switch
        {
            null => "—", bool flag => flag ? "ON" : "OFF",
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture) ?? "—",
            object other => other.ToString() ?? "—"
        } : "—";
    public static string State(PlcVariableSnapshot? value)
    {
        if (value is null) return "等待采集";
        var text = value.State switch
        {
            PlcValueState.Valid => value.IsRealtime ? "实时有效" : "读取快照",
            PlcValueState.Pending => "等待采集", PlcValueState.Stale => "数据过期",
            PlcValueState.Inactive => "未采集", PlcValueState.Uncertain => "质量不确定",
            _ => "读取失败"
        };
        if (value.IsValid && value.EngineeringValue is null) text = "无有效值";
        return text + (value.Quality is PlcObservationQuality.Bad or PlcObservationQuality.Uncertain ? " / " + value.Quality : "")
            + (string.IsNullOrWhiteSpace(value.Error) ? "" : " · " + value.Error);
    }
}
