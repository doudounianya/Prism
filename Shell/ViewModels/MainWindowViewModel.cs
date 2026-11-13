/*
 * 文件职责与维护说明：
 * 启动窗口的导航协调器，先显示登录页，认证成功后进入初始化页，最后显示主界面。
 * 主界面里的重新登录使用独立弹窗，不应再次走这里的初始化事件。
 */
using Company.Application.Share.Events;
using Company.Application.Share.Model;
using Company.Application.Share.Prism;
using Company.Core.Ioc;
using Newtonsoft.Json.Linq;
using Prism.Commands;
using Prism.Events;
using Prism.Modularity;
using Prism.Mvvm;
using ReactiveUI;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Shell.ViewModels
{
    internal class MainWindowViewModel : ReactiveObject
    {
        public ICommand LoadedCommand { get; }
        public IModuleManager ModuleManager { get; }
        public IRegionManager RegionManager { get; }
        public MainWindowViewModel(IModuleManager moduleManager, IRegionManager regionManager)
        {
            ModuleManager = moduleManager;
            RegionManager = regionManager;
            LoadedCommand = ReactiveCommand.Create(Loaded, outputScheduler: Scheduler.Immediate);
        }

        // 沿用原启动链：登录模块→登录区域→登录成功事件→初始化模块。
        // 主页面重新登录不发布这个启动事件，因此不会重复恢复日志或重建通讯服务。
        private void Loaded()
        {
            IRegion mainRegion = PrismProvider.RegionManager.Regions[RegionNames.MainRegion];
            mainRegion.NavigationService.Navigated += NavigationService_Navigated;

            //第一步，加载Login模块
            PrismProvider.ModuleManager.LoadModule(ModuleNames.ApplicationLoginModule);
            //第二步，导航Login区域
            PrismProvider.RegionManager.RequestNavigate(RegionNames.MainRegion, ViewNames.LoginView);

            //订阅用户登录成功事件
            PrismProvider.EventAggregator.GetEvent<LoginSuccessEvent>().Subscribe(OnLogined, ThreadOption.UIThread);
        }

        // 首次登录完成只切换到初始化页；耗时初始化由该页面协调，主业务界面稍后再显示。
        private void OnLogined(CurrentUser user)
        {
            // 登录窗口采用紧凑无标题栏外观；认证完成后恢复主窗口标题栏、按钮及尺寸。
            // 只调整本应用窗口的呈现，不替换框架窗口、导航区域或后台服务生命周期。
            RestoreMainWindowControls();
            MainWindow.Width = 800;
            MainWindow.Height = 450;
            // 尺寸恢复后仍以屏幕工作区居中展示准备进度；主界面导航完成后沿用原最大化流程。
            var area = SystemParameters.WorkArea;
            MainWindow.Left = area.Left + (area.Width - MainWindow.Width) / 2;
            MainWindow.Top = area.Top + (area.Height - MainWindow.Height) / 2;
            PrismProvider.ModuleManager.LoadModule(ModuleNames.ApplicationInitializeModule);
            PrismProvider.RegionManager.RequestNavigate(RegionNames.MainRegion, ViewNames.InitializeView);
        }

        /// <summary>
        /// 恢复 MahApps 标题栏和窗口按钮，并为当前主题的三个 46 宽按钮保留布局空间。
        /// 登录切换后最大化时，按钮容器可能测量为 0，内部按钮便会溢出到屏幕右侧。
        /// 登录完成和进入主界面时都显式恢复，避免无标题栏登录状态影响按钮显示或操作。
        /// </summary>
        private void RestoreMainWindowControls()
        {
            MainWindow.ResizeMode = ResizeMode.CanResize;
            if (MainWindow is MahApps.Metro.Controls.MetroWindow metro)
            {
                metro.UseNoneWindowStyle = false;
                metro.ShowTitleBar = true;
                metro.ShowMinButton = true;
                metro.ShowMaxRestoreButton = true;
                metro.ShowCloseButton = true;
                metro.IsMinButtonEnabled = true;
                metro.IsMaxRestoreButtonEnabled = true;
                metro.IsCloseButtonEnabled = true;
                metro.WindowButtonCommands.MinWidth = 3 * 46;
            }
            MainWindow.WindowStyle = WindowStyle.SingleBorderWindow;
        }

        private Window MainWindow { get; set; } = Application.Current.MainWindow;

        //当主区域导航时
        private void NavigationService_Navigated(object sender, RegionNavigationEventArgs e)
        {
            switch (e.Uri.OriginalString)
            {
                case ViewNames.LoginView:
                    break;

                case ViewNames.MainView:
                    RestoreMainWindowControls();
                    MainWindow.WindowState = WindowState.Maximized;

                    break;
                default:
                    break;
            }
        }

        private string _title = "CRT HMI";
        public string Title
        {
            get => _title;
            set => this.RaiseAndSetIfChanged(ref this._title, value);
        }

}
}
