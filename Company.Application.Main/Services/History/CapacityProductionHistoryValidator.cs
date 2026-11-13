using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Company.Application.Main.Services.History.Models;
using Company.Application.Main.Services.PLC;
using Company.Core.Ioc;
using Company.Database.PLC.Entities;
using Company.Database.PLC.Models;

namespace Company.Application.Main.Services.History;

/// <summary>
/// 校验提升机已确认小时批次，并转为 CapacityDay 来源的单计数器历史记录。
/// 只接受北京时间正确归班、已结束的一小时区间，不从缺失小时推算产量。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class CapacityProductionHistoryValidator
{
    /// <summary>
    /// 校验批次身份、UTC 确认先后及小时边界，生成稳定内容摘要并保留确认时间。
    /// </summary>
    public IReadOnlyList<PlcProductionHour> Validate(CapacityProductionHistoryBatch batch)
    {
        if (string.IsNullOrWhiteSpace(batch.DeviceId) || batch.DeviceId.Length > 64 ||
            !Regex.IsMatch(batch.BindingFingerprint, "^[A-F0-9]{64}$") || string.IsNullOrWhiteSpace(batch.ConfigurationVersion) ||
            batch.ConnectionEpoch < 1 || batch.BaselineUtc.Kind != DateTimeKind.Utc || batch.ConfirmedUtc.Kind != DateTimeKind.Utc ||
            batch.BaselineUtc >= batch.ConfirmedUtc || batch.ConfirmedUtc > DateTime.UtcNow.AddSeconds(1) ||
            batch.Periods.Count > 72 || batch.DeferredHours < 0)
            throw new ArgumentException("产能观察批次标识、时区或相邻确认时间无效。");
        var seen = new HashSet<DateTime>(); var records = new List<PlcProductionHour>();
        foreach (var p in batch.Periods)
        {
            // 提升机使用固定北京时间 08:00/20:00 日历，与旧来源的本机 08:30 日历区分。
            var window = ProductionCapacityCalendar.Window(p.ProductionDate);
            bool day = p.ShiftCode == "Day" && p.StartUtc >= window.StartUtc && p.EndUtc <= window.NightStartUtc;
            bool night = p.ShiftCode == "Night" && p.StartUtc >= window.NightStartUtc && p.EndUtc <= window.EndUtc;
            if (p.ProductionDate.TimeOfDay != TimeSpan.Zero || p.ProductionDate.Year is < 2000 or > 2098 ||
                p.StartUtc.Kind != DateTimeKind.Utc || p.EndUtc.Kind != DateTimeKind.Utc ||
                p.EndUtc - p.StartUtc != TimeSpan.FromHours(1) || p.StartUtc.Ticks % TimeSpan.TicksPerHour != 0 ||
                p.EndUtc > batch.BaselineUtc || !(day || night) || !seen.Add(p.StartUtc))
                throw new ArgumentException("仅接收北京时间归班正确、已结束且不重复的一小时区间。");
            // SourceEpoch 使用绑定指纹；同一时间窗的不同值由内容摘要区分，原始 uint 产量不做差分。
            var canonical = FormattableString.Invariant($"{p.EndUtc.Ticks}|{p.ProductionDate:yyyy-MM-dd}|{p.ShiftCode}|{p.Quantity}");
            records.Add(new() { DeviceId = batch.DeviceId, ProductionDate = p.ProductionDate, ShiftCode = p.ShiftCode,
                PeriodStartUtc = p.StartUtc, PeriodEndUtc = p.EndUtc, CounterIndex = 0, Quantity = p.Quantity,
                SourceId = ProductionSourcePolicy.CapacityDay, SourceEpoch = batch.BindingFingerprint, SourceRevision = 0,
                ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
                FirstSyncedUtc = ProductionSourcePolicy.Milliseconds(batch.ConfirmedUtc), LastSyncedUtc = ProductionSourcePolicy.Milliseconds(batch.ConfirmedUtc) });
        }
        return records.AsReadOnly();
    }
}
