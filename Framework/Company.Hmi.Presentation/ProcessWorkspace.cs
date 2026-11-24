using System.Windows.Controls;
using System.Windows.Media;

namespace Company.Hmi.Presentation;

/// <summary>工艺工作区保留共用背景，各产品填充自己的读取或示教界面。</summary>
public sealed class ProcessWorkspace : Border
{
    public ProcessWorkspace()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF5, 0xFB));
        brush.Freeze();
        Background = brush;
    }
}
