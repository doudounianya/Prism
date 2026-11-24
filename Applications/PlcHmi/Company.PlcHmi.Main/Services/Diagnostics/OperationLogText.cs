using Company.PlcHmi.Shared.PLC.Configuration;
using System.Text.RegularExpressions;

namespace Company.PlcHmi.Main.Services.Diagnostics;

/// <summary>操作记录的中文投影；只解释已有逻辑角色，不改变命令语义或 PLC 地址。</summary>
internal static class OperationLogText
{
    internal static string Field(string logical, string component, PlcVariableBinding? binding = null,
        IReadOnlyList<PlcVariableBinding>? catalog = null)
    {
        string suffix = NormalizePosition(logical.StartsWith(component + ".", StringComparison.Ordinal) ? logical[(component.Length + 1)..] : logical);
        var position = Regex.Match(suffix, @"^(RelativePositions|Positions)\[(\d+)\]\.(.+)$");
        if (position.Success)
        {
            string prefix = position.Groups[1].Value + "[" + position.Groups[2].Value + "]";
            string name = binding?.Metadata.GetValueOrDefault("位置名称") ?? "";
            if (name.Length == 0 && binding is not null)
                name = catalog?.FirstOrDefault(v => v.Protocol == binding.Protocol && v.DeviceId == binding.DeviceId &&
                    v.ComponentId == component && NormalizePosition(v.LogicalId.StartsWith(component + ".", StringComparison.Ordinal)
                        ? v.LogicalId[(component.Length + 1)..] : v.LogicalId).StartsWith(prefix + ".", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(v.Metadata.GetValueOrDefault("位置名称")))?.Metadata.GetValueOrDefault("位置名称") ?? "";
            string kind = position.Groups[1].Value == "Positions" ? "绝对位置" : "相对位置";
            if (name.Length == 0) name = kind + position.Groups[2].Value;
            string field = position.Groups[3].Value switch
            {
                "Position" => kind, "Speed" => "速度", "Acceleration" => "加速度", "Deceleration" => "减速度",
                "Run" => "运行", _ => position.Groups[3].Value
            };
            return name + " · " + field;
        }
        return suffix switch
        {
            "Request.Enable" => "使能", "Request.Disable" => "关闭使能", "Request.Home" => "回原点",
            "Request.Stop" => "停止", "Request.ResetFault" or "ResetFault" => "报警复位",
            "Request.JogPositive" => "正向点动", "Request.JogNegative" => "反向点动",
            "Request.Extend" => "伸出", "Request.Retract" => "缩回",
            "Parameters.JogSpeed" or "st_Parameter.i_Jog_Vel" => "点动速度",
            "st_Parameter.i_Jerk" => "轴加加速度", "st_Parameter.i_Stop_Dec" => "停止减速度",
            "st_Parameter.i_Halt_Dec" => "暂停减速度", "st_Parameter.i_Jog_Dec" => "点动减速度",
            "st_Parameter.i_Jog_Acc" => "点动加速度",
            "Parameters.Retract.ArrivalDelay" => "缩回到位延时", "Parameters.Retract.AlarmTimeout" => "缩回报警延时",
            "Parameters.Extend.ArrivalDelay" => "伸出到位延时", "Parameters.Extend.AlarmTimeout" => "伸出报警延时",
            _ => string.IsNullOrWhiteSpace(binding?.DisplayName) ? suffix : binding.DisplayName
        };
    }

    // 当前轴位置值沿用 PLC 结构字段名，运行请求使用 HMI 逻辑别名。
    // 只统一显示角色，让两种绑定都能引用 Excel 中同一位置名称，不改实际读写键。
    private static string NormalizePosition(string suffix)
    {
        var parameter = Regex.Match(suffix, @"^st_Parameter\.i_(Abs|Rel)_(Pos|Vel|Acc|Dec)\[(\d+)\]$");
        if (parameter.Success)
            return (parameter.Groups[1].Value == "Abs" ? "Positions" : "RelativePositions") + "[" + parameter.Groups[3].Value + "]." +
                (parameter.Groups[2].Value switch { "Pos" => "Position", "Vel" => "Speed", "Acc" => "Acceleration", _ => "Deceleration" });
        var run = Regex.Match(suffix, @"^st_Action\.b_(Abs|Rel)_On\[(\d+)\]$");
        return run.Success ? (run.Groups[1].Value == "Abs" ? "Positions" : "RelativePositions") + "[" + run.Groups[2].Value + "].Run" : suffix;
    }

    /// <summary>末项是序列的目标动作，例如伸出前清缩回仍记为伸出，并保留逐项写入明细。</summary>
    internal static string Action(string action, string logical, string requested, IEnumerable<string>? labels = null)
    {
        var fields = labels?.ToArray() ?? [];
        string field = fields.LastOrDefault() ?? Field(logical, logical.Split('.')[0]);
        string state = requested == "True" ? "True" : "False";
        return action switch
        {
            "MaintenanceLock" => "维护人员上锁", "MaintenanceUnlock" => "维护人员本人解锁",
            "MaintenanceAdminUnlockAll" => "管理员解除全部维护锁",
            "ModeDebugEnter" => "模式控制：进入调试", "ModeDebugExit" => "模式控制：退出调试",
            "ModeDebugLogoutExit" => "退出登录／身份改变：自动清除调试请求",
            "ModeInitialize" => "模式控制：初始化",
            "ModeInitializeRelease" => "模式控制：初始化按钮松开复位",
            "ModeMaintenanceEnter0" or "ModeMaintenanceEnter10" or "ModeMaintenanceEnter20" => "模式控制：进入维护",
            "ModeMaintenanceExit" => "模式控制：全部退出维护",
            "Enable" => "使能", "Disable" => "关闭使能", "Home" => "回原点", "Stop" => "停止",
            "Reset" => "报警复位", "Extend" => "伸出", "Retract" => "缩回", "Move" => field + "：True",
            "PressPositive" => "正向点动（按下）", "PressNegative" => "反向点动（按下）",
            "PressReset" => "报警复位（按下）", "ReleaseMomentary" => field + "（松开／复位）",
            "ToggleCommand" => logical.Length == 0 ? "切换命令（未发送）" : field + "：" + state,
            "SaveParameters" => fields.Length == 0 ? "修改参数（未发送）" : "修改参数：" + string.Join("、", fields.Distinct().Take(8)) + (fields.Length > 8 ? "等" : ""),
            _ => string.IsNullOrWhiteSpace(action) ? "PLC 操作" : action
        };
    }

    internal static string Outcome(string outcome) => outcome switch
    {
        "LocalConfirmed" => "人员锁记录成功", "WriteConfirmed" => "写入成功", "WriteNotConfirmed" => "写入未确认", "Cancelled" => "已取消",
        "Failed" => "操作失败", "NotSent" => "未发送", _ => "未知结果"
    };
}
