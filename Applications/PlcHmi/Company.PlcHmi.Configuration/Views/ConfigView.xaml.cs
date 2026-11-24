using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Company.Logger;

namespace Company.PlcHmi.Configuration.Views
{
    /// <summary>
    /// 系统配置弹窗的视图后置代码，初始化 XAML 并使自身标题栏跟随现有主题色。
    /// 配置保存、通知及账号弹窗入口由绑定的 ConfigViewModel 负责。
    /// </summary>
    public partial class ConfigView : UserControl
    {
        private const int CaptionColorAttribute = 35, CaptionTextColorAttribute = 36;

        /// <summary>创建配置弹窗的控件树；构造过程不直接保存配置。</summary>
        public ConfigView()
        {
            InitializeComponent();
            Loaded += (_, _) => ApplyTitleBarTheme();
        }

        /// <summary>
        /// 只给本视图所在的设置窗口设置标题栏颜色。保留 Windows 原生标题栏、拖动、系统菜单及窗口按钮，
        /// 不替换 Prism 对话框宿主，也不改变全局系统配色或其他窗口。
        /// Windows 11（内部版本 22000 起）支持此颜色属性；不支持的系统继续使用原生标题栏。
        /// </summary>
        private void ApplyTitleBarTheme()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || Window.GetWindow(this) is not { } window) return;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            // 与主窗口共用 MahApps 已加载的主题蓝；没有主题资源时沿用内容区的主蓝色。
            var color = (window.TryFindResource("MahApps.Brushes.Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(0x17, 0x65, 0xD2);
            // 主题画刷可能带透明度；原生 COLORREF 不带 Alpha，先合成到主题底色，匹配主窗口实际显示的蓝色。
            var background = (window.TryFindResource("MahApps.Brushes.ThemeBackground") as SolidColorBrush)?.Color ?? Colors.White;
            byte Blend(byte foreground, byte behind) => (byte)((foreground * color.A + behind * (255 - color.A) + 127) / 255);
            var opaque = Color.FromRgb(Blend(color.R, background.R), Blend(color.G, background.G), Blend(color.B, background.B));
            // DWM 接收 COLORREF（低字节为 R），不能直接传 WPF 的 ARGB 整数。
            int caption = opaque.R | opaque.G << 8 | opaque.B << 16;
            int text = 0x00FFFFFF;
            int captionResult = DwmSetWindowAttribute(handle, CaptionColorAttribute, ref caption, sizeof(int));
            int textResult = DwmSetWindowAttribute(handle, CaptionTextColorAttribute, ref text, sizeof(int));
            if (captionResult < 0 || textResult < 0)
                Logs.LogWarning($"系统设置标题栏配色未应用：背景 0x{captionResult:X8}，文字 0x{textResult:X8}。");
        }

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
