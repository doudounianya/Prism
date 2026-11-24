/*
 * 文件职责与维护说明：
 * 历史报警页的查询与分页协调器，维护输入条件、最后成功的查询、事件明细和状态说明。
 * 异步查询在新请求或离页时取消，过期响应不能覆盖当前页面；查询失败不伪装成零条历史。
 */
using System.Collections.ObjectModel;
using Company.PlcHmi.Main.Services.History;
using Company.PlcHmi.Main.Models;
using Company.PlcHmi.Main.Presentation;
using Company.PlcHmi.Shared.PLC.History;
using Company.PlcHmi.Shared.Persistence.Models;
using Prism.Navigation.Regions;
using ReactiveUI;
using System.Reactive.Concurrency;
using System.Windows.Input;
using System.Diagnostics;
using Company.PlcHmi.Main.Services.Diagnostics;

namespace Company.PlcHmi.Main.ViewModels;

public sealed class AlarmHistoryViewModel : ReactiveObject,INavigationAware,IRegionMemberLifetime
{
    private readonly IAlarmHistoryQueryService _service;
    private readonly SemaphoreSlim _queryGate=new(1,1),_detailGate=new(1,1);
    private CancellationTokenSource? _request,_detailRequest;
    private long _generation,_detailGeneration;
    private bool _visible,_isBusy,_hasQuery;
    private int _page=1;
    private long _total;
    private string _status="请选择时间范围并点击查询。",_range="尚未查询",_detailTitle="报警详情",_detailSummary="请选择一条历史报警。",_detailDefinition="";
    private IReadOnlyList<string> _segments=[];
    private AlarmHistoryRow? _selected;
    private AlarmHistoryQuery? _applied;
    public AlarmQueryFilter Filter {get;}=new();
    public string PersistenceNotice {get;}
    public bool KeepAlive=>true;
    public bool IsBusy {get=>_isBusy;private set{this.RaiseAndSetIfChanged(ref _isBusy,value);NotifyPaging();}}
    public bool HasQuery {get=>_hasQuery;private set{this.RaiseAndSetIfChanged(ref _hasQuery,value);NotifyPaging();}}
    public bool CanQuery=>_visible&&!IsBusy;
    public bool CanCancel=>_visible&&(_request is not null||_detailRequest is not null);
    public bool CanPrevious=>CanQuery&&HasQuery&&_page>1;
    public bool CanNext=>CanQuery&&HasQuery&&(long)_page*(_applied?.PageSize??50)<_total;
    public string Paging=>!HasQuery?"尚未查询":_total==0?"0 条记录":$"第 {_page} / {(_total+_applied!.PageSize-1)/_applied.PageSize} 页 · 共 {_total} 条";
    public string Status {get=>_status;private set=>this.RaiseAndSetIfChanged(ref _status,value);}
    public string Range {get=>_range;private set=>this.RaiseAndSetIfChanged(ref _range,value);}
    public ObservableCollection<AlarmHistoryRow> Items {get;}=[];
    public string DetailTitle {get=>_detailTitle;private set=>this.RaiseAndSetIfChanged(ref _detailTitle,value);}
    public string DetailSummary {get=>_detailSummary;private set=>this.RaiseAndSetIfChanged(ref _detailSummary,value);}
    public string DetailDefinition {get=>_detailDefinition;private set=>this.RaiseAndSetIfChanged(ref _detailDefinition,value);}
    public IReadOnlyList<string> Segments {get=>_segments;private set=>this.RaiseAndSetIfChanged(ref _segments,value);}
    public AlarmHistoryRow? SelectedRow {get=>_selected;set{this.RaiseAndSetIfChanged(ref _selected,value);_ = LoadDetailAsync(value);}}
    public ICommand QueryCommand {get;}
    public ICommand PreviousCommand {get;}
    public ICommand NextCommand {get;}
    public ICommand CancelCommand {get;}
    // 历史保存服务与本页查询相互独立：关闭采集保存后，已有数据库记录仍可查询。
    // 页面只展示持久化服务提供的事实，不根据缺记录推断当前没有报警。
    public AlarmHistoryViewModel(IAlarmHistoryQueryService service, PlcHistoryOptions? historyOptions = null)
    {
        PersistenceNotice = historyOptions is { Enabled: true, AlarmCaptureEnabled: true }
            ? "报警历史保存已启用；本页查询已入库记录，保存结果可在运行诊断中查看。"
            : "报警历史保存未启用：实时报警不会新增历史记录；数据库中已有的记录仍可查询。";

        _service=service;
        var scheduler=new SynchronizationContextScheduler(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        QueryCommand=ReactiveCommand.CreateFromTask(QueryAsync,outputScheduler:scheduler);
        PreviousCommand=ReactiveCommand.CreateFromTask(()=>ChangePageAsync(-1),outputScheduler:scheduler);
        NextCommand=ReactiveCommand.CreateFromTask(()=>ChangePageAsync(1),outputScheduler:scheduler);
        CancelCommand=ReactiveCommand.Create(CancelQuery,outputScheduler:scheduler);
    }
    private void NotifyPaging(){this.RaisePropertyChanged(nameof(CanQuery));this.RaisePropertyChanged(nameof(CanCancel));this.RaisePropertyChanged(nameof(CanPrevious));this.RaisePropertyChanged(nameof(CanNext));this.RaisePropertyChanged(nameof(Paging));}
    /// <summary>取消本页等待和明细请求，保留上次成功结果；后台SQL仍由原任务负责结束并释放门闩。</summary>
    public void CancelQuery()
    {
        if(!CanCancel)return;
        var request=_request;var detail=_detailRequest;_request=null;_detailRequest=null;
        ++_generation;++_detailGeneration;request?.Cancel();detail?.Cancel();IsBusy=false;
        Status="查询已取消；保留上次查询结果。";
        if(detail is not null)DetailSummary="详情读取已取消。";
        NotifyPaging();
    }
    public Task QueryAsync()
    {
        if(!_visible)return Task.CompletedTask;
        if(!Filter.TryCreate(1,out var query,out var error)){Status=error;return Task.CompletedTask;}
        return ExecuteAsync(query!);
    }
    public Task ChangePageAsync(int delta)
    {
        if(_applied is null||IsBusy||!_visible||delta is not (-1 or 1)||(delta<0?!CanPrevious:!CanNext))return Task.CompletedTask;
        return ExecuteAsync(_applied with{Page=_page+delta});
    }
    private async Task ExecuteAsync(AlarmHistoryQuery query)
    {
        _request?.Cancel();var request=new CancellationTokenSource();_request=request;long generation=++_generation;
        var token=request.Token;long startedAt=Stopwatch.GetTimestamp();
        IsBusy=true;Status=Items.Count>0?"正在查询数据库…（保留上次结果）":"正在查询数据库…";
        try {
            var pending=Task.Run(async()=>{
                await _queryGate.WaitAsync(token).ConfigureAwait(false);
                try{return await _service.QueryAsync(query,token).ConfigureAwait(false);}finally{_queryGate.Release();}
            },token);
            var result=await QueryDiagnostics.AwaitAsync(pending,token);
            if(!_visible||generation!=_generation||request.IsCancellationRequested)return;
            _applied=query;_page=query.Page;_total=result.Total;
            RetainedRows.Synchronize(Items,result.Items.Select(e=>new AlarmHistoryRow(e,result.ObservedDurations.TryGetValue(e.EventId,out var ms)?ms:null)).ToArray(),
                row=>row.Event.EventId,(row,next)=>row.Apply(next));
            if(SelectedRow is not null&&!Items.Contains(SelectedRow))SelectedRow=null;
            Range=AlarmDisplayFormatter.Range(query);HasQuery=true;
            Status=result.Total==0?"查询完成：所选条件没有已保存记录；不代表该时段没有报警。":$"查询完成 · 本页 {Items.Count} 条 · 区间相交检索";
        }catch(OperationCanceledException){if(generation==_generation)Status="查询已取消。";}
        catch(Exception ex){if(generation==_generation){var id=QueryDiagnostics.Failure("报警历史",QueryDiagnostics.Alarm(query),startedAt,ex);Status="查询失败，错误编号："+id+"。请检查数据库连接与历史表。"+(Items.Count>0?"保留上次查询结果。":"失败不作为零记录结果。");}}
        finally{if(generation==_generation)IsBusy=false;if(ReferenceEquals(_request,request))_request=null;request.Dispose();NotifyPaging();}
    }
    // 明细与主表查询分别拥有取消令牌和代次，快速切行不会让上一条报警的详情覆盖当前选择。
    // 有效观测片段直接取历史服务结果，监控缺口不补成持续报警时长。
    public async Task LoadDetailAsync(AlarmHistoryRow? row)
    {
        _detailRequest?.Cancel();long generation=++_detailGeneration;
        Segments=[];DetailDefinition="";DetailTitle=row is null?"报警详情":row.Device+" / "+row.Alarm;
        if(row is null||!_visible){DetailSummary="请选择一条历史报警。";return;}
        var request=new CancellationTokenSource();_detailRequest=request;DetailSummary="正在读取历史详情…";
        var token=request.Token;long startedAt=Stopwatch.GetTimestamp();NotifyPaging();
        try{
            var pending=Task.Run(async()=>{
                await _detailGate.WaitAsync(token).ConfigureAwait(false);
                try{return await _service.GetDetailAsync(row.Event.EventId,token).ConfigureAwait(false);}finally{_detailGate.Release();}
            },token);
            var detail=await QueryDiagnostics.AwaitAsync(pending,token);
            if(!_visible||generation!=_detailGeneration||request.IsCancellationRequested)return;
            if(detail is null){DetailSummary="记录已不存在，请重新查询。";return;}
            DetailDefinition=AlarmDisplayFormatter.Definition(detail.Event);
            DetailSummary=AlarmDisplayFormatter.Status(detail.Event)+"\n"+AlarmDisplayFormatter.Start(detail.Event.StartKind)+"\n"+
                AlarmDisplayFormatter.End(detail.Event.EndKind)+$"\n累计有效观测：{AlarmDisplayFormatter.Duration(detail.ObservedDurationMs)}"+
                (detail.Event.HasGap?"\n包含监控缺口；未知间隔不计入有效时长。":"");
            Segments=detail.Segments.Select(s=>$"{AlarmDisplayFormatter.Time(s.StartUtc)} → {AlarmDisplayFormatter.Time(s.EndUtc??s.LastValidUtc)}"+
                (s.EndUtc is null?"（截至最后有效观察）":"")+"\n"+AlarmDisplayFormatter.SegmentReason(s.CloseReason)).ToArray();
        }catch(OperationCanceledException){}
        catch(Exception ex){if(generation==_detailGeneration){var id=QueryDiagnostics.Failure("报警详情",QueryDiagnostics.Detail(row.Event.EventId),startedAt,ex);DetailSummary="详情读取失败，错误编号："+id+"。";}}
        finally{if(ReferenceEquals(_detailRequest,request))_detailRequest=null;request.Dispose();NotifyPaging();}
    }
    public void SetVisible(bool visible)
    {
        if(_visible==visible)return;_visible=visible;
        if(!visible){++_generation;++_detailGeneration;_request?.Cancel();_detailRequest?.Cancel();IsBusy=false;Status="页面已离开；再次查询可刷新已保存记录。";}
        NotifyPaging();
    }
    public void OnNavigatedTo(NavigationContext context)
    {
        SetVisible(true);
        if(context.Parameters.TryGetValue<AlarmHistoryQuery>("AlarmQuery",out var query)){
            Filter.Apply(query);HasQuery=false;SelectedRow=null;Status="已带入分析条件，请点击查询；列表保留上次结果，范围以列表上方说明为准。";
        }
    }
    public bool IsNavigationTarget(NavigationContext context)=>true;
    public void OnNavigatedFrom(NavigationContext context)=>SetVisible(false);
}
