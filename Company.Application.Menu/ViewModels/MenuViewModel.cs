using Company.Application.Share.Prism;
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

namespace Company.Application.Menu.ViewModels
{
    /// <summary>
    /// 将菜单点击映射为工作区导航或配置弹窗，并把当前活动页归并为菜单高亮分区。
    /// 可用状态来自共享权限服务，执行导航时再校验一次，避免只依赖按钮禁用状态。
    /// </summary>
    internal class MenuViewModel : ReactiveObject
    {
        private readonly IUserAccessService _access;
        public bool CanOpenMemberPages => _access.IsLoggedIn;
        /// <summary>模式页面包含只读状态及维护人员锁管理，不能用设备操作权限限制页面入口。</summary>
        public bool CanOpenModePage => _access.CanOpenPage(ViewNames.ModeControlView);
        public bool CanOpenParameters => _access.CanOpenPage(ViewNames.AxisParameterView);
        public bool IsAdministrator => _access.IsAdministrator;
        private string _activeSection = "";
        public string ActiveSection
        {
            get => _activeSection;
            private set => this.RaiseAndSetIfChanged(ref _activeSection, value);
        }

        /// <summary>按实际活动视图名更新所属菜单分区，多个子页共用同一分区；未知视图取消高亮。</summary>
        internal void UpdateActivePage(string? viewName)
        {
            ActiveSection = viewName switch
            {
                ViewNames.DeviceMonitorView => "设备监控",
                ViewNames.ModeControlView => "模式控制",
                ViewNames.IoMonitorView => "IO监控",
                ViewNames.AxisParameterView or ViewNames.CylinderParameterView => "参数设置",
                ViewNames.RealtimeAlarmView or ViewNames.AlarmHistoryView or ViewNames.AlarmAnalysisView => "报警监控",
                ViewNames.ProductionTodayView or ViewNames.ProductionDailyView or ViewNames.ProductionTrendView => "生产数据",
                ViewNames.ProcessParameterView => "工艺参数",
                ViewNames.OperationLogView => "操作日志",
                _ => ""
            };
        }
        public ICommand SystemConfigCommand { get; }
        public ICommand DeviceMonitorCommand { get; }
        public ICommand ModeControlCommand { get; }
        public ICommand IoMonitorCommand { get; }
        public ICommand AxisParameterCommand { get; }
        public ICommand CylinderParameterCommand { get; }
        public ICommand ProcessParameterCommand { get; }
        public ICommand RealtimeAlarmCommand { get; }
        public ICommand AlarmHistoryCommand { get; }
        public ICommand AlarmAnalysisCommand { get; }
        public ICommand ProductionTodayCommand { get; }
        public ICommand ProductionDailyCommand { get; }
        public ICommand ProductionTrendCommand { get; }
        public ICommand OperationLogCommand { get; }

        /// <summary>建立带权限条件的导航命令，并监听身份切换以刷新菜单可用状态。</summary>
        public MenuViewModel(IUserAccessService access)
        {
            _access = access;
            var scheduler=new SynchronizationContextScheduler(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
            // 身份变化事件可从其他线程发布；WPF 绑定通知统一切回应用 Dispatcher。
            _access.Changed += (_, _) =>
            {
                void Update() { this.RaisePropertyChanged(nameof(CanOpenMemberPages)); this.RaisePropertyChanged(nameof(CanOpenModePage)); this.RaisePropertyChanged(nameof(CanOpenParameters)); this.RaisePropertyChanged(nameof(IsAdministrator)); }
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                if (dispatcher.CheckAccess()) Update(); else dispatcher.InvokeAsync(Update);
            };
            // 各命令按页面用途配置可用条件；Navigate 内还会使用完整页面访问规则复核。
            DeviceMonitorCommand = ReactiveCommand.Create(() => Navigate(ViewNames.DeviceMonitorView),outputScheduler:scheduler);
            ModeControlCommand = ReactiveCommand.Create(() => Navigate(ViewNames.ModeControlView),this.WhenAnyValue(x=>x.CanOpenModePage),outputScheduler:scheduler);
            SystemConfigCommand = ReactiveCommand.Create(SystemConfig,this.WhenAnyValue(x=>x.IsAdministrator),outputScheduler: Scheduler.Immediate);
            IoMonitorCommand = ReactiveCommand.Create(() => Navigate(ViewNames.IoMonitorView),this.WhenAnyValue(x=>x.CanOpenMemberPages),outputScheduler:scheduler);
            AxisParameterCommand = ReactiveCommand.Create(() => Navigate(ViewNames.AxisParameterView),this.WhenAnyValue(x=>x.CanOpenParameters),outputScheduler:scheduler);
            CylinderParameterCommand = ReactiveCommand.Create(() => Navigate(ViewNames.CylinderParameterView),this.WhenAnyValue(x=>x.CanOpenParameters),outputScheduler:scheduler);
            ProcessParameterCommand = ReactiveCommand.Create(() => Navigate(ViewNames.ProcessParameterView),this.WhenAnyValue(x=>x.CanOpenMemberPages),outputScheduler:scheduler);
            RealtimeAlarmCommand = ReactiveCommand.Create(() => Navigate(ViewNames.RealtimeAlarmView),outputScheduler:scheduler);
            AlarmHistoryCommand = ReactiveCommand.Create(() => Navigate(ViewNames.AlarmHistoryView),outputScheduler:scheduler);
            AlarmAnalysisCommand = ReactiveCommand.Create(() => Navigate(ViewNames.AlarmAnalysisView),outputScheduler:scheduler);
            ProductionTodayCommand = ReactiveCommand.Create(() => Navigate(ViewNames.ProductionTodayView),outputScheduler:scheduler);
            ProductionDailyCommand = ReactiveCommand.Create(() => Navigate(ViewNames.ProductionDailyView),outputScheduler:scheduler);
            ProductionTrendCommand = ReactiveCommand.Create(() => Navigate(ViewNames.ProductionTrendView),outputScheduler:scheduler);
            OperationLogCommand = ReactiveCommand.Create(() => Navigate(ViewNames.OperationLogView),this.WhenAnyValue(x=>x.CanOpenMemberPages),outputScheduler:scheduler);
        }

        // 请求 Prism 导航前复核当前身份；异步导航失败通过结果回调记录异常。
        private void Navigate(string viewName)
        {
            if (!_access.CanOpenPage(viewName)) return;
            PrismProvider.RegionManager.RequestNavigate(RegionNames.WorkspaceRegion, viewName,
                result => { if (result.Exception is not null) Company.Logger.Logs.LogError(result.Exception); });
        }

        // 配置入口限定管理员；配置弹窗的提交路径也会独立检查管理员身份。
        private void SystemConfig()
        {
            if (!_access.IsAdministrator) return;
            PrismProvider.DialogService.ShowDialog(ViewNames.ConfigView);
        }
    }
}
