using Company.PlcHmi.Shared.PLC.History.Models;
using Company.PlcHmi.Shared.Persistence.Entities;
using Company.Core.Ioc;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Company.PlcHmi.Main.Services.History;
/// <summary>
/// 验证通用小时缓存快照的完整性与版本一致性，将每个有效时段展开为十个计数器记录。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class ProductionHistorySnapshotValidator
{
    /// <summary>
    /// 整体标识或有效时段结构错误时拒绝快照；显式无效时段只累计拒绝数，不以零产量填补。
    /// </summary>
    public IReadOnlyList<PlcProductionHour> Validate(ProductionHistorySnapshot snapshot, out int invalidPeriods)
    {
        if (string.IsNullOrWhiteSpace(snapshot.DeviceId) || snapshot.DeviceId.Length>64 ||
            string.IsNullOrWhiteSpace(snapshot.SourceId) || snapshot.SourceId.Length>64 ||
            string.IsNullOrWhiteSpace(snapshot.SourceEpoch) || snapshot.SourceEpoch.Length>64 ||
            snapshot.CapturedUtc.Kind!=DateTimeKind.Utc || !snapshot.Complete || snapshot.Updating ||
            snapshot.VersionBefore<0 || snapshot.VersionBefore!=snapshot.VersionAfter || snapshot.Periods.Count>72)
            throw new ArgumentException("小时缓存标识、版本或快照完整性无效。");
        var records = new List<PlcProductionHour>(); invalidPeriods=0;
        var seen=new HashSet<DateTime>();
        foreach (var p in snapshot.Periods)
        {
            // 来源明确标为无效的时段不参与产量与重叠判断；调用方可将数量呈现为数据质量信息。
            if (!p.Valid) { invalidPeriods++; continue; }
            if (p.PeriodStartUtc.Kind!=DateTimeKind.Utc || p.PeriodEndUtc.Kind!=DateTimeKind.Utc ||
                p.PeriodEndUtc<=p.PeriodStartUtc || p.PeriodEndUtc-p.PeriodStartUtc>TimeSpan.FromHours(1) ||
                p.PeriodStartUtc.Ticks%TimeSpan.TicksPerMillisecond!=0 || p.PeriodEndUtc.Ticks%TimeSpan.TicksPerMillisecond!=0 ||
                p.PeriodEndUtc>snapshot.CapturedUtc || p.Revision<0 ||
                p.ProductionDate.TimeOfDay!=TimeSpan.Zero || string.IsNullOrWhiteSpace(p.ShiftCode) ||
                p.ShiftCode.Length>16 || p.Quantities.Count!=10 || !seen.Add(p.PeriodStartUtc))
                throw new ArgumentException("有效小时记录时间、班次、版本、10个计数或业务键不完整。");
            // 同一小时为每个计数器生成独立业务记录；内容摘要覆盖截止时间、归班与该计数值。
            for(int n=0;n<10;n++)
            {
                var canonical=string.Join("|",p.PeriodEndUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    p.ProductionDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),p.ShiftCode,
                    p.Quantities[n].ToString(CultureInfo.InvariantCulture));
                records.Add(new(){DeviceId=snapshot.DeviceId,ProductionDate=p.ProductionDate.Date,
                    ShiftCode=p.ShiftCode,PeriodStartUtc=p.PeriodStartUtc,PeriodEndUtc=p.PeriodEndUtc,
                    CounterIndex=n,Quantity=p.Quantities[n],SourceId=snapshot.SourceId,SourceEpoch=snapshot.SourceEpoch,
                    SourceRevision=p.Revision,ContentHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
                    FirstSyncedUtc=snapshot.CapturedUtc,LastSyncedUtc=snapshot.CapturedUtc});
            }
        }
        // 对所有有效时段按开始时间二次检查重叠，避免同一时间范围被累计两次。
        var ordered=snapshot.Periods.Where(p=>p.Valid).OrderBy(p=>p.PeriodStartUtc).ToArray();
        for(int i=1;i<ordered.Length;i++)
            if(ordered[i].PeriodStartUtc<ordered[i-1].PeriodEndUtc)throw new ArgumentException("小时记录时间段重叠。");
        return records.AsReadOnly();
    }
}
