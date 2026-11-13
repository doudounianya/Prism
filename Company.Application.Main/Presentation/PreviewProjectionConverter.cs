/*
 * 文件职责与维护说明：
 * 预览界面的只读投影转换器，根据参数选择字段、统计数量或构造图形尺度。
 * 普通 IO 显示保留原集合引用，以减少状态刷新时重建表格；反向写入明确不支持。
 */
using Company.Application.Main.Models;
using Company.Application.Share.PLC.Monitoring.Models;
using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace Company.Application.Main.Presentation;

/// <summary>只投影页面已有的内存数据；不创建采集需求或查询设备。</summary>
public sealed class PreviewProjectionConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Project(value, parameter as string ?? "", []);
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => Project(values.FirstOrDefault(), parameter as string ?? "", values.Skip(1).ToArray());
    private static object Project(object? source, string mode, object[] arguments)
    {
        if (mode == "barLabelMargin" && source is ProductionTrendPoint point)
            return new System.Windows.Thickness(0, 0, 0, Math.Min(195, point.DayHeight + point.NightHeight + 5));
        var rows = (source as IEnumerable)?.Cast<object>().ToArray() ?? [];
        if (mode == "trendWidth") return Math.Max(1, source is int count ? count : rows.Length) * 65d;
        var fields = rows.OfType<PlcDisplayField>().ToArray();
        if (mode is "activeRecipe" or "revision")
            return fields.FirstOrDefault(f => f.LogicalId.EndsWith(mode == "revision" ? ".ActiveRevision" : ".ActiveRecipe", StringComparison.Ordinal))?.Value ?? "—";
        if (mode == "axisMetrics")
            return fields.Where(f => new[] { ".ActualPosition", ".ActualVelocity", ".ActualTorque", ".TargetPosition" }.Any(s => f.LogicalId.EndsWith(s, StringComparison.Ordinal))).ToArray();
        if (mode == "axisStatus")
            return fields.Where(f => new[] { ".Enabled", ".Homed", ".Fault", ".FaultCode" }.Any(s => f.LogicalId.EndsWith(s, StringComparison.Ordinal))).ToArray();
        if (mode == "cylinderMetrics") return fields;
        if (mode is "cylinderRetract" or "cylinderExtend")
        {
            string direction = mode == "cylinderRetract" ? "缩回" : "伸出";
            return rows.OfType<CylinderDirectionRow>().FirstOrDefault(d => d.Direction == direction)
                ?? new CylinderDirectionRow(direction, null, null, null, null, null);
        }
        if (mode.StartsWith("io", StringComparison.Ordinal))
        {
            var io = rows.OfType<IoSignalItem>().ToArray();
            if (mode == "ioRows")
            {
                string search = arguments.FirstOrDefault() as string ?? "";
                bool invalid = arguments.ElementAtOrDefault(1) is true;
                // 常规监视保持同一集合引用，质量/计数更新不重新创建 DataGrid ItemsSource。
                if (!invalid && search.Length == 0) return source ?? Array.Empty<IoSignalItem>();
                return io.Where(i => (!invalid || i.Value is null) && (search.Length == 0 ||
                    (i.DisplayName + " " + i.LogicalId).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
            }
            int known = io.Count(i => i.Value is not null);
            return mode == "ioOn" ? (known == 0 ? "—" : $"{io.Count(i => i.Value is true)} / {io.Length}") :
                io.Length == 0 ? "未采集" : $"{known} 有效 / {io.Length - known} 未知";
        }
        var alarms = rows.OfType<AlarmRuntimeSnapshot>().ToArray();
        return mode switch
        {
            "alarmActive" => alarms.Length == 0 || alarms.All(a => a.Active is null) ? "—" : alarms.Count(a => a.Active is true).ToString(CultureInfo.InvariantCulture),
            "alarmUnknown" => alarms.Length == 0 ? "—" : alarms.Count(a => a.Active is null).ToString(CultureInfo.InvariantCulture),
            "alarmAck" or "alarmFault" => "—",
            "alarmRows" => alarms.Where(a => a.Active != false && ((arguments.FirstOrDefault() as string ?? "") is not { Length: > 0 } search ||
                (a.Key.DeviceId + " " + a.Key.LogicalId + " " + a.DefinitionText).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray(),
            _ => rows
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
