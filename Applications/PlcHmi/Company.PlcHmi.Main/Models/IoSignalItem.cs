/*
 * 文件职责与维护说明：
 * 一个 IO 通道的显示状态，区分有效布尔值、未知值和未配置通道。
 * 历史有效值可以用于展示，但新一轮采集是否已到达仍由页面缓存判断，未知不能强制显示为 OFF。
 */
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using ReactiveUI;
using Company.Hmi.Contracts;

namespace Company.PlcHmi.Main.Models;

public sealed class IoSignalItem : ReactiveObject, IIoSignal
{
    public required string Channel { get; init; }
    public required string LogicalId { get; init; }
    public required string DisplayName { get; init; }
    public PlcVariableKey? Key { get; init; }
    public string BindingDetails { get; init; } = "未配置或未启用";
    private readonly PageValueCache _cache = new();
    private PlcVariableSnapshot? _snapshot => _cache.Current;
    public bool? Value => _snapshot is { IsValid: true, EngineeringValue: bool value } ? value : null;
    // 切组/回页的首读等待仅保留原文字和颜色；Value 仍为未知，统计与质量判断不能借用显示缓存。
    // 离页和范围切换产生的 Inactive 同样属于显示过渡；真实无效或超时结果仍显示未知。
    private bool? DisplayValue => Value ?? (Key is not null &&
        ((_snapshot is null && _cache.PendingReason is "等待刷新" or "页面未显示") ||
            _snapshot?.State is PlcValueState.Pending or PlcValueState.Inactive) &&
        _cache.LastValid?.EngineeringValue is bool cached ? cached : null);
    public string StateText => DisplayValue is true ? "ON" : DisplayValue is false ? "OFF" :
        Key is null ? "未配置 / 禁用" : _snapshot?.State == PlcValueState.Pending ? "等待读取" : "未知";
    // 初次显示尚无历史值、缺少绑定或等待首读时使用中性色，不能把“还没读到”画成报警色。
    // 橙色只表达已收到的无效/不确定/超时样本或明确的申请失败，实时 Value 与质量状态不变。
    private bool HasReadFailure => Key is not null && (_snapshot?.State is PlcValueState.Invalid or PlcValueState.Uncertain or PlcValueState.Stale ||
        (_snapshot is null && _cache.PendingReason is "采集申请失败" or "刷新失败"));
    public IoIndicator Indicator => DisplayValue is true ? IoIndicator.On :
        DisplayValue is null && HasReadFailure ? IoIndicator.ReadFailure : IoIndicator.Neutral;
    public string QualityText => Key is null ? "未配置 / 禁用" : !_cache.IsFresh ?
        (_cache.LastValid is null ? "" : "缓存 · ") + (_snapshot is null ? _cache.PendingReason : $"{_snapshot.State} / {_snapshot.Quality}") : $"{_snapshot!.State} / {_snapshot.Quality}";
    public string UpdatedText => _cache.LastValid?.LastValidAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";
    public string LastValueText => _cache.LastValid?.EngineeringValue?.ToString() ?? "—";
    public string Error => _snapshot?.Error ?? "";
    public void Apply(PlcVariableSnapshot? snapshot)
    {
        // UI 计时器可能反复取到同一份内存样本；相同或内容等值时不重复格式化显示文本。
        // 只与已通过本轮门槛的 Current 比较。Begin/Pause 会先清空 Current，旧样本仍需重新校验。
        if (snapshot == _cache.Current) return;
        var previous = DisplayState();
        _cache.Observe(snapshot);
        NotifyChanges(previous);
    }
    public void BeginRefresh(long epoch, string stamp, DateTimeOffset requestedAt)
    { var previous = DisplayState(); _cache.Begin(epoch, stamp, requestedAt); NotifyChanges(previous); }
    public void MarkCached(string reason = "等待刷新")
    { var previous = DisplayState(); _cache.Pause(reason); NotifyChanges(previous); }
    // 快照时间会随每次成功采样变化，不能据此重发状态、颜色和质量等全部绑定通知。
    // 比较的是最终显示输出；缓存/未知/质量失效仍由原 PageValueCache 判定，不能为省通知跳过观察。
    private DisplaySnapshot DisplayState() => new(Value, StateText, Indicator, QualityText, UpdatedText, LastValueText, Error);
    private readonly record struct DisplaySnapshot(bool? Value, string StateText, IoIndicator Indicator,
        string QualityText, string UpdatedText, string LastValueText, string Error);
    private void NotifyChanges(DisplaySnapshot previous)
    {
        var current = DisplayState();
        if (previous.Value != current.Value) this.RaisePropertyChanged(nameof(Value));
        if (previous.StateText != current.StateText) this.RaisePropertyChanged(nameof(StateText));
        if (previous.Indicator != current.Indicator) this.RaisePropertyChanged(nameof(Indicator));
        if (previous.QualityText != current.QualityText) this.RaisePropertyChanged(nameof(QualityText));
        if (previous.UpdatedText != current.UpdatedText) this.RaisePropertyChanged(nameof(UpdatedText));
        if (previous.LastValueText != current.LastValueText) this.RaisePropertyChanged(nameof(LastValueText));
        if (previous.Error != current.Error) this.RaisePropertyChanged(nameof(Error));
    }
}
