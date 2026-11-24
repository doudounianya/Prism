/*
 * 文件职责与维护说明：
 * 轴、气缸及工艺页面共用的单字段显示模型，关联 Excel 绑定、工程值、质量、时间和编辑草稿。
 * 按钮写入成功后的临时显示与公共采样缓存分开，后续真实读取可接管显示。
 */
using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using ReactiveUI;

namespace Company.PlcHmi.Main.Models;

public sealed class PlcDisplayField(PlcVariableBinding binding, string unavailableReason) : ReactiveObject
{
    public PlcVariableBinding Binding { get; } = binding;
    public PlcParameterEdit Edit { get; } = new(binding);
    public string LogicalId => Binding.LogicalId;
    public string Name => string.IsNullOrWhiteSpace(Binding.DisplayName) ? LogicalId : Binding.DisplayName;
    public string Unit => Binding.Unit;
    public string UnavailableReason { get; } = unavailableReason;
    public bool CanRead => UnavailableReason.Length == 0;
    // 绑定在本字段实例存续期间不可变；配置切换会重建字段，因此变量键无需每次访问重新分配。
    public PlcVariableKey Key { get; } = new(binding.Protocol, binding.DeviceId, binding.LogicalId);
    private string _value = "—", _state = unavailableReason.Length == 0 ? "等待采集" : unavailableReason, _time = "—";
    public string Value { get => _value; private set => this.RaiseAndSetIfChanged(ref _value, value); }
    public string State { get => _state; private set => this.RaiseAndSetIfChanged(ref _state, value); }
    public string Time { get => _time; private set => this.RaiseAndSetIfChanged(ref _time, value); }
    private PageValueCache? _cache;
    private bool? _booleanValue;
    private (bool Value, long Epoch, string Stamp, DateTimeOffset AcceptedAt)? _acceptedBoolean;
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; internal set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    /// <summary>当前读取或已成功写入的按钮显示值；后续读取优先，坏质量仍显示未知。</summary>
    public bool? BooleanValue { get => _booleanValue; private set => this.RaiseAndSetIfChanged(ref _booleanValue, value); }
    private bool? _displayBooleanValue;
    /// <summary>仅供按钮着色：重新加载同一变量时保留上次颜色，不能作为取反、写入或新鲜度判断依据。</summary>
    public bool? DisplayBooleanValue { get => _displayBooleanValue; private set => this.RaiseAndSetIfChanged(ref _displayBooleanValue, value); }
    public bool HasCurrentValue => _cache?.IsFresh == true;
    public void BeginRefresh(long epoch, string stamp, DateTimeOffset requestedAt)
    {
        _acceptedBoolean = null;
        (_cache ??= new()).Begin(epoch, stamp, requestedAt);
        Apply(null);
    }
    public void MarkCached(string reason = "等待刷新")
    {
        _acceptedBoolean = null;
        (_cache ??= new()).Pause(reason);
        Apply(null);
    }
    // 仅更新按钮显示，不把写入确认伪造成PLC读取样本或放入公共数据缓存。
    // 这是成功写入后的界面临时值，并非 PLC 读取样本，不向公共数据缓存写入。
    internal void ShowAcceptedBoolean(bool value, long epoch, string stamp)
    {
        if (!CanRead || !HasCurrentValue) return;
        _acceptedBoolean = (value, epoch, stamp, DateTimeOffset.UtcNow);
        BooleanValue = value;
        DisplayBooleanValue = value;
        State = "写入成功 · 等待后续读取";
    }
    // 只在仍刷新同一份旧有效快照时维持成功写入值。
    // 时间更新的读取、坏质量或连接变化都会清除临时值，以真实读取为准。
    private bool? DisplayBoolean(PlcVariableSnapshot? snapshot, bool valid)
    {
        if (_acceptedBoolean is { } accepted)
        {
            // UI反复刷新同一份旧快照时保持成功值；新读取、坏质量或连接变化接管显示。
            if (valid && snapshot is not null && snapshot.ConnectionEpoch == accepted.Epoch &&
                snapshot.ConfigurationVersion == accepted.Stamp && snapshot.LastAttemptAt is { } at && at <= accepted.AcceptedAt)
                return accepted.Value;
            _acceptedBoolean = null;
        }
        return valid && snapshot?.EngineeringValue is bool flag ? flag : null;
    }
    // 显示内容可以来自最后有效缓存；BooleanValue 和编辑资格则必须遵守本轮有效性。
    // 这样切回来有旧数据显示，同时不会把旧缓存变成新的操作依据。
    public void Apply(PlcVariableSnapshot? snapshot, string? reason = null)
    {
        if (_cache is not null)
        {
            if (reason is not null) _cache.Pause(reason);
            else _cache.Observe(CanRead ? snapshot : null);
            Edit.Observe(_cache.IsFresh && CanRead ? _cache.Current : null);
            BooleanValue = DisplayBoolean(_cache.Current, CanRead && _cache.IsFresh);
            // 首读、正常离页及范围切换的 Pending/Inactive 都只改变采集资格，不清空已有显示颜色。
            // 真实坏质量、超时及明确失败不走显示缓存；所有写入检查继续使用 BooleanValue/HasCurrentValue。
            DisplayBooleanValue = BooleanValue ?? (CanRead &&
                ((_cache.Current is null && _cache.PendingReason is "等待刷新" or "页面未显示") ||
                    _cache.Current?.State is PlcValueState.Pending or PlcValueState.Inactive) &&
                _cache.LastValid?.EngineeringValue is bool previous ? previous : null);
            Value = CanRead ? ComponentValueFormatter.Value(_cache.LastValid) : "—";
            var status = _cache.Current is null ? _cache.PendingReason : ComponentValueFormatter.State(_cache.Current);
            State = !CanRead ? UnavailableReason : _cache.IsFresh ? status :
                _cache.LastValid is null ? status : "缓存 · " + status;
            if (_acceptedBoolean is not null) State = "写入成功 · 等待后续读取";
            Time = CanRead && _cache.LastValid?.LastValidAt is { } last ? last.ToLocalTime().ToString("HH:mm:ss.fff") : "—";
            return;
        }
        Edit.Observe(CanRead && reason is null ? snapshot : null);
        BooleanValue = DisplayBoolean(snapshot, CanRead && reason is null && snapshot?.IsValid == true);
        DisplayBooleanValue = BooleanValue;
        Value = CanRead && reason is null ? ComponentValueFormatter.Value(snapshot) : "—";
        State = !CanRead ? UnavailableReason : reason ?? ComponentValueFormatter.State(snapshot);
        Time = CanRead && reason is null && snapshot?.LastValidAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss.fff") : "—";
    }
}
