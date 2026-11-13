using Company.Application.Share.Login;
using Company.Core.Ioc;
using Company.Logger;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Reactive.Concurrency;
using System.Windows.Threading;

namespace Company.Application.Login.ViewModels;

/// <summary>
/// 提供管理员用户列表、创建、删除及重置他人密码的弹窗交互。
/// 本层负责输入、确认提示与忙碌状态；账号服务在执行每个操作时再次验证数据库中的管理员身份。
/// </summary>
public sealed class UserManagementViewModel : DialogViewModelBase
{
    private readonly IUserAccountService _accounts;
    private readonly ISession _session;
    private bool _busy;
    private string _status = "", _newName = "", _password = "", _confirmation = "", _resetPassword = "", _resetConfirmation = "";
    private UserRole _role = UserRole.Operator;
    private UserAccountInfo? _selected;
    public bool IsAdministrator => _session.CurrentUser is { Id: > 0, Role: (int)UserRole.Administrator };
    public bool CanEdit => IsAdministrator && !_busy;
    // 当前账号不走管理员重置入口；本人必须在改密弹窗验证原密码。
    public bool CanReset => CanEdit && SelectedUser is not null && SelectedUser.Id != _session.CurrentUser?.Id;
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); RaisePropertyChanged(nameof(CanEdit)); RaisePropertyChanged(nameof(CanReset)); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string NewUserName { get => _newName; set => SetProperty(ref _newName, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string Confirmation { get => _confirmation; set => SetProperty(ref _confirmation, value); }
    public string ResetPassword { get => _resetPassword; set => SetProperty(ref _resetPassword, value); }
    public string ResetConfirmation { get => _resetConfirmation; set => SetProperty(ref _resetConfirmation, value); }
    public UserRole SelectedRole { get => _role; set => SetProperty(ref _role, value); }
    public IReadOnlyList<UserRoleChoice> Roles => UserRoles.Choices;
    public ObservableCollection<UserAccountInfo> Users { get; } = [];
    // 改选用户时清空重置密码，避免把为上一行准备的密码意外提交给新目标。
    public UserAccountInfo? SelectedUser { get => _selected; set { SetProperty(ref _selected, value); ResetPassword = ResetConfirmation = ""; RaisePropertyChanged(nameof(CanReset)); } }
    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ResetPasswordCommand { get; }
    public ICommand CloseCommand { get; }
    /// <summary>建立用户管理命令，异步服务返回后在 UI 上更新列表及状态。</summary>
    public UserManagementViewModel(IUserAccountService accounts, ISession session)
    {
        _accounts = accounts; _session = session; Title = "用户管理";
        var scheduler = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        RefreshCommand = ReactiveCommand.CreateFromTask(() => Execute(LoadUsers, "用户列表已更新。"), outputScheduler: scheduler);
        CreateCommand = ReactiveCommand.CreateFromTask(() => Execute(async () =>
        {
            await _accounts.CreateUserAsync(NewUserName, Password, Confirmation, SelectedRole);
            Password = Confirmation = NewUserName = "";
            await LoadUsers();
        }, "用户已创建。"), outputScheduler: scheduler);
        ResetPasswordCommand = ReactiveCommand.CreateFromTask(() => Execute(async () =>
        {
            if (SelectedUser is null) throw new ArgumentException("请先选择用户。");
            await _accounts.ResetPasswordAsync(SelectedUser.Id, ResetPassword, ResetConfirmation);
            ResetPassword = ResetConfirmation = "";
        }, "密码已重置，该用户下次登录时使用新密码。"), outputScheduler: scheduler);
        DeleteCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (!CanEdit || SelectedUser is not { } user) return;
            // 删除前固定选中的账号对象并确认；真正的权限、自删及最后管理员保护仍由服务校验。
            if (MessageBox.Show(System.Windows.Application.Current.MainWindow, $"确认删除用户“{user.UserName}”？", "删除用户", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await Execute(async () => { await _accounts.DeleteUserAsync(user.Id); await LoadUsers(); }, "用户已删除。");
        }, outputScheduler: scheduler);
        CloseCommand = ReactiveCommand.Create(() => { if (!IsBusy) CloseDialog(Prism.Dialogs.ButtonResult.Cancel); }, outputScheduler: scheduler);
    }
    /// <summary>刷新列表后尽量恢复原选中主键；原用户已删除时选择首行。</summary>
    private async Task LoadUsers()
    {
        var selectedId = SelectedUser?.Id;
        var users = await _accounts.GetUsersAsync();
        // 等待账号服务后回到调用时的界面上下文，再更新绑定的 ObservableCollection。
        Users.Clear(); foreach (var user in users) Users.Add(user);
        SelectedUser = Users.FirstOrDefault(u => u.Id == selectedId) ?? Users.FirstOrDefault();
    }
    /// <summary>共用防重入、界面权限提示与异常反馈，确保所有完成路径都解除忙碌状态。</summary>
    private async Task Execute(Func<Task> action, string success)
    {
        if (IsBusy) return;
        if (!IsAdministrator) { Status = "只有管理员可以管理用户。"; return; }
        IsBusy = true; Status = "正在处理…";
        try { await action(); Status = success; }
        // 业务拒绝保留具体原因供管理员修正；未知异常不向状态栏暴露数据库内部细节。
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { Status = ex.Message; }
        catch { Status = "操作失败，请检查数据库连接后重试。"; Logs.LogWarning("用户管理操作失败，请检查账号数据库连接。"); }
        finally { IsBusy = false; }
    }
    /// <summary>打开后启动列表加载，使用统一执行包装显示权限或加载错误。</summary>
    public override void OnDialogOpened(Prism.Dialogs.IDialogParameters parameters)
        => _ = Execute(LoadUsers, "请选择用户进行管理，或创建新用户。");
    /// <summary>管理请求执行期间保持弹窗，防止列表操作和关闭清理相互交叠。</summary>
    public override bool CanCloseDialog() => !IsBusy;
    /// <summary>关闭时清空密码输入及用户列表，缩短敏感输入在模型中的保留时间。</summary>
    public override void OnDialogClosed() { Password = Confirmation = ResetPassword = ResetConfirmation = ""; Users.Clear(); }
}
