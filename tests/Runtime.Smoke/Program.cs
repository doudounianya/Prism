using System.Collections;
using System.Reflection;
using System.Text.Json;
using Company.Core.Runtime;
using Company.Platform.Shared.Login;
using Company.Platform.Shared.Model;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using Company.PlcHmi.Plc.Connections;
using Company.PlcHmi.Plc.Options;
using Company.PlcHmi.Plc.Writing;
using Company.PlcHmi.Plc.Lifecycle;
using Company.Hardware.PLC.Interfaces;
using Company.Hardware.PLC.Models;

// 只把替身客户端装入隔离连接槽，不运行连接工作循环或真实 SDK。
var results = new List<(string Name,bool Passed)>();
void Check(string name,bool success) { results.Add((name,success)); if(!success) throw new Exception(name); }
async Task Run(string name,Func<Task> test)
{
    try { await test(); results.Add((name,true)); }
    catch(Exception e) { results.Add((name,false)); Console.Error.WriteLine(name+": "+e); }
}
TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);

await Run("lifecycle.order-concurrency-stop-idempotent",async()=>
{
    var lifecycle=new ApplicationLifecycle(); var calls=new List<string>();
    lifecycle.Register(new("history",10,30,_=>{calls.Add("history+");return Task.CompletedTask;},()=>{calls.Add("history-");return Task.CompletedTask;}));
    lifecycle.Register(new("plc",20,20,_=>{calls.Add("plc+");return Task.CompletedTask;},()=>{calls.Add("plc-");return Task.CompletedTask;}));
    lifecycle.Register(new("requests",0,0,_=>Task.CompletedTask,()=>{calls.Add("requests-");return Task.CompletedTask;}));
    await Task.WhenAll(lifecycle.StartAsync(),lifecycle.StartAsync());
    Check("single-start",calls.SequenceEqual(new[]{"history+","plc+"}));
    await Task.WhenAll(lifecycle.StopAsync(),lifecycle.StopAsync()); await lifecycle.StopAsync();
    Check("ordered-stop",calls.SequenceEqual(new[]{"history+","plc+","requests-","plc-","history-"}));
    Check("repeated-stop-state",lifecycle.State==ApplicationRunState.Stopped);
});
await Run("lifecycle.start-failure-rolls-back-partial-and-retries",async()=>
{
    var lifecycle=new ApplicationLifecycle();var calls=new List<string>();bool fail=true;
    lifecycle.Register(new("one",0,0,_=>{calls.Add("one+");return Task.CompletedTask;},()=>{calls.Add("one-");return Task.CompletedTask;}));
    lifecycle.Register(new("two",1,1,_=>{calls.Add("two+");if(fail)throw new Exception("start");return Task.CompletedTask;},()=>{calls.Add("two-");return Task.CompletedTask;}));
    try{await lifecycle.StartAsync();throw new InvalidOperationException("not rejected");}catch(Exception e)when(e.Message=="start"){}
    Check("rollback-includes-partial-step",calls.SequenceEqual(new[]{"one+","two+","two-","one-"}));
    fail=false;await lifecycle.StartAsync();Check("retry-start",lifecycle.State==ApplicationRunState.Running);await lifecycle.StopAsync();
});
await Run("lifecycle.stop-failure-continues-and-retries-only-failed",async()=>
{
    var lifecycle=new ApplicationLifecycle();int a=0,b=0;bool fail=true;
    lifecycle.Register(new("a",0,0,_=>Task.CompletedTask,()=>{a++;if(fail)throw new Exception();return Task.CompletedTask;}));
    lifecycle.Register(new("b",1,1,_=>Task.CompletedTask,()=>{b++;return Task.CompletedTask;}));
    await lifecycle.StartAsync();try{await lifecycle.StopAsync();}catch(AggregateException){}
    Check("cleanup-continues",a==1&&b==1);fail=false;await lifecycle.StopAsync();
    Check("cleanup-retries-only-failed",a==2&&b==1&&lifecycle.State==ApplicationRunState.Stopped);
});
await Run("lifecycle.canceled-wait-does-not-abandon-cleanup",async()=>
{
    var lifecycle=new ApplicationLifecycle();var entered=Signal();var finish=Signal();
    lifecycle.Register(new("slow",0,0,_=>Task.CompletedTask,async()=>{entered.SetResult();await finish.Task;}));
    await lifecycle.StartAsync();using var cancel=new CancellationTokenSource();var wait=lifecycle.StopAsync(cancel.Token);
    await entered.Task;cancel.Cancel();try{await wait;throw new Exception("not canceled");}catch(OperationCanceledException){}
    Check("cleanup-still-owned",lifecycle.State==ApplicationRunState.Stopping);finish.SetResult();await lifecycle.StopAsync();
    Check("cleanup-completes-after-wait-cancel",lifecycle.State==ApplicationRunState.Stopped);
});
await Run("lifecycle.stop-during-start-never-reopens-running",async()=>
{
    var lifecycle=new ApplicationLifecycle();var entered=Signal();int stops=0;
    lifecycle.Register(new("starting",0,0,async token=>{entered.SetResult();await Task.Delay(Timeout.Infinite,token);},()=>{stops++;return Task.CompletedTask;}));
    var start=lifecycle.StartAsync();await entered.Task;await lifecycle.StopAsync();try{await start;}catch(OperationCanceledException){}
    Check("stop-during-start-final-state",lifecycle.State==ApplicationRunState.Stopped&&stops==1);
});

