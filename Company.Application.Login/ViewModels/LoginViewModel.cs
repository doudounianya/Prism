using Company.Application.Share.Events;
using Company.Application.Share.Login;
using Company.Application.Share.Model;
using Company.Core.Cache;
using Company.Core.Helper;
using Company.Core.Ioc;
using Company.Database;
using Company.Logger;
using ReactiveUI;
using System;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using System.Windows.Threading;
using Company.Application.Login.Services;

namespace Company.Application.Login.ViewModels
{
    /// <summary>
    /// 管理启动登录页的账号输入、访客进入、登录偏好和延迟自动登录。
    /// 认证成功后替换共享会话并发布登录成功事件；数据库操作交给账号服务，界面状态由本模型维护。
    /// </summary>
    class LoginViewModel : ReactiveObject
    {
        // 仓储构造也会连接数据库；延迟到后台认证任务中创建。
        private IUserAccountService Accounts { get; }
        private bool _isChangingPassword;
        public bool IsChangingPassword { get => _isChangingPassword; private set => this.RaiseAndSetIfChanged(ref _isChangingPassword, value); }
        public System.Windows.Input.ICommand ChangePasswordCommand { get; }
        // 用户主动操作后阻止延迟自动登录；成功标记还防止重复发布导航所依赖的登录事件。
        private bool _loginAttempted;
        private bool _loginSucceeded;
        private bool _isLoggingIn;
        public bool IsLoggingIn
        {
            get => _isLoggingIn;
            private set => this.RaiseAndSetIfChanged(ref _isLoggingIn, value);
        }
        private string _statusMessage = "";
        public string StatusMessage
        {
            get => _statusMessage;
            private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }
        private ISession Session { get; }
        private readonly RememberedLoginStore _rememberedLogin;
        private Task _preferencesLoaded = Task.CompletedTask;
        private bool _isLoadingPreferences = true, _applyingPreferences;
        private long _inputRevision;
        /// <summary>偏好读取和加密迁移在后台进行；期间保留窗口响应，但暂不提交认证。</summary>
        public bool IsLoadingPreferences
        {
            get => _isLoadingPreferences;
            private set => this.RaiseAndSetIfChanged(ref _isLoadingPreferences, value);
        }
        public ReactiveCommand<Unit, Unit> LoginCommand { get; private set; }
        public System.Windows.Input.ICommand GuestCommand { get; }
        /// <summary>恢复登录偏好、建立 UI 命令，并安排一次可被用户操作抑制的延迟自动登录。</summary>
        public LoginViewModel(
            ICacheManager cacheManager,
            ISession session, 
            IUserAccountService accounts)
        {
            Session = session;
            Accounts = accounts;
            _rememberedLogin = new RememberedLoginStore(cacheManager);
            StatusMessage = "正在读取登录偏好…";
            // 访客仍走统一的登录成功事件；空身份由共享权限规则解释为访客访问范围。
            GuestCommand = ReactiveCommand.Create(() =>
            {
                if (IsLoadingPreferences || IsLoggingIn || IsChangingPassword || _loginSucceeded) return;
                _loginAttempted = true; _loginSucceeded = true; IsAutoLogin = false; ShowPassword = false;
                var guest = new CurrentUser();
                CurrentUser = guest; Session.CurrentUser = guest;
                PrismProvider.EventAggregator.GetEvent<LoginSuccessEvent>().Publish(guest);
            }, this.WhenAnyValue(x => x.IsLoggingIn, x => x.IsChangingPassword, x => x.IsLoadingPreferences, (busy, changing, loading) => !busy && !changing && !loading),
                outputScheduler: new SynchronizationContextScheduler(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher)));

