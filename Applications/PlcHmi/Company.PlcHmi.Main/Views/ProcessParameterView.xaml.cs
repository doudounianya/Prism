/*
 * 文件职责与维护说明：
 * 工艺参数视图报告可见性与窗口状态，确保隐藏或最小化时释放页面采集需求。
 * 筛选及字段读取在视图模型中实现，界面事件不定义业务变量地址。
 */
using Company.PlcHmi.Main.ViewModels;
using Company.Logger;
using System.Windows;
using System.Windows.Controls;

namespace Company.PlcHmi.Main.Views;

public partial class ProcessParameterView : UserControl
{
    private Window? _window;
    public ProcessParameterView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ArrangePreviewColumns();
        Loaded += (_, _) => { Detach(); _window = Window.GetWindow(this); if (_window is not null) _window.StateChanged += WindowStateChanged; UpdateVisibility(); };
        Unloaded += (_, _) => { Detach(); UpdateVisibility(); };
        IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    private void ArrangePreviewColumns()
    {
        // 与 HTML 的 1300 / 1000 px 断点一致，窄窗口将阶段参数移到下一行。
        double width = Math.Max(0, ActualWidth - 32);
        bool compact = width <= 1300, narrow = width <= 1000;
        RecipeLayout.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(compact ? 210 : 230);
        RecipeLayout.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 15);
        RecipeLayout.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        RecipeLayout.ColumnDefinitions[3].Width = new GridLength(compact ? 0 : 15);
        RecipeLayout.ColumnDefinitions[4].MinWidth = compact ? 0 : 300;
        RecipeLayout.ColumnDefinitions[4].Width = compact ? new GridLength(0) : new GridLength(.85, GridUnitType.Star);
        Grid.SetColumn(RecipeRoute, narrow ? 0 : 2);
        Grid.SetRow(RecipeRoute, narrow ? 1 : 0);
        Grid.SetColumnSpan(RecipeLibrary, narrow ? 5 : 1);
        Grid.SetColumnSpan(RecipeRoute, narrow ? 5 : 1);
        Grid.SetColumn(RecipeDetails, compact ? 0 : 4);
        Grid.SetRow(RecipeDetails, narrow ? 2 : compact ? 1 : 0);
        Grid.SetColumnSpan(RecipeDetails, compact ? 5 : 1);
        RecipeRoute.Margin = new Thickness(0, narrow ? 14 : 0, 0, 0);
        RecipeDetails.Margin = new Thickness(0, compact ? 14 : 0, 0, 0);
    }
    private void Detach() { if (_window is not null) _window.StateChanged -= WindowStateChanged; _window = null; }
    private void WindowStateChanged(object? sender, EventArgs e) => UpdateVisibility();
    private async void UpdateVisibility()
    {
        try { if (DataContext is ProcessParameterViewModel vm) await vm.SetViewVisibleAsync(IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        try { if (DataContext is ProcessParameterViewModel vm) await vm.RefreshAsync(); }
        catch (Exception ex) { Logs.LogError(ex); }
    }
}
