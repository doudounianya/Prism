using Company.Core.Runtime;
using Company.Core.Ioc;
using Company.Platform.Shared.Login;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;

namespace Company.PlcHmi.Plc.Writing;

/// <summary>操作准入的唯一策略。页面的 CanExecute 用于提示，真正排队和发送前均复核本策略。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcOperationPolicy(IUserAccessService access, ISession session, IPlcDataProvider data,
    IApplicationLifecycle lifecycle)
{
    public long AuthorizationVersion => access.Version;

    /// <summary>按住命令的清零白名单；停止仅允许已确认的位3，不开放维护或任意 False 参数。</summary>
    public static bool IsRelease(PlcVariableWrite item) =>
        item.EngineeringValue.Equals("False", StringComparison.OrdinalIgnoreCase) &&
        (item.Key.LogicalId is "UnitMode[0].Action[3]" or "UnitMode[0].Request.Initialize" or "UnitMode[0].Request.Debug" ||
         item.Key.LogicalId.EndsWith(".Request.JogPositive", StringComparison.Ordinal) ||
         item.Key.LogicalId.EndsWith(".Request.JogNegative", StringComparison.Ordinal) ||
         item.Key.LogicalId.EndsWith(".Request.ResetFault", StringComparison.Ordinal));

    internal void Check(PlcWriteContext context, IReadOnlyList<PlcVariableWrite> values)
    {
        if (values.Count == 0) throw new ArgumentException("操作请求不能为空。");
        bool release = values.All(IsRelease);
        if (lifecycle.State != ApplicationRunState.Running && !(release && lifecycle.State == ApplicationRunState.Stopping))
            throw new InvalidOperationException("后台服务尚未就绪或正在退出，不能接收新操作。");
        bool maintenanceOnly = values.All(v => v.Key.LogicalId == "UnitMode[0].Request.Maintenance");
        if (!release && !maintenanceOnly && context.AuthorizationVersion != access.Version)
            throw new InvalidOperationException("登录或操作权限已变化，请重新读取后再操作。");
        foreach (var item in values)
        {
            MaintenanceInterlock.CheckWrite(item.Key.LogicalId, item.EngineeringValue);
            if (IsRelease(item)) continue;
            if (item.Key.LogicalId == "UnitMode[0].Request.Maintenance") continue; // 仍必须先通过人员锁的可靠持久化校验。
            if (item.Key.LogicalId == "UnitMode[0].Action[3]")
            {
                if (!access.CanControlModes) throw new UnauthorizedAccessException("当前人员没有模式操作权限。");
                CheckStop(context, item.Key);
                continue;
            }
            if (item.Key.LogicalId is "UnitMode[0].Request.Debug" or "UnitMode[0].Request.Initialize")
            {
                if (!access.CanControlModes) throw new UnauthorizedAccessException("当前人员没有模式操作权限。");
                if (item.Key.LogicalId.EndsWith(".Debug", StringComparison.Ordinal) && !UserAccessRules.CanEditParameters(session.CurrentUser))
                    throw new UnauthorizedAccessException("操作员不能进入调试。");
                CheckManualStopped(context, item.Key);
            }
            else if (!access.CanEditParameters)
                throw new UnauthorizedAccessException("需由工程师或管理员登录并进入手动调试后操作。");
        }
    }

    private void CheckStop(PlcWriteContext context, PlcVariableKey command)
    {
        var keys = new[] { "Mode", "Status", "Action[3]" }
            .Select(name => new PlcVariableKey(command.Protocol, command.DeviceId, "UnitMode[0]." + name)).ToArray();
        var snapshots = data.GetSnapshots(keys);
        var fields = keys.Select(key => snapshots.FirstOrDefault(s => s.Key == key)).ToArray();
        if (fields.Any(f => f?.IsValid != true || f.Quality != PlcObservationQuality.Good ||
            f.ConnectionEpoch != context.ConnectionEpoch || f.ConfigurationVersion != context.ConfigurationStamp) ||
            fields[0]!.EngineeringValue is null || fields[1]!.EngineeringValue is null ||
            !((Convert.ToInt32(fields[0]!.EngineeringValue) == 0 && Convert.ToInt32(fields[1]!.EngineeringValue) == 40) ||
              (Convert.ToInt32(fields[0]!.EngineeringValue) == 20 && Convert.ToInt32(fields[1]!.EngineeringValue) == 50)) ||
            fields[2]!.EngineeringValue is not false)
            throw new InvalidOperationException("停止按钮需当前连接的手动初始化或自动运行有效反馈，且停止位尚未按下。");
    }

    private void CheckManualStopped(PlcWriteContext context, PlcVariableKey command)
    {
        var keys = new[] { "Mode", "Status", "Signal[5]" }
            .Select(name => new PlcVariableKey(command.Protocol,command.DeviceId,"UnitMode[0]."+name)).ToArray();
        var snapshots=data.GetSnapshots(keys);
        var fields=keys.Select(key=>snapshots.FirstOrDefault(s=>s.Key==key)).ToArray();
        if (fields.Any(f=>f?.IsValid!=true || f.Quality!=PlcObservationQuality.Good ||
            f.ConnectionEpoch!=context.ConnectionEpoch || f.ConfigurationVersion!=context.ConfigurationStamp) ||
            fields[0]!.EngineeringValue is null || Convert.ToInt32(fields[0]!.EngineeringValue)!=0 ||
            fields[1]!.EngineeringValue is null || Convert.ToInt32(fields[1]!.EngineeringValue)!=0 ||
            fields[2]!.EngineeringValue is not false)
            throw new InvalidOperationException("需要当前连接的手动、停止且急停信号未触发的有效 PLC 反馈。");
    }
}
