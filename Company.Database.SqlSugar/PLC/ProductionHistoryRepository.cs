using Company.Database.PLC;
using Company.Database.PLC.Entities;
using Company.Database.PLC.Models;
namespace Company.Database.SqlSugar.PLC;

public sealed class ProductionHistoryRepository(PlcHistoryDbSessionFactory sessions):IProductionHistoryRepository
{
    public async Task<ProductionApplyResult> ApplyAsync(string streamId,long sequence,IReadOnlyList<PlcProductionHour> records,
        PlcProductionSyncState state,CancellationToken token)
    {
        int added=0,updated=0,unchanged=0,rejected=state.Rejected;
        bool duplicate=await sessions.ApplyAsync(streamId,sequence,async db=>
        {
            if(ProductionSourcePolicy.IsCapacity(state.SourceId)){
                (added,updated,unchanged,rejected)=await ApplyCapacity(db,records,state,token).ConfigureAwait(false);
                return;
            }
            foreach(var e in records){
                token.ThrowIfCancellationRequested();
                var old=await db.Queryable<PlcProductionHour>().AS("plc_production_hour")
                    .Where(x=>x.DeviceId==e.DeviceId&&x.PeriodStartUtc==e.PeriodStartUtc&&x.CounterIndex==e.CounterIndex).FirstAsync();
                if(old is null){await db.Insertable(e).AS("plc_production_hour").IgnoreColumns(x=>x.Id).ExecuteCommandAsync();added++;continue;}
                if(old.SourceId!=e.SourceId||old.SourceEpoch!=e.SourceEpoch||e.SourceRevision<old.SourceRevision||
                    (e.SourceRevision==old.SourceRevision&&e.ContentHash!=old.ContentHash)){rejected++;continue;}
                if(e.ContentHash==old.ContentHash&&e.SourceRevision==old.SourceRevision){unchanged++;continue;}
                var next=e with{Id=old.Id,FirstSyncedUtc=old.FirstSyncedUtc};
                await db.Updateable(next).AS("plc_production_hour")
                    .Where(x=>x.DeviceId==e.DeviceId&&x.PeriodStartUtc==e.PeriodStartUtc&&x.CounterIndex==e.CounterIndex).ExecuteCommandAsync();updated++;
            }
            var nextState=state with{Added=added,Updated=updated,Unchanged=unchanged,Rejected=rejected,
                Status=rejected>0?"Partial":"Succeeded",LastSuccessUtc=rejected==0?state.LastAttemptUtc:null,
                Error=rejected>0?"存在无效记录、旧版本或来源/同版本内容冲突；已有记录未删除。":null};
            var prior=await db.Queryable<PlcProductionSyncState>().AS("plc_production_sync_state")
                .Where(x=>x.DeviceId==state.DeviceId&&x.SourceId==state.SourceId).FirstAsync();
            if(rejected==0&&prior?.LastSuccessUtc is not null&&prior.AvailableToUtc is{} oldEnd&&
                state.AvailableFromUtc is{} newStart&&newStart>oldEnd){
                await db.Insertable(new PlcMonitoringGap{GapId=Guid.NewGuid().ToString(),DeviceId=state.DeviceId,
                    AcquisitionGroup="History.Production",StartUtc=oldEnd,DetectedUtc=state.LastAttemptUtc,
                    RecoveredUtc=newStart,Reason="PLCWindowGapNeedsReview",ConfigurationVersion=""})
                    .AS("plc_monitoring_gap").ExecuteCommandAsync();
            }
            if(nextState.LastSuccessUtc is null)nextState=nextState with{LastSuccessUtc=prior?.LastSuccessUtc,
                AvailableFromUtc=prior?.AvailableFromUtc,AvailableToUtc=prior?.AvailableToUtc};
            if(prior is null)await db.Insertable(nextState).AS("plc_production_sync_state").ExecuteCommandAsync();
            else await db.Updateable(nextState).AS("plc_production_sync_state")
                .Where(x=>x.DeviceId==state.DeviceId&&x.SourceId==state.SourceId).ExecuteCommandAsync();
            // 只在完整同步成功后关闭小时历史来源的缺口。
            if(rejected==0)await db.Updateable<PlcMonitoringGap>().AS("plc_monitoring_gap")
                .SetColumns(x=>x.RecoveredUtc==state.LastAttemptUtc)
                .Where(x=>x.DeviceId==state.DeviceId&&x.AcquisitionGroup=="History.Production"&&x.RecoveredUtc==null)
                .ExecuteCommandAsync();
        },token);
        return new(duplicate,added,updated,unchanged,rejected,rejected>0?"部分记录未采纳，需核对。":null);
    }

