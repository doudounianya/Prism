using Microsoft.Data.Sqlite;
using System.IO;

namespace Company.FlyScan.Runtime;

public sealed record CompletedPiece(Guid Id, DateTimeOffset Time);
public sealed record FlyOperation(DateTimeOffset Time, string UserName, string Component, string Action, string Detail, string Result);
public sealed record ObservationSpan(DateTimeOffset From, DateTimeOffset To);
public sealed record ScanAlarm(string Code, string Name, string Severity, DateTimeOffset Start,
    DateTimeOffset? End, bool Uncertain, IReadOnlyList<ObservationSpan> KnownSpans)
{
    public TimeSpan KnownDuration(DateTimeOffset from, DateTimeOffset to) => KnownSpans.Aggregate(TimeSpan.Zero,
        (sum, span) => sum + Clip(span, from, to));

    internal static TimeSpan Clip(ObservationSpan span, DateTimeOffset from, DateTimeOffset to)
    {
        var start = span.From > from ? span.From : from;
        var end = span.To < to ? span.To : to;
        return end > start ? end - start : TimeSpan.Zero;
    }
}
public sealed record ScanAlarmReport(IReadOnlyList<ScanAlarm> Alarms, IReadOnlyList<ObservationSpan> Gaps, bool Online);

/// <summary>飞拍仿真自己的本地记录。工件以 G3 的工件 ID 为唯一键，首次结算与日志在同一事务提交。</summary>
public sealed class FlyScanHistory : IDisposable
{
    private static readonly (string Code, string Name, string Severity, int Bit)[] Definitions =
        [("CARD", "运动卡告警", "故障", 1), ("ESTOP", "急停", "急停", 2)];
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _clock;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly SqliteConnection _db;
    private Sample? _last;

