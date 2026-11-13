/*
 * 文件职责与维护说明：
 * 气缸参数页专用显示组织：从现有缩回/伸出字段组成方向行，并根据命令、完成、报警反馈归纳显示状态。
 * 状态仅供界面说明，不替代 PLC 的动作与安全逻辑。
 */
using Company.Application.Main.Models;
using Company.Application.Main.Services.PLC;
using Company.Application.Share.PLC.Runtime;
using ReactiveUI;
using System.ComponentModel;

namespace Company.Application.Main.ViewModels;

public sealed class CylinderParameterViewModel(ComponentBindingCatalog catalog, ComponentPageSession session, IPlcRuntime runtime, ComponentOperationSession operations, ModeControlViewModel? modeControl = null)
    : ComponentParameterViewModel("气缸", catalog, session, runtime, operations, modeControl)
{
    private IReadOnlyList<CylinderDirectionRow> _directions = [];
    private IReadOnlyList<PlcDisplayField> _observed = [];
    public IReadOnlyList<CylinderDirectionRow> Directions { get => _directions; private set => this.RaiseAndSetIfChanged(ref _directions, value); }
    public CylinderDirectionRow? Retract => Directions.FirstOrDefault(d => d.Direction == "缩回");
    public CylinderDirectionRow? Extend => Directions.FirstOrDefault(d => d.Direction == "伸出");
    public bool HasAlarmFeedback => Directions.Any(d => d.Alarm?.CanRead == true);

    // 状态只根据本页已有方向信号归纳，不猜测 PLC 状态码含义或增加绑定。
    // 此汇总仅用于显示。加载时与方向按钮/指示灯复用同一显示值，不能按 HasCurrentValue 清灰一次。
    // 真正坏质量会让 DisplayBooleanValue 归为未知，不再用历史 Value 字符串冒充当前反馈。
    private static bool? Signal(PlcDisplayField? field) => field?.DisplayBooleanValue;
    public string CurrentState => (Signal(Retract?.Alarm), Signal(Extend?.Alarm), Signal(Retract?.Complete), Signal(Extend?.Complete),
        Signal(Retract?.CommandFeedback), Signal(Extend?.CommandFeedback)) switch
    {
        (true, true, _, _, _, _) => "双向报警",
        (true, _, _, _, _, _) => "缩回报警",
        (_, true, _, _, _, _) => "伸出报警",
        (_, _, true, true, _, _) => "到位信号冲突",
        (_, _, _, _, true, true) => "方向命令冲突",
        (_, _, _, _, true, false) when Signal(Retract?.Complete) != true => "缩回中",
        (_, _, _, _, false, true) when Signal(Extend?.Complete) != true => "伸出中",
        (_, _, true, _, _, _) => "已缩回",
        (_, _, _, true, _, _) => "已伸出",
        (_, _, false, false, false, false) => "未到位",
        _ => "未知"
    };
    public string StateColor => !_observed.Any(f => f.DisplayBooleanValue is not null) ? "#8795A4" :
        CurrentState.Contains("报警") || CurrentState.Contains("冲突") ? "#D84436" :
        CurrentState.EndsWith("中") ? "#E56C22" : CurrentState.StartsWith("已") ? "#159947" : "#8795A4";

    private static readonly string[] Timing = ["Parameters.Retract.ArrivalDelay", "Parameters.Retract.AlarmTimeout", "Parameters.Extend.ArrivalDelay", "Parameters.Extend.AlarmTimeout"];
    private static readonly string[] FeedbackSignals = ["Retract.CommandFeedback", "Retract.Sensor", "Retract.Complete", "Retract.Alarm", "Extend.CommandFeedback", "Extend.Sensor", "Extend.Complete", "Extend.Alarm"];
    protected override bool IsHeader(PlcDisplayField f) => f.LogicalId == SelectedComponent?.Id + ".Retract.Elapsed" || f.LogicalId == SelectedComponent?.Id + ".Extend.Elapsed";
    protected override bool IsPersistentFeedback(PlcDisplayField f) => FeedbackSignals.Any(name => f.LogicalId == SelectedComponent?.Id + "." + name);
    protected override IReadOnlyList<PlcDisplayField> BuildDetails() => Timing.Select(s => Fields.FirstOrDefault(f => f.LogicalId == SelectedComponent?.Id + "." + s)).OfType<PlcDisplayField>().ToArray();
    protected override void BuildRows()
    {
        PlcDisplayField? Field(string suffix) => VisibleFields.FirstOrDefault(f => f.LogicalId == SelectedComponent?.Id + "." + suffix);
        Directions = new[] { ("缩回", "Retract"), ("伸出", "Extend") }.Select(d => new CylinderDirectionRow(d.Item1,
            Field(d.Item2 + ".CommandFeedback"), Field(d.Item2 + ".Sensor"), Field(d.Item2 + ".Complete"),
            Field(d.Item2 + ".Alarm"), Field(d.Item2 + ".Elapsed"),
            Field("Parameters." + d.Item2 + ".ArrivalDelay"), Field("Parameters." + d.Item2 + ".AlarmTimeout"))).ToArray();
        ObserveDirections();
    }
    private void ObserveDirections()
    {
        foreach (var field in _observed) field.PropertyChanged -= SignalChanged;
        _observed = Directions.SelectMany(d => new[] { d.CommandFeedback, d.Complete, d.Alarm }).OfType<PlcDisplayField>().ToArray();
        foreach (var field in _observed) field.PropertyChanged += SignalChanged;
        this.RaisePropertyChanged(nameof(Retract)); this.RaisePropertyChanged(nameof(Extend));
        this.RaisePropertyChanged(nameof(HasAlarmFeedback));
        NotifyState();
    }
    private void SignalChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(PlcDisplayField.DisplayBooleanValue) or nameof(PlcDisplayField.State)) NotifyState(); }
    private void NotifyState() { this.RaisePropertyChanged(nameof(CurrentState)); this.RaisePropertyChanged(nameof(StateColor)); }
    protected override object CaptureRows() => Directions;
    protected override void RestoreRows(object? rows) { Directions = (IReadOnlyList<CylinderDirectionRow>)rows!; ObserveDirections(); }
}
