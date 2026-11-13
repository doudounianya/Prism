using Company.Application.PLC.Acquisition;
using Company.Application.Share.PLC.Capacity.Models;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.Application.Main.Services.PLC;

/// <summary>
/// 单个已结束小时在两次真实读取中取得一致数量的确认事实，保存两次读取的 UTC 边界。
/// </summary>
internal sealed record CapacityConfirmedHour(ProductionCapacityShift Shift, ProductionCapacityHour Hour,
    DateTime BaselineUtc, DateTime ConfirmedUtc);
/// <summary>
/// 同一帧的结构快照与新确认小时结果；重复消费者观察同一帧时复用，避免状态机再次推进。
/// </summary>
internal sealed record CapacityAcquisitionObservation(ProductionCapacitySnapshot Snapshot, IReadOnlyList<CapacityConfirmedHour> Confirmed);

/// <summary>保存已核对的结构与历史小时，仅用本轮真实读取确认结算；不会把旧字段伪装成新采样。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class CapacityAcquisitionState(CapacityBindingResolver resolver, CapacitySnapshotBuilder builder,
    IPlcBindingConfigProvider configuration)
{
    // 每设备隔离配置版本和连接代次；重连或新配置重新建立完整结构与相邻读取基线。
    private sealed class DeviceState(string stamp, long epoch)
    {
        internal readonly string Stamp = stamp;
        internal readonly long Epoch = epoch;
        internal bool FullRequired = true;
        internal ProductionCapacitySnapshot? FullCandidate;
        internal ProductionCapacitySnapshot? Snapshot;
        internal DateTime? ActiveStart;
        internal DateTimeOffset LastFrame, LastGap;
        internal CapacityAcquisitionObservation? Observation;
        // Reads 只记录真实读到该小时数量的时间；Confirmed 记录已输出数量，允许后续不同数量重新确认。
        internal readonly Dictionary<DateTime, (uint Quantity, DateTimeOffset At)> Reads = [];
        internal readonly Dictionary<DateTime, uint> Confirmed = [];
    }
    private sealed record HourLocation(ProductionCapacityShift Shift, ProductionCapacityHour Hour)
    {
        internal string Prefix => CapacityBindingResolver.Hour(Shift.Slot, Hour.Slot);
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    /// <summary>
    /// 清除本次运行状态，在采集选择策略重置时要求重新完整核对。
    /// </summary>
    internal void Reset() { lock (_sync) _devices.Clear(); }

    /// <summary>
    /// 根据结构核对、小时切换及待结算状态选择本来源实际读取字段，并保留同组其他来源的原采集需求。
    /// </summary>
    public IReadOnlyCollection<PlcVariableKey> Select(string device, IReadOnlyCollection<PlcVariableKey> available, DateTimeOffset now)
    {
        var plan = resolver.Resolve(configuration.Current, device, out _);
        if (plan is null) return available;
        lock (_sync)
        {
            var selected = !_devices.TryGetValue(device, out var state) || state.Stamp != plan.Stamp
                ? plan.Fields.Keys : SelectFields(plan, state, now);
            var capacityKeys = plan.Fields.Keys.Select(plan.Key).ToHashSet();
            // 同组其他产能来源仍保持原来的常驻读取，不被本来源的策略接管。
            return selected.Select(plan.Key).Concat(available.Where(k => !capacityKeys.Contains(k))).ToArray();
        }
    }
    // 小时窗口按左闭右开定位；整点时立即属于下一小时，上一小时进入结算候选。
    private static HourLocation? Current(ProductionCapacitySnapshot snapshot, DateTime utc) => snapshot.Shifts
        .SelectMany(s => s.Hours.Select(h => new HourLocation(s, h))).SingleOrDefault(p => p.Hour.StartUtc <= utc && utc < p.Hour.EndUtc);
    // 只挑选已结束且尚未确认当前数量的小时，不能用未来预填值形成确认。
    private static IEnumerable<HourLocation> Unsettled(DeviceState state, DateTime utc) => state.Snapshot!.Shifts
        .SelectMany(s => s.Hours.Select(h => new HourLocation(s, h)))
        .Where(p => p.Hour.EndUtc <= utc && (!state.Confirmed.TryGetValue(p.Hour.StartUtc, out var count) || count != p.Hour.Quantity));
    /// <summary>
    /// 正常时仅取当前小时产量；整点结算补取相关时间/数量，结构变化、过期或时间倒退则恢复全量读取。
    /// </summary>
    private static IReadOnlyCollection<string> SelectFields(CapacityBindingPlan plan, DeviceState state, DateTimeOffset now)
    {
        var snapshot = state.Snapshot;
        if (snapshot is null || state.FullRequired || now - state.LastFrame > TimeSpan.FromMilliseconds(plan.StaleAfterMs) || now < state.LastFrame)
        { state.FullRequired = true; return plan.Fields.Keys.ToArray(); }
        var current = Current(snapshot, now.UtcDateTime);
        var previous = snapshot.Shifts.SelectMany(s => s.Hours.Select(h => new HourLocation(s, h)))
            .SingleOrDefault(h => h.Hour.StartUtc == state.ActiveStart);
        // 只允许同班内相邻一小时的平滑切换；跨班、跳小时或无法定位都需要全量重新确认槽位。
        if (current is null || previous is null || current.Hour.StartUtc != previous.Hour.StartUtc &&
            (current.Shift.StartUtc != previous.Shift.StartUtc || current.Hour.StartUtc - previous.Hour.StartUtc != TimeSpan.FromHours(1)))
        { state.FullRequired = true; return plan.Fields.Keys.ToArray(); }
        var fields = new HashSet<string>(StringComparer.Ordinal) { current.Prefix + ".Quantity" };
        var settling = Unsettled(state, now.UtcDateTime).ToArray();
        if (current.Hour.StartUtc != previous.Hour.StartUtc || settling.Length > 0)
            foreach (var target in settling.Append(current))
            {
                fields.Add(target.Prefix + ".Start"); fields.Add(target.Prefix + ".End"); fields.Add(target.Prefix + ".Quantity");
                string shift = CapacityBindingResolver.Shift(target.Shift.Slot);
                fields.Add(shift + ".Start"); fields.Add(shift + ".End"); fields.Add(shift + ".Quantity");
            }
        return fields;
    }
    /// <summary>
    /// 串行处理真实采集帧，维护全量结构核对与局部更新，并生成满足相邻一致条件的已结束小时。
    /// </summary>
    internal CapacityAcquisitionObservation Observe(CapacityBindingPlan plan, PlcAcquisitionFrame frame)
    {
        lock (_sync)
        {
            if (!_devices.TryGetValue(frame.DeviceId, out var state) || state.Stamp != plan.Stamp || state.Epoch < frame.ConnectionEpoch)
                _devices[frame.DeviceId] = state = new(plan.Stamp, frame.ConnectionEpoch);
            // 连接旧代次、时间倒序或缺口之前的帧无法恢复新状态；同时间同帧直接复用已计算结果。
            if (frame.ConnectionEpoch < state.Epoch || frame.CompletedAt < state.LastFrame || frame.CompletedAt <= state.LastGap)
                return new(state.Snapshot ?? new() { DeviceId = frame.DeviceId, Message = "忽略过期产能采样。" }, []);
            if (frame.CompletedAt == state.LastFrame && state.Observation is { } same) return same;
            var confirmed = new List<CapacityConfirmedHour>();
            try
            {
                if (frame.GroupId != "BG.Production" || frame.ConfigurationVersion != plan.Stamp || frame.ConnectionEpoch < 1 || frame.CompletedAt < frame.StartedAt)
                    throw new ArgumentException("产能采集轮次不匹配。");
                var samples = frame.Samples.ToDictionary(p => p.Key.LogicalId, StringComparer.Ordinal);
                // 即使本轮只读局部字段，要求读取的每个字段也必须质量良好且尝试时刻位于本轮内。
                object Raw(string field)
                {
                    if (!samples.TryGetValue(field, out var value) || value.Key != plan.Key(field) || !value.IsValid ||
                        value.Quality != PlcObservationQuality.Good || value.ConfigurationVersion != plan.Stamp ||
                        value.ConnectionEpoch != frame.ConnectionEpoch || value.RawValue is null ||
                        value.LastAttemptAt is not { } at || at < frame.StartedAt || at > frame.CompletedAt)
                        throw new ArgumentException("产能本轮有效采样不足：" + field);
                    return value.RawValue;
                }
                uint Count(string field) => Raw(field) is uint quantity ? quantity : throw new ArgumentException("产量不是原生UInt32：" + field);
                // 以本帧实际包含字段判断全量；不把状态里保留的旧字段补入本轮采样集合。
                bool full = plan.Fields.Keys.All(samples.ContainsKey);
                ProductionCapacitySnapshot next;
                HourLocation[] readHours;
                if (full)
                {
                    next = builder.Build(plan, frame);
                    if (next.State != ProductionCapacityState.Checking) throw new ArgumentException(next.Message);
                    var prior = state.FullCandidate;
                    // 相邻完整读取只核对缓存时间结构身份且受过期时限约束，不宣称 PLC 提供原子快照。
                    bool ready = prior is not null && prior.Identity == next.Identity &&
                        frame.CompletedAt > prior.ObservedAt && frame.CompletedAt - prior.ObservedAt <= TimeSpan.FromMilliseconds(plan.StaleAfterMs);
                    if (prior?.Identity != next.Identity) state.Reads.Clear();
                    state.FullCandidate = next;
                    // 六班次滚动后移除已不在缓存的小时索引，防止旧槽位的读取/确认身份污染新窗口。
                    var retained = next.Shifts.SelectMany(s => s.Hours).Select(h => h.StartUtc).ToHashSet();
                    foreach (var key in state.Reads.Keys.Where(k => !retained.Contains(k)).ToArray()) state.Reads.Remove(key);
                    foreach (var key in state.Confirmed.Keys.Where(k => !retained.Contains(k)).ToArray()) state.Confirmed.Remove(key);
                    next = next with { State = ready ? ProductionCapacityState.Ready : ProductionCapacityState.Checking,
                        Message = ready ? "六班次缓存核对完成；平时仅轮询当前小时产量，整点确认结算，交班及重连核对缓存。" : "正在核对启动／重连／交班缓存。" };
                    state.FullRequired = !ready;
                    state.ActiveStart = Current(next, frame.CompletedAt.UtcDateTime)?.Hour.StartUtc;
                    readHours = next.Shifts.SelectMany(s => s.Hours.Select(h => new HourLocation(s, h))).ToArray();
                }
                else
                {
                    var required = SelectFields(plan, state, frame.CompletedAt);
                    if (state.FullRequired || state.Snapshot is not { State: ProductionCapacityState.Ready } old)
                        throw new ArgumentException("等待完整缓存核对，不能应用局部产能数据。");
                    foreach (var field in required) _ = Raw(field);
                    // 局部读取若带回时间字段，必须与已核对槽位完全一致；槽位轮转后禁止继续按旧位置更新。
                    foreach (var shift in old.Shifts)
                    {
                        string prefix = CapacityBindingResolver.Shift(shift.Slot);
                        foreach (var (field, expected) in new[] { (prefix + ".Start", shift.StartUtc), (prefix + ".End", shift.EndUtc) })
                            if (samples.ContainsKey(field) && CapacityTimeDecoder.Decode(Raw(field), plan.TimeEncoding, field) != expected)
                                throw new ArgumentException("班次缓存槽位发生变化，需要重新定位。");
                        foreach (var hour in shift.Hours)
                        {
                            string hp = CapacityBindingResolver.Hour(shift.Slot, hour.Slot);
                            foreach (var (field, expected) in new[] { (hp + ".Start", hour.StartUtc), (hp + ".End", hour.EndUtc) })
                                if (samples.ContainsKey(field) && CapacityTimeDecoder.Decode(Raw(field), plan.TimeEncoding, field) != expected)
                                    throw new ArgumentException("小时缓存槽位发生变化，需要重新定位。");
                        }
                    }
                    // 仅覆盖本轮真实包含的数量，未读取字段保留为结构缓存；readHours 稍后只选实际读取小时。
                    var shifts = old.Shifts.Select(s => s with {
                        PlcQuantity = samples.ContainsKey(CapacityBindingResolver.Shift(s.Slot) + ".Quantity") ? Count(CapacityBindingResolver.Shift(s.Slot) + ".Quantity") : s.PlcQuantity,
                        Hours = s.Hours.Select(h => samples.ContainsKey(CapacityBindingResolver.Hour(s.Slot, h.Slot) + ".Quantity") ?
                            h with { Quantity = Count(CapacityBindingResolver.Hour(s.Slot, h.Slot) + ".Quantity") } : h).ToArray()
                    }).ToArray();
                    var active = Current(old, frame.CompletedAt.UtcDateTime)!;
                    var updated = shifts.SelectMany(s => s.Hours).Single(h => h.StartUtc == active.Hour.StartUtc);
                    // 同一当前小时计数下降使局部基线失效，要求完整复核，不把下降直接计算成负产量。
                    if (state.ActiveStart == active.Hour.StartUtc && updated.Quantity < active.Hour.Quantity)
                        throw new ArgumentException("当前小时计数下降，需要重新核对缓存。");
                    next = old with { Shifts = shifts, ObservedAt = frame.CompletedAt };
                    state.ActiveStart = active.Hour.StartUtc;
                    readHours = shifts.SelectMany(s => s.Hours.Select(h => new HourLocation(s, h)))
                        .Where(p => samples.ContainsKey(p.Prefix + ".Quantity")).ToArray();
                }
                foreach (var p in readHours)
                {
                    // 已结束整班若小时总和与 PLC 整班计数不符，本轮不输出该班小时的结算确认。
                    bool mismatch = p.Shift.EndUtc <= frame.CompletedAt.UtcDateTime && p.Shift.Hours.Sum(h => (decimal)h.Quantity) != p.Shift.PlcQuantity;
                    // 整点后留出两次配置周期供PLC结算，再取得两次真实一致的读数；旧缓存不能充当第二次读取。
                    if (next.State == ProductionCapacityState.Ready && !mismatch &&
                        state.Reads.TryGetValue(p.Hour.StartUtc, out var prior) && prior.Quantity == p.Hour.Quantity &&
                        prior.At.UtcDateTime >= p.Hour.EndUtc.AddMilliseconds(2L * plan.IntervalMs) && prior.At < frame.CompletedAt &&
                        frame.CompletedAt - prior.At <= TimeSpan.FromMilliseconds(plan.StaleAfterMs) &&
                        (!state.Confirmed.TryGetValue(p.Hour.StartUtc, out var saved) || saved != p.Hour.Quantity))
                    {
                        // 每个已结束小时只在相同数量尚未确认时输出；后续真实读数改变时可再次走两次确认。
                        confirmed.Add(new(p.Shift, p.Hour, prior.At.UtcDateTime, frame.CompletedAt.UtcDateTime));
                        state.Confirmed[p.Hour.StartUtc] = p.Hour.Quantity;
                    }
                    state.Reads[p.Hour.StartUtc] = (p.Hour.Quantity, frame.CompletedAt);
                }
                state.Snapshot = next;
            }
            // 结构或采样不合要求时保留最后展示快照但标过期，清掉相邻读取候选，下一轮强制全量核对。
            catch (ArgumentException ex)
            {
                state.FullRequired = true; state.FullCandidate = null; state.Reads.Clear();
                state.Snapshot = (state.Snapshot ?? new() { DeviceId = frame.DeviceId }) with {
                    State = ProductionCapacityState.Stale, ConfigurationVersion = plan.Stamp, ConnectionEpoch = frame.ConnectionEpoch,
                    Message = "产能等待重新核对：" + ex.Message };
                confirmed.Clear();
            }
            state.LastFrame = frame.CompletedAt;
            return state.Observation = new(state.Snapshot!, confirmed.AsReadOnly());
        }
    }
    /// <summary>
    /// 对当前配置且不早于最新采样的缺口清除确认候选；恢复后必须重新完成全量结构核对。
    /// </summary>
    internal void Gap(PlcMonitoringGap gap)
    {
        lock (_sync)
            if (_devices.TryGetValue(gap.DeviceId, out var state) && state.Stamp == gap.ConfigurationVersion && gap.DetectedAt >= state.LastFrame && gap.DetectedAt > state.LastGap)
            {
                state.LastGap = gap.DetectedAt; state.FullRequired = true; state.FullCandidate = null; state.Reads.Clear();
            }
    }
}

/// <summary>在业务模块初始化时安装策略；未安装策略的后台组沿用原轮询。</summary>
[ExposedService(Lifetime.Singleton, AutoInitialize = true)]
public sealed class CapacityPollingRegistration : IDisposable
{
    private readonly IDisposable _registration;
    /// <summary>
    /// 注册产能后台组的字段选择及重置回调，将按需轮询策略接入采集调度。
    /// </summary>
    public CapacityPollingRegistration(PlcBackgroundSelection selection, CapacityAcquisitionState state)
        => _registration = selection.Register("BG.Production", state.Select, state.Reset);
    /// <summary>
    /// 移除已注册的字段选择策略，释放注册句柄。
    /// </summary>
    public void Dispose() => _registration.Dispose();
}