    public FlyScanHistory(string path, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 3, Pooling = false }.ToString());
        try
        {
            _db.Open();
            using var create = Command("""
                CREATE TABLE IF NOT EXISTS pieces(id TEXT PRIMARY KEY, stamp INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS pieces_stamp ON pieces(stamp);
                CREATE TABLE IF NOT EXISTS observations(id INTEGER PRIMARY KEY, stamp INTEGER NOT NULL,
                    observed_until INTEGER NOT NULL,
                    session TEXT NOT NULL, connected INTEGER NOT NULL, flags INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS observations_stamp ON observations(stamp);
                CREATE TABLE IF NOT EXISTS operations(id INTEGER PRIMARY KEY, stamp INTEGER NOT NULL,
                    actor TEXT NOT NULL, component TEXT NOT NULL, action TEXT NOT NULL, detail TEXT NOT NULL, result TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS operations_stamp ON operations(stamp);
                """);
            create.ExecuteNonQuery();
            using var last = Command("SELECT id,stamp,observed_until,session,connected,flags FROM observations ORDER BY stamp DESC,id DESC LIMIT 1");
            using var reader = last.ExecuteReader();
            if (reader.Read()) _last = Sample.Read(reader);
            reader.Close();
            using var known = Command("SELECT MAX(observed_until) FROM observations WHERE connected=1");
            if (known.ExecuteScalar() is long timestamp) LastConfirmed = Date(timestamp);
        }
        catch { _db.Dispose(); throw; }
    }

    public DateTimeOffset Now => _clock.GetUtcNow();
    public bool Online => _last is { Connected: true } last && last.Session == _session && Now - last.End <= MaxSampleGap;
    public DateTimeOffset? LastConfirmed { get; private set; }

    public void Observe(bool connected, bool alarm, bool emergency)
    {
        var now = Now;
        int flags = (alarm ? 1 : 0) | (emergency ? 2 : 0);
        if (_last is { } last)
        {
            if (now < last.End) now = last.End;
            if (last.Session == _session && last.Connected == connected && last.Flags == flags &&
                now - last.End <= MaxSampleGap)
            {
                if (now - last.End < Heartbeat) return;
                // Stable heartbeats extend a run in place. New transitions and gaps keep their own boundaries.
                using var extend = Command("UPDATE observations SET observed_until=$end WHERE id=$id",
                    ("$end", now.UtcTicks), ("$id", last.Id));
                extend.ExecuteNonQuery();
                _last = last with { End = now };
                if (connected) LastConfirmed = now;
                return;
            }
        }
        using var insert = Command("""
            INSERT INTO observations(stamp,observed_until,session,connected,flags)
              VALUES($time,$time,$session,$online,$flags) RETURNING id
            """, ("$time", now.UtcTicks), ("$session", _session), ("$online", connected), ("$flags", flags));
        var id = (long)insert.ExecuteScalar()!;
        _last = new Sample(id, now, now, _session, connected, flags);
        if (connected) LastConfirmed = now;
    }

    public bool RecordCompletion(Guid id, DateTimeOffset time, string actor)
    {
        using var tx = _db.BeginTransaction();
        using var insert = Command("INSERT INTO pieces(id,stamp) VALUES($id,$time) ON CONFLICT(id) DO NOTHING",
            ("$id", id.ToString("N")), ("$time", time.UtcTicks));
        insert.Transaction = tx;
        var created = insert.ExecuteNonQuery() == 1;
        if (created)
            WriteOperation(new FlyOperation(time, actor, id.ToString("N"), "工件完成", "两组扫描与下料已确认", "已保存"), tx);
        tx.Commit();
        return created;
    }

    public void Record(FlyOperation operation) => WriteOperation(operation, null);

    private void WriteOperation(FlyOperation operation, SqliteTransaction? transaction)
    {
        using var cmd = Command("""
            INSERT INTO operations(stamp,actor,component,action,detail,result)
            VALUES($time,$actor,$component,$action,$detail,$result)
            """, ("$time", operation.Time.UtcTicks), ("$actor", operation.UserName), ("$component", operation.Component),
            ("$action", operation.Action), ("$detail", operation.Detail), ("$result", operation.Result));
        cmd.Transaction = transaction;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FlyOperation> Operations(DateTimeOffset from, DateTimeOffset to, string user, string keyword)
    {
        using var cmd = Command("""
            SELECT stamp,actor,component,action,detail,result FROM operations
            WHERE stamp >= $from AND stamp < $to AND instr(actor,$actor)>0
              AND ($word='' OR instr(component || ' ' || action || ' ' || detail,$word)>0)
            ORDER BY stamp DESC,id DESC
            """, ("$from", from.UtcTicks), ("$to", to.UtcTicks),
            ("$actor", user), ("$word", keyword));
        using var reader = cmd.ExecuteReader();
        var rows = new List<FlyOperation>();
        while (reader.Read())
            rows.Add(new FlyOperation(Date(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return rows;
    }

    public IReadOnlyList<CompletedPiece> Pieces(DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = Command("""
            SELECT id,stamp FROM pieces WHERE stamp >= $from AND stamp < $to ORDER BY stamp,id
            """, ("$from", from.UtcTicks), ("$to", to.UtcTicks));
        using var reader = cmd.ExecuteReader();
        var rows = new List<CompletedPiece>();
        while (reader.Read()) rows.Add(new CompletedPiece(Guid.ParseExact(reader.GetString(0), "N"), Date(reader.GetInt64(1))));
        return rows;
    }

    public ScanAlarmReport Alarms(DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = Command("SELECT id,stamp,observed_until,session,connected,flags FROM observations ORDER BY stamp,id");
        using var reader = cmd.ExecuteReader();
        var alarms = new List<ScanAlarm>();
        var active = new Dictionary<int, AlarmRun>();
        var gaps = new List<ObservationSpan>();
        Sample? prior = null;
        while (reader.Read())
        {
            var next = Sample.Read(reader);
            bool witnessed = prior is { Connected: true } known && known.Session == next.Session &&
                next.Start - known.End <= MaxSampleGap;
            if (prior is { } previous)
            {
                if (witnessed)
                {
                    foreach (var run in active.Values) AppendSpan(run.KnownSpans, previous.End, next.Start);
                }
                else
                {
                    AppendSpan(gaps, previous.End, next.Start);
                    foreach (var run in active.Values) run.Uncertain = true;
                }
            }
            if (next.Connected)
            {
                foreach (var (code, name, severity, bit) in Definitions)
                {
                    if ((next.Flags & bit) != 0)
                        active.TryAdd(bit, new AlarmRun(code, name, severity, next.Start, !witnessed));
                    else if (active.Remove(bit, out var ended) && next.Start >= from)
                        alarms.Add(ended.ToAlarm(next.Start));
                }
                foreach (var run in active.Values) AppendSpan(run.KnownSpans, next.Start, next.End);
            }
            else
            {
                AppendSpan(gaps, next.Start, next.End);
                foreach (var run in active.Values) run.Uncertain = true;
            }
            prior = next;
        }
        if (prior is { } latest && !Online)
        {
            var end = Now < to ? Now : to;
            AppendSpan(gaps, latest.End, end);
            foreach (var run in active.Values) run.Uncertain = true;
        }
        alarms.AddRange(active.Values.Select(run => run.ToAlarm(null)));
        return new ScanAlarmReport(alarms.Where(alarm => alarm.Start < to &&
                (alarm.End is null || alarm.End > from || alarm.End == alarm.Start && alarm.Start >= from))
                .OrderByDescending(row => row.Start).ToArray(),
            gaps.Where(gap => gap.To > from && gap.From < to).Select(gap => new ObservationSpan(
                gap.From < from ? from : gap.From, gap.To > to ? to : gap.To)).ToArray(), Online);
    }

    private static void AppendSpan(List<ObservationSpan> spans, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return;
        if (spans.Count > 0 && spans[^1].To == from) spans[^1] = spans[^1] with { To = to };
        else spans.Add(new ObservationSpan(from, to));
    }

    private SqliteCommand Command(string sql, params (string Key, object Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }

    private static DateTimeOffset Date(long ticks) => new(ticks, TimeSpan.Zero);
    public void Dispose() => _db.Dispose();

    private sealed record Sample(long Id, DateTimeOffset Start, DateTimeOffset End, string Session, bool Connected, int Flags)
    {
        internal static Sample Read(SqliteDataReader reader) =>
            new(reader.GetInt64(0), Date(reader.GetInt64(1)), Date(reader.GetInt64(2)), reader.GetString(3),
                reader.GetBoolean(4), reader.GetInt32(5));
    }

    private sealed class AlarmRun(string code, string name, string severity, DateTimeOffset start, bool uncertain)
    {
        internal List<ObservationSpan> KnownSpans { get; } = [];
        internal bool Uncertain { get; set; } = uncertain;
        internal ScanAlarm ToAlarm(DateTimeOffset? end) => new(code, name, severity, start, end, Uncertain, KnownSpans);
    }
}