    private static async Task<(int Added,int Updated,int Unchanged,int Rejected)> ApplyCapacity(global::SqlSugar.ISqlSugarClient db,
        IReadOnlyList<PlcProductionHour> records,PlcProductionSyncState state,CancellationToken token)
    {
        if(records.Count is <1 or >72 || records.Any(r=>r.DeviceId!=state.DeviceId||r.SourceId!=state.SourceId||r.CounterIndex!=0||
            r.SourceEpoch.Length!=64||r.LastSyncedUtc!=state.LastAttemptUtc)||records.Select(r=>r.PeriodStartUtc).Distinct().Count()!=records.Count)
            throw new ArgumentException("单项产能批次无效。");
        // 与日志流检查点在同一事务；来源状态行锁串行化不同日志流的同一设备写入。
        await db.Ado.ExecuteCommandAsync(@"INSERT IGNORE INTO plc_production_sync_state
            (DeviceId,SourceId,LastAttemptUtc,Added,Updated,Unchanged,Rejected,Status)
            VALUES (@device,@source,'2000-01-01',0,0,0,0,'Waiting')",
            new global::SqlSugar.SugarParameter("@device",state.DeviceId),new global::SqlSugar.SugarParameter("@source",state.SourceId));
        var prior=(await db.Ado.SqlQueryAsync<PlcProductionSyncState>(
            "SELECT * FROM plc_production_sync_state WHERE DeviceId=@device AND SourceId=@source FOR UPDATE",
            new global::SqlSugar.SugarParameter("@device",state.DeviceId),new global::SqlSugar.SugarParameter("@source",state.SourceId))).Single();
        if(state.LastAttemptUtc<prior.LastAttemptUtc)return(0,0,0,records.Count); // 旧观察重放不倒退来源水位。
        var starts=records.Select(r=>r.PeriodStartUtc).ToArray();
        var existing=await db.Queryable<PlcProductionHour>().AS("plc_production_hour")
            .Where(r=>r.DeviceId==state.DeviceId&&r.CounterIndex==0&&starts.Contains(r.PeriodStartUtc)).ToListAsync();
        var map=existing.ToDictionary(r=>r.PeriodStartUtc);
        bool conflict=records.Any(r=>map.TryGetValue(r.PeriodStartUtc,out var old)&&
            (old.SourceId!=r.SourceId||old.SourceEpoch!=r.SourceEpoch||r.LastSyncedUtc<old.LastSyncedUtc||
             r.LastSyncedUtc==old.LastSyncedUtc&&r.ContentHash!=old.ContentHash));
        bool reset=existing.Any(r=>r.Quantity>0)&&records.All(r=>r.Quantity==0);
        int added=0,updated=0,unchanged=0,rejected=conflict||reset?records.Count:0;
        if(rejected==0)foreach(var row in records){
            token.ThrowIfCancellationRequested();
            if(!map.TryGetValue(row.PeriodStartUtc,out var old)){
                await db.Insertable(row with{SourceRevision=1}).AS("plc_production_hour").IgnoreColumns(r=>r.Id).ExecuteCommandAsync();added++;
            }else if(row.ContentHash==old.ContentHash){unchanged++;}
            else{
                var next=row with{Id=old.Id,FirstSyncedUtc=old.FirstSyncedUtc,SourceRevision=checked(old.SourceRevision+1)};
                await db.Updateable(next).AS("plc_production_hour").Where(r=>r.Id==old.Id).ExecuteCommandAsync();updated++;
            }
        }
        if(rejected==0&&prior.LastSuccessUtc is not null&&state.AvailableFromUtc is{} first){
            async Task Gap(DateTime start,DateTime end){
                if(start>=end)return;
                if(await db.Queryable<PlcMonitoringGap>().AS("plc_monitoring_gap").Where(g=>g.DeviceId==state.DeviceId&&
                    g.AcquisitionGroup=="History.Production"&&g.StartUtc==start&&g.RecoveredUtc==end&&g.Reason=="PLCWindowGapNeedsReview").AnyAsync())return;
                await db.Insertable(new PlcMonitoringGap{GapId=Guid.NewGuid().ToString(),DeviceId=state.DeviceId,AcquisitionGroup="History.Production",
                    StartUtc=start,RecoveredUtc=end,DetectedUtc=state.LastAttemptUtc,Reason="PLCWindowGapNeedsReview",ConfigurationVersion=ProductionSourcePolicy.CapacityGapPrefix+records[0].SourceEpoch})
                    .AS("plc_monitoring_gap").ExecuteCommandAsync();
            }
            if(prior.AvailableToUtc is{} oldEnd&&first>oldEnd)await Gap(oldEnd,first);
            if(prior.AvailableFromUtc is{} oldStart&&prior.AvailableToUtc is{} oldTo&&first>oldStart){
                var end=first<oldTo?first:oldTo;
                var saved=await db.Queryable<PlcProductionHour>().AS("plc_production_hour").Where(r=>r.DeviceId==state.DeviceId&&
                    r.SourceId==state.SourceId&&r.CounterIndex==0&&r.PeriodStartUtc>=oldStart&&r.PeriodStartUtc<end).ToListAsync();
                var known=saved.Select(r=>r.PeriodStartUtc).ToHashSet();DateTime? missing=null;
                for(var at=oldStart;at<end;at=at.AddHours(1)){
                    if(!known.Contains(at))missing??=at;
                    else if(missing is{} gap){await Gap(gap,at);missing=null;}
                }
                if(missing is{} tail)await Gap(tail,end);
            }
        }
        var nextState=state with{Added=added,Updated=updated,Unchanged=unchanged,Rejected=rejected,
            Status=rejected==0?"Succeeded":"Partial",LastSuccessUtc=rejected==0?state.LastAttemptUtc:prior.LastSuccessUtc,
            AvailableFromUtc=rejected==0?state.AvailableFromUtc:prior.AvailableFromUtc,AvailableToUtc=rejected==0?state.AvailableToUtc:prior.AvailableToUtc,
            Error=reset?"疑似PLC缓存清零；已保留原产量，等待核对。":conflict?"来源、时间或同一观察内容冲突；原记录未覆盖。":null};
        await db.Updateable(nextState).AS("plc_production_sync_state").Where(s=>s.DeviceId==state.DeviceId&&s.SourceId==state.SourceId).ExecuteCommandAsync();
        return(added,updated,unchanged,rejected);
    }
}
