using Company.Application.Main.Services.History.Models;
using Company.Application.Share.PLC.History;
using Company.Database.PLC;
using Company.Database.PLC.Entities;
using Company.Database.PLC.Models;
using Company.Core.Ioc;
using System.Security.Cryptography;
using System.Text;

namespace Company.Application.Main.Services.History;

/// <summary>
/// 提供报警历史只读查询，并将已落盘的观察批次投影为状态、事件、有效片段及监控缺口。
/// 事件边界区分连续观察到的升降沿与恢复后核对，避免把未知时段计入已确认活动时长。
/// </summary>
[ExposedService(Lifetime.Singleton,typeof(IAlarmHistoryQueryService))]
public sealed class AlarmHistoryService(IAlarmHistoryRepository repository,PlcHistoryOptions options):IAlarmHistoryQueryService
{
    public bool PersistenceEnabled=>options.Enabled&&options.AlarmCaptureEnabled;
    /// <summary>
    /// 校验查询条件并读取分页历史；保存开关关闭时仍可查询已有数据。
    /// </summary>
    public Task<AlarmHistoryPage> QueryAsync(AlarmHistoryQuery query,CancellationToken token=default)
    {
        // 只读查询独立于后台保存开关，不创建日志、连接PLC或启动写入器。
        query.Validate();return repository.QueryAsync(query,token);
    }
    /// <summary>
    /// 将已校验的查询交给仓储计算统计结果，不改变报警状态。
    /// </summary>
    public Task<AlarmAnalysisResult> AnalyzeAsync(AlarmHistoryQuery query,CancellationToken token=default)
    {
        query.Validate();return repository.AnalyzeAsync(query,token);
    }
    /// <summary>
    /// 仅接受 GUID 事件标识，读取该事件和相关明细。
    /// </summary>
    public Task<AlarmHistoryDetail?> GetDetailAsync(string eventId,CancellationToken token=default)
    {
        if(!Guid.TryParse(eventId,out _))throw new ArgumentException("报警事件标识无效。",nameof(eventId));
        return repository.GetDetailAsync(eventId,token);
    }
    // 根据日志流、序号及事实用途生成稳定标识；同一项重放时不会随机生成新的事件、片段或缺口。
    private static string Id(string stream,long seq,string suffix)=>new Guid(SHA256.HashData(
        Encoding.UTF8.GetBytes(stream+"|"+seq+"|"+suffix)).AsSpan(0,16)).ToString();

