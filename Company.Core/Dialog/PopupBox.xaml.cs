using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Company.Core.Dialog
{
    /// <summary>
    /// PopupBox.xaml 的交互逻辑
    /// </summary>
    public partial class PopupBox : Popup
    {
        public PopupBox()
        {
            InitializeComponent();
        }


        /// <summary>
        /// 透明度
        /// </summary>
        public new double Opacity
        {
            get { return (double)GetValue(OpacityProperty); }
            set { SetValue(OpacityProperty, value); }
        }

        public static new readonly DependencyProperty OpacityProperty =
            DependencyProperty.Register("Opacity", typeof(double), typeof(PopupBox), new PropertyMetadata(1.0, new PropertyChangedCallback(OnOpacityPropertyChangedCallback)));

        private static void OnOpacityPropertyChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is PopupBox box)) return;
            if (!(e.NewValue is double v)) return;
            box.border.Opacity = v;//赋值新的透明度
        }



        public string Message
        {
            get { return (string)GetValue(MessageProperty); }
            set { SetValue(MessageProperty, value); }
        }

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register("Message", typeof(string), typeof(PopupBox), new PropertyMetadata(string.Empty));



        // WPF 对象只能在应用程序的 UI 线程上创建。
        private static PopupBox? dialog;
        private static DispatcherTimer? timer;
        private static Window? currentOwner;
        private static bool fading;
        private static long fadeStarted;

        /// <summary>
        /// 弹出消息；再次调用会重新开始显示和淡出计时。
        /// </summary>
        public static void Show(string message, Window? owner = null, int seconds = 1)
        {
            var application = Application.Current;
            if (application == null) return;
            var dispatcher = application.Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            try
            {
                dispatcher.Invoke(new Action(() =>
                {
                    if (dispatcher.HasShutdownStarted) return;
                    owner ??= application.MainWindow;
                    if (owner == null || owner.Dispatcher != dispatcher || !owner.IsVisible) return;

                    if (dialog == null)
                    {
                        dialog = new PopupBox();
                        timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
                        timer.Tick += Timer_Tick;
                        dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;
                    }

                    ClosePopup();
                    currentOwner = owner;
                    currentOwner.Closed += Owner_Closed;
                    dialog.Message = message;
                    dialog.PlacementTarget = owner;
                    dialog.Placement = PlacementMode.Center;
                    dialog.StaysOpen = true;
                    dialog.AllowsTransparency = true;
                    dialog.VerticalOffset = owner.ActualHeight / 3;
                    dialog.Opacity = 0.9;
                    dialog.IsOpen = true;

                    timer!.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (double)seconds * 1000));
                    timer.Start();
                }));
            }
            catch (Exception exception)
            {
                TraceFailure(exception);
            }
        }

        private static void Timer_Tick(object? sender, EventArgs e)
        {
            if (dialog == null || timer == null) return;
            if (!fading)
            {
                fading = true;
                fadeStarted = Stopwatch.GetTimestamp();
                timer.Interval = TimeSpan.FromMilliseconds(16);
                return;
            }

            var progress = Stopwatch.GetElapsedTime(fadeStarted).TotalMilliseconds / 500;
            dialog.Opacity = 0.9 * Math.Max(0, 1 - progress);
            if (progress >= 1) ClosePopup();
        }

        private static void ClosePopup()
        {
            timer?.Stop();
            fading = false;
            if (currentOwner != null) currentOwner.Closed -= Owner_Closed;
            currentOwner = null;
            if (dialog == null) return;
            dialog.IsOpen = false;
            dialog.Message = string.Empty;
            dialog.PlacementTarget = null;
        }

        private static void Owner_Closed(object? sender, EventArgs e) => ClosePopup();

        private static void Dispatcher_ShutdownStarted(object? sender, EventArgs e)
        {
            ClosePopup();
            if (timer != null) timer.Tick -= Timer_Tick;
            if (sender is Dispatcher dispatcher)
                dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
            timer = null;
            dialog = null;
        }

        private static void TraceFailure(Exception exception)
        {
            try { System.Diagnostics.Trace.TraceError("PopupBox 显示失败：{0}", exception); }
            catch { /* 诊断失败不能中断调用方。 */ }
        }
    }
}
