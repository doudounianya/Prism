/*
 * 文件职责与维护说明：
 * 报警排名的显示模型，把已保存历史的分析结果转换为柱形宽度、百分比和文字。
 * 派生显示属性由计算得到，没有独立的 PLC 读写功能；界面绑定这些只读属性时应使用单向模式。
 */
using Company.Application.Main.Presentation;
using Company.Database.PLC.Models;
using ReactiveUI;

namespace Company.Application.Main.Models;

public sealed class AlarmAnalysisItem(AlarmAnalysisRow data,int rank,double percent,string metricText) : ReactiveObject
{
    private AlarmAnalysisRow _data = data with { };
    private int _rank = rank;
    private double _percent = percent;
    private string _metricText = metricText;
    public AlarmAnalysisRow Data => _data;
    public int Rank => _rank;
    public double Percent => _percent;
    public string MetricText => _metricText;
    internal void Apply(AlarmAnalysisItem next)
    {
        var label = Label; var description = Description; var duration = Duration; var note = DefinitionNote;
        this.RaiseAndSetIfChanged(ref _data, next.Data with { }, nameof(Data));
        this.RaiseAndSetIfChanged(ref _rank, next.Rank, nameof(Rank));
        this.RaiseAndSetIfChanged(ref _percent, next.Percent, nameof(Percent));
        this.RaiseAndSetIfChanged(ref _metricText, next.MetricText, nameof(MetricText));
        if (label != Label) this.RaisePropertyChanged(nameof(Label));
        if (description != Description) this.RaisePropertyChanged(nameof(Description));
        if (duration != Duration) this.RaisePropertyChanged(nameof(Duration));
        if (note != DefinitionNote) this.RaisePropertyChanged(nameof(DefinitionNote));
    }
    public string Label=>$"{Data.DeviceId} / {Data.AlarmKey}";
    public string Description=>Data.Message??"未记录历史说明";
    public string Duration=>AlarmDisplayFormatter.Duration(Data.ObservedDurationMs);
    public string DefinitionNote=>Data.DefinitionVersions>1?$"包含 {Data.DefinitionVersions} 个定义版本；当前显示最近匹配历史说明":"最近匹配历史说明";
}