    /// <summary>
    /// 校验并重放一个报警日志项，在内存副本上推进状态后交由仓储提交聚合与日志序号。
    /// </summary>
    public async Task ApplyAsync(PlcHistoryJournalEntry entry,CancellationToken token)
    {
        var b=entry.Alarm??throw new ArgumentException("缺少报警观察事实。");
        if(string.IsNullOrWhiteSpace(b.DeviceId)||b.DeviceId.Length>64||
            b.ObservedUtc.Kind!=DateTimeKind.Utc||b.AcquisitionGroup is not ("BG.Alarm" or "History.Production")||
            b.Observations.Any(o=>o.DeviceId!=b.DeviceId||o.AlarmKey.Length>128||
                string.IsNullOrWhiteSpace(o.AlarmKey)||o.ObservedUtc.Kind!=DateTimeKind.Utc||o.ObservedUtc>b.ObservedUtc||
                o.LastValidUtc>o.ObservedUtc||o.BindingFingerprint.Length!=64)||
            b.Observations.Select(o=>o.AlarmKey).Distinct().Count()!=b.Observations.Count)
            throw new ArgumentException("报警观察事实无效。");
        // 已提交序号直接返回，涵盖数据库提交完成但本地日志检查点尚未推进的重放场景。
        if(await repository.GetCommittedSequenceAsync(entry.StreamId,token).ConfigureAwait(false)>=entry.Sequence)return;
        var aggregate=await repository.LoadAsync(b.DeviceId,b.AcquisitionGroup,token).ConfigureAwait(false);
        // 使用记录副本组装本次聚合；仓储 ApplyAsync 成功前不把计算中的状态当作数据库已提交结果。
        var states=aggregate.States.ToDictionary(s=>s.AlarmKey,s=>s with{});
        var events=aggregate.Events.ToDictionary(e=>e.EventId,e=>e with{});
        var segments=aggregate.Segments.ToDictionary(s=>s.SegmentId,s=>s with{});
        var gaps=aggregate.Gaps.ToDictionary(g=>g.GapId,g=>g with{});
        PlcAlarmEvent? Event(PlcAlarmState state)=>state.OpenEventId is{} id&&events.TryGetValue(id,out var e)?e:null;
        PlcAlarmSegment? Segment(PlcAlarmState state)=>segments.Values.FirstOrDefault(s=>s.EventId==state.OpenEventId&&s.EndUtc is null);
        // 片段只代表可确认的活动区间；防止异常时间倒序导致负时长。
        void CloseSegment(PlcAlarmState state,DateTime end,string reason)
        {
            if(Segment(state) is not{} segment)return;
            segments[segment.SegmentId]=segment with{EndUtc=end<segment.StartUtc?segment.StartUtc:end,CloseReason=reason};
        }
        // 未知不会伪造 false 或结束事件：只关闭最后有效片段，保留事件并标记含缺口。
        void Unknown(PlcAlarmState state,string reason,DateTime at)
        {
            if(state.LastSampleUtc>at||state.LastGapUtc>at)return;
            CloseSegment(state,state.LastValidUtc??at,reason);
            if(Event(state) is{} e)events[e.EventId]=e with{HasGap=true,UpdatedUtc=at};
            states[state.AlarmKey]=state with{CurrentActive=null,State="Unknown",LastGapUtc=at,UpdatedUtc=at};
        }
        // 同一聚合同时只保留一个尚未恢复的缺口，后续恢复条件统一检查全部受监控键。
        void OpenGap(DateTime start,string reason)
        {
            if(gaps.Values.Any(g=>g.RecoveredUtc is null))return;
            var id=Id(entry.StreamId,entry.Sequence,"gap:"+b.AcquisitionGroup);
            gaps[id]=new(){GapId=id,DeviceId=b.DeviceId,AcquisitionGroup=b.AcquisitionGroup,
                StartUtc=start>b.ObservedUtc?b.ObservedUtc:start,DetectedUtc=b.ObservedUtc,
                Reason=reason,ConfigurationVersion=b.ConfigurationVersion};
        }
        if(b.IsGap)
        {
            var last=states.Values.Where(s=>s.LastValidUtc.HasValue).Select(s=>s.LastValidUtc!.Value).DefaultIfEmpty(b.ObservedUtc).Min();
            // 晚到缺口不能把比它更新的有效采样改回未知。
            if(states.Count==0||states.Values.Any(s=>s.LastSampleUtc is null||s.LastSampleUtc<=b.ObservedUtc)){
                OpenGap(b.GapStartUtc??last,b.GapReason??"MonitoringGap");
                foreach(var state in states.Values.ToArray())Unknown(state,b.GapReason??"MonitoringGap",b.ObservedUtc);
            }
        }
        else
        {
            // 新运行首次观察与数据库上次有效时间之间的连续性无法证明，必须先建立重启缺口。
            if(b.Initial&&states.Values.Any(s=>s.LastValidUtc<b.ObservedUtc)){
                var earliest=states.Values.Where(s=>s.LastValidUtc.HasValue).Min(s=>s.LastValidUtc!.Value);
                OpenGap(earliest,"RestartOrFirstObservation");
                foreach(var state in states.Values.ToArray())Unknown(state,"RestartOrFirstObservation",b.ObservedUtc);
            }
            // 当前配置不再监控的键进入 Unconfigured，结束其事件关联而不假设曾观察到解除。
            if(b.Initial&&b.MonitoredKeys.Count>0)
                foreach(var state in states.Values.Where(s=>!b.MonitoredKeys.Contains(s.AlarmKey)).ToArray()){
                    Unknown(state,"BindingRemoved",b.ObservedUtc);
                    if(Event(state) is{} e)events[e.EventId]=e with{Status="BindingRemoved",HasGap=true,UpdatedUtc=b.ObservedUtc};
                    states[state.AlarmKey]=states[state.AlarmKey] with{OpenEventId=null,State="Unconfigured"};
                }
            foreach(var o in b.Observations)
            {
                token.ThrowIfCancellationRequested();
                var old=states.GetValueOrDefault(o.AlarmKey)??new PlcAlarmState{DeviceId=o.DeviceId,AlarmKey=o.AlarmKey};
                if(old.LastSampleUtc>o.ObservedUtc||old.LastGapUtc>o.ObservedUtc||
                    (old.RunId==b.RunId&&o.ConnectionEpoch<old.ConnectionEpoch))continue;
                // 相同逻辑键若地址或定义变化，不能沿用旧事件身份及旧的活动基线。
                bool changedBinding=old.BindingFingerprint.Length>0&&old.BindingFingerprint!=o.BindingFingerprint;
                if(changedBinding){
                    Unknown(old,"BindingChanged",o.ObservedUtc);
                    if(Event(old) is{} previous)events[previous.EventId]=previous with{Status="BindingChanged",HasGap=true,UpdatedUtc=o.ObservedUtc};
                    old=old with{OpenEventId=null,LastKnownActive=null,CurrentActive=null,State="Pending",LastGapUtc=o.ObservedUtc};
                    OpenGap(old.LastValidUtc??o.ObservedUtc,"BindingChanged");
                }
                // 恢复、重连、首次观察及定义变化均使相邻观察失去连续性，影响事件开始/结束来源。
                bool resumed=o.Initial||b.Initial||changedBinding||old.State!="Valid"||old.RunId!=b.RunId||old.ConnectionEpoch!=o.ConnectionEpoch;
                var next=old with{BindingFingerprint=o.BindingFingerprint,RunId=b.RunId,ConnectionEpoch=o.ConnectionEpoch,
                    LastSampleUtc=o.ObservedUtc,UpdatedUtc=o.ObservedUtc};
                // 无效观察只形成未知状态与缺口，不以最后值代替当前事实。
                if(o.Active is null){
                    states[o.AlarmKey]=next;Unknown(next,"InvalidObservation",o.ObservedUtc);
                    OpenGap(old.LastValidUtc??o.ObservedUtc,"InvalidObservation");continue;
                }
                DateTime valid=o.LastValidUtc??o.ObservedUtc;
                if(o.Active.Value){
                    var e=Event(old);
                    // 仅连续有效 false→true 可称为 ObservedRise；首次或恢复发现活动记为 Discovered。
                    if(e is null){
                        string id=Id(entry.StreamId,entry.Sequence,"event:"+o.AlarmKey);
                        e=new(){EventId=id,DeviceId=o.DeviceId,AlarmKey=o.AlarmKey,AlarmIndex=o.AlarmIndex,
                            AlarmCode=o.AlarmCode,Message=o.Message,Severity=o.Severity,Component=o.Component,
                            StartObservedUtc=o.ObservedUtc,StartKind=!resumed&&old.LastKnownActive==false?"ObservedRise":"Discovered",
                            ConfigurationVersion=b.ConfigurationVersion,BindingFingerprint=o.BindingFingerprint,
                            CreatedUtc=o.ObservedUtc,UpdatedUtc=o.ObservedUtc};
                        next=next with{OpenEventId=id};events[id]=e;
                    }
                    else if(resumed){CloseSegment(old,old.LastValidUtc??o.ObservedUtc,"ContinuityUnknown");e=e with{HasGap=true};}
                    var segment=Segment(next);
                    if(segment is null){
                        string id=Id(entry.StreamId,entry.Sequence,"segment:"+o.AlarmKey);
                        segment=new(){SegmentId=id,EventId=e.EventId,StartUtc=o.ObservedUtc,LastValidUtc=valid};
                    }else segment=segment with{LastValidUtc=valid};
                    segments[segment.SegmentId]=segment;
                    events[e.EventId]=e with{LastValidUtc=valid,UpdatedUtc=o.ObservedUtc};
                }
                // 连续观察到 false 才以本次时刻关闭有效片段；恢复后 false 仅核对事件已结束。
                else if(Event(old) is{} e){
                    if(resumed)CloseSegment(old,old.LastValidUtc??o.ObservedUtc,"ContinuityUnknown");
                    else CloseSegment(old,o.ObservedUtc,"ObservedFall");
                    events[e.EventId]=e with{EndObservedUtc=o.ObservedUtc,EndKind=resumed?"Reconciled":"ObservedFall",
                        HasGap=e.HasGap||resumed,Status="Closed",UpdatedUtc=o.ObservedUtc};
                    next=next with{OpenEventId=null};
                }
                next=next with{CurrentActive=o.Active,LastKnownActive=o.Active,State="Valid",LastValidUtc=valid};
                states[o.AlarmKey]=next;
            }
            // 只有本批所有当前受监控键都取得同一观察时刻的有效状态，才关闭尚未恢复的缺口。
            bool allValid=b.MonitoredKeys.Count>0&&b.MonitoredKeys.All(key=>
                states.TryGetValue(key,out var state)&&state.State=="Valid"&&state.LastSampleUtc==b.ObservedUtc);
            if(allValid)foreach(var gap in gaps.Values.Where(g=>g.RecoveredUtc is null&&g.DetectedUtc<=b.ObservedUtc).ToArray())
                gaps[gap.GapId]=gap with{RecoveredUtc=b.ObservedUtc};
        }
        // 聚合状态与流序号一并交给仓储，使重放能依据相同身份判断是否已应用。
        await repository.ApplyAsync(entry.StreamId,entry.Sequence,new(states.Values.ToArray(),events.Values.ToArray(),
            segments.Values.ToArray(),gaps.Values.ToArray()),token).ConfigureAwait(false);
    }
}
