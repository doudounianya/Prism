namespace Company.Application.Main.Presentation;

/// <summary>仅供界面投影的快照。显示可保留上次值，CanWrite 必须另行通过当前读取和授权校验。</summary>
public sealed record ModeControlDisplay(int? Mode, int? Status, IReadOnlyList<bool?> Actions,
    IReadOnlyList<bool?> Signals, bool CanWrite)
{
    public static ModeControlDisplay Empty { get; } = new(null, null, new bool?[9], new bool?[6], false);
    /// <summary>独立于一般模式操作的调试角色许可，图内和底部按钮共用，不能借查看页签获得权限。</summary>
    public bool CanDebug { get; init; }
    public bool? Action(int index) => index >= 0 && index < Actions.Count ? Actions[index] : null;
    public bool? Signal(int index) => index >= 0 && index < Signals.Count ? Signals[index] : null;

    /// <summary>明确的按钮白名单；返回目标操作下标和值，不修改任何模式或状态枚举。</summary>
    public bool TryRequest(string action, out int index, out bool value)
    {
        (index, value) = action switch
        {
            "DebugEnter" => (6, true), "DebugExit" => (6, false), "Initialize" => (7, true),
            "MaintenanceEnter0" or "MaintenanceEnter10" or "MaintenanceEnter20" => (8, true),
            "MaintenanceExit" => (8, false), _ => (-1, false)
        };
        if (!CanWrite || index < 0 || Action(index) is null || Mode is null || Status is null) return false;
        if (index == 6 && !CanDebug) return false;
        // 用户确认急停来源为 b_Signal[5]，True 表示急停。未知也不能当作未急停。
        // 维护人员登记不是设备动作；原请求的清零不因急停而丢失。
        if (value && index != 8 && Signal(5) != false) return false;
        return action switch
        {
            "DebugEnter" => Mode == 0 && Status == 0 && Action(6) == false,
            "DebugExit" => Action(6) == true,
            "Initialize" => Mode == 0 && Status == 0 && Action(7) == false,
            "MaintenanceEnter0" => Status == 0 && Action(8) == false,
            "MaintenanceEnter10" => Status == 10 && Action(8) == false,
            "MaintenanceEnter20" => Status == 20 && Action(8) == false,
            "MaintenanceExit" => Action(8) == true,
            _ => false
        };
    }
    public static string ModeName(int? mode) => mode switch { 0 => "手动模式", 10 => "维护模式", 20 => "自动模式", null => "未知", _ => "未知模式：" + mode };
    // 当前状态栏只显示名称；状态码仍用于 PLC 判断及原流程图节点，不参与此处文字格式。
    public static string StatusName(int? status) => status switch { 0 => "停止", 10 => "故障", 20 => "暂停", 30 => "调试", 40 => "初始化", 50 => "运行", 60 => "维护任务", 70 => "急停", null => "未知", _ => "未知状态" };
}

public sealed class ModeActionEventArgs(string action) : EventArgs
{
    public string Action { get; } = action;
}
