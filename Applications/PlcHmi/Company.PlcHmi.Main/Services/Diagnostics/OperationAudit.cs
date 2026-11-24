using Company.PlcHmi.Plc.Writing;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.Logger;
using System.Globalization;
using System.Text.Json;

namespace Company.PlcHmi.Main.Services.Diagnostics;

/// <summary>
/// 应用层的操作记录。只描述用户请求及通讯写入报告，不推断电机、气缸已经完成动作。
/// 不建立连接、不申请采样、不改变数据库；原值仅取现有内存缓存，并明确携带质量和连接边界。
/// </summary>
internal static class OperationAudit
{
    internal const string Prefix = "HMI_OPERATION ";

    /// <summary>捕获纯身份，不持有 CurrentUser 或密码引用；松开操作继续使用按下时的原操作者。</summary>
    internal sealed record Actor(int Id, string Name, int Role);
    internal static Actor CaptureActor(ISession session)
    {
        try
        {
            var user = session.CurrentUser;
            return new(user?.Id ?? 0, Text(user?.UserName), user?.Role ?? 0);
        }
        catch { return new(0, "", 0); }
    }

    /// <summary>一次操作的不可复用记录；开始时固定身份及目标，完成后在通讯门闩外持久化。</summary>
    internal sealed class Attempt(Actor actor, string action, string? device, string? component, string? componentName = null)
    {
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private readonly string _action = Text(action), _device = Text(device), _component = Text(component);
        private PlcWriteContext? _context;
        private Item[] _items = [];
        private Result[] _results = [];
        private string _outcome = "NotSent", _exception = "";
        private DateTimeOffset? _completed;
        internal string ComponentName { get; private set; } = Text(componentName ?? component);

        internal void Prepare(PlcWriteContext context, IReadOnlyList<IReadOnlyList<PlcVariableWrite>> steps,
            IPlcDataProvider? data)
        {
            _context = context;
            try
            {
                // 名称只取现有 Excel 目录，并在操作发生时固定；以后改名不会改写已保存历史。
                IReadOnlyList<PlcVariableBinding> catalog;
                try { catalog = data?.GetCatalog() ?? []; }
                catch { catalog = []; } // 目录暂不可用也必须保留待写值，不能使整条操作明细丢失。
                if (!_action.StartsWith("Maintenance", StringComparison.Ordinal))
                ComponentName = Text(catalog.FirstOrDefault(v => v.DeviceId == _device && v.ComponentId == _component &&
                    v.Protocol == steps.SelectMany(s => s).FirstOrDefault()?.Key.Protocol &&
                    v.Metadata.TryGetValue("构件名称", out var name) && !string.IsNullOrWhiteSpace(name))?
                    .Metadata.GetValueOrDefault("构件名称") ?? ComponentName);
                _items = steps.SelectMany((step, index) => step.Select(value =>
                    new Item(index + 1, Text(value.Key.DeviceId), Text(value.Key.LogicalId), Value(value.EngineeringValue),
                        ReadBefore(data, value.Key, context), OperationLogText.Field(value.Key.LogicalId, _component,
                            catalog.FirstOrDefault(v => v.Protocol == value.Key.Protocol && v.DeviceId == value.Key.DeviceId &&
                                v.LogicalId == value.Key.LogicalId), catalog)))).ToArray();
            }
            catch { _items = []; }
        }

        internal void Complete(PlcWriteReport report, bool cancelled)
        {
            // 不记录 Message：协议错误自由文本可能包含端点、连接配置或服务端返回的敏感内容。
            _results = report.Results.Select(result => new Result(Text(result.Key.DeviceId), Text(result.Key.LogicalId),
                result.State.ToString(), result.Verified)).ToArray();
            _outcome = cancelled ? "Cancelled" : report.Success ? "WriteConfirmed" : "WriteNotConfirmed";
            _completed = DateTimeOffset.UtcNow;
        }

        internal void Fail(Exception exception)
        {
            _outcome = exception is OperationCanceledException ? "Cancelled" : "Failed";
            _exception = exception.GetType().FullName ?? exception.GetType().Name;
            _completed = DateTimeOffset.UtcNow;
        }

        /// <summary>多人上锁/非最后一人解锁只更新本地人员锁，不伪造 PLC 写入成功报告。</summary>
        internal void CompleteLocal() { _outcome = "LocalConfirmed"; _completed = DateTimeOffset.UtcNow; }

