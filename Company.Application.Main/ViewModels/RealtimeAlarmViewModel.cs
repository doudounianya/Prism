/*
 * 文件职责与维护说明：
 * 实时报警页面只读取后台报警服务的内存快照，按业务键保留行实例。
 * 有效未报警项可从活动表移除，未知状态仍单独显示；页面隐藏只停止 UI 刷新，不停止后台报警采集与历史保存。
 */
using Company.Application.Share.PLC.Monitoring;
using Company.Application.Share.PLC.Monitoring.Models;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using Company.Application.Main.Models;
using Company.Application.Share.PLC.Runtime.Models;

namespace Company.Application.Main.ViewModels;

/// <summary>定时复制内存快照；不执行 PLC 请求，不保存历史报警。</summary>
public sealed class RealtimeAlarmViewModel : ReactiveObject, INavigationAware, IRegionMemberLifetime
{
    private readonly IAlarmMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private bool _visible;
    private IReadOnlyList<AlarmRuntimeSnapshot> _items = [];
    private string _summary = "";
    private string _searchText = "", _activeCountText = "—", _unknownCountText = "—";
    private readonly Dictionary<PlcVariableKey, AlarmDisplayRow> _rows = new();
    public ObservableCollection<AlarmDisplayRow> VisibleItems { get; } = [];
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; this.RaiseAndSetIfChanged(ref _searchText, value); RefreshRows(); }
    }
    public string ActiveCountText { get => _activeCountText; private set => this.RaiseAndSetIfChanged(ref _activeCountText, value); }
    public string UnknownCountText { get => _unknownCountText; private set => this.RaiseAndSetIfChanged(ref _unknownCountText, value); }
    public IReadOnlyList<AlarmRuntimeSnapshot> Items { get => _items; private set => this.RaiseAndSetIfChanged(ref _items, value); }
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }
    public bool KeepAlive => true;
    public RealtimeAlarmViewModel(IAlarmMonitor monitor)
    {
        _monitor = monitor;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
    }
    public void Refresh()
    {
        Items = _monitor.GetSnapshots();
        ActiveCountText = Items.Count == 0 || Items.All(a => a.Active is null) ? "—" : Items.Count(a => a.Active is true).ToString();
        UnknownCountText = Items.Count == 0 ? "—" : Items.Count(a => a.Active is null).ToString();
        Summary = Items.Count == 0 ? "没有已启用的报警绑定；未读取 PLC。" :
            $"活动报警 {Items.Count(a => a.Active == true)}    未知 / 等待 {Items.Count(a => a.Active is null)}    监控点 {Items.Count}";
        RefreshRows();
    }
    // 仅更新活动或未知的行，并按报警键保留已有显示对象。
    // 无效/未知状态不会被当作 False；目录中已删除的键才从行缓存移除。
    private void RefreshRows()
    {
        var selected = Items.Where(a => a.Active != false && (string.IsNullOrEmpty(SearchText) ||
            (a.Key.DeviceId + " " + a.Key.LogicalId + " " + a.DefinitionText).Contains(SearchText, StringComparison.OrdinalIgnoreCase))).ToArray();
        var selectedKeys = selected.Select(a => a.Key).ToHashSet();
        for (int i = VisibleItems.Count - 1; i >= 0; i--)
            if (!selectedKeys.Contains(VisibleItems[i].Key)) VisibleItems.RemoveAt(i);
        var catalogKeys = Items.Select(a => a.Key).ToHashSet();
        foreach (var key in _rows.Keys.Where(k => !catalogKeys.Contains(k)).ToArray()) _rows.Remove(key);
        for (int i = 0; i < selected.Length; i++)
        {
            var snapshot = selected[i];
            if (!_rows.TryGetValue(snapshot.Key, out var row)) _rows.Add(snapshot.Key, row = new(snapshot));
            else row.Apply(snapshot);
            if (i < VisibleItems.Count && ReferenceEquals(VisibleItems[i], row)) continue;
            int existing = VisibleItems.IndexOf(row);
            if (existing < 0) VisibleItems.Insert(i, row); else VisibleItems.Move(existing, i);
        }
    }
    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (visible)
        {
            _timer.Start();
            _timer.Dispatcher.BeginInvoke(new Action(() => { if (_visible) Refresh(); }), DispatcherPriority.Background);
        }
        else _timer.Stop();
    }
    public void OnNavigatedTo(NavigationContext navigationContext) => SetVisible(true);
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) => SetVisible(false);
}
