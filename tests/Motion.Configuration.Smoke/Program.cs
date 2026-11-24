using System.Text.Json;
using Company.Hardware.ControlCard;
using Company.Hardware.ControlCard.None;

var checks=new List<string>();
void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);}
var profile=MotionConfigurationFile.Load(args[0]);
Check(profile.Axes.Length==13&&profile.Coordinates.Length==1&&profile.Synchronizations.Length==1,"profile counts");
Check(profile.Axes.Select(a=>a.HardwareAxis).SequenceEqual(Enumerable.Range(0,13)),"confirmed controller numbers");
Check(profile.Axes.Take(4).All(a=>a.Bus==MotionBusKind.Localbus)&&profile.Axes.Skip(4).All(a=>a.Bus==MotionBusKind.EtherCAT),"bus assignments");
Check(!profile.Enabled&&!profile.AllowNativeHardware,"example does not activate hardware");
AxisId A(string key)=>new(key);
var master=A("Front.R1");var slave=A("Front.R2");var coordinate=new CoordinateId("Front.Scan");
Check(profile.Coordinates[0].Axes.SequenceEqual(new[]{A("Front.X"),A("Front.Y"),master}),"XYR1 dimension ordering");
Check(MotionConfigurationFile.Parse(MotionConfigurationFile.Serialize(profile)).Synchronizations[0].SlaveAxis==slave,"JSON round trip");
var clock=new ManualClock();var card=NoneControlCard.CreateForSimulation(clock);
profile.Enabled=true;
Check(card.Initialize(profile),"13 axis initialization");
Check(card.AxisStates.Count==13&&card.CoordinateStates.Count==1,"dynamic state lists");
Check(card.SetAxisEnabled(null,true,out _),"enable configured axes");
// 变更原对象，运行中仍使用深快照。
profile.Synchronizations[0].Ratio=99;profile.Coordinates[0].Axes[2]=slave;profile.Axes[0].HardwareAxis=99;
Check(card.MoveRelative(master,20,out _),"master motion accepted");clock.Advance(0.1);card.RefreshAxisState(null);
var r1=card.AxisStates.Single(a=>a.Axis==master);var r2=card.AxisStates.Single(a=>a.Axis==slave);
Check(r1.ActualPosition>0&&Math.Abs(r1.ActualPosition-r2.ActualPosition)<1e-9,"follower tracks displacement");
Check(Math.Abs(r1.ActualVelocity-r2.ActualVelocity)<1e-9&&r1.Busy&&r2.Busy,"follower same velocity");
Check(!card.MoveRelative(slave,1,out _),"independent slave motion blocked");
Check(!card.HomeAxis(slave,out _),"independent slave homing blocked");
Check(card.TryStop(slave,StopMode.Decelerate,out _),"stop through slave accepted");card.RefreshAxisState(null);
Check(!card.AxisStates.Single(a=>a.Axis==master).Busy&&!card.AxisStates.Single(a=>a.Axis==slave).Busy,"coupled stop");
Check(card.MoveRelative(master,10,out _),"master motion after stop");
Check(card.SetSimulatedFault(slave,true),"follower fault injected");card.RefreshAxisState(null);
Check(!card.AxisStates.Single(a=>a.Axis==master).Busy,"slave fault stops master");
Check(!card.MoveRelative(master,1,out _),"slave fault blocks master restart");
card.SetSimulatedFault(slave,false);Check(card.ResetAxisFault(slave,out _),"coupled fault reset");
Check(card.TryStop(slave,StopMode.Decelerate,out _),"explicit stop clears previous pending request after fault");
Check(card.MoveLinearPath(coordinate,[new(){X=10,Y=5,Z=30}],out _),"configured XYR1 interpolation");
clock.Advance(0.05);card.RefreshAxisState(null);
r1=card.AxisStates.Single(a=>a.Axis==master);r2=card.AxisStates.Single(a=>a.Axis==slave);
Check(Math.Abs(r1.ActualPosition-r2.ActualPosition)<1e-9,"follower tracks during interpolation");
Check(card.MoveAbsolute(A("Back2.FixtureR"),15,out _),"thirteenth axis independent during front scan");
Check(!card.MoveRelative(slave,1,out _),"slave remains protected during trajectory");
Check(card.PauseCoordinate(coordinate,out _),"interpolation pause");
clock.Advance(1);card.RefreshAxisState(null);
Check(card.AxisStates.Single(a=>a.Axis==slave).Paused,"slave follows pause");
Check(card.ResumeCoordinate(coordinate,out _),"interpolation resume");
clock.Advance(2);card.RefreshAxisState(null);card.RefreshCoordinateState(coordinate);
Check(Math.Abs(card.AxisStates.Single(a=>a.Axis==master).ActualPosition-30)<1e-9,"mapped third interpolation component");
Check(Math.Abs(card.AxisStates.Single(a=>a.Axis==slave).ActualPosition-30)<1e-9,"slave final displacement");
Check(Math.Abs(card.AxisStates.Single(a=>a.Axis==A("Back2.FixtureR")).ActualPosition-15)<1e-9,"axis 13 reached target");
Check(card.GetCapabilities().Single(c=>c.Feature==MotionFeature.ElectronicGear).CanExecute,"simulation synchronization capability");
card.Close();
Check(card.AxisStates.All(a=>!a.Valid),"close invalidates all 13 states");
var clean=MotionConfigurationFile.Load(args[0]);
void Reject(Action<MotionConfiguration> mutate,string name)
{
 var c=clean.Snapshot();mutate(c);bool failed=false;try{c.Validate();}catch(ArgumentException){failed=true;}Check(failed,name);
}
Reject(c=>c.Axes[1].Axis=c.Axes[0].Axis,"duplicate logical id");
Reject(c=>c.Axes[4].HardwareAxis=0,"duplicate physical id across buses");
Reject(c=>c.Coordinates[0].Axes[2]=A("Missing.Axis"),"missing group reference");
Reject(c=>c.Coordinates[0].Axes[2]=slave,"slave included in interpolation group");
Reject(c=>c.Coordinates[0].Axes=[master,master,A("Front.X")],"repeated group dimension");
Reject(c=>c.Synchronizations[0].Ratio=0,"zero gear ratio");
Reject(c=>c.Synchronizations[0].Ratio=double.NaN,"non-finite ratio");
Reject(c=>c.Synchronizations[0].SlaveAxis=master,"self synchronization");
Reject(c=>c.Synchronizations=[c.Synchronizations[0],new(){Id="Reverse",Enabled=true,MasterAxis=slave,SlaveAxis=master}],"cycle rejected");
Reject(c=>c.Synchronizations=[c.Synchronizations[0],new(){Id="Second",Enabled=true,MasterAxis=A("Back1.WatchR"),SlaveAxis=slave}],"multiple masters rejected");
Reject(c=>c.Synchronizations[0].SlaveAxis=A("Missing.Axis"),"missing slave reference");
Reject(c=>c.Axes[3].Unit=MotionAxisUnit.Millimetre,"axis kind and unit mismatch");
Reject(c=>c.Coordinates=[c.Coordinates[0],new(){CoordinateId=new("Other"),HardwareCoordinate=0}],"duplicate controller coordinate slot");
Reject(c=>c.Axes[0].Axis=default,"empty logical key");
Reject(c=>c.Axes=[],"empty configuration");
var legacy=new MotionConfiguration{Enabled=true,InputBankCount=0,OutputBankCount=0};
Check(card.Initialize(legacy)&&card.AxisStates.Count==4,"legacy four-axis defaults remain usable");
Check(!card.MoveAbsolute(master,1,out _),"unknown axis does not borrow another axis");card.Close();
var negative=clean.Snapshot();negative.Synchronizations[0].Ratio=-2;negative.Enabled=true;
Check(card.Initialize(negative)&&card.SetAxisEnabled(null,true,out _),"reverse ratio initialization");
Check(card.MoveRelative(master,10,out _),"reverse ratio master move");clock.Advance(2);card.RefreshAxisState(null);
Check(Math.Abs(card.AxisStates.Single(a=>a.Axis==slave).ActualPosition+20)<1e-9,"reverse ratio displacement");card.Close();
var result=new{passed=checks.Count,checks,realHardwareConnected=false};
if(args.Length>1)File.WriteAllText(args[1],JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Configurable motion checks: {checks.Count}/{checks.Count}; no real hardware connected.");

sealed class ManualClock:TimeProvider
{
 private DateTimeOffset now=DateTimeOffset.Parse("2026-11-24T00:00:00Z");
 public override DateTimeOffset GetUtcNow()=>now;
 public void Advance(double seconds)=>now=now.AddSeconds(seconds);
}
