using Company.Application.Share.Login;
using Company.Application.Share.Model;
using Company.Core.Ioc;
using ReactiveUI;
using System.Reactive.Concurrency;
using System.Windows.Input;
using System.Windows.Threading;
namespace Company.Application.Login.ViewModels;

/// <summary>
/// 管理主界面中的账号登录弹窗。认证成功且角色满足登录规则后替换共享会话，
/// 通过弹窗 OK 结果结束流程；此入口不写启动页的记住密码或自动登录缓存。
/// </summary>
public sealed class SignInViewModel : DialogViewModelBase
{
    private readonly IUserAccountService _accounts;
    private readonly ISession _session;
    private string _userName = "", _password = "", _status = "";
    private bool _busy;
    // 切换账号立即清空密码，避免把上一账号的输入用于另一个身份。
    public string UserName { get => _userName; set { if (SetProperty(ref _userName, value)) Password = ""; } }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); RaisePropertyChanged(nameof(CanSubmit)); } }
    public bool CanSubmit => !IsBusy;
    public ICommand SubmitCommand { get; }
    public ICommand CloseCommand { get; }
    /// <summary>建立认证与关闭命令，使用应用 Dispatcher 的调度器发布命令结果。</summary>
    public SignInViewModel(IUserAccountService accounts, ISession session)
    {
        _accounts = accounts; _session = session; Title = "登录账号";
        var scheduler = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        SubmitCommand = ReactiveCommand.CreateFromTask(Submit, outputScheduler: scheduler);
        CloseCommand = ReactiveCommand.Create(() => { if (!IsBusy) CloseDialog(Prism.Dialogs.ButtonResult.Cancel); }, outputScheduler: scheduler);
    }
    /// <summary>校验必填项并认证；处理完成前禁止重复提交与关闭弹窗。</summary>
    private async Task Submit()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrEmpty(Password)) { Status = "请输入用户名和密码。"; return; }
        // 固定本次输入，等待期间即使绑定值变化，本次认证仍使用同一对用户名和密码。
        var name = UserName; var password = Password;
        IsBusy = true; Status = "正在验证账号…";
        bool success = false;
        try
        {
            var user = await _accounts.AuthenticateAsync(name, password);
            if (user is null) { Status = "用户名或密码错误，请重新输入。"; return; }
            var identity = new CurrentUser { Id = user.Id, UserName = user.UserName, Role = user.Role, LoginTime = DateTime.Now };
            // 认证通过后仍检查角色是否属于可登录身份；非法角色不写入共享会话。
            if (!UserAccessRules.IsLoggedIn(identity)) { Status = "账号角色未配置，请联系管理员。"; return; }
            _session.CurrentUser = identity;
            Password = ""; success = true;
        }
        catch { Status = "登录失败，请检查数据库连接后重试。"; }
        finally { IsBusy = false; }
        if (success) CloseDialog(Prism.Dialogs.ButtonResult.OK);
    }
    /// <summary>每次打开清除上次输入和状态，使弹窗从空白登录表单开始。</summary>
    public override void OnDialogOpened(Prism.Dialogs.IDialogParameters parameters) { UserName = ""; Password = ""; Status = "请输入账号信息。"; }
    /// <summary>认证尚未结束时保持弹窗，避免会话在关闭后才被异步替换。</summary>
    public override bool CanCloseDialog() => !IsBusy;
    /// <summary>关闭后清空视图模型中的密码输入。</summary>
    public override void OnDialogClosed() => Password = "";
}
