/*
 * 文件职责与维护说明：
 * 业务页签的附加导航行为：从按钮读取目标名称，限制为既有页面，再按当前身份检查访问权限。
 * 最终仍调用原 Prism 工作区导航，不创建另一套导航框架。
 */
using Company.PlcHmi.Shared.Prism;
using Prism.Ioc;
using Prism.Navigation.Regions;
using System.Windows;
using System.Windows.Controls;
using Company.PlcHmi.Shared.Login;

namespace Company.PlcHmi.Main.Presentation;

/// <summary>业务页签复用现有 Prism 工作区导航，不改变框架菜单和区域。</summary>
public static class PreviewNavigation
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(string), typeof(PreviewNavigation), new PropertyMetadata(null, Changed));
    public static string? GetTarget(DependencyObject item) => (string?)item.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject item, string value) => item.SetValue(TargetProperty, value);
    private static void Changed(DependencyObject item, DependencyPropertyChangedEventArgs args)
    {
        if (item is not Button button) return;
        button.Click -= Navigate;
        if (args.NewValue is string) button.Click += Navigate;
    }
    private static void Navigate(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || button.Tag as string == "current" || GetTarget(button) is not { } target) return;
        Request(target);
    }

    public static void Request(string target)
    {
        // 只允许此批已有业务页面，避免把任意字符串变成导航入口。
        if (target is not (ViewNames.RealtimeAlarmView or ViewNames.AlarmHistoryView or ViewNames.AlarmAnalysisView
            or ViewNames.ProductionTodayView or ViewNames.ProductionDailyView or ViewNames.ProductionTrendView
            or ViewNames.AxisParameterView or ViewNames.CylinderParameterView)) return;
        if (!ContainerLocator.Container.Resolve<IUserAccessService>().CanOpenPage(target)) return;
        ContainerLocator.Container.Resolve<IRegionManager>().RequestNavigate(RegionNames.WorkspaceRegion, target);
    }
}
