using Company.Platform.Shared.Model;
namespace Company.PlcHmi.Shared.Login;

/// <summary>提供当前会话的页面访问和操作能力；Changed 通知与 Version 便于界面发现授权上下文变化。</summary>
public interface IUserAccessService
{
    bool IsLoggedIn { get; }
    bool CanControlModes { get; }
    bool CanEditParameters { get; }
    bool IsAdministrator { get; }
    /// <summary>授权状态版本；需要跨异步操作校验权限的调用方应保存并在提交前比较。</summary>
    long Version { get; }
    event EventHandler? Changed;
    /// <summary>按共享导航名判断页面是否可打开；具体参数编辑或设备控制仍需检查对应能力。</summary>
    bool CanOpenPage(string viewName);
}
