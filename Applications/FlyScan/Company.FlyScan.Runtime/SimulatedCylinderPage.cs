using Company.Hmi.Contracts;

namespace Company.FlyScan.Runtime;

/// <summary>首台飞拍尚无气缸点位映射，共用页显示待配置。</summary>
public sealed class SimulatedCylinderPage : ICylinderParameterPage
{
    public IReadOnlyList<string> Devices { get; } = ["IMC30G-E 仿真"];
    public string SelectedDevice { get => Devices[0]; set { } }
    public IReadOnlyList<object> Components => [];
    public object? SelectedComponent { get => null; set { } }
    public string CurrentState => "待配置";
    public string StateColor => "#8795A4";
    public string OperationMessage => "气缸 IO 点位待配置。";
    public bool CanUseControls => false;
    public bool HasAlarmFeedback => false;
    public object? Retract => null;
    public object? Extend => null;
    public IReadOnlyList<object> Directions => [];
    public void SelectComponent(int offset) { }
    public Task SetViewVisibleAsync(bool visible) => Task.CompletedTask;
    public Task OperateAsync(string action, int? position = null) => Task.CompletedTask;
    public ParameterEntry? BeginEntry(object field) => null;
}
