using Company.FlyScan.Runtime;
using Microsoft.Data.Sqlite;
using Prism.Commands;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

namespace Company.FlyScan.Shell;

internal abstract class HistoryPage : INotifyPropertyChanged
{
    private bool _visible;
    protected readonly FlyScanHistory History;
    protected bool Visible => _visible;
    public string Status { get; protected set; } = "";
    public bool CanQuery => Visible;
    public bool CanCancel => false;
    public ICommand QueryCommand { get; }
    public ICommand CancelCommand { get; } = new DelegateCommand(() => { }, () => false);
    public event PropertyChangedEventHandler? PropertyChanged;

    protected HistoryPage(FlyScanHistory history)
    {
        History = history;
        QueryCommand = new DelegateCommand(Query);
    }

    internal void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (visible) Query();
        else Changed();
    }

    internal void Query()
    {
        if (!Visible) return;
        try { Refresh(); }
        catch (Exception ex) when (ex is SqliteException or ArgumentOutOfRangeException or FormatException or InvalidOperationException)
        {
            Status = ex.Message;
        }
        Changed();
    }

    protected abstract void Refresh();
    protected void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    protected void Changed(params string[] properties)
    {
        foreach (var property in properties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    protected static (DateTimeOffset From, DateTimeOffset To) ParseRange(DateTime? first, string fromTime,
        DateTime? last, string toTime)
    {
        if (first is null || last is null || !TimeOnly.TryParseExact(fromTime, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(toTime, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            throw new FormatException("请选择日期并使用 HH:mm:ss 格式输入时间。");
        var from = new DateTimeOffset(first.Value.Date.Add(start.ToTimeSpan()));
        var to = new DateTimeOffset(last.Value.Date.Add(end.ToTimeSpan()));
        if (to <= from) throw new ArgumentOutOfRangeException(nameof(last), "结束时间须晚于开始时间。");
        return (from, to);
    }
}

internal sealed class AlarmHistoryFilter
{
    internal AlarmHistoryFilter(DateTime now)
    {
        FromDate = now.Date.AddDays(-6);
        ToDate = now.Date.AddDays(1);
    }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string FromTime { get; set; } = "00:00:00";
    public string ToTime { get; set; } = "00:00:00";
    public string Severity { get; set; } = "";
    public string Keyword { get; set; } = "";
    public IReadOnlyList<string> Severities { get; } = ["故障", "急停"];
    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100];
    public int PageSize { get; set; } = 50;
}

internal sealed class FlyAlarmPage : HistoryPage
{
    private readonly bool _live;
    private readonly Action<string>? _openHistory;
    private AlarmRow[] _rows = [];
    private int _page;
    private object? _selected;
    private int _pageSize = 50;
    public AlarmHistoryFilter Filter { get; }
    public IEnumerable<object> Items => _openHistory is null ? _rows.Skip(_page * _pageSize).Take(_pageSize) : RankedRows;
    public IReadOnlyList<AlarmRow> VisibleItems => _rows;
    public IReadOnlyList<AlarmRanking> Ranking { get; private set; } = [];
    public IReadOnlyList<AlarmRanking> RankedRows { get; private set; } = [];
    public IReadOnlyList<string> SortOptions { get; } = ["发生次数", "有效时长"];
    public IReadOnlyList<int> TopOptions { get; } = [5, 10, 20];
    public string SortBy { get; set; } = "发生次数";
    public int TopCount { get; set; } = 5;
    public AlarmTotals Data { get; private set; } = new(0);
    public string Occurrences => Data.ObservedEvents.ToString(CultureInfo.InvariantCulture);
    public string Duration { get; private set; } = "—";
    public string Uncertain { get; private set; } = "—";
    public string Gaps { get; private set; } = "—";
    public string Summary { get; private set; } = "";
    public string RangeText { get; private set; } = "";
    public string Range => RangeText;
    public string ActiveCountText => History.Online ? _rows.Count(row => row.StateText == "活动").ToString() : "—";
    public string FaultCountText => ActiveCountText;
    public string FaultCountNote => "运动卡告警与急停";
    public string UnknownCountText => History.Online ? "0" : "2";
    public bool CanNext => (_page + 1) * _pageSize < _rows.Length;
    public bool CanPrevious => _page > 0;
    public string Paging => $"第 {_page + 1} / {Math.Max(1, (_rows.Length + _pageSize - 1) / _pageSize)} 页 · 共 {_rows.Length} 条";
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand SelectRankingCommand { get; }
    public bool CanOpenHistory => _selected is AlarmRanking;
    public AlarmRow? SelectedRow
    {
        get => _selected as AlarmRow;
        set
        {
            if (Equals(_selected, value)) return;
            _selected = value;
            Changed(nameof(SelectedRow), nameof(DetailTitle), nameof(DetailDefinition), nameof(DetailSummary), nameof(Segments));
        }
    }
    public object? SelectedItem
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value)) return;
            _selected = value;
            Changed(nameof(SelectedItem), nameof(CanOpenHistory));
        }
    }
    public string DetailTitle => SelectedRow?.Alarm ?? "请选择报警记录";
    public string DetailDefinition => SelectedRow?.Description ?? "";
    public string DetailSummary => SelectedRow?.BoundarySource ?? "";
    public IReadOnlyList<string> Segments => SelectedRow?.Segments ?? [];

    internal FlyAlarmPage(FlyScanHistory history, bool live = false, Action<string>? openHistory = null) : base(history)
    {
        _live = live;
        _openHistory = openHistory;
        Filter = new AlarmHistoryFilter(history.Now.LocalDateTime);
        PreviousCommand = new DelegateCommand(() => { if (CanPrevious) { _page--; SelectedRow = null; Changed(); } });
        NextCommand = new DelegateCommand(() => { if (CanNext) { _page++; SelectedRow = null; Changed(); } });
        SelectRankingCommand = new DelegateCommand<object>(item => SelectedItem = item);
        OpenHistoryCommand = new DelegateCommand(() =>
        {
            if (SelectedItem is AlarmRanking selected) _openHistory?.Invoke(selected.Code);
        });
    }

    protected override void Refresh()
    {
        if (_live)
        {
            var now = History.Now;
            var live = History.LiveAlarms(now);
            var active = live.Alarms.ToDictionary(item => item.Code);
            _rows = live.Online
                ? active.Values.Select(item => AlarmRow.From(item, DateTimeOffset.MinValue, now, false)).ToArray()
                : new[] { ("CARD", "运动卡告警", "故障"), ("ESTOP", "急停", "急停") }
                    .Select(item => active.TryGetValue(item.Item1, out var existing)
                        ? AlarmRow.From(existing, DateTimeOffset.MinValue, now, true)
                        : AlarmRow.Unknown(item.Item1, item.Item2, item.Item3)).ToArray();
            if (_selected is AlarmRow previous)
                _selected = _rows.FirstOrDefault(item => item.Alarm == previous.Alarm);
            Summary = live.Online ? "运动卡仿真 · 在线" : "运动卡仿真 · 等待可确认反馈";
            return;
        }
        var range = ParseRange(Filter.FromDate, Filter.FromTime, Filter.ToDate, Filter.ToTime);
        var report = History.Alarms(range.Item1, range.Item2);
        var keyword = Filter.Keyword.Trim();
        var severity = Filter.Severity.Trim();
        _pageSize = Math.Clamp(Filter.PageSize, 1, 100);
        var selected = report.Alarms.Where(item =>
            (severity.Length == 0 || item.Severity == severity) &&
            (keyword.Length == 0 || (item.Code + " " + item.Name).Contains(keyword, StringComparison.OrdinalIgnoreCase))).ToArray();
        _rows = selected.Select(item => AlarmRow.From(item, range.Item1, range.Item2, false)).ToArray();
        _page = 0;
        _selected = null;
        var ranked = selected.GroupBy(item => (item.Code, item.Name))
            .Select(group => new { group.Key, Starts = group.Count(alarm => alarm.Start >= range.Item1),
                Seconds = group.Sum(alarm => alarm.KnownDuration(range.Item1, range.Item2).TotalSeconds) }).ToArray();
        double Metric(int starts, double seconds) => SortBy == "有效时长" ? seconds : starts;
        double max = Math.Max(1, ranked.Select(item => Metric(item.Starts, item.Seconds)).DefaultIfEmpty().Max());
        RankedRows = ranked.OrderByDescending(item => Metric(item.Starts, item.Seconds)).ThenBy(item => item.Key.Code, StringComparer.Ordinal)
            .Select((item, i) => new AlarmRanking(i + 1, item.Key.Code, item.Key.Name, item.Starts, Seconds(item.Seconds),
                SortBy == "有效时长" ? Seconds(item.Seconds) : $"{item.Starts} 次", Metric(item.Starts, item.Seconds) / max * 100)).ToArray();
        Ranking = RankedRows.Take(TopCount).ToArray();
        Data = new AlarmTotals(ranked.Sum(item => item.Starts));
        Duration = Seconds(ranked.Sum(item => item.Seconds));
        Uncertain = selected.Count(item => item.Uncertain).ToString();
        Gaps = report.Gaps.Count.ToString();
        RangeText = $"{range.Item1.LocalDateTime:yyyy-MM-dd HH:mm:ss} ≤ 时间 < {range.Item2.LocalDateTime:yyyy-MM-dd HH:mm:ss}（本地时间）";
        Status = $"查到 {_rows.Length} 条飞拍报警。";
        Summary = $"报警类型 {ranked.Length} · 设备采集缺口 {Gaps}";
    }

    internal static string Seconds(double seconds) => $"{seconds:0.###} s";
    internal sealed record AlarmTotals(int ObservedEvents);
    internal sealed record AlarmKey(string DeviceId, string LogicalId);
    internal sealed record AlarmRanking(int Rank, string Code, string Description, int Occurrences,
        string Duration, string MetricText, double Percent)
    {
        public string Label => "飞拍 · " + Code;
        public string Meaning => Description;
    }
    internal sealed record AlarmRow(string Alarm, string Description, string Severity, string StartText,
        string EndText, string ObservedDuration, string BoundarySource, string StateText, IReadOnlyList<string> Segments)
    {
        public AlarmKey Key => new("IMC30G-E 仿真", Alarm);
        public string DefinitionText => Description;

        internal static AlarmRow Unknown(string code, string name, string severity) =>
            new(code, name, severity, "—", "—", "未知", "反馈未知", "未知", []);

        internal static AlarmRow From(ScanAlarm alarm, DateTimeOffset from, DateTimeOffset to, bool unknown)
        {
            string duration = Seconds(alarm.KnownDuration(from, to).TotalSeconds);
            return new AlarmRow(alarm.Code, alarm.Name, alarm.Severity, Stamp(alarm.Start),
                alarm.End is { } end ? Stamp(end) : "—", alarm.Uncertain ? $"{duration} · 缺口未知" : duration,
                alarm.Uncertain ? "观察边界含未知区间。" : "连续观察边界。",
                unknown ? "未知" : alarm.End is null ? "活动" : "已解除",
                alarm.KnownSpans.Where(span => span.From < to && span.To > from).Select(span =>
                    $"{Stamp(span.From < from ? from : span.From)} → {Stamp(span.To > to ? to : span.To)}").ToArray());
        }
    }

    internal static string Stamp(DateTimeOffset time) => time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