await Run("operation.manual-stop-roles-emergency-and-feedback",async()=>
{
    await using var f=new Fixture();await f.Start();
    var context=f.Writer.Capture("D");
    f.Session.CurrentUser.Role=2;
    Check("operator-debug-denied",!(await f.Write("UnitMode[0].Request.Debug",true,context)).Success&&f.Client.Writes.Count==0);
    f.Session.CurrentUser.Role=1;f.Data.Emergency=true;
    Check("emergency-initialize-denied",!(await f.Write("UnitMode[0].Request.Initialize",true,context)).Success);
    f.Data.Emergency=false;f.Data.Status=50;
    Check("running-initialize-denied",!(await f.Write("UnitMode[0].Request.Initialize",true,context)).Success);
    f.Data.Status=0;f.Data.Quality=PlcObservationQuality.Bad;
    Check("bad-quality-denied",!(await f.Write("UnitMode[0].Request.Initialize",true,context)).Success);
    f.Data.Quality=PlcObservationQuality.Good;
    Check("manual-stop-debug-accepted",(await f.Write("UnitMode[0].Request.Debug",true,context)).Success);
    Check("manual-stop-initialize-accepted",(await f.Write("UnitMode[0].Request.Initialize",true,context)).Success);
    f.Access.Version++;f.Access.CanControlModes=false;f.Data.Emergency=true;
    Check("old-owner-release-after-logout",(await f.Write("UnitMode[0].Request.Initialize",false,context)).Success);
    Check("old-debug-release-after-logout",(await f.Write("UnitMode[0].Request.Debug",false,context)).Success);
    Check("no-new-action-after-logout",!(await f.Write("UnitMode[0].Request.Initialize",true,context)).Success);
});
await Run("operation.queue-rechecks-authorization-before-send",async()=>
{
    await using var f=new Fixture();await f.Start();await f.DeviceGate.WaitAsync();
    var context=f.Writer.Capture("D");var queued=f.Write("UnitMode[0].Request.Initialize",true,context);
    f.Access.Version++;f.DeviceGate.Release();var report=await queued;
    Check("queued-old-authorization-not-sent",!report.Success&&report.Results.All(r=>r.State==PlcWriteState.NotSent)&&f.Client.Writes.Count==0);
});
await Run("operation.queue-rechecks-feedback-and-freezes-request",async()=>
{
    await using var f=new Fixture();await f.Start();await f.DeviceGate.WaitAsync();
    var context=f.Writer.Capture("D");var values=new List<PlcVariableWrite>{new(f.Key("UnitMode[0].Request.Initialize"),"True")};
    var pending=f.Writer.ExecuteAsync(new(context,[values]));
    values[0]=new(f.Key("UnitMode[0].Request.Debug"),"True");f.DeviceGate.Release();
    Check("queued-request-copy",(await pending).Results.Single().Key.LogicalId=="UnitMode[0].Request.Initialize");
    await f.Write("UnitMode[0].Request.Initialize",false);
    await f.DeviceGate.WaitAsync();var blocked=f.Write("UnitMode[0].Request.Initialize",true);f.Data.Status=50;f.DeviceGate.Release();
    Check("queued-feedback-change-not-sent",!(await blocked).Success&&f.Client.Writes.Count==2);
});
await Run("operation.parameter-batch-prevalidation-sends-nothing",async()=>
{
    await using var f=new Fixture();await f.Start();
    var result=await f.Writer.WriteSequenceAsync(f.Writer.Capture("D"),[[new(f.Key("UnitMode[0].Request.Initialize"),"True")],[new(f.Key("UnitMode[0].Request.Debug"),"True")]],true);
    Check("action-not-mixed-with-verified-parameter",!result.Success&&f.Client.Writes.Count==0&&result.Results.All(r=>r.State==PlcWriteState.NotSent));
});
await Run("operation.shutdown-drains-press-releases-original-before-disconnect",async()=>
{
    await using var f=new Fixture();await f.Start();var entered=Signal();var finish=Signal();
    f.Client.BeforeReturn=async()=>{entered.SetResult();await finish.Task;};
    var write=f.Write("UnitMode[0].Request.Initialize",true);await entered.Task;
    var stop=f.Lifecycle.StopAsync();
    Check("new-request-denied-during-shutdown",!(await f.Write("UnitMode[0].Request.Initialize",true)).Success);
    Check("connection-not-stopped-before-inflight",f.Runtime.Stops==0);
    f.Client.BeforeReturn=null;finish.SetResult();await write;await stop;
    Check("press-followed-by-release",f.Client.Writes.Select(w=>w.Value).SequenceEqual(new object?[]{true,false}));
    Check("disconnect-follows-release",f.Runtime.Stops==1&&f.Lifecycle.State==ApplicationRunState.Stopped);
});
await Run("operation.unknown-result-not-replayed-across-new-connection",async()=>
{
    await using var f=new Fixture();await f.Start();var old=f.Writer.Capture("D");f.Client.Fail=true;
    var report=await f.Write("UnitMode[0].Request.Initialize",true,old);
    Check("unknown-is-not-success",report.Results.Single().State==PlcWriteState.OutcomeUnknown);
    f.Client.Fail=false;f.SetEpoch(8);
    Check("old-release-not-sent-to-new-connection",!(await f.Write("UnitMode[0].Request.Initialize",false,old)).Success&&f.Client.Writes.Count==1);
    await f.Lifecycle.StopAsync();Check("no-automatic-replay-on-exit",f.Client.Writes.Count==1);
});
await Run("operation.parameter-gate-and-readback",async()=>
{
    await using var f=new Fixture();await f.Start();var context=f.Writer.Capture("D");
    f.Access.CanEditParameters=false;
    Check("parameter-operator-denied",!(await f.Writer.WriteAsync(context,[new(f.Key("Axis[0].Positions[0].Speed"),"12")],true)).Success);
    f.Access.CanEditParameters=true;
    DebugParameterAccess.Begin(1,"test","D",7,Fixture.Stamp,()=>true);
    DebugParameterAccess.Observe(new[]{("Mode",(object)0),("Status",(object)30),("Action[6]",(object)true),("Signal[5]",(object)false)}.Select(x=>f.Data.Snapshot("UnitMode[0]."+x.Item1,x.Item2)).ToArray());
    var result=await f.Writer.WriteAsync(context,[new(f.Key("Axis[0].Positions[0].Speed"),"12")],true);
    Check("parameter-write-verified",result.Success&&result.Results.Single().Verified==true&&f.Client.Reads==1);
    DebugParameterAccess.Revoke();
});
await Run("operation.maintenance-lock-is-preserved-on-exit",async()=>
{
    await using var f=new Fixture();await f.Start();
    var user=new UserAccountInfo(99,"test-lock",1,DateTime.UtcNow); // 身份仅用于测试进程自己的 Config 目录。
    try
    {
        MaintenanceInterlock.Add(user);
        Check("maintenance-manager-request-accepted",(await f.Write("UnitMode[0].Request.Maintenance",true)).Success);
        Check("maintenance-blocks-new-equipment-action",!(await f.Write("UnitMode[0].Request.Initialize",true)).Success);
        await f.Lifecycle.StopAsync();
        Check("shutdown-does-not-unlock-personnel",MaintenanceInterlock.Holds(99)&&f.Client.Writes.Count==1);
    }
    finally { MaintenanceInterlock.ReleaseAll(user);MaintenanceInterlock.Observe(false,0,0,DateTimeOffset.UtcNow.AddSeconds(1)); }
});
await Run("operation.stop-press-release-state-role-and-epoch",async()=>
{
    await using var f=new Fixture();await f.Start();
    f.Session.CurrentUser.Role=2;f.Access.CanEditParameters=false;f.Data.Status=40;
    var context=f.Writer.Capture("D");
    Check("operator-manual-initialization-stop",(await f.Write("UnitMode[0].Action[3]",true,context)).Success);
    f.Data.Status=0;f.Data.StopPressed=true;f.Access.Version++;f.Access.CanControlModes=false;
    Check("stop-release-after-state-and-identity-change",(await f.Write("UnitMode[0].Action[3]",false,context)).Success);
    Check("stop-press-then-release-values",f.Client.Writes.Select(x=>x.Value).SequenceEqual(new object[]{true,false}));
    Check("stop-new-press-after-logout-denied",!(await f.Write("UnitMode[0].Action[3]",true,context)).Success);
    f.Access.CanControlModes=true;f.Data.Mode=20;f.Data.Status=50;f.Data.StopPressed=false;
    Check("operator-automatic-running-stop",(await f.Write("UnitMode[0].Action[3]",true)).Success);
    f.Data.Quality=PlcObservationQuality.Bad;
    Check("bad-feedback-stop-denied",!(await f.Write("UnitMode[0].Action[3]",true)).Success);
    f.Data.Quality=PlcObservationQuality.Good;f.Data.Status=0;
    Check("stopped-state-not-new-stop-press",!(await f.Write("UnitMode[0].Action[3]",true)).Success);
    f.SetEpoch(8);
    Check("stop-release-no-cross-reconnect",!(await f.Write("UnitMode[0].Action[3]",false,context)).Success);
});
await Run("operation.stop-queue-recheck-and-shutdown-release",async()=>
{
    await using var f=new Fixture();await f.Start();f.Data.Mode=20;f.Data.Status=50;
    await f.DeviceGate.WaitAsync();var pending=f.Write("UnitMode[0].Action[3]",true);
    f.Data.Status=0;f.DeviceGate.Release();
    Check("queued-stop-stale-state-not-sent",!(await pending).Success&&f.Client.Writes.Count==0);
    f.Data.Status=50;
    Check("stop-before-shutdown-accepted",(await f.Write("UnitMode[0].Action[3]",true)).Success);
    await f.Lifecycle.StopAsync();
    Check("shutdown-releases-held-stop",f.Client.Writes.Count==2&&f.Client.Writes.Last().Value is false);
});
await Run("operation.stop-does-not-open-other-physical-bits",async()=>
{
    await using var f=new Fixture();await f.Start();f.Data.Mode=20;f.Data.Status=50;
    Check("physical-pause-not-opened",!(await f.Write("UnitMode[0].Action[2]",true)).Success&&f.Client.Writes.Count==0);
    var user=new UserAccountInfo(98,"stop-lock",1,DateTime.UtcNow);
    try
    {
        MaintenanceInterlock.Add(user);
        Check("maintenance-blocks-stop-press",!(await f.Write("UnitMode[0].Action[3]",true)).Success);
        Check("maintenance-allows-original-stop-release",(await f.Write("UnitMode[0].Action[3]",false)).Success);
    }
    finally{MaintenanceInterlock.ReleaseAll(user);MaintenanceInterlock.Observe(false,0,0,DateTimeOffset.UtcNow.AddSeconds(1));}
});
var summary=new{total=results.Count,passed=results.Count(x=>x.Passed),failed=results.Where(x=>!x.Passed).Select(x=>x.Name).ToArray(),realDeviceConnected=false};
if(args.Length>0)File.WriteAllText(args[0],JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Runtime/operation checks: {summary.passed}/{summary.total}; failed={summary.failed.Length}; real device connected=false");
return summary.failed.Length==0?0:1;

public sealed class Fixture : IAsyncDisposable
{
    public const string Stamp="V0.8:hash";
    public ApplicationLifecycle Lifecycle=new(); public Access Access=new();public Session Session=new();public Data Data=new();
    public Client Client=new();public Runtime Runtime=new();public PlcWriteService Writer;public PlcConnectionManager Manager;
    private readonly object device;public SemaphoreSlim DeviceGate;
    public Fixture()
    {
        var options=new PlcRuntimeOptions{EnableWrites=true};
        Manager=new(new PlcClientFactory(options),DispatchProxy.Create<IPlcConnectionProfileProvider,NoCalls>(),options);
        var definition=new PlcDeviceDefinition{Source=new("test",1,new Dictionary<string,int>()),Enabled=true,Id="D",Protocol=PlcProtocol.OpcUa};
        var type=typeof(PlcConnectionManager).GetNestedType("Device",BindingFlags.NonPublic)!;
        device=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[definition],null)!;
        type.GetField("Client",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(device,Client);
        DeviceGate=(SemaphoreSlim)type.GetField("Gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(device)!;
        ((IDictionary)Field("_devices").GetValue(Manager)!).Add("D",device);
        Field("_planStamp").SetValue(Manager,Stamp);Field("_stop").SetValue(Manager,new CancellationTokenSource());SetEpoch(7);
        Writer=new(new Configuration(definition),Runtime,Manager,options,new(Access,Session,Data,Lifecycle),Lifecycle);
        Runtime.OnStop=async()=>{await (Task)typeof(PlcConnectionManager).GetMethod("StopAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(Manager,null)!;};
        _=new PlcLifecycleAdapter(Runtime,options,Lifecycle,Writer);
    }
    private FieldInfo Field(string name)=>typeof(PlcConnectionManager).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    public void SetEpoch(long epoch){Runtime.Epoch=epoch;device.GetType().GetField("Status",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(device,new PlcDeviceRuntimeStatus("D",PlcDeviceState.Connected,epoch,DateTimeOffset.UtcNow,null));}
    public Task Start()=>Lifecycle.StartAsync();
    public PlcVariableKey Key(string id)=>new(PlcProtocol.OpcUa,"D",id);
    public Task<PlcWriteReport> Write(string id,bool value,PlcWriteContext? context=null)=>Writer.WriteAsync(context??Writer.Capture("D"),[new(Key(id),value?"True":"False")]);
    public async ValueTask DisposeAsync(){await Lifecycle.StopAsync();}
}
public class NoCalls : DispatchProxy { protected override object? Invoke(MethodInfo? m,object?[]? a)=>throw new Exception("Unexpected external dependency: "+m?.Name); }
public sealed class Session : ISession { public CurrentUser CurrentUser{get;set;}=new(){Id=1,UserName="test",Role=1}; }
public sealed class Access : IUserAccessService
{
    public bool IsLoggedIn=>CanControlModes;public bool CanControlModes{get;set;}=true;public bool CanEditParameters{get;set;}=true;
    public bool IsAdministrator=>true;public long Version{get;set;}=5;public event EventHandler? Changed{add{}remove{}}public bool CanOpenPage(string viewName)=>true;
}
public sealed class Data : IPlcDataProvider
{
    public int Mode;public int Status;public bool StopPressed;public bool Emergency;public PlcObservationQuality Quality=PlcObservationQuality.Good;
    public PlcVariableSnapshot Snapshot(string id,object value)=>new(){Key=new(PlcProtocol.OpcUa,"D",id),EngineeringValue=value,State=PlcValueState.Valid,Quality=Quality,ConnectionEpoch=7,ConfigurationVersion=Fixture.Stamp,LastValidAt=DateTimeOffset.UtcNow};
    public IReadOnlyList<PlcVariableSnapshot> GetSnapshots(IReadOnlyCollection<PlcVariableKey> keys)=>keys.Select(k=>Snapshot(k.LogicalId,k.LogicalId.EndsWith(".Mode")?Mode:k.LogicalId.EndsWith(".Status")?Status:k.LogicalId.EndsWith(".Action[3]")?StopPressed:Emergency)).ToArray();
    public IReadOnlyList<PlcVariableBinding> GetCatalog()=>[];public IReadOnlyList<PlcAcquisitionGroupDefinition> GetGroups()=>[];public event EventHandler? ValuesChanged{add{}remove{}}
}
public sealed class Runtime : IPlcRuntime
{
    public long Epoch=7;public int Stops;public Func<Task>? OnStop;
    public PlcRuntimeStatus Status=>new(PlcRuntimeState.Running,Fixture.Stamp,[new("D",PlcDeviceState.Connected,Epoch,DateTimeOffset.UtcNow,null)],[]);
    public Task<PlcRuntimeStartResult> StartAsync(string? configurationFilePath=null,CancellationToken cancellationToken=default)=>Task.FromResult(new PlcRuntimeStartResult(true,Status));
    public Task ReconnectDeviceAsync(string deviceId,CancellationToken cancellationToken=default)=>throw new Exception("No external reconnect in tests");
    public async Task StopAsync(CancellationToken cancellationToken=default){Stops++;if(OnStop is not null)await OnStop();}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
public sealed class Configuration : IPlcBindingConfigProvider
{
    public Configuration(PlcDeviceDefinition device)
    {
        var source=new PlcConfigurationSource("test",1,new Dictionary<string,int>());
        var variables=new List<PlcVariableBinding>();
        foreach(var (name,index) in new[]{("Debug",6),("Initialize",7),("Maintenance",8)})
            variables.Add(new(){Source=source,Enabled=true,DeviceId="D",Protocol=PlcProtocol.OpcUa,Category="设备状态",ComponentId="UnitMode[0]",LogicalId="UnitMode[0].Request."+name,DataType=PlcDataType.Bool,Access=PlcAccess.Write,WriteMode=index==7?"握手请求":"保持请求",Address=new OpcUaBindingAddress("urn:test","test.System.st_UnitMode[0].b_Action",index)});
        variables.Add(new(){Source=source,Enabled=true,DeviceId="D",Protocol=PlcProtocol.OpcUa,Category="轴",ComponentId="Axis[0]",LogicalId="Axis[0].Positions[0].Speed",DataType=PlcDataType.Float,Access=PlcAccess.Write,WriteMode="参数暂存",Address=new OpcUaBindingAddress("urn:test","test.speed",null)});
        foreach(var index in new[]{2,3})
            variables.Add(new(){Source=source,Enabled=true,DeviceId="D",Protocol=PlcProtocol.OpcUa,Category="设备状态",ComponentId="UnitMode[0]",LogicalId=$"UnitMode[0].Action[{index}]",DataType=PlcDataType.Bool,Access=PlcAccess.ReadWrite,WriteMode="握手请求",Address=new OpcUaBindingAddress("urn:test","test.System.st_UnitMode[0].b_Action",index)});
        Current=new(){Version="V0.8",FilePath="test",FileSha256="hash",Devices=[device],Variables=variables,AcquisitionGroups=[]};
    }
    public PlcBindingConfiguration? Current{get;}public PlcConfigurationLoadResult? LastLoadResult=>null;
    public Task<PlcConfigurationLoadResult> LoadAsync(string? filePath=null,CancellationToken cancellationToken=default)=>throw new Exception("No configuration file read in tests");
}
public sealed class Client : IPlcClient
{
    public string DeviceId=>"D";public PlcConnectionState ConnectionState=>PlcConnectionState.Connected;
    public event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged{add{}remove{}}
    public List<PlcWriteRequest> Writes=new();public Func<Task>? BeforeReturn;public bool Fail;public int Reads;
    public Task ConnectAsync(CancellationToken token=default)=>throw new Exception("Real connection path must not run");
    public Task DisconnectAsync(CancellationToken token=default)=>Task.CompletedTask;public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    public Task<PlcWriteResult> WriteAsync(PlcWriteRequest request,CancellationToken token=default)=>throw new NotSupportedException();
    public async Task<PlcBatchResult<PlcWriteResult>> WriteBatchAsync(IReadOnlyList<PlcWriteRequest> requests,CancellationToken token=default)
    {
        Writes.AddRange(requests);if(BeforeReturn is{} callback)await callback();if(Fail)throw new IOException("fake interrupted write");
        return new(requests.Select(r=>new PlcWriteResult(r.Point,PlcWriteState.Succeeded,null,DateTimeOffset.UtcNow,null)),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
    }
    public Task<PlcBatchResult<PlcReadResult>> ReadBatchAsync(IReadOnlyList<PlcPoint> points,CancellationToken token=default)
    {
        Reads++;return Task.FromResult(new PlcBatchResult<PlcReadResult>(points.Select(p=>new PlcReadResult(p,Writes.Last(w=>w.Point==p).Value,typeof(float),true,PlcDataQuality.Good,null,null,null,DateTimeOffset.UtcNow,null)),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow));
    }
}
