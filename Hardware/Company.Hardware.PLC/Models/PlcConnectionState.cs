namespace Company.Hardware.PLC.Models;

public enum PlcConnectionState { Disconnected, Connecting, Connected, Disconnecting, Faulted, Disposed }

public sealed class PlcConnectionStateChangedEventArgs(
    PlcConnectionState previous, PlcConnectionState current, DateTimeOffset changedAt,
    PlcError? error = null) : EventArgs
{
    public PlcConnectionState Previous { get; } = previous;
    public PlcConnectionState Current { get; } = current;
    public DateTimeOffset ChangedAt { get; } = changedAt;
    public PlcError? Error { get; } = error;
}
