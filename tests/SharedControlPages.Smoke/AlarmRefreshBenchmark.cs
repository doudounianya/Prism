using Company.FlyScan.Runtime;
using Company.Hmi.Presentation;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static partial class Program
{
    private const int ObservationCount = 50_000;
    private const int RefreshCount = 300;
    private static readonly DateTimeOffset EndOfHistory = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static int RunAlarmRefreshBenchmark(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["--alarm-refresh-seed", var path]:
                    SeedAlarmRefreshHistory(path);
                    return 0;
                case ["--alarm-refresh-benchmark", var input, var output]:
                    BenchmarkAlarmRefresh(input, output);
                    return 0;
                default:
                    Console.Error.WriteLine("Use --alarm-refresh-seed <new sqlite file> or --alarm-refresh-benchmark <seed file> <new json file>.");
                    return 2;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void SeedAlarmRefreshHistory(string path)
    {
        using (File.Open(path, FileMode.CreateNew)) { }
        using (var history = new FlyScanHistory(path, new AlarmBenchmarkClock(EndOfHistory))) { }
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var tx = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO observations(stamp,observed_until,session,connected,flags)
            VALUES($stamp,$until,$session,$connected,$flags)
            """;
        var stamp = insert.Parameters.Add("$stamp", SqliteType.Integer);
        var until = insert.Parameters.Add("$until", SqliteType.Integer);
        var session = insert.Parameters.Add("$session", SqliteType.Text);
        var connected = insert.Parameters.Add("$connected", SqliteType.Integer);
        var flags = insert.Parameters.Add("$flags", SqliteType.Integer);
        insert.Prepare();
        for (var i = 0; i < ObservationCount; i++)
        {
            var phase = i % 50;
            var time = EndOfHistory.AddSeconds(-2 * (ObservationCount - i));
            stamp.Value = time.UtcTicks;
            until.Value = time.AddSeconds(1).UtcTicks;
            session.Value = (i / 500).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            connected.Value = phase is >= 14 and <= 17 ? 0 : 1;
            flags.Value = (phase is >= 5 and <= 21 ? 1 : 0) | (phase is >= 11 and <= 30 ? 2 : 0);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void BenchmarkAlarmRefresh(string input, string output)
    {
        using var results = File.Open(output, FileMode.CreateNew);
        var fixtureHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
        var workingFile = Path.Combine(Path.GetTempPath(), $"flyscan-alarm-refresh-{Guid.NewGuid():N}.sqlite");
        File.Copy(input, workingFile);
        try
        {
            var evidence = MeasureAlarmRefresh(workingFile, fixtureHash);
            JsonSerializer.Serialize(results, evidence, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            File.Delete(workingFile);
        }
    }

    private static object MeasureAlarmRefresh(string workingFile, string fixtureHash)
    {
        var clock = new AlarmBenchmarkClock(EndOfHistory);
        var pageType = typeof(Company.FlyScan.Shell.App).Assembly.GetType("Company.FlyScan.Shell.FlyAlarmPage", throwOnError: true)!;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        using var history = new FlyScanHistory(workingFile, clock);
        history.Observe(true, true, false);
        clock.Utc = EndOfHistory.AddSeconds(1);
        history.Observe(true, true, false);

        var model = Activator.CreateInstance(pageType, instance, null, [history, true, null], null)!;
        var query = pageType.BaseType!.GetMethod("Query", instance)!.CreateDelegate<Action>(model);
        var setVisible = pageType.BaseType.GetMethod("SetVisible", instance)!.CreateDelegate<Action<bool>>(model);
        var view = new RealtimeAlarmView { DataContext = model };
        var grid = (DataGrid)view.FindName("Signals");
        var size = new Size(1366, 768);

        void Refresh()
        {
            query();
            // Let WPF process pending binding work, then measure and arrange the actual page.
            view.Dispatcher.Invoke(FlushAlarmBindings, DispatcherPriority.Background);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
        }

        setVisible(true);
        for (var i = 0; i < 20; i++) Refresh();
        if (grid.Items.Count != 1) throw new InvalidOperationException("Expected the seeded live alarm in the WPF grid.");

        var timings = new long[RefreshCount];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < timings.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Refresh();
            timings[i] = Stopwatch.GetTimestamp() - start;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;
        Array.Sort(timings);
        double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        var snapshots = new List<object>();
        void Capture(string name, FlyScanHistory source, bool captureView = true)
        {
            var from = EndOfHistory.AddSeconds(-63);
            var to = clock.Utc.AddTicks(1);
            var report = source.Alarms(from, to);
            snapshots.Add(new
            {
                Stage = name,
                report.Online,
                AlarmResults = report.Alarms.Select(alarm => new
                {
                    Alarm = alarm,
                    DurationTicks = alarm.KnownDuration(from, to).Ticks
                }).ToArray(),
                report.Gaps,
                GridRows = captureView ? grid.Items.Cast<object>().Select(row =>
                    JsonSerializer.SerializeToElement(row, row.GetType())).ToArray() : null
            });
        }

        Capture("online", history);
        clock.Utc = EndOfHistory.AddSeconds(2);
        history.Observe(false, false, false);
        clock.Utc = EndOfHistory.AddSeconds(5);
        history.Observe(false, false, false);
        Refresh();
        Capture("disconnected", history);
        clock.Utc = EndOfHistory.AddSeconds(8);
        history.Observe(true, true, true);
        clock.Utc = EndOfHistory.AddSeconds(9);
        history.Observe(true, true, true);
        Refresh();
        Capture("reconnected", history);
        clock.Utc = EndOfHistory.AddSeconds(15);
        Refresh();
        Capture("stale", history);
        using var reopened = new FlyScanHistory(workingFile, clock);
        Capture("reopened", reopened, captureView: false);
        clock.Utc = EndOfHistory.AddSeconds(16);
        reopened.Observe(true, false, true);
        clock.Utc = EndOfHistory.AddSeconds(18);
        reopened.Observe(true, false, false);
        Capture("cleared-after-restart", reopened, captureView: false);

        var full = reopened.Alarms(DateTimeOffset.MinValue, clock.Utc.AddTicks(1));
        var fullJson = JsonSerializer.SerializeToUtf8Bytes(full);

        return new
        {
            FixtureSha256 = fixtureHash,
            Observations = ObservationCount,
            FullHistory = new
            {
                Sha256 = Convert.ToHexString(SHA256.HashData(fullJson)),
                Alarms = full.Alarms.Count,
                Gaps = full.Gaps.Count,
                KnownAlarmTicks = full.Alarms.Sum(alarm => alarm.KnownDuration(DateTimeOffset.MinValue, clock.Utc).Ticks)
            },
            Refreshes = RefreshCount,
            TotalMilliseconds = Milliseconds(timings.Sum()),
            MedianMilliseconds = Milliseconds(timings[RefreshCount / 2 - 1] + timings[RefreshCount / 2]) / 2,
            P95Milliseconds = Milliseconds(timings[(int)Math.Ceiling(RefreshCount * 0.95) - 1]),
            UiThreadAllocatedBytes = allocated,
            Gen0Collections = gen0,
            Gen1Collections = gen1,
            Gen2Collections = gen2,
            Snapshots = snapshots
        };
    }

    private static void FlushAlarmBindings() { }

    private sealed class AlarmBenchmarkClock(DateTimeOffset utc) : TimeProvider
    {
        internal DateTimeOffset Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
