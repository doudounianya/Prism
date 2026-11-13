/*
 * 文件职责与维护说明：
 * 程序宿主入口：初始化日志诊断、阻止重复实例、交由原 Prism 模块系统启动，并在退出时处理既有硬件/日志生命周期。
 * 单实例检查先于业务模块启动，避免第二个进程重复连接或启动采集。
 */
using Company.Application.Share.Events;
using Company.Application.Share.Prism;
using Company.Core;
using Company.Core.Ioc;
using Company.Logger;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using Shell.Views;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Shell;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : PrismApplication
{
    private readonly object loggingDiagnosticLock = new();
    private readonly string loggingDiagnosticPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CRT_HMI", "logging-diagnostic.txt");
    private string? loggingFailure;
    private bool loggingDiagnosticFileWritten;
    private Mutex? singleInstanceMutex;

    public App()
    {
        //程序域异常
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        //应用程序异常
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        //多线程异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => Logs.LogError((Exception)e.ExceptionObject);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        => Logs.LogError(e.Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => Logs.LogError(e.Exception);

    protected override Window CreateShell()
    {
        return new MainWindow();
    }


    // 重复实例在日志和模块初始化之前退出，避免第二个实例产生并行业务连接。
    // 日志配置检查完成后才进入既有 Prism 启动流程。
    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstanceMutex = TryAcquireSingleInstance();
        if (singleInstanceMutex is null)
        {
            MessageBox.Show("CRT HMI 已在运行，不能重复启动。\n请查看已打开的窗口，或从右下角系统托盘恢复窗口。",
                "CRT HMI 已在运行", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        InitializeLogging(Path.Combine(AppContext.BaseDirectory, "NLog.config"));

        if (loggingFailure != null)
        {
            var diagnosticLocation = loggingDiagnosticFileWritten
                ? $"独立诊断文件：{loggingDiagnosticPath}"
                : "独立诊断文件也无法写入，请检查磁盘空间及当前用户目录权限。";
            MessageBox.Show(
                $"日志系统发生错误，请检查日志配置和目录权限。\n\n{diagnosticLocation}",
                "CRT HMI 日志诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        base.OnStartup(e);
    }

    // 互斥量名称在当前 Windows 会话内固定；主实例持有对象直到退出。
    // 第二个实例不接管已有进程，只提示用户并结束自己的启动。
    private static Mutex? TryAcquireSingleInstance()
    {
        var mutex = new Mutex(true, @"Local\CRT_HMI.SingleInstance", out var createdNew);
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }

    // 启动诊断尝试实际日志目标写入，避免配置可解析却无法落盘的故障被遗漏。
    // 诊断自身失败使用独立渠道，不能再次递归调用同一故障日志目标。
    private void InitializeLogging(string configurationFile)
    {
        InternalLogger.LogLevel = NLog.LogLevel.Warn;
        InternalLogger.InternalEventOccurred -= OnInternalLogEvent;
        InternalLogger.InternalEventOccurred += OnInternalLogEvent;

        var logFactory = NLog.LogManager.LogFactory;
        var previousThrowConfigExceptions = logFactory.ThrowConfigExceptions;
        try
        {
            // 配置解析严格检查；运行期间的文件写入仍保留 NLog 默认容错行为。
            logFactory.ThrowConfigExceptions = true;
            if (!File.Exists(configurationFile))
                throw new FileNotFoundException("找不到日志配置文件。", configurationFile);

            var configuration = new XmlLoggingConfiguration(configurationFile, logFactory);
            var fileTargets = configuration.AllTargets.OfType<FileTarget>().ToArray();
            if (fileTargets.Length == 0)
                throw new InvalidOperationException("日志配置中没有可用的文件目标。");

            NLog.LogManager.Configuration = configuration;
            var loggerName = typeof(Logs).FullName!;
            if (!NLog.LogManager.GetLogger(loggerName).IsInfoEnabled)
                throw new InvalidOperationException("日志配置未启用应用程序的信息日志。");

            foreach (var target in fileTargets)
            {
                target.WriteAsyncLogEvent(new AsyncLogEventInfo(
                    new NLog.LogEventInfo(NLog.LogLevel.Info, loggerName, "日志文件写入检查"),
                    exception =>
                    {
                        if (exception != null)
                            RecordLoggingFailure(exception.ToString());
                    }));
            }

            Logs.LogInfo("启动应用程序");
        }
        catch (Exception exception)
        {
            RecordLoggingFailure(exception.ToString());
        }
        finally
        {
            logFactory.ThrowConfigExceptions = previousThrowConfigExceptions;
        }
    }

    private void OnInternalLogEvent(object? sender, InternalLogEventArgs e)
    {
        RecordLoggingFailure($"{e.Level}: {e.Message}\n{e.Exception}");
    }

    private void RecordLoggingFailure(string message)
    {
        lock (loggingDiagnosticLock)
        {
            // 只保留首次故障，避免持续写入失败时反复写诊断文件。
            if (loggingFailure != null) return;
            loggingFailure = message;
            var diagnostic = $"{DateTime.Now:O}\n{message}";
            if (diagnostic.Length > 32768) diagnostic = diagnostic[..32768];

            // 此处不能调用 NLog，避免内部日志事件递归或死锁。
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(loggingDiagnosticPath)!);
                File.WriteAllText(loggingDiagnosticPath, diagnostic);
                loggingDiagnosticFileWritten = true;
            }
            catch (Exception exception)
            {
                diagnostic += $"\n无法写入独立日志诊断文件：{exception}";
            }

            try
            {
                Trace.TraceError(diagnostic);
            }
            catch (Exception)
            {
                // 诊断通道自身失败也不能向 NLog 回调传播异常。
            }
        }
    }


    // 真正退出时按硬件/业务、应用退出事件、日志关闭的顺序收尾。
    // 各阶段使用 finally 保证后续释放仍会尝试；某一阶段失败不能导致互斥量和事件订阅永久残留。
    protected override void OnExit(ExitEventArgs e)
    {
        // 重复启动的实例未初始化日志、模块和硬件，也不执行它们的退出流程。
        if (singleInstanceMutex is null)
        {
            base.OnExit(e);
            return;
        }
        try
        {
            try
            {
                CloseAllHardware();
            }
            catch (Exception exception)
            {
                RecordExitFailure("关闭硬件", exception);
            }

            try
            {
                base.OnExit(e);
            }
            catch (Exception exception)
            {
                RecordExitFailure("应用退出事件", exception);
                throw;
            }
        }
        finally
        {
            try
            {
                Logs.LogInfo("关闭应用程序");
            }
            catch (Exception exception)
            {
                RecordLoggingFailure($"写入退出日志失败：{exception}");
            }
            finally
            {
                try
                {
                    NLog.LogManager.Shutdown();
                }
                catch (Exception exception)
                {
                    RecordLoggingFailure($"关闭日志系统失败：{exception}");
                }
                finally
                {
                    InternalLogger.InternalEventOccurred -= OnInternalLogEvent;
                    AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
                    DispatcherUnhandledException -= OnDispatcherUnhandledException;
                    TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                    singleInstanceMutex.ReleaseMutex();
                    singleInstanceMutex.Dispose();
                    singleInstanceMutex = null;
                }
            }
        }
    }

    private void RecordExitFailure(string stage, Exception exception)
    {
        try
        {
            Logs.LogError(new InvalidOperationException($"应用退出阶段失败：{stage}", exception));
        }
        catch (Exception loggingException)
        {
            RecordLoggingFailure($"{stage}失败：{exception}\n记录退出错误失败：{loggingException}");
        }
    }
    /// <summary>
    /// 关闭所有硬件
    /// </summary>
    private void CloseAllHardware()
    {
        PrismProvider.EventAggregator?.GetEvent<CloseAllHardwareEvent>().Publish();
    }
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {

    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        // 在原 Prism 模块目录中补充 Core 模块；其余应用模块仍按既有目录机制发现。
        moduleCatalog.AddModule<CoreModule>();
    }

    protected override IModuleCatalog CreateModuleCatalog()
    {
        // 模块目录基于程序所在目录解析，启动工作目录变化时仍定位同一份 Modules。
        return new DirectoryModuleCatalog
        {
            ModulePath = Path.GetFullPath(ModuleNames.ModulePath, AppContext.BaseDirectory)
        };
    }
}



