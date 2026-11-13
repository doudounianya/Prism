/*
 * 文件职责与维护说明：
 * 趋势图的一天数据，把白班、夜班数量和记录状态转换为图形高度及说明文字。
 * 图形高度是页面尺度，不是另一份产量；缺失值保留为空，不能等同于已采集到零。
 */
using Company.Application.Main.Presentation;
using Company.Database.PLC.Models;
using ReactiveUI;
using System.Collections.ObjectModel;
namespace Company.Application.Main.Models;

public sealed class ProductionTrendPoint(ProductionTrendDay data, double dayHeight, double nightHeight, string source) : ReactiveObject
{
    private ProductionTrendDay _data = data;
    private double _dayHeight = dayHeight, _nightHeight = nightHeight;
    private string _source = source;
    public ProductionTrendDay Data => _data;
    public double DayHeight => _dayHeight;
    public double NightHeight => _nightHeight;
    public string Source => _source;
    public System.Windows.Thickness LabelMargin => new(0, 0, 0, Math.Min(195, DayHeight + NightHeight + 5));
    public DateTime Date => Data.ProductionDate;
    public string DateText => Date.ToString("MM-dd");
    public string Day => ProductionDisplayFormatter.Quantity(Data.DayQuantity);
    public string Night => ProductionDisplayFormatter.Quantity(Data.NightQuantity);
    public string Total => ProductionDisplayFormatter.Quantity(Data.RecordedTotal);
    public string State => Data.Complete ? "已记录完整" : Data.DayStatus + " / " + Data.NightStatus;
    public string Description => Source + " · " + Date.ToString("yyyy-MM-dd") + " · 白班 " + Day + " · 夜班 " + Night + " · " + State +
        (Data.InvalidRecords > 0 ? $" · {Data.InvalidRecords}条异常未计入" : "");

    private void Apply(ProductionTrendPoint next)
    {
        string day = Day, night = Night, total = Total, state = State, description = Description;
        this.RaiseAndSetIfChanged(ref _data, next.Data, nameof(Data));
        this.RaiseAndSetIfChanged(ref _source, next.Source, nameof(Source));
        var labelMargin = LabelMargin;
        this.RaiseAndSetIfChanged(ref _dayHeight, next.DayHeight, nameof(DayHeight));
        this.RaiseAndSetIfChanged(ref _nightHeight, next.NightHeight, nameof(NightHeight));
        if (labelMargin != LabelMargin) this.RaisePropertyChanged(nameof(LabelMargin));
        if (day != Day) this.RaisePropertyChanged(nameof(Day));
        if (night != Night) this.RaisePropertyChanged(nameof(Night));
        if (total != Total) this.RaisePropertyChanged(nameof(Total));
        if (state != State) this.RaisePropertyChanged(nameof(State));
        if (description != Description) this.RaisePropertyChanged(nameof(Description));
    }

    internal static void Synchronize(ObservableCollection<ProductionTrendPoint> rows, IReadOnlyList<ProductionTrendPoint> incoming)
    {
        for (int i = 0; i < incoming.Count; i++)
        {
            var next = incoming[i]; int found = -1;
            for (int j = i; j < rows.Count; j++) if (rows[j].Date == next.Date) { found = j; break; }
            if (found < 0) rows.Insert(i, next);
            else { if (found != i) rows.Move(found, i); rows[i].Apply(next); }
        }
        while (rows.Count > incoming.Count) rows.RemoveAt(rows.Count - 1);
    }
}

