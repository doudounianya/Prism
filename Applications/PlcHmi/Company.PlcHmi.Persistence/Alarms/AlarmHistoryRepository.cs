using Company.PlcHmi.Shared.Persistence;
using Company.PlcHmi.Shared.Persistence.Entities;
using Company.PlcHmi.Shared.Persistence.Models;
using SqlSugar;
using Company.PlcHmi.Persistence.Journal;
namespace Company.PlcHmi.Persistence;

public sealed class AlarmHistoryRepository(HistoryTransaction sessions):IAlarmHistoryRepository
{
    public Task<long> GetCommittedSequenceAsync(string streamId,CancellationToken token)=>sessions.GetCommittedAsync(streamId,token);
    public async Task<AlarmHistoryAggregate> LoadAsync(string deviceId,string group,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var db=sessions.Open(token);
        var states=group=="BG.Alarm"?await db.Queryable<PlcAlarmState>().AS("plc_alarm_state").Where(x=>x.DeviceId==deviceId).ToListAsync():[];
        var ids=states.Where(x=>x.OpenEventId!=null).Select(x=>x.OpenEventId!).ToArray();
        var events=ids.Length==0?[]:await db.Queryable<PlcAlarmEvent>().AS("plc_alarm_event").Where(x=>ids.Contains(x.EventId)).ToListAsync();
        var segments=ids.Length==0?[]:await db.Queryable<PlcAlarmSegment>().AS("plc_alarm_segment").Where(x=>ids.Contains(x.EventId)&&x.EndUtc==null).ToListAsync();
        var gaps=await db.Queryable<PlcMonitoringGap>().AS("plc_monitoring_gap")
            .Where(x=>x.DeviceId==deviceId&&x.AcquisitionGroup==group&&x.RecoveredUtc==null).ToListAsync();
        return new(states,events,segments,gaps);
    }
    public async Task ApplyAsync(string streamId,long sequence,AlarmHistoryMutation mutation,CancellationToken token)
    {
        await sessions.ApplyAsync(streamId,sequence,async db=>
        {
            foreach(var e in mutation.Events){
                token.ThrowIfCancellationRequested();
                if(await db.Queryable<PlcAlarmEvent>().AS("plc_alarm_event").Where(x=>x.EventId==e.EventId).AnyAsync())
                    await db.Updateable(e).AS("plc_alarm_event").Where(x=>x.EventId==e.EventId).ExecuteCommandAsync();
                else await db.Insertable(e).AS("plc_alarm_event").ExecuteCommandAsync();
            }
            foreach(var s in mutation.Segments){
                token.ThrowIfCancellationRequested();
                if(await db.Queryable<PlcAlarmSegment>().AS("plc_alarm_segment").Where(x=>x.SegmentId==s.SegmentId).AnyAsync())
                    await db.Updateable(s).AS("plc_alarm_segment").Where(x=>x.SegmentId==s.SegmentId).ExecuteCommandAsync();
                else await db.Insertable(s).AS("plc_alarm_segment").ExecuteCommandAsync();
            }
            foreach(var s in mutation.States){
                token.ThrowIfCancellationRequested();
                if(await db.Queryable<PlcAlarmState>().AS("plc_alarm_state").Where(x=>x.DeviceId==s.DeviceId&&x.AlarmKey==s.AlarmKey).AnyAsync())
                    await db.Updateable(s).AS("plc_alarm_state").Where(x=>x.DeviceId==s.DeviceId&&x.AlarmKey==s.AlarmKey).ExecuteCommandAsync();
                else await db.Insertable(s).AS("plc_alarm_state").ExecuteCommandAsync();
            }
            foreach(var g in mutation.Gaps){
                token.ThrowIfCancellationRequested();
                if(await db.Queryable<PlcMonitoringGap>().AS("plc_monitoring_gap").Where(x=>x.GapId==g.GapId).AnyAsync())
                    await db.Updateable(g).AS("plc_monitoring_gap").Where(x=>x.GapId==g.GapId).ExecuteCommandAsync();
                else await db.Insertable(g).AS("plc_monitoring_gap").ExecuteCommandAsync();
            }
        },token);
    }
    public async Task<AlarmHistoryPage> QueryAsync(AlarmHistoryQuery query,CancellationToken token)
    {
        query.Validate();token.ThrowIfCancellationRequested();using var db=sessions.Open();
        var q=db.Queryable<PlcAlarmEvent>().AS("plc_alarm_event")
            .WhereIF(query.DeviceId!=null,e=>e.DeviceId==query.DeviceId)
            .WhereIF(query.AlarmKey!=null,e=>e.AlarmKey==query.AlarmKey)
            .WhereIF(query.Severity!=null,e=>e.Severity==query.Severity);
        if(query.Keyword is not null)q=q.Where(
            "(LOCATE(@keyword,COALESCE(AlarmCode,''))>0 OR LOCATE(@keyword,COALESCE(Message,''))>0 OR LOCATE(@keyword,COALESCE(Component,''))>0)",
            new[]{new SugarParameter("@keyword",query.Keyword)});
        q=query.Overlap?q.Where(e=>e.StartObservedUtc<query.ToUtc&&(e.EndObservedUtc==null||e.EndObservedUtc>query.FromUtc)):
            q.Where(e=>e.StartObservedUtc>=query.FromUtc&&e.StartObservedUtc<query.ToUtc);
        long count=await q.CountAsync();
        token.ThrowIfCancellationRequested();
        var items=await q.OrderBy(e=>e.StartObservedUtc,OrderByType.Desc).OrderBy(e=>e.EventId)
            .Skip(checked((query.Page-1)*query.PageSize)).Take(query.PageSize).ToListAsync();
        token.ThrowIfCancellationRequested();
        var durations=items.ToDictionary(e=>e.EventId,_=>0m);
        if(items.Count>0){
            var p=items.Select((e,i)=>new SugarParameter("@id"+i,e.EventId)).ToArray();
            string ids=string.Join(",",p.Select(x=>x.ParameterName));
            var rows=await db.Ado.SqlQueryAsync<EventDuration>(
                "SELECT EventId,SUM(GREATEST(0,TIMESTAMPDIFF(MICROSECOND,StartUtc,COALESCE(EndUtc,LastValidUtc)))/1000) ObservedDurationMs "+
                "FROM plc_alarm_segment WHERE EventId IN ("+ids+") GROUP BY EventId",p);
            foreach(var row in rows)durations[row.EventId]=row.ObservedDurationMs;
        }
        token.ThrowIfCancellationRequested();
        return new(count,items){ObservedDurations=durations};
    }
    public async Task<AlarmHistoryDetail?> GetDetailAsync(string eventId,CancellationToken token)
    {
        if(!Guid.TryParse(eventId,out _))throw new ArgumentException("报警事件标识无效。",nameof(eventId));
        token.ThrowIfCancellationRequested();using var db=sessions.Open();
        var e=await db.Queryable<PlcAlarmEvent>().AS("plc_alarm_event").Where(x=>x.EventId==eventId).FirstAsync();
        token.ThrowIfCancellationRequested();if(e is null)return null;
        var segments=await db.Queryable<PlcAlarmSegment>().AS("plc_alarm_segment").Where(x=>x.EventId==eventId)
            .OrderBy(x=>x.StartUtc).OrderBy(x=>x.SegmentId).ToListAsync();
        token.ThrowIfCancellationRequested();
        decimal duration=segments.Sum(s=>Math.Max(0m,((s.EndUtc??s.LastValidUtc).Ticks-s.StartUtc.Ticks)/(decimal)TimeSpan.TicksPerMillisecond));
        return new(e,segments,duration);
    }
    public sealed class EventDuration
    {
        public string EventId {get;set;}="";
        public decimal ObservedDurationMs {get;set;}
    }
    public async Task<AlarmAnalysisResult> AnalyzeAsync(AlarmHistoryQuery query,CancellationToken token)
    {
        query.Validate();token.ThrowIfCancellationRequested();using var db=sessions.Open();
        var p=new[]{new SugarParameter("@from",query.FromUtc),new SugarParameter("@to",query.ToUtc),
            new SugarParameter("@device",query.DeviceId),new SugarParameter("@alarm",query.AlarmKey),
            new SugarParameter("@severity",query.Severity),new SugarParameter("@keyword",query.Keyword)};
        const string filter=" AND (@device IS NULL OR e.DeviceId=@device) AND (@alarm IS NULL OR e.AlarmKey=@alarm) "+
            "AND (@severity IS NULL OR e.Severity=@severity) AND (@keyword IS NULL OR "+
            "LOCATE(@keyword,COALESCE(e.AlarmCode,''))>0 OR LOCATE(@keyword,COALESCE(e.Message,''))>0 OR LOCATE(@keyword,COALESCE(e.Component,''))>0)";
        var counts=await db.Ado.SqlQueryAsync<AlarmAnalysisRow>(
            "SELECT e.DeviceId,e.AlarmKey,COUNT(*) ObservedEvents,"+
            "SUM(CASE WHEN e.StartKind='ObservedRise' THEN 1 ELSE 0 END) SampledRises,"+
            "SUM(CASE WHEN e.StartKind='Discovered' THEN 1 ELSE 0 END) DiscoveredEvents "+
            "FROM plc_alarm_event e WHERE e.StartObservedUtc>=@from AND e.StartObservedUtc<@to"+filter+" GROUP BY e.DeviceId,e.AlarmKey",p);
        token.ThrowIfCancellationRequested();
        var durations=await db.Ado.SqlQueryAsync<AlarmAnalysisRow>(
            "SELECT e.DeviceId,e.AlarmKey,SUM(GREATEST(0,TIMESTAMPDIFF(MICROSECOND,GREATEST(s.StartUtc,@from),"+
            "LEAST(COALESCE(s.EndUtc,s.LastValidUtc),@to)))/1000) ObservedDurationMs "+
            "FROM plc_alarm_segment s JOIN plc_alarm_event e ON e.EventId=s.EventId "+
            "WHERE s.StartUtc<@to AND COALESCE(s.EndUtc,s.LastValidUtc)>@from"+filter+" GROUP BY e.DeviceId,e.AlarmKey",p);
        token.ThrowIfCancellationRequested();
        var uncertain=await db.Ado.SqlQueryAsync<AlarmAnalysisRow>(
            "SELECT e.DeviceId,e.AlarmKey,COUNT(*) UncertainEvents FROM plc_alarm_event e "+
            "WHERE e.StartObservedUtc<@to AND (e.EndObservedUtc IS NULL OR e.EndObservedUtc>@from) "+
            "AND (e.HasGap=1 OR e.StartKind='Discovered' OR e.EndKind='Reconciled')"+filter+" GROUP BY e.DeviceId,e.AlarmKey",p);
        token.ThrowIfCancellationRequested();
        var map=new Dictionary<(string,string),AlarmAnalysisRow>();
        foreach(var row in counts.Concat(durations).Concat(uncertain)){
            var key=(row.DeviceId,row.AlarmKey);
            if(!map.TryGetValue(key,out var target))map[key]=target=new(){DeviceId=row.DeviceId,AlarmKey=row.AlarmKey};
            target.ObservedEvents+=row.ObservedEvents;target.SampledRises+=row.SampledRises;
            target.DiscoveredEvents+=row.DiscoveredEvents;target.ObservedDurationMs+=row.ObservedDurationMs;
            target.UncertainEvents+=row.UncertainEvents;
        }
        var definitions=await db.Ado.SqlQueryAsync<AlarmAnalysisRow>(
            "WITH filtered AS (SELECT e.*,ROW_NUMBER() OVER(PARTITION BY e.DeviceId,e.AlarmKey ORDER BY e.StartObservedUtc DESC,e.EventId) rn "+
            "FROM plc_alarm_event e WHERE e.StartObservedUtc<@to AND (e.EndObservedUtc IS NULL OR e.EndObservedUtc>@from)"+filter+
            "), versions AS (SELECT DeviceId,AlarmKey,COUNT(DISTINCT BindingFingerprint) DefinitionVersions FROM filtered GROUP BY DeviceId,AlarmKey) "+
            "SELECT f.DeviceId,f.AlarmKey,f.Message,f.Severity,f.Component,v.DefinitionVersions FROM filtered f JOIN versions v "+
            "ON f.DeviceId=v.DeviceId AND f.AlarmKey=v.AlarmKey WHERE f.rn=1",p);
        token.ThrowIfCancellationRequested();
        foreach(var definition in definitions)if(map.TryGetValue((definition.DeviceId,definition.AlarmKey),out var target)){
            target.Message=definition.Message;target.Severity=definition.Severity;target.Component=definition.Component;
            target.DefinitionVersions=definition.DefinitionVersions;
        }
        long gaps=await db.Queryable<PlcMonitoringGap>().AS("plc_monitoring_gap")
            .Where(g=>g.AcquisitionGroup=="BG.Alarm"&&g.StartUtc<query.ToUtc&&(g.RecoveredUtc==null||g.RecoveredUtc>query.FromUtc))
            .WhereIF(query.DeviceId!=null,g=>g.DeviceId==query.DeviceId).CountAsync();
        token.ThrowIfCancellationRequested();
        return new(map.Values.OrderByDescending(x=>x.ObservedEvents).ToArray(),gaps);
    }
}
