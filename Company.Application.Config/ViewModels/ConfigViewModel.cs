using Company.Application.Config.Services;
using Company.Application.Share.Config;
using Company.Core.Ioc;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Company.Application.Share.Login;

namespace Company.Application.Config.ViewModels
{
    /// <summary>
    /// 系统配置弹窗的命令入口，暴露配置管理器/提供器供绑定，并连接用户管理与本人改密弹窗。
    /// 提交时重新检查当前会话的管理员身份，先持久化，再发出变更通知并返回 OK。
    /// </summary>
    internal class ConfigViewModel : DialogViewModelBase
    {
        public SystemConfigManager SystemConfigManager { get; }
        public SystemConfigProvider SystemConfigProvider { get; }

        public ICommand OKCommand { get; }
        public ICommand UserManagementCommand { get; }
        public ICommand ChangePasswordCommand { get; }
        public bool IsAdministrator => _session.CurrentUser is { Id: > 0, Role: (int)UserRole.Administrator };
        private readonly ISession _session;
        /// <summary>接入当前配置与会话，建立保存、用户管理和本人改密命令。</summary>
        public ConfigViewModel(SystemConfigManager systemConfigManager, SystemConfigProvider systemConfigProvider, ISession session)
        {
            Title = "系统设置";
            _session = session;
            SystemConfigManager = systemConfigManager;
            SystemConfigProvider = systemConfigProvider;
            OKCommand = ReactiveCommand.Create(Submit, outputScheduler: Scheduler.Immediate);
            UserManagementCommand = ReactiveCommand.Create(() =>
            {
                if (IsAdministrator) PrismProvider.DialogService.ShowDialog(Company.Application.Share.Prism.ViewNames.UserManagementView);
            }, outputScheduler: Scheduler.Immediate);
            ChangePasswordCommand = ReactiveCommand.Create(() => PrismProvider.DialogService.ShowDialog(Company.Application.Share.Prism.ViewNames.ChangePasswordView), outputScheduler: Scheduler.Immediate);
        }

        // 未建立独立编辑副本；保存的是管理器当前持有的配置对象，通知发生在写入完成之后。
        private void Submit()
        {
            // 权限在执行时复核，防止弹窗打开后会话变化仍能完成保存。
            if (!IsAdministrator) { CloseDialog(ButtonResult.Cancel); return; }
            SystemConfigManager.Save();//保存参数到本机JSON
            SystemConfigProvider.Invoke();//通知应用软件配置已更新
            CloseDialog(ButtonResult.OK);
        }
        /// <summary>每次打开按当前会话刷新管理员绑定状态。</summary>
        public override void OnDialogOpened(Prism.Dialogs.IDialogParameters parameters) => RaisePropertyChanged(nameof(IsAdministrator));
    }
}
