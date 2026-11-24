using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Company.PlcHmi.Shared.PLC.Capacity.Models;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.PlcHmi.Main.Services.PLC;

/// <summary>
/// 从同一采集轮次构造六班次结构快照，验证原生类型、十二小时连续性及六班次覆盖。
/// 成功结果仍为 Checking；是否相邻核对通过由产能状态机决定。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class CapacitySnapshotBuilder
{
    /// <summary>
    /// 构建本轮完整快照；预期的数据或数值异常转换为 Invalid 状态和诊断文字。
    /// </summary>
    internal ProductionCapacitySnapshot Build(CapacityBindingPlan plan, PlcAcquisitionFrame frame)
    {
        var initial = new ProductionCapacitySnapshot { DeviceId = plan.DeviceId, ConfigurationVersion = plan.Stamp,
            ConnectionEpoch = frame.ConnectionEpoch, ObservedAt = frame.CompletedAt, State = ProductionCapacityState.Invalid };
        try
        {
            if (frame.DeviceId != plan.DeviceId || frame.GroupId != "BG.Production" || frame.ConfigurationVersion != plan.Stamp ||
                frame.ConnectionEpoch < 1 || frame.CompletedAt < frame.StartedAt) throw new ArgumentException("采集轮次不匹配。");
            var samples = frame.Samples.ToDictionary(s => s.Key);
            // 每个字段必须由本轮真实尝试产生且质量有效；缓存中的上轮值不能充当同轮字段。
            object Raw(string id)
            {
                if (!samples.TryGetValue(plan.Key(id), out var sample) || !sample.IsValid || sample.Quality != PlcObservationQuality.Good ||
                    sample.ConfigurationVersion != plan.Stamp || sample.ConnectionEpoch != frame.ConnectionEpoch || sample.RawValue is null ||
                    sample.LastAttemptAt is not { } attempt || attempt < frame.StartedAt || attempt > frame.CompletedAt)
                    throw new ArgumentException("同轮有效采样不足：" + id);
                return sample.RawValue;
            }
            DateTime Time(string id) => CapacityTimeDecoder.Decode(Raw(id), plan.TimeEncoding, id);
            uint Quantity(string id) => Raw(id) is uint count ? count : throw new ArgumentException("产量必须为原生 UInt32。");
            var shifts = new List<ProductionCapacityShift>(); var identity = new StringBuilder(); var warnings = new List<string>();
            for (int s = 0; s < 6; s++)
            {
                string prefix = CapacityBindingResolver.Shift(s); var start = Time(prefix + ".Start"); var end = Time(prefix + ".End");
                if (Raw(prefix + ".Name") is not string rawName) throw new ArgumentException("班次名称原生类型必须为String。");
                // 班次归属以北京时间窗口为准；已识别名称若与窗口冲突则拒绝，未知名称保留警告。
                var (code, date) = ProductionCapacityCalendar.ClassifyShift(start, end);
                string name = rawName.Trim().ToUpperInvariant();
                string? namedCode = name is "D" or "DAY" or "白班" ? "Day" : name is "N" or "NIGHT" or "夜班" ? "Night" : null;
                if (namedCode is not null && namedCode != code) throw new ArgumentException($"班次槽{s}名称与北京时间窗口冲突。");
                if (namedCode is null)
                {
                    string escaped = JsonSerializer.Serialize(rawName.Length <= 64 ? rawName : rawName[..64] + "…");
                    warnings.Add($"班次槽{s}名称无法识别，原始文本（转义）{escaped}；按有效时间窗口显示{(code == "Day" ? "白班" : "夜班")}。");
                }
                // 按实际开始时间排序校验小时连续性，同时保留原槽位供后续局部轮询定位。
                var hours = Enumerable.Range(0, 12).Select(h => { string p = CapacityBindingResolver.Hour(s, h);
                    return new ProductionCapacityHour(h, Time(p + ".Start"), Time(p + ".End"), Quantity(p + ".Quantity")); }).OrderBy(h => h.StartUtc).ToArray();
                for (int h = 0; h < 12; h++)
                    if (hours[h].StartUtc != start.AddHours(h) || hours[h].EndUtc != start.AddHours(h + 1))
                        throw new ArgumentException("小时区间重复、重叠、不连续或不属于该班次。");
                var count = Quantity(prefix + ".Quantity");
                if (end <= frame.CompletedAt.UtcDateTime && hours.Sum(h => (decimal)h.Quantity) != count)
                    warnings.Add($"{date:yyyy-MM-dd} {(code == "Day" ? "白班" : "夜班")}：PLC班次累计与十二小时之和不一致，待核对。");
                shifts.Add(new(s, code, date, start, end, count, Array.AsReadOnly(hours)));
                // 身份只覆盖槽位及时间结构，不包含产量；相邻结构核对不要求正在增长的当前小时计数相等。
                identity.Append(CultureInfo.InvariantCulture, $"{s}|{code}|{start.Ticks}|{end.Ticks};");
                foreach (var hour in hours) identity.Append(CultureInfo.InvariantCulture, $"{hour.Slot}|{hour.StartUtc.Ticks}|{hour.EndUtc.Ticks};");
            }
            // 六班次必须首尾相接；重复、重叠与缺班都会让整个结构不可用于按小时定位。
            var ordered = shifts.OrderBy(s => s.StartUtc).ToArray();
            if (ordered.Zip(ordered.Skip(1)).Any(p => p.First.EndUtc != p.Second.StartUtc))
                throw new ArgumentException("六个班次重复、重叠或不连续。");
            return initial with { State = ProductionCapacityState.Checking, Message = "等待相邻轮次核对缓存身份；不是PLC原子快照。",
                Identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))),
                Shifts = Array.AsReadOnly(ordered), Warnings = warnings.AsReadOnly() };
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        { return initial with { Message = "产能快照无效：" + ex.Message }; }
    }
}
