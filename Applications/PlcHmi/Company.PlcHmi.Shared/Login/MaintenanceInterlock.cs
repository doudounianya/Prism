using System.IO;
using System.Text.Json;

namespace Company.PlcHmi.Shared.Login;

/// <summary>人员维护锁。与单一登录会话分离，不保存密码，不建立连接或新增采样任务。
/// 本地记录先可靠落盘，再允许发送维护请求；未知结果、断线及重启均保留锁。
/// 只有读取到请求位已清零且 PLC 已退出维护，才能移除最后一条待确认记录。</summary>
public static class MaintenanceInterlock
{
    public sealed record Person(int Id, string UserName, string RoleName, DateTimeOffset LockedAt, bool Releasing = false)
    {
        public string StateText => Releasing ? "等待 PLC 退出确认" : "已上锁";
        public string TimeText => LockedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
    public sealed record Saved(Person[] People, bool PlcHeld, DateTimeOffset PendingSince);
    private static readonly object Sync = new();
    private static readonly string FileName = Path.Combine(AppContext.BaseDirectory, "Config", "maintenance-locks.json");
    private static Saved _state = new([], false, DateTimeOffset.MinValue);
    private static bool _unreadable;
    public static event EventHandler? Changed;
    static MaintenanceInterlock()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FileName)) ?? throw new IOException("维护记录为空。");
            if (saved.People is null || saved.People.Any(p => p.Id <= 0) || saved.People.Select(p => p.Id).Distinct().Count() != saved.People.Length)
                throw new IOException("维护人员记录不完整。");
            _state = saved;
        }
        // 直接读取，避免 File.Exists 把访问失败或路径类型错误报告为文件不存在。
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch { _state = new([], true, DateTimeOffset.MinValue); _unreadable = true; }
    }
    public static Person[] People { get { lock (Sync) return _state.People.ToArray(); } }
    public static bool Blocked { get { lock (Sync) return _unreadable || _state.PlcHeld || _state.People.Length > 0; } }
    public static bool Holds(int id) { lock (Sync) return _state.People.Any(p => p.Id == id); }
    private static void Save(Saved next)
    {
        // 持久化失败仍在内存保持锁定，禁止继续写 PLC；不覆盖原有有效文件。
        _state = next;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
            var temporary = FileName + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, FileName, true);
        }
        catch { _unreadable = true; throw; }
    }
    private static void Notify() => Changed?.Invoke(null, EventArgs.Empty);
    public static bool Add(UserAccountInfo user)
    {
        bool first;
        try
        {
            lock (Sync)
            {
                if (_unreadable) throw new InvalidOperationException("维护锁记录无法读取，禁止覆盖，请检查维护记录文件。");
                if (_state.People.Any(p => p.Releasing)) throw new InvalidOperationException("最后一人退出尚待 PLC 确认，请先核对退出结果。");
                if (Holds(user.Id)) throw new InvalidOperationException("此用户已经上锁，不能重复上锁。");
                first = _state.People.Length == 0;
                Save(new([.._state.People, new(user.Id, user.UserName, user.RoleName, DateTimeOffset.UtcNow)], true, DateTimeOffset.UtcNow));
            }
        }
        finally { Notify(); }
        return first;
    }
    /// <summary>返回是否需要申请最后一人退出。最后一条记录保留到 PLC 回读确认；失败时可由本人再次验证重试。</summary>
    public static bool Release(UserAccountInfo user)
    {
        bool last;
        try
        {
            lock (Sync)
            {
                var own = _state.People.SingleOrDefault(p => p.Id == user.Id && p.UserName == user.UserName)
                    ?? throw new UnauthorizedAccessException("只能解除本人持有的维护锁。");
                last = _state.People.Length == 1;
                Save(last ? new([own with { Releasing = true }], true, DateTimeOffset.UtcNow)
                    : _state with { People = _state.People.Where(p => p.Id != own.Id).ToArray() });
            }
        }
        finally { Notify(); }
        return last;
    }
    /// <summary>管理员最终解锁入口。调用方必须用既有账号服务重新认证管理员；保留全部人员的待确认记录，不能立即开放操作。</summary>
    public static void ReleaseAll(UserAccountInfo administrator)
    {
        if (administrator.Id <= 0 || administrator.Role != (int)UserRole.Administrator)
            throw new UnauthorizedAccessException("只有重新验证身份的管理员可以解除全部维护锁。");
        try
        {
            lock (Sync)
            {
                var people = _state.People.Length > 0 ? _state.People.Select(p => p with { Releasing = true }).ToArray()
                    : [new Person(administrator.Id, administrator.UserName, administrator.RoleName, DateTimeOffset.UtcNow, true)];
                // 管理员也可处理损坏记录，但新待确认记录必须落盘成功，失败继续保持锁定。
                _unreadable = false;
                Save(new(people, true, DateTimeOffset.UtcNow));
            }
        }
        finally { Notify(); }
    }
    /// <summary>只接收当前连接、当前配置的有效反馈；使用三个反馈的最早时间，拒绝操作前旧缓存解除锁定。</summary>
    public static void Observe(bool request, int mode, int status, DateTimeOffset sampledAt)
    {
        bool changed = false;
        lock (Sync)
        {
            if (_unreadable) return;
            var held = request || mode == 10 || status == 60;
            if (!held && sampledAt >= _state.PendingSince && _state.People.Length > 0 && _state.People.All(p => p.Releasing))
            { Save(new([], false, DateTimeOffset.MinValue)); changed = true; }
            else if (held && !_state.PlcHeld)
            { Save(_state with { PlcHeld = true }); changed = true; }
            else if (!held && _state.People.Length == 0 && _state.PlcHeld)
            { Save(new([], false, DateTimeOffset.MinValue)); changed = true; }
        }
        if (changed) Notify();
    }
    /// <summary>写入服务与设备队列发送前共同检查。维护管理与原按住按钮清零可以通过，其余写入全部拒绝。</summary>
    public static void CheckWrite(string logicalId, string value)
    {
        lock (Sync)
        {
            if (logicalId == "UnitMode[0].Request.Maintenance")
            {
                if (_unreadable || !bool.TryParse(value, out var on) ||
                    (on ? !_state.People.Any(p => !p.Releasing) : _state.People.Any(p => !p.Releasing)))
                    throw new InvalidOperationException("维护请求必须通过人员锁管理提交。");
                return;
            }
            bool release = value.Equals("False", StringComparison.OrdinalIgnoreCase) &&
                (logicalId is "UnitMode[0].Action[3]" or "UnitMode[0].Request.Initialize" or "UnitMode[0].Request.Debug" || logicalId.EndsWith(".Request.JogPositive", StringComparison.Ordinal) || logicalId.EndsWith(".Request.JogNegative", StringComparison.Ordinal) || logicalId.EndsWith(".Request.ResetFault", StringComparison.Ordinal));
            if (Blocked && !release) throw new InvalidOperationException("维护锁定期间禁止设备操作和参数写入，请由全部人员分别解锁并等待 PLC 确认退出。");
        }
    }
}