            LoginCommand = ReactiveCommand.CreateFromTask(ExecuteLoginAsync,
                this.WhenAnyValue(x => x.IsLoggingIn, x => x.IsChangingPassword, x => x.IsLoadingPreferences, (busy, changing, loading) => !busy && !changing && !loading),
                outputScheduler: new SynchronizationContextScheduler(
                    new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher)));
            ChangePasswordCommand = ReactiveCommand.Create(() =>
            {
                if (IsLoadingPreferences || IsLoggingIn || IsChangingPassword || _loginSucceeded) return;
                _loginAttempted = true; IsChangingPassword = true;
                try
                {
                    PrismProvider.DialogService.ShowDialog(Company.Application.Share.Prism.ViewNames.ChangePasswordView,
                        new Prism.Dialogs.DialogParameters { { "UserName", CurrentUser.UserName } });
                    // 改密弹窗关闭后保留账号名、清除原输入密码，要求明确使用当前有效密码重新登录。
                    CurrentUser = new CurrentUser { UserName = CurrentUser.UserName };
                    StatusMessage = "请输入密码后登录；修改成功后请使用新密码。";
                }
                finally { IsChangingPassword = false; }
            }, this.WhenAnyValue(x => x.IsLoggingIn, x => x.IsChangingPassword, x => x.IsLoadingPreferences, (busy, changing, loading) => !busy && !changing && !loading),
                outputScheduler: new SynchronizationContextScheduler(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher)));
            _preferencesLoaded = LoadUserCacheAsync();
            _ = AutoLoginAsync();
        }

        /// <summary>等待页面稳定后回到界面线程检查偏好，仅在尚无人工登录/改密等操作时尝试认证。</summary>
        private async Task AutoLoginAsync()
        {
            try
            {
                // 旧缓存迁移完成后再开始延迟，避免使用尚未解密或未可靠保存的凭据。
                await _preferencesLoaded.ConfigureAwait(false);
                await Task.Delay(1500).ConfigureAwait(false);
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted) return;
                // Delay 不捕获界面上下文；显式派发后才读写绑定状态并执行认证流程。
                await dispatcher.InvokeAsync(async () =>
                {
                    if (IsAutoLogin && !_loginAttempted && !_loginSucceeded && !IsChangingPassword)
                        await ExecuteLoginAsync();
                }).Task.Unwrap();
            }
            catch (TaskCanceledException) { /* 退出时不再自动登录。 */ }
            catch (Exception exception) { Logs.LogError(exception); }
        }

        private bool isRemember;
        public bool IsRemember
        {
            get => isRemember;
            set
            {
                if (isRemember == value) return;
                if (!_applyingPreferences) { ++_inputRevision; _loginAttempted = true; }
                this.RaiseAndSetIfChanged(ref isRemember, value);
            }

        }

        private bool isAutoLogin;
        public bool IsAutoLogin
        {
            get => isAutoLogin;
            set
            {
                if (isAutoLogin == value) return;
                if (!_applyingPreferences) { ++_inputRevision; _loginAttempted = true; }
                this.RaiseAndSetIfChanged(ref isAutoLogin, value);
            }
        }

        private CurrentUser currentUser = new CurrentUser() { UserName = "admin" };

        // 替换身份时额外通知两个包装属性，保证用户名和 PasswordBox 的绑定随对象一起刷新。
        public CurrentUser CurrentUser
        {
            get => currentUser;
            set
            {
                this.RaiseAndSetIfChanged(ref currentUser, value);
                this.RaisePropertyChanged(nameof(UserName));
                this.RaisePropertyChanged(nameof(Password));
            }
        }

        // 记住的密码只属于原账号；编辑用户名后必须重新输入，避免沿用 admin 的密码。
        public string UserName
        {
            get => CurrentUser.UserName;
            set
            {
                if (CurrentUser.UserName == value) return;
                ++_inputRevision;
                CurrentUser.UserName = value;
                Password = "";
                ShowPassword = false;
                IsAutoLogin = false;
                _loginAttempted = true;
                this.RaisePropertyChanged();
                StatusMessage = "已切换账号，请输入该账号的密码。";
            }
        }

        public string Password
        {
            get => CurrentUser.Password;
            set
            {
                if (CurrentUser.Password == value) return;
                ++_inputRevision; _loginAttempted = true;
                CurrentUser.Password = value;
                this.RaisePropertyChanged();
            }
        }

        private bool _showPassword;
        public bool ShowPassword
        {
            get => _showPassword;
            set => this.RaiseAndSetIfChanged(ref _showPassword, value);
        }

        /// <summary>以当前输入快照完成认证、尽力保存偏好，再一次性提交会话身份及登录成功事件。</summary>
        private async Task ExecuteLoginAsync()
        {
            // 命令可用状态之外保留方法级防重入，避免认证、改密与成功后的重复提交交叠。
            if (IsLoadingPreferences || IsLoggingIn || IsChangingPassword || _loginSucceeded) return;
            _loginAttempted = true;
            if (string.IsNullOrEmpty(CurrentUser.UserName) || string.IsNullOrEmpty(CurrentUser.Password))
            {
                StatusMessage = "请输入用户名和密码。";
                return;
            }
            // 输入可以继续编辑，但一次认证及其缓存始终使用同一份账号快照。
            var user = new CurrentUser { UserName = CurrentUser.UserName, Password = CurrentUser.Password };
            bool remember = IsRemember, autoLogin = IsAutoLogin;
            IsLoggingIn = true;
            StatusMessage = "正在验证账号，请稍候…";
            try
            {
                var member = await Accounts.AuthenticateAsync(user.UserName, user.Password);
                // 无身份表示凭据不匹配，保持当前页面；服务异常则由下方 catch 给出连接失败提示。
                if (member is null)
                {
                    StatusMessage = "用户名或密码错误。请确认完整用户名，并重新输入该账号的密码。";
                    return;
                }
                user.LoginTime = DateTime.Now;
                user.Id = member.Id;
                user.UserName = member.UserName;
                user.Role = member.Role;
                StatusMessage = "正在保存登录偏好…";
                await Task.Run(() => SaveUserCache(user, remember, autoLogin));
                // 缓存只保存受当前 Windows 用户保护的密文；会话和成功事件只携带身份，不保留登录密码。
                user.Password = "";
                // 也清空即将替换的输入模型，避免其他仍持有旧表单引用的绑定保留明文。
                CurrentUser.Password = "";
                // await 回到界面线程，再更新会话并触发原有导航事件。
                CurrentUser = user;
                Session.CurrentUser = user;
                _loginSucceeded = true;
                ShowPassword = false;
                StatusMessage = "登录成功，正在加载…";
                PrismProvider.EventAggregator.GetEvent<LoginSuccessEvent>().Publish(user);
            }
            catch (Exception exception)
            {
                _loginSucceeded = false;
                StatusMessage = "登录失败，请检查数据库连接后重试。";
                Logs.LogError(exception);
            }
            finally { user.Password = ""; IsLoggingIn = false; }
        }


        /// <summary>保存本次认证开始时的偏好快照；缓存写入失败只记录日志，不推翻已通过的认证。</summary>
        private void SaveUserCache(CurrentUser user, bool remember, bool autoLogin)
        {
            try
            {
                _rememberedLogin.Save(user, remember, autoLogin);
            }
            catch (Exception exception)
            {
                // 登录偏好涉及凭据，不把异常载荷或序列化上下文写入日志。
                Logs.LogWarning("身份验证已通过，但登录偏好未能完整保存；下次启动请重新输入。异常类型：" + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 恢复缓存中的账号输入和登录偏好；这里只填充登录表单，不建立已认证的会话。
        /// </summary>
        private async Task LoadUserCacheAsync()
        {
            long revision = _inputRevision;
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            try
            {
                var state = await Task.Run(() => _rememberedLogin.Load()).ConfigureAwait(false);
                if (dispatcher.HasShutdownStarted) return;
                await dispatcher.InvokeAsync(() =>
                {
                    // 读取期间用户仍可编辑；迟到的缓存不覆盖人工输入或已修改的偏好。
                    if (_inputRevision == revision && !_loginAttempted && !_loginSucceeded)
                    {
                        _applyingPreferences = true;
                        try
                        {
                            CurrentUser = new CurrentUser { UserName = state.UserName, Password = state.Password };
                            IsRemember = state.Remember; IsAutoLogin = state.AutoLogin;
                            StatusMessage = state.Notice ?? "";
                        }
                        finally { _applyingPreferences = false; }
                    }
                    else if (StatusMessage == "正在读取登录偏好…")
                        StatusMessage = "请输入用户名和密码。";
                    IsLoadingPreferences = false;
                });
            }
            catch (Exception exception)
            {
                if (dispatcher.HasShutdownStarted) return;
                await dispatcher.InvokeAsync(() =>
                {
                    if (_inputRevision == revision) { CurrentUser = new CurrentUser { UserName = "admin" }; IsRemember = false; }
                    IsAutoLogin = false; IsLoadingPreferences = false;
                    StatusMessage = "登录偏好无法恢复，请手动输入账号信息。";
                });
                Logs.LogWarning("登录偏好无法恢复；请手动输入。异常类型：" + exception.GetType().Name);
            }
        }
    }
}
