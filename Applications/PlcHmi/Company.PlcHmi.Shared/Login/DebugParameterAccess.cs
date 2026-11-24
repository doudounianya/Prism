using Company.Platform.Shared.Model;
using Company.PlcHmi.Shared.PLC.Runtime.Models;

namespace Company.PlcHmi.Shared.Login;

/// <summary>当前登录人员的调试参数访问资格。不保存密码、不建立连接、不单独轮询。
/// 无论由 HMI 请求还是 PLC 侧进入调试，都必须收到同一连接/配置的新反馈：手动、调试状态、请求位 True 且急停信号 False。
/// 轴、气缸页面公开查看；只有当前持有调试资格的工程师/管理员可以修改参数或控制设备。</summary>
public static class DebugParameterAccess
{
    private static readonly object Sync = new();
    private static int _owner;
    private static string _name = "", _device = "", _stamp = "";
    private static long _epoch;
    // confirmed 保留已确认的操作资格，readReady 控制实际发送；两者均不限制只读页面入口。
    // 切页交接时可保留资格，但在新范围取得有效反馈前不能发送参数或构件动作。
    private static bool _confirmed, _readReady;
    private static DateTimeOffset _after;
    private static Func<bool>? _current;
    public static event EventHandler? Changed;
    private static bool Current() { try { return _current?.Invoke() == true; } catch { return false; } }
    private static bool Valid() => _confirmed && Current();
    public static bool CanEdit(CurrentUser? user)
    {
        lock (Sync) return UserAccessRules.CanEditParameters(user) && user!.Id == _owner && user.UserName == _name && Valid();
    }
    /// <summary>判断是否已经为当前人员及原连接建立过观察会话，不代表 PLC 已确认授权。
    /// 页面周期刷新不能反复 Begin，否则每次都会重置确认时间，始终等不到新的有效反馈。</summary>
    public static bool IsBoundTo(int userId, string userName, string device, long epoch, string stamp)
    {
        lock (Sync) return userId != 0 && _owner == userId && _name == userName && _device == device &&
            _epoch == epoch && _stamp == stamp && Current();
    }
    public static void Begin(int userId, string userName, string device, long epoch, string stamp, Func<bool> current)
    {
        bool notify;
        lock (Sync)
        {
            notify = _confirmed; _confirmed = false; _readReady = false;
            _owner = userId; _name = userName; _device = device; _epoch = epoch; _stamp = stamp; _current = current; _after = DateTimeOffset.UtcNow;
        }
        if (notify) Changed?.Invoke(null, EventArgs.Empty);
    }
    public static void Revoke()
    {
        bool notify;
        lock (Sync) { notify = _confirmed; _confirmed = false; _readReady = false; _owner = 0; _name = ""; _current = null; }
        if (notify) Changed?.Invoke(null, EventArgs.Empty);
    }
    /// <summary>正常切页时 Pending/Inactive 只代表采集范围交接，不把缓存冒充新授权，也不反复弹走新页面。
    /// 实际坏质量、断线、配置/连接变化、退出调试会撤销操作资格。</summary>
    public static void Observe(IReadOnlyList<PlcVariableSnapshot> snapshots)
    {
        bool notify = false;
        lock (Sync)
        {
            if (_owner == 0) return;
            var fields = new[] { "UnitMode[0].Mode", "UnitMode[0].Status", "UnitMode[0].Action[6]", "UnitMode[0].Signal[5]" }
                .Select(id => snapshots.FirstOrDefault(s => s.Key.DeviceId == _device && s.Key.LogicalId == id)).ToArray();
            bool? next = null;
            _readReady = false;
            if (!Current() || fields.Any(f => f?.State is PlcValueState.Invalid or PlcValueState.Uncertain or PlcValueState.Stale || f?.IsValid == true && f.Quality != PlcObservationQuality.Good)) next = false;
            else if (fields.All(f => f?.IsValid == true && f.Quality == PlcObservationQuality.Good && f.ConnectionEpoch == _epoch && f.ConfigurationVersion == _stamp && f.LastValidAt >= _after))
            {
                next = fields[0]!.EngineeringValue is not null && Convert.ToInt32(fields[0]!.EngineeringValue) == 0 &&
                    fields[1]!.EngineeringValue is not null && Convert.ToInt32(fields[1]!.EngineeringValue) == 30 &&
                    fields[2]!.EngineeringValue is true && fields[3]!.EngineeringValue is false;
                _readReady = next == true;
            }
            if (next is { } confirmed && confirmed != _confirmed) { _confirmed = confirmed; notify = true; }
        }
        if (notify) Changed?.Invoke(null, EventArgs.Empty);
    }
    /// <summary>参数与构件动作在进入通讯队列和真正发送前均验证；原按住动作清零仍可独立执行。</summary>
    public static void CheckWrite(string category, string logicalId, string value, string device, long epoch, string stamp)
    {
        if (category is not ("轴" or "气缸")) return;
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase) &&
            (logicalId.EndsWith(".Request.JogPositive", StringComparison.Ordinal) || logicalId.EndsWith(".Request.JogNegative", StringComparison.Ordinal) || logicalId.EndsWith(".Request.ResetFault", StringComparison.Ordinal))) return;
        lock (Sync)
            if (!Valid() || !_readReady || device != _device || epoch != _epoch || stamp != _stamp)
                throw new InvalidOperationException("需由设备工程师、工艺工程师或管理员登录，并等待 PLC 确认进入参数调试后才能操作。");
    }
}
