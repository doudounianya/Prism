using Company.Hardware.ControlCard;
using Company.Hardware.ControlCard.None;
using System.ComponentModel;

namespace Company.FlyScan.Runtime;

public sealed partial class SimulatedMachine
{
    private static readonly CoordinateId ScanCoordinate = new("Front.Scan");
    private static readonly AxisId[] ScanAxes = [new("Front.X"), new("Front.Y"), new("Front.R1"), new("Front.R2")];
    private readonly SimulationClock _clock = new();
    private object? _scanOwner;

    internal DateTimeOffset ScanTime => _clock.GetUtcNow();
    internal bool CanBeginScan => !_disposed && _scanOwner is null && IsLoggedIn && Mode == 20 && Status is 0 or 20;
    internal bool OwnsScanAxes(object owner) => ReferenceEquals(_scanOwner, owner);

    public ScanPoint CaptureTeachingPoint()
    {
        if (!CanAdjust || !_card.RefreshAxisState(null))
            throw new InvalidOperationException("示教需要手动调试、有效轴反馈及对应角色。");
        var states = GetScanAxes();
        if (states.Any(axis => !axis.Valid || axis.Busy || axis.Paused || axis.Alarm || axis.EmergencyStop))
            throw new InvalidOperationException("请等待 XYR1 与 R2 停稳且反馈正常后再记录。");
        return Position(states);
    }

    internal void AcquireScanAxes(object owner)
    {
        if (!CanBeginScan)
            throw new InvalidOperationException("请登录并切换到自动模式停止或暂停状态。");
        var group = Coordinates.SingleOrDefault(item => item.CoordinateId == ScanCoordinate);
        if (group is not { CoordinateEnabled: true } || !group.Axes.SequenceEqual(ScanAxes.Take(3)) ||
            !Synchronizations.Any(item => item.Enabled && item.MasterAxis == ScanAxes[2] &&
                item.SlaveAxis == ScanAxes[3] && item.Ratio == 1))
            throw new InvalidOperationException("飞拍配置需要 Front.Scan 的 XYR1 和 R1/R2 的 1:1 跟随。");
        if (!_card.RefreshAxisState(null) || GetScanAxes().Any(axis => !axis.Valid || axis.Busy ||
            axis.Paused || axis.Alarm || axis.EmergencyStop))
            throw new InvalidOperationException("飞拍开始前需要轴反馈有效且停稳。");
        foreach (var axis in ScanAxes.Take(3))
            if (!_card.SetAxisEnabled(axis, true, out var message))
                throw new InvalidOperationException(message);

        _clock.Freeze();
        _scanOwner = owner;
    }

    internal void PublishScanRunning(object owner)
    {
        if (OwnsScanAxes(owner)) SetScanStatus(50);
    }

    internal void ReleaseScanAxes(object owner, int status)
    {
        if (!OwnsScanAxes(owner)) return;
        _scanOwner = null;
        _clock.Thaw();
        if (Mode == 20 && Status == 50) SetScanStatus(status);
    }

    internal void AdvanceScanClock(TimeSpan elapsed) => _clock.Advance(elapsed);

    internal bool StartScanPath(IEnumerable<ScanPoint> waypoints, out string message) =>
        _card.MoveLinearPath(ScanCoordinate, waypoints.Select(point => new Position3D
        {
            X = point.X, Y = point.Y, Z = point.R1
        }).ToList(), out message);

    internal ScanMotionFeedback? ReadScanMotion(out IReadOnlyList<SimulationPathSlice> trace)
    {
        if (!_card.ReadSimulatedPath(ScanCoordinate, out trace)) return null;
        var state = _card.CoordinateStates.Single(item => item.CoordinateId == ScanCoordinate);
        var axes = GetScanAxes();
        return state.Valid && !state.Alarm && axes.All(axis => axis.Valid && !axis.Alarm && !axis.EmergencyStop)
            ? new ScanMotionFeedback(Position(axes), axes[3].ActualPosition, axes[2].ActualVelocity,
                axes[3].ActualVelocity, state.Busy, state.Arrived && !state.Enabled && !state.Queued)
            : null;
    }

    internal bool ClearScanPath() =>
        _card.CancelTrajectory(ScanCoordinate, out _) &&
        _card.CoordinateStates.Single(item => item.CoordinateId == ScanCoordinate)
            is { Valid: true, Cleared: true, Busy: false, Queued: false, Enabled: false };

    private AxisState[] GetScanAxes()
    {
        var states = _card.AxisStates;
        return ScanAxes.Select(id => states.Single(axis => axis.Axis == id)).ToArray();
    }

    private static ScanPoint Position(AxisState[] axes) =>
        new(axes[0].ActualPosition, axes[1].ActualPosition, axes[2].ActualPosition);

    private void SetScanStatus(int status)
    {
        _status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    // 手动调试沿用实时时钟。流程占用后只由 Tick 推进时间，UI 读反馈不能自行推进轴。
    private sealed class SimulationClock : TimeProvider
    {
        private DateTimeOffset _time = TimeProvider.System.GetUtcNow();
        private long _timestamp = TimeProvider.System.GetTimestamp();
        private bool _frozen;

        public override DateTimeOffset GetUtcNow() =>
            _frozen ? _time : _time + TimeProvider.System.GetElapsedTime(_timestamp);

        internal void Freeze()
        {
            _time = GetUtcNow();
            _frozen = true;
        }

        internal void Advance(TimeSpan elapsed) => _time += elapsed;

        internal void Thaw()
        {
            _timestamp = TimeProvider.System.GetTimestamp();
            _frozen = false;
        }
    }
}
