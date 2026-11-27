using Company.FlyScan.Runtime;
using Company.Hmi.Presentation;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

internal static partial class Program
{
    private const string HistoryFixtureFile = "history-fixture.json";
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    private static bool CheckPackageHistoryInputs(string phase, string data)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "package-manifest.json")) ||
            (phase == "seed" ? Directory.Exists(data) : !File.Exists(Path.Combine(data, HistoryFixtureFile)) ||
                !File.Exists(Path.Combine(data, "history.sqlite"))))
        {
            Console.Error.WriteLine("Package history replay requires a verified installation and isolated fixture storage.");
            return false;
        }
        return true;
    }

    private static void SeedPackageHistory(string data)
    {
        var todayPiece = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(todayPiece.ToOffset(BeijingOffset).DateTime.AddHours(-8)).AddDays(-3);
        var alarmStart = new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), BeijingOffset);
        var evening = new DateTimeOffset(day.ToDateTime(new TimeOnly(20, 0)), BeijingOffset);
        var clock = new HistoryClock();
        using (var history = new FlyScanHistory(Path.Combine(data, "history.sqlite"), clock))
        {
            foreach (var (second, connected, card, emergency) in new[]
            {
                (0, true, false, false), (2, true, true, false), (5, false, true, false),
                (9, true, true, false), (11, true, false, true), (12, true, false, false),
                (14, true, true, false), (16, true, false, false), (18, true, true, false),
            })
            {
                clock.Now = alarmStart.AddSeconds(second);
                history.Observe(connected, card, emergency);
            }
            DateTimeOffset[] samples =
                [evening.AddSeconds(-1), evening, evening.AddHours(12).AddSeconds(-1), evening.AddHours(12), todayPiece];
            var workpieces = samples.Select(_ => Guid.NewGuid()).ToArray();
            for (int index = 0; index < samples.Length; index++)
                Check(history.RecordCompletion(workpieces[index], samples[index], "G4-REOPEN"), $"stored fixture piece {index + 1}");
            Check(!history.RecordCompletion(workpieces[0], evening.AddDays(1), "G4-REOPEN"),
                "duplicate completion preserves the original shift and history");
        }
        File.WriteAllText(Path.Combine(data, HistoryFixtureFile),
            JsonSerializer.Serialize(new HistoryFixture(day, alarmStart, todayPiece)));
    }

    private static async Task VerifyPackageHistory(Window window, string data)
    {
        var fixture = JsonSerializer.Deserialize<HistoryFixture>(File.ReadAllText(Path.Combine(data, HistoryFixtureFile)))
            ?? throw new InvalidDataException("The package history fixture is missing.");
        var pages = Named<TabControl>("Pages");
        await Open("AlarmsTab");
        var realtime = Named<RealtimeAlarmView>("RealtimePage");
        var realtimeTable = Table(realtime);
        Check(realtime.IsVisible && !Rows(realtimeTable).Any(row => Field(row, "StateText") == "活动"),
            "a reopened desktop does not display the previous session's persisted alarm as live");

        Click(realtime, "报警分析");
        await Idle();
        var analysis = Named<AlarmAnalysisView>("AlarmAnalysisPage");
        var from = fixture.AlarmStart.AddSeconds(1).LocalDateTime;
        var to = fixture.AlarmStart.AddSeconds(10).LocalDateTime;
        SetAlarmWindow(analysis, from, to);
        Set(Bound<ComboBox>(analysis, ComboBox.TextProperty, "Filter.Severity"), ComboBox.TextProperty, "故障");
        Click(analysis, "查询");
        await Idle();
        var rank = Table(analysis);
        var card = Rows(rank).Single();
        Check(Field(card, "Code") == "CARD" && Field(card, "Duration") == "4 s" &&
            Field(analysis.DataContext, "Gaps") == "1" && Field(analysis.DataContext, "Uncertain") == "1",
            "reopened analysis reports four confirmed seconds, one unknown event and one acquisition gap");
        var countColumn = rank.Columns.OfType<DataGridTextColumn>().Single(column => Equals(column.Header, "次数"));
        rank.ScrollIntoView(card, countColumn);
        rank.UpdateLayout();
        await Wait(() => countColumn.GetCellContent(card) is TextBlock { Text: "1" },
            "the visible ranking count cell shows one observed alarm");
        Capture(window, data, "alarm-ranking");

        rank.SelectedItem = card;
        SetAlarmWindow(analysis, from.AddDays(-1), to.AddDays(-1));
        Click(analysis, "查看所选报警历史");
        await Idle();
        var history = Named<AlarmHistoryView>("AlarmHistoryPage");
        Check(history.IsVisible && HistoryDate(history, "Filter.FromDate") == from.Date &&
            HistoryDate(history, "Filter.ToDate") == to.Date &&
            HistoryText(history, "Filter.FromTime") == Time(from) && HistoryText(history, "Filter.ToTime") == Time(to) &&
            Bound<ComboBox>(history, ComboBox.TextProperty, "Filter.Severity").Text == "故障" &&
            HistoryText(history, "Filter.Keyword") == "CARD",
            "alarm history opens with the selected code and the last successful analysis filters");
        var row = Rows(Table(history)).Single();
        Check(Field(row, "Alarm") == "CARD" && Field(row, "ObservedDuration") == "4 s · 缺口未知",
            "the reopened history shows the same clipped duration and excludes the later alarm");
        Capture(window, data, "alarm-history");

        await Open("ProductionTab");
        var today = Named<ProductionTodayView>("TodayPage");
        var todayFilter = (DateTime)Bound<DatePicker>(today, DatePicker.SelectedDateProperty, "Filter.Date").SelectedDate!;
        bool sameProductionDay = todayFilter.Date == fixture.TodayPiece.ToOffset(BeijingOffset).DateTime.AddHours(-8).Date;
        Check(Field(today.DataContext, "First") == (sameProductionDay ? "1" : "0"),
            "today's output is read from the reopened database with the Beijing production-day boundary");
        Click(today, "产量趋势");
        await Idle();
        var trend = Named<ProductionTrendView>("TrendPage");
        Set(Bound<DatePicker>(trend, DatePicker.SelectedDateProperty, "Filter.FromDate"), DatePicker.SelectedDateProperty,
            fixture.Day.ToDateTime(TimeOnly.MinValue));
        Set(Bound<DatePicker>(trend, DatePicker.SelectedDateProperty, "Filter.ToDate"), DatePicker.SelectedDateProperty,
            fixture.Day.AddDays(1).ToDateTime(TimeOnly.MinValue));
        Click(trend, "查询");
        await Idle();
        var trendRows = Rows(Table(trend));
        Check(trendRows.Select(item => (Field(item, "Day"), Field(item, "Night"), Field(item, "Total")))
                .SequenceEqual([("1", "2", "3"), ("1", "0", "1")]) && Field(trend.DataContext, "Total") == "4",
            "reopened production trend shows four unique completions across both shift boundaries");
        Capture(window, data, "production-trend");
        Table(trend).SelectedItem = trendRows[0];
        Click(trend, "查看选中日期日报");
        await Idle();
        var daily = Named<ProductionDailyView>("DailyPage");
        var shifts = Walk<DataGrid>(daily).Select(Rows).ToArray();
        Check(daily.IsVisible && HistoryDate(daily, "Filter.Date") == fixture.Day.ToDateTime(TimeOnly.MinValue) &&
            Field(daily.DataContext, "Second") == "1" && Field(daily.DataContext, "Third") == "2" &&
            shifts is [{ Length: 12 }, { Length: 12 }] && Field(shifts[0][11], "Quantity") == "1" &&
            Field(shifts[1][0], "Quantity") == "1" && Field(shifts[1][11], "Quantity") == "1",
            "the daily drill-down preserves the selected production date and the three independent hour buckets");
        Capture(window, data, "production-daily");

        await Open("LogTab");
        var operation = Named<OperationLogView>("LogPage");
        Set(Bound<TextBox>(operation, TextBox.TextProperty, "UserName"), TextBox.TextProperty, "G4-REOPEN");
        Set(Bound<TextBox>(operation, TextBox.TextProperty, "Keyword"), TextBox.TextProperty, "工件完成");
        Click(operation, "查询");
        await Idle();
        var completions = Rows(Table(operation));
        Check(completions.Length == 5 && completions.Select(item => Field(item, "ComponentName")).Distinct().Count() == 5,
            "the reopened operation log has one record per completed workpiece");

        T Named<T>(string name) where T : FrameworkElement => (T)window.FindName(name);

        async Task Open(string tab)
        {
            pages.SelectedItem = Named<TabItem>(tab);
            await Idle();
        }
        static string Time(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        static string HistoryText(DependencyObject page, string path) => Bound<TextBox>(page, TextBox.TextProperty, path).Text;
        static DateTime? HistoryDate(DependencyObject page, string path) =>
            Bound<DatePicker>(page, DatePicker.SelectedDateProperty, path).SelectedDate;
        static void SetAlarmWindow(DependencyObject page, DateTime start, DateTime end)
        {
            Set(Bound<DatePicker>(page, DatePicker.SelectedDateProperty, "Filter.FromDate"), DatePicker.SelectedDateProperty, start.Date);
            Set(Bound<DatePicker>(page, DatePicker.SelectedDateProperty, "Filter.ToDate"), DatePicker.SelectedDateProperty, end.Date);
            Set(Bound<TextBox>(page, TextBox.TextProperty, "Filter.FromTime"), TextBox.TextProperty, Time(start));
            Set(Bound<TextBox>(page, TextBox.TextProperty, "Filter.ToTime"), TextBox.TextProperty, Time(end));
        }
        static T Bound<T>(DependencyObject page, DependencyProperty property, string path) where T : FrameworkElement =>
            Walk<T>(page).Single(control => control.GetBindingExpression(property)?.ParentBinding.Path.Path == path);
        static void Set(FrameworkElement control, DependencyProperty property, object value)
        {
            control.SetCurrentValue(property, value);
            control.GetBindingExpression(property)!.UpdateSource();
        }
        static DataGrid Table(DependencyObject page) => Walk<DataGrid>(page).First();
        static object[] Rows(DataGrid grid) => grid.Items.Cast<object>().ToArray();
        static string? Field(object item, string property) => item.GetType().GetProperty(property)?.GetValue(item)?.ToString();
    }

    private sealed record HistoryFixture(DateOnly Day, DateTimeOffset AlarmStart, DateTimeOffset TodayPiece);

    private sealed class HistoryClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
