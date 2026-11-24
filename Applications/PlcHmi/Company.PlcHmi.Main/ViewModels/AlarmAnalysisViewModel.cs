/*
 * 文件职责与维护说明：
 * 报警分析页的查询协调器：从输入条件建立查询，在后台取得已保存的历史分析结果，再在 UI 线程更新排名。
 * 取消令牌和代次拦截过期结果；打开历史时使用已成功应用的查询范围。
 */
using System.Collections.ObjectModel;
using Company.PlcHmi.Main.Services.History;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Shared.PLC.History;
using Company.PlcHmi.Shared.Prism;
using Company.PlcHmi.Shared.Persistence.Models;
using Company.Core.Ioc;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Reactive.Concurrency;
using System.Windows.Input;
using System.Diagnostics;
using Company.PlcHmi.Main.Services.Diagnostics;

namespace Company.PlcHmi.Main.ViewModels;

public sealed class AlarmAnalysisViewModel : ReactiveObject,INavigationAware,IRegionMemberLifetime
{
    private readonly IAlarmHistoryQueryService _service;
    private readonly SemaphoreSlim _gate=new(1,1);
    private CancellationTokenSource? _request;
    private long _generation;
    private bool _visible,_isBusy,_hasQuery;
    private string _status="请选择条件并点击查询。",_range="尚未查询",_summary="尚未查询",_meaning="",_sortBy="发生次数";
    private string _occurrences="—",_duration="—",_uncertain="—",_gaps="—";
    private int _topCount=5;
    private AlarmAnalysisResult? _result;
    private AlarmHistoryQuery? _applied;
    private AlarmAnalysisItem? _selected;
    public AlarmQueryFilter Filter {get;}=new();
    public string PersistenceNotice {get;}
    public bool KeepAlive=>true;
    public IReadOnlyList<string> SortOptions {get;}=["发生次数","有效观测时长"];
    public IReadOnlyList<int> TopOptions {get;}=[5,10];
    public bool IsBusy {get=>_isBusy;private set{this.RaiseAndSetIfChanged(ref _isBusy,value);this.RaisePropertyChanged(nameof(CanQuery));this.RaisePropertyChanged(nameof(CanCancel));}}
    public bool CanQuery=>_visible&&!IsBusy;
    public bool CanCancel=>_visible&&_request is not null;
    public bool HasQuery {get=>_hasQuery;private set=>this.RaiseAndSetIfChanged(ref _hasQuery,value);}
    public bool HasData=>Items.Count>0;
    public bool CanOpenHistory=>_visible&&SelectedItem is not null&&_applied is not null&&!IsBusy&&HasQuery;
    public string Status {get=>_status;private set=>this.RaiseAndSetIfChanged(ref _status,value);}
    public string Range {get=>_range;private set=>this.RaiseAndSetIfChanged(ref _range,value);}
    public string Summary {get=>_summary;private set=>this.RaiseAndSetIfChanged(ref _summary,value);}
    public string Meaning {get=>_meaning;private set=>this.RaiseAndSetIfChanged(ref _meaning,value);}
    public string Occurrences {get=>_occurrences;private set=>this.RaiseAndSetIfChanged(ref _occurrences,value);}
    public string Duration {get=>_duration;private set=>this.RaiseAndSetIfChanged(ref _duration,value);}
    public string Uncertain {get=>_uncertain;private set=>this.RaiseAndSetIfChanged(ref _uncertain,value);}
    public string Gaps {get=>_gaps;private set=>this.RaiseAndSetIfChanged(ref _gaps,value);}
    public ObservableCollection<AlarmAnalysisItem> Items {get;}=[];
    public ObservableCollection<AlarmAnalysisItem> Ranking {get;}=[];
    public AlarmAnalysisItem? SelectedItem {get=>_selected;set{this.RaiseAndSetIfChanged(ref _selected,value);this.RaisePropertyChanged(nameof(CanOpenHistory));}}
    public string SortBy {get=>_sortBy;set{this.RaiseAndSetIfChanged(ref _sortBy,value);RebuildRanking();}}
    public int TopCount {get=>_topCount;set{this.RaiseAndSetIfChanged(ref _topCount,value);RebuildRanking();}}
    public ICommand QueryCommand {get;}
    public ICommand OpenHistoryCommand {get;}
    public ICommand SelectRankingCommand {get;}
    public ICommand CancelCommand {get;}
    // 查询命令的结果通知回到 UI 调度器；数据库访问经查询服务异步完成。
    // 历史保存提示描述当前开关，不意味着本页会补采或补造历史事件。
    public AlarmAnalysisViewModel(IAlarmHistoryQueryService service, PlcHistoryOptions? historyOptions = null)
    {
        PersistenceNotice = historyOptions is { Enabled: true, AlarmCaptureEnabled: true }
            ? "报警历史保存已启用；本页查询已入库记录，保存结果可在运行诊断中查看。"
            : "报警历史保存未启用：实时报警不会新增历史记录；数据库中已有的记录仍可查询。";

        var scheduler=new SynchronizationContextScheduler(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        _service=service;QueryCommand=ReactiveCommand.CreateFromTask(QueryAsync,outputScheduler:scheduler);
        CancelCommand=ReactiveCommand.Create(CancelQuery,outputScheduler:scheduler);
        SelectRankingCommand=ReactiveCommand.Create<AlarmAnalysisItem>(item=>SelectedItem=item,outputScheduler:scheduler);
        OpenHistoryCommand=ReactiveCommand.Create(()=>{
            if(!CanOpenHistory||BuildHistoryQuery() is not{} query)return;
            PrismProvider.RegionManager.RequestNavigate(RegionNames.WorkspaceRegion,ViewNames.AlarmHistoryView,
                result=>{if(result.Exception is not null)Status="历史页面打开失败，请查看日志。";},new NavigationParameters{{"AlarmQuery",query}});
        },outputScheduler:scheduler);
    }
    public AlarmHistoryQuery? BuildHistoryQuery()=>_applied is{} query&&SelectedItem is{} selected?
        query with{DeviceId=selected.Data.DeviceId,AlarmKey=selected.Data.AlarmKey,Page=1}:null;
    /// <summary>取消等待且使旧响应失效，图表和成功查询范围继续保留，不提前释放后台查询门闩。</summary>
    public void CancelQuery()
    {
        if(!CanCancel)return;
        var request=_request;_request=null;++_generation;request?.Cancel();IsBusy=false;
        Status="查询已取消；保留上次统计。";this.RaisePropertyChanged(nameof(CanOpenHistory));
    }
    // 新查询替代旧请求时，取消令牌与递增代次共同阻止旧响应回写。
    // 只在查询成功后更新已应用条件，使排名跳转历史使用的范围与图表一致。
    public async Task QueryAsync()
    {
        if(!_visible)return;
        if(!Filter.TryCreate(1,out var query,out var error)){Status=error;return;}
        _request?.Cancel();var request=new CancellationTokenSource();_request=request;long generation=++_generation;
        var token=request.Token;long startedAt=Stopwatch.GetTimestamp();
        IsBusy=true;Status=Items.Count>0?"正在查询数据库…（保留上次统计）":"正在查询数据库…";
        try{
            var pending=Task.Run(async()=>{
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try{return await _service.AnalyzeAsync(query!,token).ConfigureAwait(false);}finally{_gate.Release();}
            },token);
            var result=await QueryDiagnostics.AwaitAsync(pending,token);
            if(!_visible||generation!=_generation||request.IsCancellationRequested)return;
            _result=result;_applied=query;Range=AlarmDisplayFormatter.Range(query!);HasQuery=true;
            Occurrences=result.Items.Sum(x=>x.ObservedEvents).ToString();Duration=AlarmDisplayFormatter.Duration(result.Items.Sum(x=>x.ObservedDurationMs));
            Uncertain=result.Items.Sum(x=>x.UncertainEvents).ToString();Gaps=result.MonitoringGaps.ToString();
            Summary=$"报警类型 {result.Items.Count} · 采样上升沿 {result.Items.Sum(x=>x.SampledRises)} · 连接／恢复后发现 {result.Items.Sum(x=>x.DiscoveredEvents)}";
            Meaning=result.DurationMeaning+" 不确定事件按区间相交统计；采集缺口为所选设备范围，不能当作单个报警的缺口次数。";
            RebuildRanking();Status=result.Items.Count==0?"查询完成：没有匹配的统计记录；不表示该时段无报警。":"查询完成 · 合计对应全部命中结果，Top N 仅影响图表展示。";
        }catch(OperationCanceledException){if(generation==_generation)Status="查询已取消。";}
        catch(Exception ex){if(generation==_generation){var id=QueryDiagnostics.Failure("报警分析",QueryDiagnostics.Alarm(query!),startedAt,ex);Status="分析查询失败，错误编号："+id+"。请检查数据库连接与历史表。"+(Items.Count>0?"保留上次统计。":"不能按零报警解释。");}}
        finally{
            if(generation==_generation){IsBusy=false;this.RaisePropertyChanged(nameof(CanOpenHistory));}
            if(ReferenceEquals(_request,request))_request=null;request.Dispose();this.RaisePropertyChanged(nameof(CanCancel));
        }
    }
    private void RebuildRanking()
    {
        if(_result is null)return;
        var previous=SelectedItem?.Label;
        decimal Metric(AlarmAnalysisRow row)=>SortBy=="有效观测时长"?row.ObservedDurationMs:row.ObservedEvents;
        var rows=_result.Items.OrderByDescending(Metric).ThenBy(x=>x.DeviceId,StringComparer.Ordinal).ThenBy(x=>x.AlarmKey,StringComparer.Ordinal).ToArray();
        decimal max=rows.Length==0?0:rows.Max(Metric);
        RetainedRows.Synchronize(Items,rows.Select((row,i)=>new AlarmAnalysisItem(row,i+1,max==0?0:(double)(Metric(row)/max*100),
            SortBy=="有效观测时长"?AlarmDisplayFormatter.Duration(row.ObservedDurationMs):row.ObservedEvents+" 次")).ToArray(),item=>(item.Data.DeviceId,item.Data.AlarmKey),(item,next)=>item.Apply(next));
        RetainedRows.Synchronize(Ranking,Items.Take(TopCount==10?10:5).ToArray(),item=>(item.Data.DeviceId,item.Data.AlarmKey),(item,next)=>{});
        this.RaisePropertyChanged(nameof(HasData));SelectedItem=Items.FirstOrDefault(x=>x.Label==previous);
    }
    public void SetVisible(bool visible)
    {
        if(_visible==visible)return;_visible=visible;
        if(!visible){++_generation;_request?.Cancel();IsBusy=false;Status="页面已离开；再次查询可刷新统计。";}
        this.RaisePropertyChanged(nameof(CanQuery));this.RaisePropertyChanged(nameof(CanOpenHistory));
        this.RaisePropertyChanged(nameof(CanCancel));
    }
    public void OnNavigatedTo(NavigationContext context)=>SetVisible(true);
    public bool IsNavigationTarget(NavigationContext context)=>true;
    public void OnNavigatedFrom(NavigationContext context)=>SetVisible(false);
}
