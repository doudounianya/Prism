using System.Text.RegularExpressions;
using Company.Application.Share.PLC.Configuration;

namespace Company.Application.Main.Services.PLC;

// 时间编码策略由绑定说明显式决定：原生 UTC、声明 UTC、Unix 秒或 PLC 北京时间墙上值。
internal enum CapacityTimeEncoding { NativeUtc, UtcDateTime, UnixSecondsUtc, PlcLocalDateTime }

/// <summary>仅解释已显式确认的产能值；不修改通讯原始值或采样时间戳。</summary>
internal static class CapacityTimeDecoder
{
    /// <summary>
    /// 解析说明中的 TimeEncoding/TimeZone，拒绝重复键与不受支持的类型组合。
    /// </summary>
    internal static CapacityTimeEncoding Policy(PlcVariableBinding binding)
    {
        var tokens = Regex.Matches(binding.Notes, @"(?<![A-Za-z0-9_])(TimeEncoding|TimeZone)=([^;\s]*)")
            .Cast<Match>().GroupBy(m => m.Groups[1].Value, StringComparer.Ordinal).ToArray();
        if (tokens.Any(g => g.Count() != 1)) throw new ArgumentException("产能时间配置键重复：" + binding.LogicalId);
        var settings = tokens.ToDictionary(g => g.Key, g => g.Single().Groups[2].Value, StringComparer.Ordinal);
        settings.TryGetValue("TimeEncoding", out var encoding); settings.TryGetValue("TimeZone", out var zone);
        // 只有显式本地时间策略允许配置时区，且限定为本来源约定的 Asia/Shanghai。
        if (encoding == "PlcLocalDateTime")
        {
            if (binding.DataType != PlcDataType.DateTime || zone != ProductionCapacityCalendar.ZoneId)
                throw new ArgumentException("PLC本地时间必须配置DateTime及TimeZone=Asia/Shanghai：" + binding.LogicalId);
            return CapacityTimeEncoding.PlcLocalDateTime;
        }
        if (zone is not null) throw new ArgumentException("TimeZone仅用于显式PLC本地时间：" + binding.LogicalId);
        return (binding.DataType, encoding) switch
        {
            (PlcDataType.DateTime, null) => CapacityTimeEncoding.NativeUtc,
            (PlcDataType.DateTime, "UtcDateTime") => CapacityTimeEncoding.UtcDateTime,
            (PlcDataType.UInt32, "UnixSecondsUtc") => CapacityTimeEncoding.UnixSecondsUtc,
            _ => throw new ArgumentException("产能时间类型或TimeEncoding无效：" + binding.LogicalId)
        };
    }

    /// <summary>
    /// 按已解析策略转为 UTC；不隐式使用运行电脑本地时区，并拒绝未初始化或越界年份。
    /// </summary>
    internal static DateTime Decode(object value, CapacityTimeEncoding encoding, string id)
    {
        DateTime utc;
        if (encoding == CapacityTimeEncoding.UnixSecondsUtc && value is uint seconds)
            utc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        else if (encoding == CapacityTimeEncoding.PlcLocalDateTime && value is DateTime wallClock && wallClock.Kind != DateTimeKind.Local)
            // 此来源的年月日时分秒是PLC北京时间，即使服务器将Kind标为Utc也不能先调用ToLocalTime。
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), ProductionCapacityCalendar.Zone);
        // 默认原生 DateTime 必须已标为 Utc；仅显式 UtcDateTime 才允许将 Unspecified 按 UTC 解释。
        else if ((encoding is CapacityTimeEncoding.NativeUtc or CapacityTimeEncoding.UtcDateTime) && value is DateTime dt && dt.Kind == DateTimeKind.Utc)
            utc = dt;
        else if (encoding == CapacityTimeEncoding.UtcDateTime && value is DateTime unspecified && unspecified.Kind == DateTimeKind.Unspecified)
            utc = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);
        else throw new ArgumentException("产能时间原生类型或时区未明确：" + id);
        if (utc.Year is < 2000 or > 2098) throw new ArgumentException("产能时间未初始化或超出范围：" + id);
        return utc;
    }
}