internal sealed class FlyOperationPage : HistoryPage
{
    private const int PageSize = 50;
    private IReadOnlyList<FlyOperation> _rows = [];
    private int _page;
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string UserName { get; set; } = "";
    public string Keyword { get; set; } = "";
    public string LogNote => "记录来自本机飞拍仿真。飞拍流程及示教操作按用户和工件保留。";
    public bool CanNext => (_page + 1) * PageSize < _rows.Count;
    public bool CanPrevious => _page > 0;
    public string Paging => $"第 {_page + 1} / {Math.Max(1, (_rows.Count + PageSize - 1) / PageSize)} 页 · 共 {_rows.Count} 条";
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public IEnumerable<LogRow> Items => _rows.Skip(_page * PageSize).Take(PageSize).Select(item =>
        new LogRow(FlyAlarmPage.Stamp(item.Time), item.UserName, "IMC30G-E 仿真", item.Component,
            item.Action, item.Detail, item.Result));

    internal FlyOperationPage(FlyScanHistory history) : base(history)
    {
        ToDate = history.Now.LocalDateTime.Date;
        FromDate = ToDate.Value.AddDays(-6);
        PreviousCommand = new DelegateCommand(() => { if (CanPrevious) { _page--; Changed(); } });
        NextCommand = new DelegateCommand(() => { if (CanNext) { _page++; Changed(); } });
    }

    protected override void Refresh()
    {
        var range = ParseRange(FromDate, "00:00:00", ToDate?.AddDays(1), "00:00:00");
        _rows = History.Operations(range.From, range.To, UserName.Trim(), Keyword.Trim());
        _page = 0;
        Status = $"查到 {_rows.Count} 条飞拍操作记录。";
    }

    internal sealed record LogRow(string Time, string UserName, string Device, string ComponentName,
        string Operation, string Changes, string Result);
}
