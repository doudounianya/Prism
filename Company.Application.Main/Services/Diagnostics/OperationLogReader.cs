using Company.Application.Main.Models;
using Company.Application.Share.Login;
using Company.Application.Share.Prism;
using Company.Core.Ioc;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Company.Application.Main.Services.Diagnostics;

/// <summary>
/// 从已有 NLog 文件读取结构化 PLC 操作记录，沿用原日志轮转及保存期限，不新增数据库或独立日志框架。
/// 文件扫描在后台逐行执行，允许运行中的日志追加和轮转；查询只返回最新一万条。
/// 已解析文件采用有容量上限的进程内缓存，文件变化才重读；不建立数据库、磁盘索引或新的日志目标。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class OperationLogReader(IUserAccessService access, string? directory = null)
{
    public const int MaximumRows = 10000;
    private readonly string _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "logs");
    private const long CacheBudget = 32 * 1024 * 1024;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private long _cacheBytes, _sequence;
    private sealed record FileStamp(long Length, long WrittenTicks, long CreatedTicks);
    private sealed record IndexedEntry(OperationLogEntry Entry, string Search);
    private sealed record CachedFile(FileStamp Stamp, IndexedEntry[] Entries, int Invalid, long Bytes, long Used);

    public Task<OperationLogResult> QueryAsync(OperationLogQuery query, CancellationToken token = default)
    {
        long version = access.Version;
        Authorize(version);
        if (query.ToUtc <= query.FromUtc || query.ToUtc - query.FromUtc > TimeSpan.FromDays(32))
            throw new ArgumentException("请选择有效时间范围，最多查询 31 个自然日。");
        return Task.Run(() => ReadAsync(query, version, token), token);
    }

    private void Authorize(long version)
    {
        if (!access.CanOpenPage(ViewNames.OperationLogView) || access.Version != version)
            throw new UnauthorizedAccessException("请登录后查看操作日志。");
    }

    private async Task<OperationLogResult> ReadAsync(OperationLogQuery query, long version, CancellationToken token)
    {
        // 取消后立刻重新查询也不能产生并发全盘扫描；等待门闩及逐行读取都响应取消。
        await _scanGate.WaitAsync(token).ConfigureAwait(false);
        try { return await ReadCoreAsync(query, version, token).ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }

    private async Task<OperationLogResult> ReadCoreAsync(OperationLogQuery query, long version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Authorize(version);
        var latest = new PriorityQueue<OperationLogEntry, (DateTimeOffset, string)>();
        var retainedIds = new HashSet<string>(StringComparer.Ordinal);
        int skipped = 0, invalid = 0; bool truncated = false;
        string user = query.UserName.Trim(), keyword = query.Keyword.Trim();
        // 只扫描原日志目标的文件。普通应用日志不属于操作审计，绝不作为自由文本展示。
        string[] files;
        try { files = Directory.GetFiles(_directory, "CRT_HMI-*.log"); }
        catch (DirectoryNotFoundException) { files = []; }
        // 轮转清理后移除相应缓存，不能继续显示已删除文件中的记录。
        var existing = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missing in _files.Keys.Where(path => !existing.Contains(path)).ToArray()) Remove(missing);

        void Accept(IndexedEntry indexed)
        {
            var entry = indexed.Entry;
            if (entry.StartedUtc < query.FromUtc || entry.StartedUtc >= query.ToUtc ||
                !entry.UserName.Contains(user, StringComparison.OrdinalIgnoreCase) ||
                (keyword.Length > 0 && !indexed.Search.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return;
            if (!retainedIds.Add(entry.Id)) return;
            latest.Enqueue(entry, (entry.StartedUtc, entry.Id));
            if (latest.Count > MaximumRows) { retainedIds.Remove(latest.Dequeue().Id); truncated = true; }
        }
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); Authorize(version);
            try
            {
                var stamp = Stamp(file);
                if (!MayContain(file, stamp, query)) continue;
                if (_files.TryGetValue(file, out var cached) && cached.Stamp == stamp)
                {
                    _files[file] = cached with { Used = ++_sequence };
                    invalid += cached.Invalid;
                    foreach (var entry in cached.Entries) { token.ThrowIfCancellationRequested(); Accept(entry); }
                    continue;
                }
                // 文件被追加、截断、替换或改写时，旧投影立即失效；不拿旧数据冒充本次成功读取。
                Remove(file);
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                // 固定查询开始时该文件的长度；日志不断追加不会使一次查询永远无法结束。
                using var snapshot = new SnapshotStream(stream, stream.Length);
                using var reader = new StreamReader(snapshot);
                var parsed = new List<IndexedEntry>(); long bytes = 128; int fileInvalid = 0;
                bool cacheable = true;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is null) break;
                    int marker = line.IndexOf(OperationAudit.Prefix, StringComparison.Ordinal);
                    if (marker < 0) continue;
                    var entry = Parse(line[(marker + OperationAudit.Prefix.Length)..]);
                    if (entry is null) { ++invalid; ++fileInvalid; continue; }
                    var indexed = new IndexedEntry(entry, entry.ComponentName + " " + entry.Operation + " " + entry.Changes);
                    Accept(indexed);
                    if (cacheable)
                    {
                        // 计入字符串、对象和引用的保守估算；超大单文件不缓存，仍逐行筛选，不丢较早的匹配记录。
                        bytes += 512L + 2L * (entry.Id.Length + entry.UserName.Length + entry.Device.Length + entry.ComponentName.Length +
                            entry.Operation.Length + entry.Changes.Length + entry.Result.Length + indexed.Search.Length);
                        if (bytes <= CacheBudget) parsed.Add(indexed);
                        else { parsed.Clear(); cacheable = false; }
                    }
                }
                // 扫描中继续追加/轮转时只返回这次文件快照，不发布可复用缓存；下一次会重新读取。
                token.ThrowIfCancellationRequested(); Authorize(version);
                if (cacheable && Stamp(file) == stamp)
                {
                    while (_cacheBytes + bytes > CacheBudget && _files.Count > 0)
                        Remove(_files.MinBy(pair => pair.Value.Used).Key);
                    _files[file] = new(stamp, parsed.ToArray(), fileInvalid, bytes, ++_sequence);
                    _cacheBytes += bytes;
                }
            }
            catch (IOException) { Remove(file); ++skipped; }
            catch (UnauthorizedAccessException) { Remove(file); ++skipped; }
        }
        token.ThrowIfCancellationRequested(); Authorize(version);
        return new(latest.UnorderedItems.Select(i => i.Element).OrderByDescending(i => i.StartedUtc)
            .ThenByDescending(i => i.Id, StringComparer.Ordinal).ToArray(), truncated, skipped, invalid);
    }

    private void Remove(string path)
    { if (_files.Remove(path, out var cached)) _cacheBytes -= cached.Bytes; }
    private static FileStamp Stamp(string path)
    {
        var file = new FileInfo(path); file.Refresh();
        if (!file.Exists) throw new FileNotFoundException();
        return new(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
    }

    /// <summary>
    /// 按原日志日期排除明显早于查询的文件，并保留一天边界余量。未知文件名及近期改写文件始终读取。
    /// 不按结束日期排除较晚文件：跨午夜/延迟完成的操作可能后来才落盘，最终仍用记录的 StartedUtc 精确筛选。
    /// </summary>
    private static bool MayContain(string path, FileStamp stamp, OperationLogQuery query)
    {
        var match = Regex.Match(Path.GetFileName(path), @"^CRT_HMI-(\d{4}-\d{2}-\d{2})(?:_\d+)?\.log$", RegexOptions.CultureInvariant);
        if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day)) return true;
        var from = query.FromUtc.ToLocalTime().Date;
        var cutoff = from > DateTime.MinValue.Date ? from.AddDays(-1) : from;
        return day >= cutoff || stamp.WrittenTicks >= query.FromUtc.UtcTicks;
    }

    /// <summary>兼容旧版没有中文说明的记录；不读取普通异常正文，损坏或未写完的一行留待下次查询。</summary>
    private static OperationLogEntry? Parse(string json)
    {
        if (json.Length > 1024 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string id = Text(root, "Id"), time = Text(root, "StartedUtc");
            if (id.Length == 0 || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)) return null;
            string user = root.TryGetProperty("Actor", out var actor) ? Text(actor, "Name") : "";
            string component = Text(root, "Component"), name = Text(root, "ComponentName");
            if (name.Length == 0) name = component;
            var fields = new List<string>(); var changes = new List<string>();
            string lastKey = "", lastValue = "";
            if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray().Take(128))
                {
                    lastKey = Text(item, "LogicalId"); lastValue = SafeValue(Text(item, "Requested"));
                    string label = Text(item, "Label");
                    if (label.Length == 0) label = OperationLogText.Field(lastKey, component);
                    fields.Add(label);
                    string old = item.TryGetProperty("Before", out var before) && Text(before, "State") == "ValidCache"
                        ? SafeValue(Text(before, "Value")) : "未知";
                    changes.Add(label + "：" + old + " → " + lastValue);
                }
            string description = Text(root, "Description");
            if (description.Length == 0) description = OperationLogText.Action(Text(root, "Action"), lastKey, lastValue, fields);
            return new(id, started.ToUniversalTime(), user, Text(root, "Device"), name, description,
                string.Join("；", changes), OperationLogText.Outcome(Text(root, "Outcome")));
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? new string((value.GetString() ?? "").Where(c => !char.IsControl(c)).Take(1024).ToArray()) : "";
    private static string SafeValue(string value) => value is "True" or "False" or "未知" or "文本已隐藏" ? value :
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? value : "文本已隐藏";

    /// <summary>只读长度边界，不关闭外层文件；避免追加中的日志被一次查询无限追赶。</summary>
    private sealed class SnapshotStream(Stream source, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            remaining -= read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
