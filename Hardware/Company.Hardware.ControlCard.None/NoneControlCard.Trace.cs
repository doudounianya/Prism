namespace Company.Hardware.ControlCard.None;

public readonly record struct SimulationPosition(double X, double Y, double Z);

public readonly record struct SimulationPathSlice(int Segment, SimulationPosition Start,
    SimulationPosition End, DateTimeOffset StartedAt, TimeSpan Duration);

public sealed partial class NoneControlCard
{
    private CoordinateId _traceCoordinate;
    private List<SimulationPathSlice>? _trace;

    /// <summary>读取一次仿真反馈，并返回这次步进实际走过的各段，包含同一步内经过的拐点。</summary>
    public bool ReadSimulatedPath(CoordinateId coordinate, out IReadOnlyList<SimulationPathSlice> slices)
    {
        var trace = new List<SimulationPathSlice>();
        var success = Synchronize(() =>
        {
            _traceCoordinate = coordinate;
            _trace = trace;
            try { return RefreshCoordinateState(coordinate); }
            finally { _trace = null; }
        });
        slices = trace;
        return success;
    }
}
