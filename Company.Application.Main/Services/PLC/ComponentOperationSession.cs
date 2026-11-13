using Company.Application.PLC.Writing;
using Company.Application.Main.Services.Diagnostics;
using Company.Application.Share.Login;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Core.Ioc;

namespace Company.Application.Main.Services.PLC;

/// <summary>单页面操作会话；切换对象取消尚未发送的操作，点动释放绑定原设备与原连接。</summary>
[ExposedService(Lifetime.Transient)]
public sealed class ComponentOperationSession
{
    private readonly IPlcWriteService writer;
    private readonly IUserAccessService access;
    private readonly ISession session;
    private readonly IPlcDataProvider? data;
    public event EventHandler? PermissionsChanged;
    /// <summary>
    /// 绑定写入服务与权限服务，并订阅权限变化以使原页面操作上下文失效。
    /// </summary>
    public ComponentOperationSession(IPlcWriteService writer, ISession session, IUserAccessService access, IPlcDataProvider? data = null)
    {
        this.writer = writer; this.access = access; this.session = session; this.data = data;
        access.Changed += SessionChanged;
    }
    /// <summary>
    /// 权限变化时递增会话代次、取消待发送操作并启动原按住命令释放，再通知界面刷新权限。
    /// </summary>
    private async void SessionChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _generation);
        var old = _target; _target = new(); old.Cancel(); old.Dispose();
        // 先启动释放，界面通知失败也不能阻止原按住命令复位。
        var release = ReleaseJogAsync();
        try { PermissionsChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Company.Logger.Logs.LogWarning("身份权限界面更新失败：" + ex.Message); }
        try { await release; }
        catch (Exception ex) { Company.Logger.Logs.LogWarning("身份切换后的按住按钮复位未确认：" + ex.Message); }
    }
    // 本页面以 access.Version 比较交互授权上下文；传输上下文清零该字段，连接/配置代次仍原样交由写入服务校验。
    private static PlcWriteContext Transport(PlcWriteContext context) => context with { AuthorizationVersion = 0 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _target = new();
    private (string Device, string Component, PlcProtocol Protocol)? _selection;
    private long _generation;
    // 原身份只保存无密码快照；即使登出或切换构件，释放仍归属于原按下操作及原连接。
    private (PlcWriteContext Context, PlcVariableKey Key, OperationAudit.Actor Actor, string Component, string Name)? _jog;
    private CancellationTokenSource? _jogPress;
    public bool Enabled => writer.Enabled;
    public bool LoggedIn => access.IsLoggedIn;
    public bool CanEditParameters => access.CanEditParameters;
    public bool HasJog => _jog is not null;
    /// <summary>
    /// 页面对象变化或离页时取消旧目标操作，并按原保存上下文释放尚未松开的命令。
    /// </summary>
    public Task ChangeTargetAsync(string? device, string? component, PlcProtocol protocol, bool active)
    {
        var selection = active && device is not null && component is not null ? (device, component, protocol) : ((string, string, PlcProtocol)?)null;
        if (_selection == selection) return Task.CompletedTask;
        _selection = selection; Interlocked.Increment(ref _generation);
        var old = _target; _target = new(); old.Cancel(); old.Dispose();
        return ReleaseJogAsync();
    }
    /// <summary>
    /// 检查当前参数/构件操作权限，捕获选中设备的写入上下文及权限版本供后续提交时比较。
    /// </summary>
    public PlcWriteContext Capture()
    {
        if (!CanEditParameters) throw new UnauthorizedAccessException("需由设备工程师、工艺工程师或管理员登录，并等待 PLC 确认进入调试后再操作参数。");
        if (_selection is null) throw new InvalidOperationException("请打开当前构件页面。");
        return writer.Capture(_selection.Value.Device) with { AuthorizationVersion = access.Version };
    }
    /// <summary>
    /// 依据当前选择构造逻辑键和值；实际地址与写入能力仍由写入服务解析。
    /// </summary>
    private PlcVariableWrite Value(string suffix, string value)
    {
        var selection = _selection ?? throw new InvalidOperationException("页面已切换。");
        return new(new(selection.Protocol, selection.Device, selection.Component + suffix), value);
    }
    /// <summary>
    /// 串行执行约定的构件动作；停止动作先释放按住命令，其他动作在按住期间拒绝进入。
    /// </summary>
    public async Task<PlcWriteReport> ExecuteAsync(string action, int? position = null)
    {
        var audit = BeginAudit(action);
        List<OperationAudit.Attempt> audits = [audit];
        var generation = _generation; var token = _target.Token;
        try
        {
            await _gate.WaitAsync(token);
            try
            {
            if (generation != _generation) throw new OperationCanceledException();
            var context = Capture();
            if (action == "Stop") await ReleaseCoreAsync(audits);
            else if (_jog is not null) throw new InvalidOperationException("请先松开当前按住按钮。");
            // 步骤顺序表达此页面已有的写入协议；例如伸缩先清相反请求再置目标，不代表设备侧动作已完成。
            IReadOnlyList<IReadOnlyList<PlcVariableWrite>> steps = action switch
            {
                "Enable" => new[] { new[] { Value(".Request.Enable", "True") } },
                "Disable" => new[] { new[] { Value(".Request.Disable", "False") } },
                "Home" => new[] { new[] { Value(".Request.Home", "True") } },
                "Reset" => new[] { new[] { Value(_selection!.Value.Component.StartsWith("Axis[", StringComparison.Ordinal) ? ".Request.ResetFault" : ".ResetFault", "True") } },
                "Stop" => new[] { new[] { Value(".Request.Stop", "True") } },
                "Move" when position is >= 0 => new[] { new[] { Value($".Positions[{position}].Run", "True") } },
                "Extend" => new[] { new[] { Value(".Request.Retract", "False") }, new[] { Value(".Request.Extend", "True") } },
                "Retract" => new[] { new[] { Value(".Request.Extend", "False") }, new[] { Value(".Request.Retract", "True") } },
                _ => throw new ArgumentException("未知构件操作。")
            };
            audit.Prepare(context, steps, data);
            var result = await writer.WriteSequenceAsync(Transport(context), steps, cancellationToken: token);
            audit.Complete(result, token.IsCancellationRequested);
            return result;
            }
            finally { _gate.Release(); }
        }
        catch (Exception exception) { audit.Fail(exception); throw; }
        finally { await OperationAudit.WriteAsync(audits); }
    }
    /// <summary>
    /// 校验位置编号后复用受限命令写入口，保留调用方之前读取时捕获的上下文。
    /// </summary>
    public async Task<PlcWriteReport> SetPositionRunAsync(PlcWriteContext context, int position, bool value)
    {
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        return await SetCommandAsync(context, $".Positions[{position}].Run", value);
    }
    /// <summary>
    /// 只允许白名单运行位；写入前重新比较页面代次、设备连接上下文及权限版本，防止旧读取结果误写。
    /// </summary>
    public async Task<PlcWriteReport> SetCommandAsync(PlcWriteContext context, string suffix, bool value)
    {
        // 限定可取反命令角色和合法位置下标，不允许任意后缀绕过页面既定命令范围。
        if (suffix is not (".Request.Enable" or ".Request.Home" or ".Request.Stop") &&
            !(suffix.StartsWith(".Positions[", StringComparison.Ordinal) && suffix.EndsWith("].Run", StringComparison.Ordinal) &&
              int.TryParse(suffix[11..^5], out var position) && position >= 0) &&
            !(suffix.StartsWith(".RelativePositions[", StringComparison.Ordinal) && suffix.EndsWith("].Run", StringComparison.Ordinal) &&
              int.TryParse(suffix[19..^5], out var relativePosition) && relativePosition is >= 0 and <= 9))
            throw new ArgumentException("不支持的取反命令。", nameof(suffix));
        var generation = _generation; var token = _target.Token;
        var audit = BeginAudit("ToggleCommand");
        try
        {
            await _gate.WaitAsync(token);
            try
            {
            if (generation != _generation || Capture() != context)
                throw new InvalidOperationException("连接或页面已变化，请重新读取运行位。");
            if (_jog is not null) throw new InvalidOperationException("请先松开当前按住按钮。");
            PlcVariableWrite[] values = [Value(suffix, value ? "True" : "False")];
            audit.Prepare(context, [values], data);
            var result = await writer.WriteAsync(Transport(context), values, cancellationToken: token);
            audit.Complete(result, token.IsCancellationRequested);
            return result;
            }
            finally { _gate.Release(); }
        }
        catch (Exception exception) { audit.Fail(exception); throw; }
        finally { await OperationAudit.WriteAsync([audit]); }
    }
    /// <summary>
    /// 将正负方向映射为同一按住写入与释放流程。
    /// </summary>
    public Task<PlcWriteReport> PressJogAsync(bool positive) => PressMomentaryAsync(positive ? "Positive" : "Negative");
    /// <summary>
    /// 记录将来需要清零的原始目标再发置位；失败、取消或页面变化后尝试释放该原目标。
    /// </summary>
    public async Task<PlcWriteReport> PressMomentaryAsync(string action)
    {
        var suffix = action switch
        {
            "Positive" => ".Request.JogPositive",
            "Negative" => ".Request.JogNegative",
            "Reset" => ".Request.ResetFault",
            _ => throw new ArgumentException("不支持的按住命令。", nameof(action))
        };
        var generation = _generation; var token = _target.Token;
        var actor = OperationAudit.CaptureActor(session);
        var audit = BeginAudit("Press" + action, actor);
        List<OperationAudit.Attempt> audits = [audit];
        try
        {
            await _gate.WaitAsync(token);
            try
            {
            if (generation != _generation) throw new OperationCanceledException();
            if (_jog is not null) throw new InvalidOperationException("已有按钮处于按住状态。");
            var context = Capture();
            var on = Value(suffix, "True");
            var off = action == "Reset" ? null : Value(action == "Positive" ? ".Request.JogNegative" : ".Request.JogPositive", "False");
            // 在发送前记录释放目标；即使置位结果未知，松开仍尝试复位原连接上的同一个点。
            _jog = (context, on.Key, actor, _selection!.Value.Component, _selection.Value.Component);
            var held = _jog.Value;
            using var press = CancellationTokenSource.CreateLinkedTokenSource(token);
            _jogPress = press;
            try
            {
                IReadOnlyList<IReadOnlyList<PlcVariableWrite>> steps = off is null ? [[on]] : [[off], [on]];
                audit.Prepare(context, steps, data);
                _jog = (held.Context, held.Key, held.Actor, held.Component, audit.ComponentName);
                var result = await writer.WriteSequenceAsync(Transport(context), steps, cancellationToken: press.Token);
                audit.Complete(result, press.IsCancellationRequested);
                // 置位报告失败也可能存在结果未知的通讯情况，保留并尝试执行对应清零。
                if (!result.Success || generation != _generation) await ReleaseCoreAsync(audits);
                return result;
            }
            catch { await ReleaseCoreAsync(audits); throw; }
            finally { Interlocked.CompareExchange(ref _jogPress, null, press); }
            }
            finally { _gate.Release(); }
        }
        catch (Exception exception) { audit.Fail(exception); throw; }
        finally { await OperationAudit.WriteAsync(audits); }
    }
    /// <summary>
    /// 立即取消尚未发送的按住置位，再取得串行门闩，对已保存原目标执行清零尝试。
    /// </summary>
    public async Task<PlcWriteReport> ReleaseJogAsync()
    {
        // 松开时立即取消尚未发出的置位；不等待前一个通讯请求返回后再决定是否启动点动。
        try { Volatile.Read(ref _jogPress)?.Cancel(); } catch (ObjectDisposedException) { }
        List<OperationAudit.Attempt> audits = [];
        await _gate.WaitAsync();
        try
        {
            try { return await ReleaseCoreAsync(audits); }
            finally { _gate.Release(); }
        }
        finally { await OperationAudit.WriteAsync(audits); }
    }
    /// <summary>
    /// 取走一次按住目标并写 False；释放不使用已取消页面令牌，仍受原连接上下文有效性校验。
    /// </summary>
    private async Task<PlcWriteReport> ReleaseCoreAsync(List<OperationAudit.Attempt> audits)
    {
        var jog = _jog; _jog = null;
        if (jog is null) return new([]);
        // 不携带已取消的页面令牌；服务仍校验旧代次，绝不跨重连补发。
        var audit = new OperationAudit.Attempt(jog.Value.Actor, "ReleaseMomentary", jog.Value.Key.DeviceId, jog.Value.Component, jog.Value.Name);
        audits.Add(audit);
        try
        {
            PlcVariableWrite[] values = [new(jog.Value.Key, "False")];
            // 清零优先：此路径不为取得审计原值而等待缓存锁，原值明确标未知；实际报告仍逐项记录。
            audit.Prepare(jog.Value.Context, [values], null);
            var result = await writer.WriteAsync(Transport(jog.Value.Context), values);
            audit.Complete(result, false);
            return result;
        }
        catch (Exception exception) { audit.Fail(exception); throw; }
    }
    /// <summary>
    /// 保存已编辑参数前重新验证上下文与权限，并要求写入服务执行参数回读校验。
    /// </summary>
    public async Task<PlcWriteReport> SaveAsync(PlcWriteContext context, IReadOnlyList<PlcVariableWrite> parameters)
    {
        var audit = BeginAudit("SaveParameters");
        var generation = _generation; var token = _target.Token;
        try
        {
            await _gate.WaitAsync(token);
            try
            {
            if (generation != _generation || Capture() != context) throw new InvalidOperationException("连接或页面已变化，请重新读取参数。");
            if (_jog is not null) throw new InvalidOperationException("请先松开当前按住按钮。");
            audit.Prepare(context, [parameters], data);
            var result = await writer.WriteAsync(Transport(context), parameters, verifyParameters: true, cancellationToken: token);
            audit.Complete(result, token.IsCancellationRequested);
            return result;
            }
            finally { _gate.Release(); }
        }
        catch (Exception exception) { audit.Fail(exception); throw; }
        finally { await OperationAudit.WriteAsync([audit]); }
    }

    /// <summary>入口即固定账号和所选构件；审计绝不延后从新会话借用姓名或密码。</summary>
    private OperationAudit.Attempt BeginAudit(string action, OperationAudit.Actor? actor = null) =>
        new(actor ?? OperationAudit.CaptureActor(session), action, _selection?.Device, _selection?.Component);
}

