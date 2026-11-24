using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Collections.Specialized;
using Company.PlcHmi.Menu.ViewModels;
using Company.PlcHmi.Shared.Prism;
using Company.Core.Ioc;
using Prism.Navigation.Regions;

namespace Company.PlcHmi.Menu.Views
{
    /// <summary>
    /// 菜单视图除了初始化 XAML，还观察工作区的活动视图，使菜单高亮跟随真实导航结果。
    /// 区域可能晚于菜单出现，因此同时监听区域集合与目标区域的 ActiveViews。
    /// </summary>
    public partial class MenuView : UserControl
    {
        private IRegionManager? _regions;
        private IRegion? _workspace;
        /// <summary>在 Loaded 时连接工作区观察，在 Unloaded 时解除连接。</summary>
        public MenuView()
        {
            InitializeComponent();
            Loaded += (_, _) => AttachWorkspace();
            Unloaded += (_, _) => DetachWorkspace();
        }

        private void AttachWorkspace()
        {
            // 同一视图可多次 Loaded；连接前先解除上一次观察，避免重复订阅区域事件。
            DetachWorkspace();
            _regions = PrismProvider.RegionManager;
            if (_regions is null) return;
            _regions.Regions.CollectionChanged += RegionsChanged;
            ObserveWorkspace();
        }

        private void RegionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ObserveWorkspace();
        private void ObserveWorkspace()
        {
            if (_regions is null || !_regions.Regions.ContainsRegionWithName(RegionNames.WorkspaceRegion)) return;
            var workspace = _regions.Regions[RegionNames.WorkspaceRegion];
            // 区域被替换时把监听从旧实例移到新实例，不能只按区域名称保留旧引用。
            if (!ReferenceEquals(_workspace, workspace))
            {
                if (_workspace is not null) _workspace.ActiveViews.CollectionChanged -= ActiveViewsChanged;
                _workspace = workspace;
                _workspace.ActiveViews.CollectionChanged += ActiveViewsChanged;
            }
            UpdateSelection();
        }

        private void ActiveViewsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSelection();
        // 以工作区最后一个活动 FrameworkElement 的类型名映射分区，不从按钮点击猜测当前页。
        private void UpdateSelection()
        {
            if (DataContext is MenuViewModel vm)
                vm.UpdateActivePage(_workspace?.ActiveViews.OfType<FrameworkElement>().LastOrDefault()?.GetType().Name);
        }

        // 解除两层事件连接并清空引用，避免卸载的菜单继续收到区域导航通知。
        private void DetachWorkspace()
        {
            if (_workspace is not null) _workspace.ActiveViews.CollectionChanged -= ActiveViewsChanged;
            if (_regions is not null) _regions.Regions.CollectionChanged -= RegionsChanged;
            _workspace = null;
            _regions = null;
        }
    }
}
