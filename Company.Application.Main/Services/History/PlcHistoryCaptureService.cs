using Company.Application.Main.Services.History.Models;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Database.PLC.Models;
using Company.Core.Ioc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Company.Application.Main.Services.History;
/// <summary>
/// 将报警采集帧转换为可重放的观察事实，先保存本地日志，再由独立写入器更新数据库。
/// 无效采样记录为未知；本服务只描述软件观察，不把断线期间的状态推断为报警解除。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcHistoryCaptureService(PlcPersistenceJournal journal,IPlcBindingConfigProvider configuration):IPlcSampleConsumer
{
    // 采样与缺口可能并发到达；同一门闩保护去重基线、检查点及日志追加的先后关系。
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly Dictionary<PlcVariableKey,(bool? Active,long Epoch,DateTime At,string Fingerprint)> _last=new();
    private readonly Dictionary<string,DateTime> _checkpoint=new(),_gaps=new();
    // unreliable 表示本地可靠性曾中断；恢复时先写缺口，且只在该缺口可靠落盘后标记 recoveryGapDurable。
    private readonly HashSet<string> _unreliable=new(),_recoveryGapDurable=new();
    // 配置派生数据仅在 _gate 内创建、替换和读取；不改写提供器发布的只读配置或采样快照。
    // 一次完整构建后才发布缓存，构建失败不会把半成品留给下一帧；相同版本及文件哈希可跨 BeginRun 复用。
    private sealed record AlarmBindingDescriptor(int? AlarmIndex,string Fingerprint,string? AlarmCode,string? Message,string? Severity,string? Component);
    private sealed record AlarmConfigurationCache(string ConfigurationVersion,
        IReadOnlyDictionary<PlcVariableKey,AlarmBindingDescriptor> Bindings,IReadOnlyDictionary<string,string[]> MonitoredKeys);
    private AlarmConfigurationCache? _alarmConfigurationCache;
    private string _runId="";
    private volatile bool _accepting;
    private string? _error;
    public string? Error=>Volatile.Read(ref _error);
    /// <summary>
    /// 开始新的进程内观察批次，清除上一轮去重信息；已落盘的待提交日志由日志组件继续恢复。
    /// </summary>
    public void BeginRun(){_runId=Guid.NewGuid().ToString();_last.Clear();_checkpoint.Clear();_gaps.Clear();_unreliable.Clear();_recoveryGapDurable.Clear();_error=null;_accepting=true;}
    /// <summary>
    /// 等待当前捕获操作离开临界区后停止接收，避免关闭日志时仍有新的报警事实追加。
    /// </summary>
    public async Task SuspendAsync(){await _gate.WaitAsync().ConfigureAwait(false);try{_accepting=false;}finally{_gate.Release();}}
    // 持久化统一采用 UTC 毫秒精度，避免同一事实在不同精度下形成不一致的边界。
    private static DateTime Utc(DateTimeOffset time){long ticks=time.UtcTicks;return new DateTime(ticks-ticks%TimeSpan.TicksPerMillisecond,DateTimeKind.Utc);}
    private static string? Meta(PlcVariableBinding v,string key)=>v.Metadata.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:null;
    /// <summary>
    /// 在采集门闩内按运行配置标记建立报警索引，避免每帧重扫绑定、解析编号及排序计算指纹。
    /// 监控键保留原映射的枚举顺序及非法报警名称；非法名称仍不产生观察，不能因此缩小受监控集合。
    /// </summary>
    private AlarmConfigurationCache GetAlarmConfigurationCache(PlcBindingConfiguration config,string configurationVersion)
    {
        if(_alarmConfigurationCache is{} cached&&cached.ConfigurationVersion==configurationVersion)return cached;
        // 先形成原有完整映射，保留相同的活动项筛选和重复键检查，再提取不随采样改变的描述信息。
        var map=config.ActiveVariables.Where(v=>v.Category=="报警"&&v.AcquisitionGroup=="BG.Alarm"&&v.Access.HasFlag(PlcAccess.Read))
            .ToDictionary(v=>new PlcVariableKey(v.Protocol,v.DeviceId,v.LogicalId));
        var bindings=new Dictionary<PlcVariableKey,AlarmBindingDescriptor>();
        foreach(var (key,binding) in map){
            var match=Regex.Match(key.LogicalId,@"^Alarm\[(\d+)\]$");
            if(!match.Success||!int.TryParse(match.Groups[1].Value,out int index)){
                // 原采样路径会跳过这些名称；只保留键，不执行原本不会用到的指纹及元数据计算。
                bindings.Add(key,new(null,"",null,null,null,null));continue;
            }
            // 属性名称、排列、地址文本、元数据排序及默认 JSON 序列化均沿用原算法，确保旧日志指纹可比较。
            var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{
                binding.Protocol,binding.DeviceId,binding.LogicalId,Address=binding.Address.ToString(),
                binding.InvertBoolean,binding.DataType,Definition=binding.Metadata.OrderBy(x=>x.Key).ToArray()}))));
            bindings.Add(key,new(index,fingerprint,Meta(binding,"报警编码"),Meta(binding,"报警说明"),
                Meta(binding,"报警等级"),Meta(binding,"所属构件")));
        }
        var monitoredKeys=map.Keys.GroupBy(k=>k.DeviceId).ToDictionary(g=>g.Key,g=>g.Select(k=>k.LogicalId).ToArray());
        return _alarmConfigurationCache=new(configurationVersion,bindings,monitoredKeys);
    }
    /// <summary>
    /// 只接受当前配置版本、设备及连接代次相符的报警帧；有变化或检查点到期时追加整批观察。
    /// </summary>
    public async Task OnFrameAsync(PlcAcquisitionFrame frame,CancellationToken token)
    {
        if(!_accepting||frame.GroupId!="BG.Alarm")return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(!_accepting)return;
            var config=configuration.Current;
            if(config is null||frame.ConfigurationVersion!=config.Version+":"+config.FileSha256)return;
            DateTime at=Utc(frame.CompletedAt);
            if(_gaps.TryGetValue(frame.DeviceId,out var gap)&&at<=gap)return;
            // 逻辑键来自当前启用且可读的报警绑定；只在配置标记改变时重建派生数据。
            var cache=GetAlarmConfigurationCache(config,frame.ConfigurationVersion);
            // 保留观察对应的完整协议键，日志可靠追加后可直接更新基线，无需反查整个报警映射。
            var observations=new List<(PlcVariableKey Key,AlarmObservation Observation)>();bool changed=false;
            foreach(var sample in frame.Samples){
                if(sample.Key.DeviceId!=frame.DeviceId||sample.ConfigurationVersion!=frame.ConfigurationVersion||
                    sample.ConnectionEpoch!=frame.ConnectionEpoch||!cache.Bindings.TryGetValue(sample.Key,out var binding))continue;
                if(binding.AlarmIndex is not{} index)continue;
                var fingerprint=binding.Fingerprint;
                // null 表示没有有效布尔观察；不能用 false 填补，否则会伪造报警下降沿。
                bool? active=sample.IsValid&&sample.EngineeringValue is bool value?value:null;
                bool existed=_last.TryGetValue(sample.Key,out var old);
                if(existed&&(at<old.At||sample.ConnectionEpoch<old.Epoch))continue;
                changed|=!existed||active!=old.Active||sample.ConnectionEpoch!=old.Epoch||fingerprint!=old.Fingerprint;
                observations.Add((sample.Key,new(){DeviceId=frame.DeviceId,AlarmKey=sample.Key.LogicalId,AlarmIndex=index,Active=active,
                    ObservedUtc=at,LastValidUtc=sample.LastValidAt is{} valid?Utc(valid):null,
                    ConnectionEpoch=sample.ConnectionEpoch,Initial=!existed||sample.IsInitialAfterReconnect,
                    BindingFingerprint=fingerprint,AlarmCode=binding.AlarmCode,Message=binding.Message,
                    Severity=binding.Severity,Component=binding.Component}));
            }
            if(observations.Count==0)return;
            bool initial=!_checkpoint.ContainsKey(frame.DeviceId);
            // 未变化的稳定状态仍按检查点周期记录；尚未可靠入队时强制重试，不提前推进内存基线。
            bool due=initial||changed||_unreliable.Contains(frame.DeviceId)||at-_checkpoint[frame.DeviceId]>=TimeSpan.FromMilliseconds(frame.CheckpointIntervalMs??1000);
            if(!due)return;
            var batch=new AlarmObservationBatch{DeviceId=frame.DeviceId,RunId=_runId,ObservedUtc=at,Initial=initial,
                ConfigurationVersion=frame.ConfigurationVersion,Observations=observations.Select(x=>x.Observation).ToArray(),
                MonitoredKeys=cache.MonitoredKeys[frame.DeviceId]};
            // 上次日志失败后，先补记可靠性缺口，再写恢复观察；第二步失败也不会重复写已持久化的恢复缺口。
            if(_unreliable.Contains(frame.DeviceId)&&!_recoveryGapDurable.Contains(frame.DeviceId)){
                await journal.AppendAsync(new(){Kind="Alarm",Alarm=new(){DeviceId=frame.DeviceId,RunId=_runId,
                    ConfigurationVersion=frame.ConfigurationVersion,ObservedUtc=at,IsGap=true,
                    GapStartUtc=_checkpoint.TryGetValue(frame.DeviceId,out var checkpoint)?checkpoint:null,
                    GapReason="LocalJournalFailure"}},token).ConfigureAwait(false);
                _recoveryGapDurable.Add(frame.DeviceId);
            }
            await journal.AppendAsync(new(){Kind="Alarm",Alarm=batch},token).ConfigureAwait(false);
            // 只有 AppendAsync 成功后才接受这批观察为新的去重基线；数据库提交不在采集回调中等待。
            foreach(var (key,sample) in observations){
                _last[key]=(sample.Active,sample.ConnectionEpoch,sample.ObservedUtc,sample.BindingFingerprint);
            }
            _checkpoint[frame.DeviceId]=at;_unreliable.Remove(frame.DeviceId);_recoveryGapDurable.Remove(frame.DeviceId);
            if(_unreliable.Count==0)_error=null;
        }catch(Exception ex){_unreliable.Add(frame.DeviceId);_error="历史观察事实尚未可靠写入本地日志："+ex.GetType().Name;throw;}
        finally{_gate.Release();}
    }
    /// <summary>
    /// 记录监控中断并将当前活动性改为未知；按设备拒绝重复或过时缺口，保留最后有效观察时间。
    /// </summary>
    public async Task OnGapAsync(PlcMonitoringGap gap,CancellationToken token)
    {
        if(!_accepting||gap.GroupId!="BG.Alarm")return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(!_accepting)return;
            var config=configuration.Current;
            if(config is null||gap.ConfigurationVersion!=config.Version+":"+config.FileSha256)return;
            var at=Utc(gap.DetectedAt);
            if(_gaps.TryGetValue(gap.DeviceId,out var prior)&&at<=prior)return;
            await journal.AppendAsync(new(){Kind="Alarm",Alarm=new(){DeviceId=gap.DeviceId,AcquisitionGroup=gap.GroupId,
                RunId=_runId,ConfigurationVersion=gap.ConfigurationVersion,ObservedUtc=at,IsGap=true,
                GapStartUtc=gap.LastValidAt is{} valid?Utc(valid):null,GapReason=gap.Reason}},token).ConfigureAwait(false);
            // 缺口先落盘，再影响内存状态；后续晚到且不晚于缺口的帧不得恢复为有效。
            _gaps[gap.DeviceId]=at;
            foreach(var key in _last.Keys.Where(k=>k.DeviceId==gap.DeviceId).ToArray()){
                var old=_last[key];_last[key]=(null,old.Epoch,old.At,old.Fingerprint);
            }
            _unreliable.Remove(gap.DeviceId);_recoveryGapDurable.Remove(gap.DeviceId);if(_unreliable.Count==0)_error=null;
        }catch(Exception ex){_unreliable.Add(gap.DeviceId);_error="监控缺口尚未可靠写入本地日志："+ex.GetType().Name;throw;}
        finally{_gate.Release();}
    }
}
