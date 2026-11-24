using Company.PlcHmi.Main.Services.History.Models;
using Company.Core.Ioc;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Company.PlcHmi.Main.Services.History;
/// <summary>
/// 管理带序号的本地追加日志与提交检查点，隔离采集速度和数据库可用性。
/// 每条载荷附 SHA-256 校验；独占目录锁避免多个实例同时维护同一日志流。
/// </summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcPersistenceJournal(PlcHistoryOptions options):IAsyncDisposable
{
    // Committed 是数据库已确认后保存的位置，LastSequence 是已知尾序号；StreamId 跨进程重启保持不变。
    private sealed record Metadata(string StreamId,long Committed,long LastSequence);
    private sealed record Envelope(string Payload,string Sha256);
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly SortedDictionary<long,PlcHistoryJournalEntry> _pending=new();
    private FileStream? _owner;
    private string _stream="";
    private long _committed,_last;
    private long _journalBytes;
    private int _sinceCompact;
    private bool _hasCommittedPrefix;
    private string? _lastAppendError;
    private bool _initialized;
    public string StreamId=>_stream;
    public long Pending=>Interlocked.Read(ref _last)-Interlocked.Read(ref _committed);
    // 状态面板只读取缓存，避免每次刷新都访问磁盘；容量以实际 WAL 字节数为准，包含尚未回收的已提交前缀。
    public long JournalBytes=>Interlocked.Read(ref _journalBytes);
    public long MaximumJournalBytes=>options.MaximumJournalBytes;
    public long CommittedSequence=>Interlocked.Read(ref _committed);
    public string? LastAppendError=>Volatile.Read(ref _lastAppendError);
    public bool Initialized=>_initialized;
    private string Wal=>Path.Combine(options.JournalDirectory,"pending.jsonl");
    private string Meta=>Path.Combine(options.JournalDirectory,"checkpoint.json");
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>
    /// 将序列化载荷编码为单行封套；尾部换行作为完整记录边界，校验值用于发现载荷损坏。
    /// </summary>
    private static byte[] Encode(PlcHistoryJournalEntry entry)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(entry);
        var envelope=new Envelope(Convert.ToBase64String(bytes),Hash(bytes));
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)+"\n");
    }
    /// <summary>
    /// 先将新内容写入临时文件并请求刷盘，再替换目标，用于保存检查点或压缩后的日志。
    /// </summary>
    private static void DurableReplace(string path,byte[] bytes)
    {
        var temp=path+".tmp";
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){
            file.Write(bytes);file.Flush(true);
        }
        // 已有文件通过同目录原子替换切换，失败时保留原文件；绝不先删除旧 WAL 或检查点。
        // 新建目标还没有可替换的旧文件，同目录移动即可完成发布。
        if(File.Exists(path))File.Replace(temp,path,null);
        else File.Move(temp,path);
    }
    private void SaveMetadata()=>DurableReplace(Meta,JsonSerializer.SerializeToUtf8Bytes(new Metadata(_stream,_committed,_last)));
    /// <summary>
    /// 调用方必须持有门闩。只回收已由持久化检查点确认的前缀，完整保留所有待提交事实及其原序号。
    /// 替换成功之前不改变回收标志或容量缓存；失败后旧 WAL 仍可重放，并可在下一次压力出现时重试。
    /// </summary>
    private void CompactCommittedPrefix()
    {
        // 不能仅依赖进程内的提交计数：重启后计数归零，旧 WAL 中仍可能存在已提交记录。
        // 已无可回收前缀时立即返回，避免日志已满期间每次追加都重写整份待提交队列。
        if(!_hasCommittedPrefix)return;
        using var memory=new MemoryStream();
        foreach(var entry in _pending.Values)memory.Write(Encode(entry));
        DurableReplace(Wal,memory.ToArray());
        Interlocked.Exchange(ref _journalBytes,memory.Length);
        _hasCommittedPrefix=false;_sinceCompact=0;
    }
    /// <summary>
    /// 独占日志目录，恢复流标识和检查点，校验完整记录并建立连续的待提交队列。
    /// </summary>
    public async Task InitializeAsync(CancellationToken token=default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(_initialized)return;options.Validate();Directory.CreateDirectory(options.JournalDirectory);
            _owner=new FileStream(Path.Combine(options.JournalDirectory,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            try{
                if(File.Exists(Meta)){
                    var meta=JsonSerializer.Deserialize<Metadata>(File.ReadAllBytes(Meta))??throw new InvalidDataException("日志元数据为空。");
                    if(!Guid.TryParse(meta.StreamId,out _)||meta.Committed<0||meta.LastSequence<meta.Committed)throw new InvalidDataException("日志元数据无效。");
                    _stream=meta.StreamId;_committed=meta.Committed;_last=meta.LastSequence;
                }else{
                    // 有日志却缺少元数据时不能生成新流标识，否则同一事实可能失去原有幂等身份。
                    if(File.Exists(Wal)&&new FileInfo(Wal).Length>0)throw new InvalidDataException("日志元数据丢失；拒绝猜测新日志流。");
                    _stream=Guid.NewGuid().ToString();_last=_committed=0;SaveMetadata();
                }
                _pending.Clear();_hasCommittedPrefix=false;_sinceCompact=0;
                Interlocked.Exchange(ref _journalBytes,0);
                if(File.Exists(Wal)){
                    long size=new FileInfo(Wal).Length;Interlocked.Exchange(ref _journalBytes,size);
                    if(size>options.MaximumJournalBytes)throw new InvalidDataException("日志超出配置容量。");
                    byte[] bytes=File.ReadAllBytes(Wal);int start=0;long previous=0;
                    Interlocked.Exchange(ref _journalBytes,bytes.LongLength);
                    for(int i=0;i<bytes.Length;i++){
                        if(bytes[i]!=10)continue;
                        var envelope=JsonSerializer.Deserialize<Envelope>(bytes.AsSpan(start,i-start))??throw new InvalidDataException("日志记录为空。");
                        var data=Convert.FromBase64String(envelope.Payload);
                        if(Hash(data)!=envelope.Sha256)throw new InvalidDataException("日志校验失败；记录已保留，需核对。");
                        var entry=JsonSerializer.Deserialize<PlcHistoryJournalEntry>(data)??throw new InvalidDataException("日志载荷为空。");
                        // 文件内序号必须连续且属于同一流；检查点之前的记录可保留在文件中，但不重新加入待提交队列。
                        if(entry.FormatVersion!=1||entry.StreamId!=_stream||entry.Sequence<1||
                            (previous!=0&&entry.Sequence!=previous+1))throw new InvalidDataException("日志版本、来源或序号无效。");
                        previous=entry.Sequence;_last=Math.Max(_last,entry.Sequence);
                        if(entry.Sequence>_committed)_pending.Add(entry.Sequence,entry);
                        else _hasCommittedPrefix=true;
                        start=i+1;
                    }
                    // 只截断没有换行提交标记的尾部；完整记录校验失败绝不静默丢弃。
                    if(start<bytes.Length){using var file=new FileStream(Wal,FileMode.Open,FileAccess.Write,FileShare.None);file.SetLength(start);file.Flush(true);Interlocked.Exchange(ref _journalBytes,start);}
                }
                // 内存队列必须完整覆盖 committed+1 到 last；拒绝跳号，不能把缺失记录当作已经入库。
                if(_pending.Count>options.MaximumPending||(_last>_committed&&
                    (_pending.Count==0||_pending.First().Key!=_committed+1||_pending.Last().Key!=_last||_pending.Count!=_last-_committed)))
                    throw new InvalidDataException("待提交日志不连续或超出容量。");
                Volatile.Write(ref _lastAppendError,null);_initialized=true;
            }catch{_owner.Dispose();_owner=null;throw;}
        }finally{_gate.Release();}
    }
    /// <summary>
    /// 分配下一连续序号，写入并刷盘后加入内存队列；容量不足或写入失败时不确认调用方事实。
    /// </summary>
    public async Task<long> AppendAsync(PlcHistoryJournalEntry entry,CancellationToken token=default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(!_initialized)throw new InvalidOperationException("历史日志尚未初始化。");
            if(Pending>=options.MaximumPending)throw new IOException("历史待提交数量已达上限。");
            var next=entry with{StreamId=_stream,Sequence=checked(_last+1)};
            // 不保留调用方可变数组的引用；内存队列必须与落盘字节表达相同事实。
            next=JsonSerializer.Deserialize<PlcHistoryJournalEntry>(JsonSerializer.SerializeToUtf8Bytes(next))!;
            var bytes=Encode(next);
            long size=File.Exists(Wal)?new FileInfo(Wal).Length:0;
            Interlocked.Exchange(ref _journalBytes,size);
            // 总字节数包含已入库但尚未压缩的前缀。空间不足时先安全回收它，再判断真正的待提交容量。
            // 压缩不分配新序号、不移动检查点；整个过程与追加、提交共用门闩，避免并发删除待提交记录。
            if(bytes.LongLength>options.MaximumJournalBytes-size){
                CompactCommittedPrefix();
                size=Interlocked.Read(ref _journalBytes);
                if(bytes.LongLength>options.MaximumJournalBytes-size)throw new IOException("历史日志空间已达上限。");
            }
            // 单次追加失败则回退到写入前长度；只有完整字节成功刷盘才更新 _last。
            using(var file=new FileStream(Wal,FileMode.OpenOrCreate,FileAccess.Write,FileShare.None)){
                file.Seek(0,SeekOrigin.End);long old=file.Length;
                try{file.Write(bytes);file.Flush(true);Interlocked.Exchange(ref _journalBytes,file.Length);}
                catch{file.SetLength(old);file.Flush(true);throw;}
            }
            _pending.Add(next.Sequence,next);Interlocked.Exchange(ref _last,next.Sequence);
            Volatile.Write(ref _lastAppendError,null);
            return next.Sequence;
        }catch(Exception ex){Volatile.Write(ref _lastAppendError,ex.Message);throw;}
        finally{_gate.Release();}
    }
    /// <summary>
    /// 在门闩内读取最早待提交事实，不改变队列和提交位置。
    /// </summary>
    public async Task<PlcHistoryJournalEntry?> PeekAsync(CancellationToken token=default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{return _pending.Count==0?null:_pending.First().Value;}finally{_gate.Release();}
    }
    /// <summary>
    /// 只接受当前队首序号；保存检查点成功后出队，每累计 128 次提交回收已提交前缀以控制文件增长。
    /// </summary>
    public async Task CommitAsync(long sequence,CancellationToken token=default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(sequence!=_committed+1||!_pending.ContainsKey(sequence))throw new InvalidOperationException("本地提交位置不连续。");
            long old=_committed;_committed=sequence;
            // 检查点保存失败时恢复内存提交位置，让写入器可以重放当前事实；此处不提前删除待提交项。
            try{SaveMetadata();}catch{_committed=old;throw;}
            _pending.Remove(sequence);
            _hasCommittedPrefix=true;
            if(++_sinceCompact>=128)CompactCommittedPrefix();
        }finally{_gate.Release();}
    }
    /// <summary>
    /// 释放目录独占锁并清空内存索引；磁盘日志和检查点保留用于后续初始化恢复。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try{_initialized=false;_owner?.Dispose();_owner=null;_pending.Clear();}
        finally{_gate.Release();}
    }
}