        internal void Write()
        {
            try
            {
                var completed = _completed ?? DateTimeOffset.UtcNow;
                string payload = JsonSerializer.Serialize(new
                {
                    Id = _id, StartedUtc = _started, CompletedUtc = completed,
                    ElapsedMs = Math.Max(0, (completed - _started).TotalMilliseconds),
                    Actor = actor, Action = _action, Device = _device, Component = _component,
                    ComponentName, Description = OperationLogText.Action(_action, _items.LastOrDefault()?.LogicalId ?? "",
                        _items.LastOrDefault()?.Requested ?? "", _items.Select(i => i.Label)),
                    ConnectionEpoch = _context?.ConnectionEpoch, ConfigurationStamp = Text(_context?.ConfigurationStamp),
                    Items = _items, Results = _results, Outcome = _outcome, ExceptionType = _exception,
                    Meaning = "仅记录通讯写入结果，不代表设备动作完成"
                });
                Logs.LogInfo(Prefix + payload);
            }
            catch { /* 审计失败不改变已完成的通讯结果，也不能阻止后续按住按钮清零。 */ }
        }
    }

    /// <summary>
    /// 调用方先释放操作门闩，再异步写已有日志。慢磁盘不会占住门闩，UI 也可以继续处理松开事件。
    /// 不另建后台队列或改变日志生命周期；操作返回前等待其记录尝试结束，便于既有退出流程收尾。
    /// </summary>
    internal static async Task WriteAsync(IReadOnlyList<Attempt> attempts)
    {
        if (attempts.Count == 0) return;
        try { await Task.Run(() => { foreach (var attempt in attempts) attempt.Write(); }).ConfigureAwait(false); }
        catch { /* 日志服务本身或线程调度异常均不推翻操作结果。 */ }
    }

    private sealed record Item(int Step, string Device, string LogicalId, string Requested, Before Before, string Label);
    private sealed record Result(string Device, string LogicalId, string State, bool? Verified);
    private sealed record Before(string Value, string State, DateTimeOffset? ObservedUtc = null);

    private static Before ReadBefore(IPlcDataProvider? data, PlcVariableKey key, PlcWriteContext context)
    {
        if (data is null) return new("未知", "Unavailable");
        try
        {
            var catalog = data.GetCatalog();
            var binding = catalog.FirstOrDefault(v => v.Enabled && v.Protocol == key.Protocol &&
                v.DeviceId == key.DeviceId && v.LogicalId == key.LogicalId);
            if (binding is null) return new("未知", "NoBinding");
            // 动作通常分为只写命令和只读反馈。只允许同地址、同类型、同工程量换算的别名提供原缓存值。
            var readable = binding.Access.HasFlag(PlcAccess.Read) ? binding : catalog.FirstOrDefault(v => v.Enabled &&
                v.Access.HasFlag(PlcAccess.Read) && v.Protocol == binding.Protocol && v.DeviceId == binding.DeviceId &&
                v.Address == binding.Address && v.DataType == binding.DataType && v.InvertBoolean == binding.InvertBoolean &&
                (v.Scale ?? 1) == (binding.Scale ?? 1) && (v.Offset ?? 0) == (binding.Offset ?? 0));
            if (readable is null) return new("未知", "NoReadableBinding");
            var readKey = new PlcVariableKey(readable.Protocol, readable.DeviceId, readable.LogicalId);
            var snapshot = data.GetSnapshots([readKey]).FirstOrDefault(s => s.Key == readKey);
            if (snapshot is null) return new("未知", "Unavailable");
            if (snapshot.ConnectionEpoch != context.ConnectionEpoch) return new("未知", "ConnectionChanged");
            if (snapshot.ConfigurationVersion != context.ConfigurationStamp) return new("未知", "ConfigurationChanged");
            if (!snapshot.IsValid)
                return new("未知", snapshot.State.ToString());
            if (snapshot.Quality != PlcObservationQuality.Good)
                return new("未知", "Quality:" + snapshot.Quality);
            return new(Value(snapshot.EngineeringValue), "ValidCache", snapshot.LastValidAt);
        }
        catch { return new("未知", "Unavailable"); }
    }

    // 工程量按固定文化格式记录，避免逗号小数与系统区域设置造成含义变化。
    // 任意文本参数不原样入日志：无法在此层判断文本是否包含密码或连接配置，使用明确隐藏标记。
    private static string Value(object? value) => value switch
    {
        null => "未知",
        bool flag => flag ? "True" : "False",
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "未知",
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        string text when bool.TryParse(text, out var flag) => flag ? "True" : "False",
        string text when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number.ToString(CultureInfo.InvariantCulture),
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        _ => "文本已隐藏"
    };

    // 标识来自账号或配置，不读取密码；限制长度、移除控制字符，JSON 序列化再次确保单条记录边界。
    private static string Text(string? text) => string.IsNullOrEmpty(text) ? "" :
        new string(text.Where(c => !char.IsControl(c)).Take(256).ToArray());
}
