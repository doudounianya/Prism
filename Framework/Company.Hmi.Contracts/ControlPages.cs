using System.ComponentModel;

namespace Company.Hmi.Contracts;

/// <summary>状态图所需的反馈和可用性。状态流转及命令准入由应用决定。</summary>
public interface IModeDiagramFeedback
{
    int? Mode { get; }
    int? Status { get; }
    bool SupportsPersonnelLocks { get; }
    bool? Action(int index);
    bool? Signal(int index);
    bool CanRequest(string action);
}

public interface IModeControlPage : INotifyPropertyChanged
{
    IModeDiagramFeedback Feedback { get; }
    string? ConfirmedDiagram { get; }
    (int Mode, int Status)? ConfirmedState { get; }
    Task SetVisibleAsync(bool visible);
    Task ExecuteAsync(string action);
    Task PressStopAsync();
    Task ReleaseStopAsync();
    Task PressInitializationAsync();
    Task ReleaseInitializationAsync();
    Task ReleaseMomentaryRequestsAsync();
}

public interface IModeRequestRow
{
    string Suffix { get; }
}

/// <summary>产品在打开键盘时存储提交上下文，并在提交时重新确认权限和目标。</summary>
public sealed record ParameterEntry(string Name, string Unit, string Draft, bool IsText,
    Func<string, string?> Validate, Func<string, Task> CommitAsync);

public interface IParameterPage
{
    Task SetViewVisibleAsync(bool visible);
    Task OperateAsync(string action, int? position = null);
    ParameterEntry? BeginEntry(object field);
}

public interface IAxisParameterPage : IParameterPage
{
    bool CanOperate { get; }
    Task MoveAsync(object row);
    Task PressMomentaryAsync(string action);
    Task ReleaseJogAsync();
}

public interface ICylinderParameterPage : IParameterPage
{
    bool HasAlarmFeedback { get; }
    void SelectComponent(int offset);
}
