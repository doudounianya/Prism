/*
 * 文件职责与维护说明：
 * 应用层工作区宿主：继续让 Prism 管理活动内容，同时保留各页视觉树，只切换显隐，降低反复切页时的闪烁。
 * 显示前再次校验页面权限，保留的页面对象不因此获得操作权限。
 */
using System.Windows;
using System.Windows.Controls;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.Prism;
using Company.Core.Ioc;
using Prism.Ioc;
using Prism.Navigation.Regions;

namespace Company.PlcHmi.Main.Presentation;

/// <summary>保留各页面的视觉树；Content 仍由原有 Prism 单活动区域管理。</summary>
[TemplatePart(Name = "PART_Pages", Type = typeof(Grid))]
public sealed class ResidentWorkspaceHost : ContentControl
{
    private readonly Dictionary<FrameworkElement, ContentPresenter> _pages = new(ReferenceEqualityComparer.Instance);
    private Grid? _panel;

    /// <summary>隐藏准备最终视觉树，不设置 Content，也不激活 Prism 导航或页面采集。</summary>
    public void WarmPage(FrameworkElement page)
    {
        VerifyAccess();
        Retain(page).Visibility = Visibility.Hidden;
    }

    /// <summary>准备结束后保留实例和父级；正常导航仍通过 Display 校验权限。</summary>
    public void FinishWarmup(FrameworkElement page)
    {
        VerifyAccess();
        if (_pages.TryGetValue(page, out var presenter))
            presenter.Visibility = ReferenceEquals(Content, page) ? Visibility.Visible : Visibility.Collapsed;
    }

    public override void OnApplyTemplate()
    {
        if (_panel is not null) _panel.Children.Clear();
        base.OnApplyTemplate();
        _panel = GetTemplateChild("PART_Pages") as Grid
            ?? throw new InvalidOperationException("工作区模板缺少 PART_Pages。");
        foreach (var presenter in _pages.Values) _panel.Children.Add(presenter);
        Display(Content as FrameworkElement);
    }

    // 仍调用 ContentControl 原逻辑以保留 Prism 适配；模板内部改为显示已保留的视觉树。
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        // 保留 ContentControl 的逻辑树和 Prism 适配行为，模板不再装卸 SelectedContent。
        base.OnContentChanged(oldContent, newContent);
        Display(newContent as FrameworkElement);
    }

    private ContentPresenter Retain(FrameworkElement page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        var presenter = new ContentPresenter
        {
            Content = page,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        _pages.Add(page, presenter);
        _panel?.Children.Add(presenter);
        return presenter;
    }

    // 在菜单检查之外再次确认目标权限，防止直接导航绕过入口限制。
    // 正常切页在同一 UI 消息中更新所有页显隐，渲染时不主动产生先清空的中间帧。
    private void Display(FrameworkElement? selected)
    {
        // 导航命令之外再次检查目标，包括直接调用 Prism 的导航请求。
        if (selected is not null && PrismProvider.Container is { } container &&
            ((IContainerProvider)container).IsRegistered<IUserAccessService>() && !container.Resolve<IUserAccessService>().CanOpenPage(selected.GetType().Name))
        {
            selected = null;
            Dispatcher.BeginInvoke(new Action(() => PrismProvider.RegionManager.RequestNavigate(RegionNames.WorkspaceRegion, ViewNames.DeviceMonitorView)));
        }
        var next = selected is null ? null : Retain(selected);
        // 同一次 UI 消息内完成显隐，绘制时没有中间空白帧。
        foreach (var presenter in _pages.Values)
            presenter.Visibility = ReferenceEquals(presenter, next) ? Visibility.Visible : Visibility.Collapsed;
    }

}
