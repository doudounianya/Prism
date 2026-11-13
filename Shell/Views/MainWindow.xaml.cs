/*
 * 文件职责与维护说明：
 * 宿主窗口的关闭与托盘交互：关闭时让用户选择托盘、取消或退出，恢复时保留原最大化状态。
 * 真正退出才进入原生命周期；隐藏到托盘仍保留业务进程和后台服务。
 */
using Company.Logger;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Shell.Views;

public partial class MainWindow : MetroWindow
{
    private Forms.NotifyIcon? trayIcon;
    private bool closePromptPending;
    private bool allowClose;
    private bool sessionEnding;
    private WindowState restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        Application.Current.SessionEnding += OnSessionEnding;
    }

    // 默认取消当前 Closing，再异步显示三选一提示；这避免关闭事件重入。
    // 只有明确允许关闭或 Windows 会话结束才放行原窗口关闭流程。
    protected override void OnClosing(CancelEventArgs e)
    {
        if (allowClose || sessionEnding)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (closePromptPending) return;
        closePromptPending = true;
        // 先结束当前 Closing 回调，再显示提示，避免关闭过程中的重入。
        Dispatcher.BeginInvoke(new Action(ChooseCloseActionAsync));
    }

    private async void ChooseCloseActionAsync()
    {
        try
        {
            var choice = await this.ShowMessageAsync("关闭 CRT HMI",
                "请选择操作。最小化到托盘后，后台采集、报警保存和产能同步继续按当前设置运行。",
                MessageDialogStyle.AffirmativeAndNegativeAndSingleAuxiliary,
                new MetroDialogSettings
                {
                    AffirmativeButtonText = "最小化到托盘",
                    NegativeButtonText = "取消",
                    FirstAuxiliaryButtonText = "关闭程序",
                    DefaultButtonFocus = MessageDialogResult.Negative,
                    DialogTitleFontSize = 22,
                    DialogMessageFontSize = 15,
                    DialogButtonFontSize = 15,
                    // 仅这条关闭提示使用紧凑窗口适配样式；不更换框架对话框或修改其他弹窗。
                    CustomResourceDictionary = new ResourceDictionary
                    {
                        Source = new Uri("/Shell;component/Presentation/CloseConfirmationStyles.xaml", UriKind.Relative)
                    },
                    AnimateShow = false,
                    AnimateHide = false
                });

            if (sessionEnding || Dispatcher.HasShutdownStarted) return;
            if (choice == MessageDialogResult.Affirmative)
                MinimizeToTray();
            else if (choice == MessageDialogResult.FirstAuxiliary)
            {
                allowClose = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            try { Logs.LogError(exception); } catch { /* 保留窗口，避免错误提示导致退出。 */ }
            if (!sessionEnding && !Dispatcher.HasShutdownStarted)
                MessageBox.Show(this, "窗口操作未完成，程序将保持打开。请稍后重试。",
                    "CRT HMI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { closePromptPending = false; }
    }

    // 隐藏窗口前记住普通/最大化状态，并显示系统托盘图标。
    // Hide 只影响窗口显示，后台通讯与历史服务仍由原进程继续运行。
    private void MinimizeToTray()
    {
        if (trayIcon is null)
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray)));
            menu.Items.Add("关闭程序", null, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                RestoreFromTray();
                Close();
            })));
            trayIcon = new Forms.NotifyIcon
            {
                Text = "CRT HMI",
                Icon = System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu
            };
            trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
        }

        restoreState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        trayIcon.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = restoreState;
        Activate();
        if (trayIcon is not null) trayIcon.Visible = false;
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => sessionEnding = true;

    // 真正关闭后解除系统会话事件，并释放托盘图标和菜单，避免退出后残留图标资源。
    protected override void OnClosed(EventArgs e)
    {
        Application.Current.SessionEnding -= OnSessionEnding;
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.ContextMenuStrip?.Dispose();
            trayIcon.Dispose();
            trayIcon = null;
        }
        base.OnClosed(e);
    }
}
