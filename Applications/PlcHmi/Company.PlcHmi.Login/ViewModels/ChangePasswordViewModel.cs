using Company.PlcHmi.Shared.Login;
using Company.Core.Ioc;
using Company.Logger;
using ReactiveUI;
using System.Windows.Input;
using System.Reactive.Concurrency;
using System.Windows.Threading;

namespace Company.PlcHmi.Login.ViewModels;

/// <summary>
/// 负责本人改密弹窗的输入、忙碌和成功状态；原密码验证及写库规则由账号服务执行。
/// 已登录时固定为当前账号，未登录时允许根据传入用户名编辑账号后验证原密码。
/// </summary>
public sealed class ChangePasswordViewModel : DialogViewModelBase
{
    private readonly IUserAccountService _accounts;
    private readonly ISession _session;
    private string _name = "", _old = "", _password = "", _confirmation = "", _status = "";
    private bool _busy, _succeeded;
    // 忙碌与成功后均锁定账号输入；成功状态还阻止同一次弹窗再次提交旧密码。
    public bool CanEditUserName => _session.CurrentUser is not { Id: > 0 } && !IsBusy && !Succeeded;
    public bool CanSubmit => !IsBusy && !Succeeded;
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); RaisePropertyChanged(nameof(CanSubmit)); RaisePropertyChanged(nameof(CanEditUserName)); } }
    public bool Succeeded { get => _succeeded; private set { SetProperty(ref _succeeded, value); RaisePropertyChanged(nameof(CanSubmit)); RaisePropertyChanged(nameof(CanEditUserName)); } }
    public string UserName { get => _name; set => SetProperty(ref _name, value); }
    public string OldPassword { get => _old; set => SetProperty(ref _old, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string Confirmation { get => _confirmation; set => SetProperty(ref _confirmation, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand SubmitCommand { get; }
    public ICommand CloseCommand { get; }
    /// <summary>建立改密及关闭命令，让命令结果通过 UI 调度器通知绑定。</summary>
    public ChangePasswordViewModel(IUserAccountService accounts, ISession session)
    {
        _accounts = accounts; _session = session; Title = "修改本人密码";
        var scheduler = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        SubmitCommand = ReactiveCommand.CreateFromTask(Submit, outputScheduler: scheduler);
        CloseCommand = ReactiveCommand.Create(() => { if (!IsBusy) CloseDialog(Prism.Dialogs.ButtonResult.Cancel); }, outputScheduler: scheduler);
    }
    /// <summary>提交原密码和两次新密码；成功后擦除输入，并保留成功提示直至关闭。</summary>
    private async Task Submit()
    {
        if (!CanSubmit) return;
        IsBusy = true; Status = "正在验证原密码…";
        try
        {
            await _accounts.ChangePasswordAsync(UserName, OldPassword, Password, Confirmation);
            OldPassword = Password = Confirmation = ""; Succeeded = true;
            Status = "密码已修改，下次登录请使用新密码。";
        }
        // 可预期的输入/权限/业务拒绝直接显示服务消息；其他异常统一提示数据库连接问题。
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { Status = ex.Message; }
        catch { Status = "修改失败，请检查数据库连接后重试。"; Logs.LogWarning("修改本人密码失败，请检查账号数据库连接。"); }
        finally { IsBusy = false; }
    }
    /// <summary>优先使用当前登录账号，其次使用调用方提供的用户名，并重置成功状态。</summary>
    public override void OnDialogOpened(Prism.Dialogs.IDialogParameters parameters)
    {
        UserName = _session.CurrentUser is { Id: > 0 } user ? user.UserName : parameters.GetValue<string>("UserName") ?? "";
        Succeeded = false; Status = "请输入原密码及两次新密码。";
        RaisePropertyChanged(nameof(CanEditUserName));
    }
    /// <summary>改密请求未结束时禁止关闭，避免隐藏进行中的提交结果。</summary>
    public override bool CanCloseDialog() => !IsBusy;
    /// <summary>关闭时清空原密码及两次新密码输入。</summary>
    public override void OnDialogClosed() => OldPassword = Password = Confirmation = "";
}
