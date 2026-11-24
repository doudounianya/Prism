using Company.Platform.Shared.Config;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.Prism;
using Company.Core.Ioc;
using Company.Logger;
using ReactiveUI;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Company.Core.Runtime;

namespace Company.PlcHmi.Startup.ViewModels
{
    /// <summary>
    /// 编排进入主界面前的模块加载、历史恢复和 PLC 启动，并向准备页报告进度。
    /// 服务就绪后隐藏准备显示数据与布局；业务页仍仅在真正显示时申请当前范围采集。
    /// </summary>
    class InitializeViewModel : ReactiveObject
    {
        private string _message = "正在加载应用配置...";
        // 初始化成功后保持标记，阻止重复 Loaded 再次启动；失败时恢复以允许后续重试。
        private bool _isNavigating;
        private FrameworkElement? _preloadContent;
        private Size _preloadSize = new(1280, 880);
        /// <summary>仅在初始化期间挂载主工作区；隐藏容器不触发页面导航或操作权限。</summary>
        public FrameworkElement? PreloadContent
        {
            get => _preloadContent;
            private set => this.RaiseAndSetIfChanged(ref _preloadContent, value);
        }
        public double PreloadWidth => _preloadSize.Width;
        public double PreloadHeight => _preloadSize.Height;
        private readonly IApplicationLifecycle _lifecycle;
        public string Message
        {
            get => _message;
            set => this.RaiseAndSetIfChanged(ref _message, value);
        }

        public ICommand LoadedCommand { get; }

        /// <summary>读取软件名称用于进度提示，并建立由视图加载事件触发的异步初始化命令。</summary>
        public InitializeViewModel(ISoftwareConfigProvider configProvider, IApplicationLifecycle lifecycle)
        {
            _lifecycle = lifecycle;
            Message = $"正在加载 {configProvider.SoftwareConfig.Name}...";
            LoadedCommand = ReactiveCommand.CreateFromTask(InitAsync,
                outputScheduler: new SynchronizationContextScheduler(
                    new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher)));
        }

        /// <summary>按消费者注册、历史恢复、PLC 启动的顺序准备主工作区，然后请求主界面导航。</summary>
        private async Task InitAsync()
        {
            if (_isNavigating)
            {
                return;
            }

            _isNavigating = true;
            try
            {
                // 主模块先完成后台消费者注册，再启动采集，避免丢失首次有效状态。
                Message = "正在注册 PLC 监控服务...";
                // 先让初始化页面完成显示；模块注册和导航仍由界面线程负责。
                await Dispatcher.Yield(DispatcherPriority.Background);
                PrismProvider.ModuleManager.LoadModule(ModuleNames.PlcHmiMainModule);
                await _lifecycle.StartAsync(new Progress<string>(message => Message = message));
                await PrepareDisplayDataAsync();
                Message = "准备完成，正在启动...";
                PrismProvider.RegionManager.RequestNavigate(RegionNames.MainRegion, ViewNames.MainView);
            }
            // 保留准备页并给出统一失败提示，详细异常写日志；重置标记后允许重新触发。
            catch (Exception ex)
            {
                _isNavigating = false;
                Message = "主界面加载失败，请查看运行日志。";
                Logs.LogError(ex);
            }
        }

        /// <summary>
        /// 在不可见的最终工作区内准备单例页面、显示行、模板及首次布局。
        /// WPF 控件必须在 UI 线程创建；每页之间让出调度，不把控件交给后台线程。
        /// </summary>
        private async Task PrepareDisplayDataAsync()
        {
            string[] pages =
            [
                ViewNames.DeviceMonitorView, ViewNames.ModeControlView, ViewNames.IoMonitorView,
                ViewNames.AxisParameterView, ViewNames.CylinderParameterView, ViewNames.ProcessParameterView,
                ViewNames.RealtimeAlarmView, ViewNames.AlarmHistoryView, ViewNames.AlarmAnalysisView,
                ViewNames.ProductionTodayView, ViewNames.ProductionDailyView, ViewNames.ProductionTrendView,
                ViewNames.OperationLogView
            ];
            Message = "正在准备显示数据...";
            await Dispatcher.Yield(DispatcherPriority.Background);
            // 登录窗口尚未最大化，不能按其小尺寸预布局，否则首次导航仍需重做整页布局。
            // 按系统工作区估算最大化后的内容尺寸，扣除标题栏等窗口装饰。
            var window = System.Windows.Application.Current.MainWindow;
            var area = SystemParameters.WorkArea;
            var content = window?.Content as FrameworkElement;
            var chromeWidth = Math.Max(0, (window?.ActualWidth ?? 0) - (content?.ActualWidth ?? 0));
            var chromeHeight = Math.Max(0, (window?.ActualHeight ?? 0) - (content?.ActualHeight ?? 0));
            _preloadSize = new Size(Math.Max(800, area.Width - chromeWidth), Math.Max(480, area.Height - chromeHeight));
            this.RaisePropertyChanged(nameof(PreloadWidth));
            this.RaisePropertyChanged(nameof(PreloadHeight));
            if (PrismProvider.Container.Resolve<object>(ViewNames.MainView) is not FrameworkElement main)
                throw new InvalidOperationException("无法准备主工作区。");
            // Initialize 不引用 Main 工程；使用主视图的小型预热入口维持既有模块边界。
            var warm = main.GetType().GetMethod("WarmWorkspacePage")?.CreateDelegate<Action<FrameworkElement>>(main)
                ?? throw new InvalidOperationException("主工作区缺少准备入口。");
            var finish = main.GetType().GetMethod("FinishWorkspacePage")?.CreateDelegate<Action<FrameworkElement>>(main)
                ?? throw new InvalidOperationException("主工作区缺少准备完成入口。");
            PreloadContent = main;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                LayoutWorkspace();
                for (int i = 0; i < pages.Length; i++)
                {
                    Message = $"正在准备显示数据（{i + 1}/{pages.Length}）...";
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var watch = Stopwatch.StartNew();
                    FrameworkElement? view = null;
                    RoutedEventHandler? onLoaded = null;
                    try
                    {
                        view = PrismProvider.Container.Resolve<object>(pages[i]) as FrameworkElement
                            ?? throw new InvalidOperationException($"无法准备 {pages[i]}。");
                        var createdMs = watch.ElapsedMilliseconds;
                        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        onLoaded = (_, _) => loaded.TrySetResult();
                        view.Loaded += onLoaded;
                        warm(view);
                        if (view.IsLoaded) loaded.TrySetResult();
                        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        // 只布局主容器，让页面按真实工作区（扣除导航、状态栏后的区域）测量。
                        LayoutWorkspace();
                        Logs.LogInfo($"显示数据准备完成：{pages[i]}，创建 {createdMs} ms，加载及布局 {watch.ElapsedMilliseconds - createdMs} ms，总计 {watch.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        // 单页预热失败不阻断整个软件；记录原因，首次正常导航仍可按原流程加载。
                        Logs.LogWarning($"显示数据准备未完成：{pages[i]}，{ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        if (view is not null)
                        {
                            if (onLoaded is not null) view.Loaded -= onLoaded;
                            finish(view);
                        }
                    }
                }
            }
            finally
            {
                // 先从初始化容器卸下主视图再导航；业务页一直留在原工作区，避免重复父级。
                PreloadContent = null;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }

            void LayoutWorkspace()
            {
                main.Measure(_preloadSize);
                main.Arrange(new Rect(_preloadSize));
                main.UpdateLayout();
            }
        }

    }
}
