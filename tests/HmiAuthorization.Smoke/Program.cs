using Company.Platform.Services.Identity;
using Company.Platform.Services.Authorization;
using Company.Platform.Shared.Login;
using Company.Platform.Shared.Model;
using Company.PlcHmi.Shared.Login;
using Company.PlcHmi.Shared.Prism;
using Company.PlcHmi.Shared.PLC.Configuration;
using Company.PlcHmi.Shared.PLC.Runtime.Models;
using System.Text.Json;

var checks=new List<(string Name,bool Passed)>();
void Check(string name,bool value){checks.Add((name,value));if(!value)throw new Exception(name);}
var session=new Session();using var policy=new PlcHmiAuthorizationPolicy();using var auth=new UserAuthorizationService(session,policy);
string[] publicPages=[ViewNames.DeviceMonitorView,ViewNames.RealtimeAlarmView,ViewNames.AlarmHistoryView,ViewNames.AlarmAnalysisView,ViewNames.ProductionTodayView,ViewNames.ProductionDailyView,ViewNames.ProductionTrendView,ViewNames.AxisParameterView,ViewNames.CylinderParameterView];
string[] loggedPages=[ViewNames.ModeControlView,ViewNames.IoMonitorView,ViewNames.ProcessParameterView,ViewNames.OperationLogView];
foreach(int role in new[]{0,1,2,3,4,99})
{
    session.CurrentUser=new(){Id=1,UserName="synthetic",Role=role};bool logged=role is >=1 and <=4;
    foreach(var page in publicPages)Check($"role.{role}.public.{page}",auth.IsGranted(PlcHmiPermissions.Page(page)));
    foreach(var page in loggedPages)Check($"role.{role}.logged.{page}",auth.IsGranted(PlcHmiPermissions.Page(page))==logged);
    foreach(var page in new[]{ViewNames.ConfigView,ViewNames.UserManagementView})Check($"role.{role}.admin.{page}",auth.IsGranted(PlcHmiPermissions.Page(page))==(role==1));
    Check($"role.{role}.unknown",!auth.IsGranted(PlcHmiPermissions.Page("unknown")));
    Check($"role.{role}.mode-operation",auth.IsGranted(PlatformPermissions.OperateEquipment)==logged);
    Check($"role.{role}.no-debug-no-write",!auth.IsGranted(PlatformPermissions.ConfigureEquipment));
}
session.CurrentUser=new(){Id=3,UserName="engineer",Role=3};
DebugParameterAccess.Begin(3,"engineer","D",7,"stamp",()=>session.CurrentUser.Id==3&&session.CurrentUser.UserName=="engineer");
var snapshots=new[]{("Mode",(object)0),("Status",(object)30),("Action[6]",(object)true),("Signal[5]",(object)false)}.Select(x=>new PlcVariableSnapshot{Key=new(PlcProtocol.OpcUa,"D","UnitMode[0]."+x.Item1),EngineeringValue=x.Item2,State=PlcValueState.Valid,Quality=PlcObservationQuality.Good,ConnectionEpoch=7,ConfigurationVersion="stamp",LastValidAt=DateTimeOffset.UtcNow}).ToArray();
var version=auth.Version;DebugParameterAccess.Observe(snapshots);
Check("debug.feedback-authorizes-engineer",auth.IsGranted(PlatformPermissions.ConfigureEquipment)&&auth.Version>version);
version=auth.Version;session.CurrentUser=new();
Check("logout.revokes-debug-and-increments-version",!auth.IsGranted(PlatformPermissions.ConfigureEquipment)&&!auth.IsLoggedIn&&auth.Version>version);
session.CurrentUser=new(){Id=1,UserName="administrator",Role=1};
var lockUser=new UserAccountInfo(91,"synthetic-lock",2,DateTime.Now);var guard=new PlcHmiAccountMutationPolicy();
try
{
    version=auth.Version;MaintenanceInterlock.Add(lockUser);
    Check("maintenance.invalidates-queued-permission",auth.Version>version);
    Check("maintenance.blocks-operation",!auth.IsGranted(PlatformPermissions.OperateEquipment)&&!auth.IsGranted(PlatformPermissions.ConfigureEquipment));
    Check("maintenance.keeps-readonly-parameters",auth.IsGranted(PlcHmiPermissions.Page(ViewNames.AxisParameterView))&&auth.IsGranted(PlcHmiPermissions.Page(ViewNames.CylinderParameterView)));
    Check("maintenance.keeps-mode-page-for-unlock",auth.IsGranted(PlcHmiPermissions.Page(ViewNames.ModeControlView)));
    Check("maintenance.process-page-blocked",!auth.IsGranted(PlcHmiPermissions.Page(ViewNames.ProcessParameterView)));
    bool delete=false,reset=false;
    try{guard.CheckDelete(lockUser);}catch(InvalidOperationException){delete=true;}
    try{guard.CheckResetPassword(lockUser);}catch(InvalidOperationException){reset=true;}
    Check("maintenance.account-mutation-protected",delete&&reset);
}
finally
{
    MaintenanceInterlock.ReleaseAll(lockUser with{Role=1});MaintenanceInterlock.Observe(false,0,0,DateTimeOffset.UtcNow.AddSeconds(1));
}
Check("maintenance.unlocked-operation-restored",auth.IsGranted(PlatformPermissions.OperateEquipment));
int changed=0;auth.Changed+=(_,_)=>changed++;auth.Dispose();policy.Dispose();DebugParameterAccess.Revoke();session.CurrentUser=new();
Check("disposed.adapters-do-not-publish",changed==0);
var report=new{total=checks.Count,passed=checks.Count(x=>x.Passed),failed=checks.Where(x=>!x.Passed).Select(x=>x.Name).ToArray(),realDeviceConnected=false};
if(args.Length>0)File.WriteAllText(args[0],JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"HMI policy checks: {report.passed}/{report.total}; no real device connected.");
