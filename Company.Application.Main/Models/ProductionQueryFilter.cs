/*
 * 文件职责与维护说明：
 * 生产日、日报、趋势共用的查询输入及数据来源选择。
 * PLC 缓存与数据库历史按各自生产日口径生成请求；切换来源时更新默认日期，不能混用不同班次边界。
 */
using Company.Application.Main.Services.History;
using Company.Application.Main.Services.PLC;
using Company.Application.Share.PLC.Capacity.Models;
using Company.Database.PLC.Models;
using ReactiveUI;
namespace Company.Application.Main.Models;

public sealed class ProductionQueryFilter : ReactiveObject
{
    private string _deviceId = "";
    private DateTime? _date = ProductionCapacityCalendar.CurrentProductionDate(DateTime.UtcNow);
    private DateTime? _from = ProductionCapacityCalendar.CurrentProductionDate(DateTime.UtcNow).AddDays(-6), _to = ProductionCapacityCalendar.CurrentProductionDate(DateTime.UtcNow);
    private ProductionCapacityDataSource _dataSource;
    private string _historyChoice = ProductionSourcePolicy.CapacityDay;
    public bool IsDatabase => DataSource == ProductionCapacityDataSource.DatabaseHistory;
    public string? SourceId => !IsDatabase || _historyChoice == ProductionSourcePolicy.CapacityDay ? ProductionSourcePolicy.CapacityDay : null;
    public IReadOnlyList<KeyValuePair<string, string>> HistoryChoices { get; } =
        [new(ProductionSourcePolicy.CapacityDay, "提升机 · 08:00／20:00"), new("Legacy", "原历史 · 08:30／20:30")];
    public string HistoryChoice
    {
        get => _historyChoice;
        set
        {
            if(value != "Legacy" && value != ProductionSourcePolicy.CapacityDay) throw new ArgumentException("历史口径无效。");
            if(_historyChoice == value) return;
            this.RaiseAndSetIfChanged(ref _historyChoice, value); UpdateDefaultDates();
            this.RaisePropertyChanged(nameof(SourceId));
        }
    }
    private bool _dateEdited, _fromEdited, _toEdited;
    public IReadOnlyList<KeyValuePair<ProductionCapacityDataSource, string>> Sources { get; } =
        [new(ProductionCapacityDataSource.PlcCache, "PLC缓存"), new(ProductionCapacityDataSource.DatabaseHistory, "已入库历史")];
    public ProductionCapacityDataSource DataSource
    {
        get => _dataSource;
        set
        {
            if (_dataSource == value) return;
            this.RaiseAndSetIfChanged(ref _dataSource, value);
            UpdateDefaultDates(); this.RaisePropertyChanged(nameof(IsDatabase)); this.RaisePropertyChanged(nameof(SourceId));
        }
    }
    private void UpdateDefaultDates()
    {
        var date = ProductionCapacityCalendar.DateFor(DataSource, DateTime.UtcNow, SourceId);
        if (!_dateEdited) this.RaiseAndSetIfChanged(ref _date, date, nameof(Date));
        if (!_fromEdited) this.RaiseAndSetIfChanged(ref _from, date.AddDays(-6), nameof(FromDate));
        if (!_toEdited) this.RaiseAndSetIfChanged(ref _to, date, nameof(ToDate));
    }
    public string DeviceId { get => _deviceId; set => this.RaiseAndSetIfChanged(ref _deviceId, value); }
    public DateTime? Date { get => _date; set { if (_date != value) _dateEdited = true; this.RaiseAndSetIfChanged(ref _date, value); } }
    public DateTime? FromDate { get => _from; set { if (_from != value) _fromEdited = true; this.RaiseAndSetIfChanged(ref _from, value); } }
    public DateTime? ToDate { get => _to; set { if (_to != value) _toEdited = true; this.RaiseAndSetIfChanged(ref _to, value); } }
    public ProductionDayQuery Day(DateTime? date = null)
    {
        var query = new ProductionDayQuery(DeviceId.Trim(), (date ?? Date ?? throw new ArgumentException("请选择生产日期。")).Date, [0]) { SourceId = SourceId };
        query.Validate(); return query;
    }
    public ProductionTrendQuery Trend()
    {
        var query = new ProductionTrendQuery(DeviceId.Trim(), FromDate?.Date ?? throw new ArgumentException("请选择开始日期。"),
            ToDate?.Date ?? throw new ArgumentException("请选择结束日期。"), 0) { SourceId = SourceId };
        query.Validate(); return query;
    }
    public void Apply(ProductionDayQuery query)
    {
        query.Validate();
        if (!query.CounterIndices.SequenceEqual([0])) throw new ArgumentException("当前只接入提升机单项产能。");
        DeviceId = query.DeviceId; Date = query.ProductionDate; HistoryChoice = ProductionSourcePolicy.IsCapacity(query.SourceId) ? ProductionSourcePolicy.CapacityDay : "Legacy";
    }
}
