using Company.FlyScan.Runtime;
using Prism.Commands;
using System.Windows;
using System.Windows.Input;

namespace Company.FlyScan.Shell;

internal enum ProductionPageKind { Today, Daily, Trend }

internal sealed class ProductionFilter
{
    internal ProductionFilter(DateOnly date)
    {
        Date = ToDate = date.ToDateTime(TimeOnly.MinValue);
        FromDate = Date.Value.AddDays(-6);
    }

    public string DeviceId { get; set; } = "IMC30G-E 仿真";
    public string DataSource { get; set; } = "history";
    public string HistoryChoice { get; set; } = "confirmed";
    public bool IsDatabase => true;
    public IReadOnlyList<KeyValuePair<string, string>> Sources { get; } = [new("history", "飞拍本地记录")];
    public IReadOnlyList<KeyValuePair<string, string>> HistoryChoices { get; } = [new("confirmed", "工件完成 · 08:00 / 20:00")];
    public DateTime? Date { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

internal sealed class FlyProductionPage : HistoryPage
{
    private readonly ProductionPageKind _kind;
    private TrendRow? _selected;
    public ProductionFilter Filter { get; }
    public bool IsToday => _kind == ProductionPageKind.Today;
    public string QuantitySource => "飞拍完成件";
    public string QuantityColumn => "完成件 / 件";
    public string CalendarNote => "北京时间当日 08:00 至次日 08:00。工件在两组扫描和下料确认后按工件 ID 计一次，归入确认时所在班次。";
    public string ActionText => IsToday ? "刷新" : "查询";
    public string FirstLabel => "飞拍 · 合计";
    public string SecondLabel => "飞拍 · 白班累计";
    public string ThirdLabel => "飞拍 · 夜班累计";
    public string First { get; private set; } = "—";
    public string Second { get; private set; } = "—";
    public string Third { get; private set; } = "—";
    public string DayHeader => "白班 · 08:00 — 20:00（北京时间）";
    public string NightHeader => "夜班 · 20:00 — 次日 08:00（北京时间）";
    public string DayState => "以确认完成的飞拍工件统计";
    public string NightState => DayState;
    public string DayTotal => $"班次小时合计 {Second} 件";
    public string NightTotal => $"班次小时合计 {Third} 件";
    public string Notice => Status;
    public string Range { get; private set; } = "";
    public string Days => $"{Items.Count(item => item.Total > 0)} / {Items.Count}";
    public string Day => Items.Sum(item => item.Day).ToString("N0");
    public string Night => Items.Sum(item => item.Night).ToString("N0");
    public string Total => Items.Sum(item => item.Total).ToString("N0");
    public string Scale { get; private set; } = "";
    public string Synchronization => "飞拍本地历史";
    public IReadOnlyList<PeriodRow> DayRows { get; private set; } = [];
    public IReadOnlyList<PeriodRow> NightRows { get; private set; } = [];
    public IReadOnlyList<TrendRow> Items { get; private set; } = [];
    public bool CanOpenDaily => SelectedItem is not null;
    public string SelectedDescription => _selected?.Description ?? "请选择生产日。";
    public TrendRow? SelectedItem
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Changed(nameof(SelectedItem), nameof(SelectedDescription), nameof(CanOpenDaily));
        }
    }
    public ICommand OpenDailyCommand { get; }
    public ICommand SelectPointCommand { get; }

    internal FlyProductionPage(FlyScanHistory history, ProductionPageKind kind, Action<DateTime>? openDaily = null) : base(history)
    {
        _kind = kind;
        Filter = new ProductionFilter(FlyProductionCalendar.DayOf(history.Now));
        OpenDailyCommand = new DelegateCommand(() =>
        {
            if (_selected is { } row) openDaily?.Invoke(row.Date);
        });
        SelectPointCommand = new DelegateCommand<TrendRow>(row => SelectedItem = row);
    }

    protected override void Refresh()
    {
        var now = History.Now;
        if (IsToday) Filter.Date = FlyProductionCalendar.DayOf(now).ToDateTime(TimeOnly.MinValue);
        if (_kind == ProductionPageKind.Trend)
        {
            var trend = FlyProductionCalendar.Read(History, ReadDate(Filter.FromDate), ReadDate(Filter.ToDate));
            int max = Math.Max(1, trend.Max(day => day.Total));
            Items = trend.Select(day => new TrendRow(day.Date.ToDateTime(TimeOnly.MinValue), day.DayPieces, day.NightPieces,
                day.DayPieces * 150d / max, day.NightPieces * 150d / max)).ToArray();
            _selected = null;
            Range = $"{trend[0].Date:yyyy-MM-dd} 至 {trend[^1].Date:yyyy-MM-dd}";
            Scale = $"峰值 {trend.Max(day => day.Total):N0} 件";
            Status = $"合计 {trend.Sum(day => day.Total):N0} 件";
            return;
        }

        var date = ReadDate(Filter.Date);
        var data = FlyProductionCalendar.Read(History, date, date)[0];
        DayRows = data.Hours.Where(hour => hour.DayShift).Select(hour => PeriodRow.From(hour, now)).ToArray();
        NightRows = data.Hours.Where(hour => !hour.DayShift).Select(hour => PeriodRow.From(hour, now)).ToArray();
        First = data.Total.ToString("N0");
        Second = data.DayPieces.ToString("N0");
        Third = data.NightPieces.ToString("N0");
        Status = "";
    }

    private static DateOnly ReadDate(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) :
        throw new FormatException("请选择生产日期。");

    internal sealed record PeriodRow(string Period, string Quantity, string State, string Source)
    {
        internal static PeriodRow From(ProductionHour hour, DateTimeOffset now) =>
            new($"{hour.From:MM-dd HH:mm} → {hour.To:MM-dd HH:mm}", hour.From > now ? "/" : hour.Pieces.ToString("N0"),
                hour.From > now ? "待开始" : now < hour.To ? "进行中" : "已结束", "飞拍本地记录");
    }

    internal sealed record TrendRow(DateTime Date, int Day, int Night, double DayHeight, double NightHeight)
    {
        public int Total => Day + Night;
        public string DateText => Date.ToString("MM-dd");
        public string State => "工件确认";
        public bool Value => true;
        public string Description => $"{Date:yyyy-MM-dd} · 白班 {Day:N0} 件 · 夜班 {Night:N0} 件 · 合计 {Total:N0} 件";
        public Thickness LabelMargin => new(0, 0, 0, DayHeight + NightHeight + 5);
    }
}
