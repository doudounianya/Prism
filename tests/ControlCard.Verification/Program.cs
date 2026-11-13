using Company.Hardware.ControlCard;
using Company.Hardware.ControlCard.None;
using Company.Hardware.ControlCard.AdTech;
using Company.Hardware.ControlCard.INOVANCE;
using Company.Application.Initialize.Services;
using Company.Application.Share.Events;
using Prism.DryIoc;
using Prism.Events;
using Prism.Ioc;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

// 只执行 None、Fake 和纯函数，不调用真实厂商SDK。原生驱动只验证默认关闭的准入门控。
var checks = new List<(string Name, bool Passed, string? Detail)>();
void Check(bool success, string name, string? detail = null) => checks.Add((name, success, detail));
void Group(string name, Action action)
{
    try { action(); } catch (Exception ex) { checks.Add((name + " unexpected", false, ex.ToString())); }
}
Provider Enabled() => new() { ControlCardConfig = new() { Enabled = true, DiNum = 2, DoNum = 2 } };
Fake Ready(Provider? provider = null)
{
    var fake = new Fake();
    Check(fake.Init(provider ?? Enabled()), "fake initialization");
    return fake;
}
void Stop(Fake f) => Check(f.TryStop(null, AxisStopMode.减速停止, out _), "fake stop");

Group("initialization", () =>
{
    var f = new Fake();
    Check(!f.Init(new Provider()) && f.OpenCalls == 0, "disabled config never opens");
    var p = Enabled(); p.ControlCardConfig.AxisSingleConfigs[1].HardwareAxis = 0;
    Check(!f.Init(p) && f.OpenCalls == 0, "duplicate hardware axes rejected before open");
    var fail = new Fake { ConfigureResult = false };
    Check(!fail.Init(Enabled()) && !fail.Initialized && !fail.IsReady && fail.CloseCalls == 1, "configuration failure rolls back");
    fail.ConfigureResult = true;
    Check(fail.Init(Enabled()) && fail.IsReady, "retry initialization after failure");
    Check(!fail.Init(Enabled()), "duplicate initialization rejected"); fail.Close();
    var closing = Ready(); closing.ThrowStop = true; closing.Close();
    Check(closing.CloseCalls == 1 && !closing.Initialized && !closing.IsReady, "stop exception cannot block close");
    var release = Ready(); release.FailCloseCount = 1; release.Close(); release.Close();
    Check(release.CloseCalls == 2 && !release.Initialized, "failed resource release can be retried");
});
Group("motion guards", () =>
{
    var f = Ready();
    f.Hardware[AxisType.X].Enable = false;
    Check(!f.PtpSingleMoveBy(AxisType.X, 3, out _) && f.MoveCalls == 0, "disabled relative motion rejected");
    f.Hardware[AxisType.X].Enable = true; f.Hardware[AxisType.X].Busy = true;
    Check(!f.PtpSingleMoveBy(AxisType.X, 3, out _) && f.MoveCalls == 0, "busy relative motion rejected");
    f.Hardware[AxisType.X].Busy = false;
    Check(f.PtpSingleMoveBy(AxisType.X, 3, out _) && f.LastPosition == 3, "ready relative motion uses argument"); Stop(f);
    Check(!f.PtpSingleMoveTo(AxisType.X, double.NaN, out _), "NaN rejected");
    Check(!f.PtpSingleMoveTo(AxisType.X, double.PositiveInfinity, out _), "infinite target rejected");
    Check(!f.PtpSingleMoveTo((AxisType)999, 0, out _), "invalid axis controlled rejection");
    Check(!f.JogSingleMove(AxisType.X, (MoveDirection)999, out _), "invalid jog direction rejected");
    f.Hardware[AxisType.X].Alarm = true;
    Check(!f.PtpSingleMoveTo(AxisType.X, 5, out _), "alarm blocks positioning");
    Check(!f.GoHome(AxisType.X, out _), "alarm blocks homing");
    f.Hardware[AxisType.X].Alarm = false; f.Hardware[AxisType.X].EmergencyStop = true;
    Check(!f.JogSingleMove(AxisType.X, MoveDirection.正向, out _), "emergency blocks jog");
    f.Hardware[AxisType.X].EmergencyStop = false; f.HomeResult = false;
    Check(!f.GoHome(AxisType.X, out _), "homing SDK failure returns false");
    f.Hardware[AxisType.X].Enable = false;
    Check(!f.PtpSinglePause(AxisType.X, out _) && !f.PtpSingleResume(AxisType.X, out _), "pause resume preconditions do not report success");
    f.Close(); Check(!f.GetAxisStatus(null) && !f.PtpSingleResume(AxisType.X, out _), "closed session cannot query or resume");
});
Group("snapshots and numeric limits", () =>
{
    var p = Enabled(); p.ControlCardConfig.AxisSingleConfigs[0].SoftLimitEnabled = true;
    p.ControlCardConfig.AxisSingleConfigs[0].SoftLimitNegative = -10; p.ControlCardConfig.AxisSingleConfigs[0].SoftLimitPositive = 10;
    var f = Ready(p); p.ControlCardConfig.AxisSingleConfigs[0].SoftLimitPositive = 999;
    Check(!f.PtpSingleMoveTo(AxisType.X, 11, out _), "configuration snapshot cannot change live limits");
    f.AxisStatuss[0].Enable = false;
    Check(f.PtpSingleMoveTo(AxisType.X, 4.5, out _), "public snapshot cannot change internal enable"); Stop(f);
    Check(f.LastPosition == 4.5, "fractional position preserved");
    Check(!f.OutputSetStatus(new BitStatus(1, -1), true, out _), "negative IO bit rejected");
    Check(!f.OutputSetStatus(new BitStatus(0, 16), true, out _), "cross-group IO bit rejected");
    Check(!f.OutputSetStatus(new BitStatus(2, 0), true, out _), "out of range IO group rejected");
    Check(f.OutputSetStatus(new BitStatus(1, 15), true, out _) && f.OutputCalls == 1, "last configured bit accepted");
    f.InputGetStatus(out _); f.OutputGetStatus(out _); f.Close();
    Check(f.DiStatuss.All(s => !s.Valid) && f.DoStatuss.All(s => !s.Valid), "close invalidates IO snapshots");
    Check(f.Init(Enabled()) && f.DiStatuss.Count == 32 && f.DoStatuss.Count == 32, "reopen does not duplicate IO"); f.Close();
});
Group("pending concurrency and bounded waiting", () =>
{
    var f = Ready(); int accepted = 0;
    Parallel.For(0, 16, i => { if (f.PtpSingleMoveTo(AxisType.X, i + 1, out _)) Interlocked.Increment(ref accepted); });
    Check(accepted == 1 && f.MoveCalls == 1, "scan delay cannot accept concurrent duplicate move");
    var watch = Stopwatch.StartNew();
    Check(!f.WaitForAxisIdle(AxisType.X, TimeSpan.FromMilliseconds(40), CancellationToken.None, out _), "pending motion wait times out");
    Check(watch.ElapsedMilliseconds < 1000, "timeout is bounded");
    Check(!f.PtpSingleMoveTo(AxisType.X, 1, out _), "timeout does not authorize new movement");
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    Check(!f.WaitForAxisIdle(AxisType.X, TimeSpan.FromSeconds(1), cancel.Token, out _), "wait cancellation");
    Stop(f); Check(f.PtpSingleMoveTo(AxisType.X, 2, out _), "explicit stop releases pending command");
    var waiter = Task.Run(() => f.WaitForAxisIdle(AxisType.X, TimeSpan.FromSeconds(1), CancellationToken.None, out _));
    f.Close(); Check(waiter.Wait(1000) && !waiter.Result, "close invalidates waiter without deadlock");
});
Group("unsupported and uncertain command outcomes", () =>
{
    var f = Ready(); f.UnsupportedHome = true;
    Check(!f.GoHome(AxisType.X, out var reason) && reason.Contains("不支持"), "unsupported homing gives an explicit rejection");
    Check(f.PtpSingleMoveTo(AxisType.X, 6, out _), "unsupported command does not poison later positioning"); Stop(f);
    f.ThrowMove = true;
    Check(!f.PtpSingleMoveTo(AxisType.X, 7, out _), "unknown SDK submission exception is failure");
    f.ThrowMove = false;
    Check(!f.PtpSingleMoveTo(AxisType.X, 8, out _), "unknown submission outcome remains interlocked"); Stop(f); f.Close();
});
Group("None lifecycle and real motion state", () =>
{
    var clock = new ManualClock(); var c = NoneControlCard.CreateForSimulation(clock); var p = Enabled();
    Check(c.Init(p) && c.IsReady, "None initializes and becomes ready");
    Check(!c.PtpSingleMoveTo(AxisType.X, 10, out _), "None defaults disabled axes");
    Check(c.SetAxisEnable(null, true, out _), "None enable all");
    Check(c.PtpSingleMoveTo(AxisType.X, 10.25, out _), "None absolute request");
    Check(c.GetAxisStatus(AxisType.X) && c.AxisStatuss[0].Busy, "None motion busy feedback");
    clock.Advance(2); c.GetAxisStatus(AxisType.X);
    Check(Math.Abs(c.AxisStatuss[0].EncfPos - 10.25) < 1e-6 && c.AxisStatuss[0].Arrived, "None absolute arrival at requested position");
    Check(c.PtpSingleMoveBy(AxisType.X, -2.5, out _), "None relative request after arrival"); clock.Advance(2); c.GetAxisStatus(AxisType.X);
    Check(Math.Abs(c.AxisStatuss[0].EncfPos - 7.75) < 1e-6, "None relative distance applied");
    Check(c.PtpSingleMoveTo(AxisType.X, 100, out _), "None long motion"); clock.Advance(.1);
    Check(c.PtpSinglePause(AxisType.X, out _), "None pause"); c.GetAxisStatus(AxisType.X); var paused = c.AxisStatuss[0].EncfPos;
    clock.Advance(10); c.GetAxisStatus(AxisType.X); Check(c.AxisStatuss[0].EncfPos == paused && c.AxisStatuss[0].Paused, "paused position does not advance");
    Check(c.PtpSingleResume(AxisType.X, out _), "None resume"); clock.Advance(5); c.GetAxisStatus(AxisType.X);
    Check(Math.Abs(c.AxisStatuss[0].EncfPos - 100) < 1e-6, "resume reaches remaining target");
    Check(c.GoHome(AxisType.X, out _), "None homing request"); clock.Advance(5); c.GetAxisStatus(AxisType.X);
    Check(c.AxisStatuss[0].Homed && c.AxisStatuss[0].EncfPos == 0, "None homed completion is separate feedback");
    Check(c.JogSingleMove(AxisType.X, MoveDirection.反向, out _), "None negative jog"); clock.Advance(.2); c.GetAxisStatus(AxisType.X);
    Check(c.AxisStatuss[0].EncfPos < 0 && c.AxisStatuss[0].Busy, "None jog moves in configured direction");
    Check(c.TryStop(AxisType.X, AxisStopMode.立即停止, out _), "None immediate stop request"); c.GetAxisStatus(AxisType.X); var stopped = c.AxisStatuss[0].EncfPos;
    clock.Advance(2); c.GetAxisStatus(AxisType.X); Check(!c.AxisStatuss[0].Busy && c.AxisStatuss[0].EncfPos == stopped, "None stop holds position");
    c.SetSimulatedInput(1, 15, true); Check(c.InputGetStatus(out _) && c.DiStatuss[31].Value && !c.DiStatuss[0].Value, "None IO group bit mapping");
    Check(c.OutputSetStatus(new BitStatus(1, 0), true, out _) && c.OutputGetStatus(out _) && c.DoStatuss[16].Value, "None output write and readback");
    c.Close(); Check(!c.Initialized && !c.IsReady, "None closed state");
});
Group("None coordinates", () =>
{
    var clock = new ManualClock(); var c = NoneControlCard.CreateForSimulation(clock); c.Init(Enabled()); c.SetAxisEnable(null, true, out _);
    var points = new List<PosXYZ> { new() { Pos_X = .5, Pos_Y = 1, Pos_Z = 2 }, new() { Pos_X = 3, Pos_Y = 4, Pos_Z = 5 } };
    Check(c.CrdLineMoveTo_XYZ(CrdType.CrdNo_0, points, out _), "None coordinate accepts arbitrary point count");
    Check(!c.CrdLineMoveTo_XYZ(CrdType.CrdNo_1, points, out _), "shared axes cannot run two coordinates");
    Check(c.CrdPause(CrdType.CrdNo_0, out _), "None coordinate pause");
    Check(c.CrdResume(CrdType.CrdNo_0, out _), "None coordinate resume");
    clock.Advance(10); Check(c.GetCrdStatus(null), "None coordinate status"); c.GetAxisStatus(null);
    Check(!c.CrdStatuss[0].Busy && Math.Abs(c.AxisStatuss[2].EncfPos - 5) < 1e-6, "None multi-point trajectory reaches last XYZ");
    Check(c.CrdClr(CrdType.CrdNo_0, out _), "None coordinate clear");
    Check(c.CrdLineMoveTo_XYZ(CrdType.CrdNo_0, [new() { Pos_X = 1, Pos_Y = 1, Pos_Z = 1 }], out _), "coordinate reusable after clear"); c.Close();
});
Group("None faults, limits and cancellation", () =>
{
    var clock = new ManualClock(); var c = NoneControlCard.CreateForSimulation(clock); c.Init(Enabled()); c.SetAxisEnable(null, true, out _);
    Check(c.SetSimulatedFault(AxisType.X, true), "simulate axis alarm");
    Check(!c.PtpSingleMoveTo(AxisType.X, 2, out _), "simulated alarm blocks movement");
    Check(c.ResetAxisFault(AxisType.X, out _), "None alarm reset");
    Check(c.PtpSingleMoveTo(AxisType.X, 2, out _), "motion allowed after confirmed reset"); c.TryStop(AxisType.X, AxisStopMode.减速停止, out _);
    c.SetSimulatedFault(AxisType.X, false, emergencyStop: true);
    Check(!c.SetAxisEnable(AxisType.X, true, out _), "emergency prevents enabling");
    Check(!c.ResetAxisFault(AxisType.X, out _), "alarm reset cannot clear active emergency input");
    c.SetSimulatedFault(AxisType.X, false, positiveLimit: true);
    Check(!c.PtpSingleMoveTo(AxisType.X, 10, out _), "positive limit blocks further positive motion");
    Check(c.PtpSingleMoveTo(AxisType.X, -1, out _), "positive limit permits retreat"); clock.Advance(1); c.GetAxisStatus(AxisType.X);
    Check(c.AxisStatuss[0].EncfPos == -1, "retreat reaches negative target");
    Check(!c.SetSimulatedInput(2, 0, true) && !c.SetSimulatedInput(0, 16, true), "simulation injection validates IO addresses"); c.Close();
});
Group("pure vendor data conversion", () =>
{
    var t = typeof(INOVANCEControlCard);
    var decode = t.GetMethod("DecodeIoBit", BindingFlags.NonPublic | BindingFlags.Static)!;
    Check((bool)decode.Invoke(null, [unchecked((short)0x8000), 15])!, "signed IO word preserves bit15");
    Check(!(bool)decode.Invoke(null, [unchecked((short)0x8000), 0])!, "signed IO word does not activate bit0");
    var index = t.GetMethod("OutputIndex", BindingFlags.NonPublic | BindingFlags.Static)!;
    Check(Convert.ToInt32(index.Invoke(null, [2u, 1u, 0])) == 16, "vendor IO group address");
    Check(Convert.ToInt32(index.Invoke(null, [2048u, 2047u, 15])) == 32767, "last representable native IO index");
    foreach (int bit in new[] { -1, 16 })
    {
        bool rejected = false; try { index.Invoke(null, [2u, 0u, bit]); } catch (TargetInvocationException) { rejected = true; }
        Check(rejected, "vendor invalid IO bit " + bit);
    }
    bool overflowRejected = false;
    try { index.Invoke(null, [4096u, 2048u, 0]); } catch (TargetInvocationException) { overflowRejected = true; }
    Check(overflowRejected, "native output address overflow rejected");
});
Group("Prism modules and shutdown integration", () =>
{
    var container = new DryIocContainerExtension();
    new ControlCardModule().RegisterTypes(container);
    // 故意与目录顺序不同，命名注册必须仍然稳定。
    new INOVANCEControlCardModule().RegisterTypes(container);
    new AdtechControlCardModule().RegisterTypes(container);
    new NoneControlCardModule().RegisterTypes(container);
    var factory = container.Resolve<ControlCardFactory>();
    Check(ReferenceEquals(factory, container.Resolve<IControlCardFactory>()), "factory interface shares original Prism singleton");
    Check(factory.Get(ControlCardKind.None) is NoneControlCard, "factory selects None by name");
    Check(factory.Get(ControlCardKind.AdTech) is AdtechControlCard, "factory selects AdTech by name");
    Check(factory.Get(ControlCardKind.INOVANCE) is INOVANCEControlCard, "factory selects INOVANCE by name");
    Check(ReferenceEquals(factory.Get(ControlCardKind.None), factory.Get(ControlCardKind.None)), "selected driver is singleton");
    foreach (var kind in new[] { ControlCardKind.AdTech, ControlCardKind.INOVANCE })
    {
        var real = factory.Get(kind);
        Check(!real.Initialized, kind + " module registration does not initialize");
        Check(!real.Init(Enabled()) && !real.Initialized, kind + " native gate remains closed");
    }
    var adtech = (AdtechControlCard)factory.Get(ControlCardKind.AdTech);
    var adtechProvider = Enabled(); adtechProvider.ControlCardConfig.AllowNativeHardware = true;
    Check(!adtech.Init(adtechProvider), "AdTech unconfirmed protection inputs reject before native access");
    adtech.Options.ProtectionInputValuesAreRawLevels = false;
    Check(!adtech.Init(adtechProvider), "AdTech default bus restart gate rejects before native access");
    var simulation = factory.Get(ControlCardKind.None); Check(simulation.Init(Enabled()), "factory None can be explicitly initialized");
    var events = new EventAggregator(); using var lifecycle = new ControlCardLifecycleAdapter(events, factory);
    events.GetEvent<CloseAllHardwareEvent>().Publish(); Check(!simulation.Initialized, "existing close event closes selected cards");
    factory.Dispose(); bool disposed = false; try { factory.Get(ControlCardKind.None); } catch (ObjectDisposedException) { disposed = true; }
    Check(disposed, "disposed factory cannot create drivers");
    var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToArray();
    Check(!modules.Any(m => m is not null && (m.StartsWith("IMC_API", StringComparison.OrdinalIgnoreCase) || m.Equals("adtemc.dll", StringComparison.OrdinalIgnoreCase))), "no native vendor DLL loaded");
});
// 目录扫描由 DiscoveryProbe 独立进程验证。本进程已解析驱动，原 Prism 会跳过已加载程序集，
// 所以不能在这里再以同名复制件“发现四次”；该前提错误的测试已由真实启动前提的子进程替代。

var report = checks.Select(c => new { c.Name, c.Passed, c.Detail }).ToArray();
File.WriteAllText(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var failed in checks.Where(c => !c.Passed)) Console.WriteLine("FAIL " + failed.Name + ": " + failed.Detail);
Console.WriteLine($"Passed {checks.Count(c => c.Passed)}/{checks.Count}; native vendor API calls: 0");
Environment.ExitCode = checks.Any(c => !c.Passed) ? 1 : 0;

sealed class Provider : IControlCardConfigProvider
{
    public ControlCardConfig ControlCardConfig { get; set; } = new();
    public event Action? OnChanged { add { } remove { } }
}
sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(double seconds) => now = now.AddSeconds(seconds);
}
sealed class Fake : ControlCardBase
{
    public readonly Dictionary<AxisType, AxisStatus> Hardware = Enum.GetValues<AxisType>().ToDictionary(a => a, a => new AxisStatus { Axis = a, Enable = true });
    public int OpenCalls, CloseCalls, MoveCalls, OutputCalls, FailCloseCount;
    public bool ConfigureResult = true, HomeResult = true, ThrowStop, UnsupportedHome, ThrowMove;
    public double LastPosition;
    protected override bool DoInit() { OpenCalls++; return true; }
    protected override bool DoConfigure() => ConfigureResult;
    protected override void DoClose() { CloseCalls++; if (FailCloseCount-- > 0) throw new InvalidOperationException("controlled close failure"); }
    protected override bool DoGetAxisStatus(AxisType? axis)
    {
        foreach (var state in AxisStates.Where(s => axis == null || s.Axis == axis))
        {
            var h = Hardware[state.Axis]; state.Enable = h.Enable; state.Busy = h.Busy;
            state.Alarm = h.Alarm; state.EmergencyStop = h.EmergencyStop; state.EncfPos = h.EncfPos;
            state.Arrived = h.Arrived; state.Homed = h.Homed; state.Valid = true; state.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return true;
    }
    protected override bool DoSetAxisEnable(AxisType? axis, bool enabled) { foreach (var h in Hardware.Values.Where(h => axis == null || h.Axis == axis)) h.Enable = enabled; return true; }
    protected override bool DoGoHome(AxisType axis, AxisSingleConfig config) => UnsupportedHome ? throw new NotSupportedException("回零不支持") : HomeResult;
    protected override bool DoPtpSingleMoveTo(AxisType axis, double position, AxisSingleConfig config) { if (ThrowMove) throw new IOException("controlled unknown submission outcome"); MoveCalls++; LastPosition = position; return true; }
    protected override bool DoPtpSingleMoveBy(AxisType axis, double position, AxisSingleConfig config) { MoveCalls++; LastPosition = position; return true; }
    protected override bool DoJogSingleMove(AxisType axis, MoveDirection direction, AxisSingleConfig config) => true;
    protected override void DoStop(AxisType? axis, AxisStopMode mode) { if (ThrowStop) throw new InvalidOperationException("controlled stop failure"); foreach (var h in Hardware.Values) h.Busy = false; }
    protected override bool DoPtpSinglePause(AxisType axis) => true;
    protected override bool DoPtpSingleResume(AxisType axis) => true;
    protected override bool DoGetCrdStatus(CrdType? crd) => false;
    protected override bool DoCrdLineMoveTo_XYZ(CrdType crd, List<PosXYZ> points, AxisThreeConfig config) => false;
    protected override bool DoCrdPause(CrdType crd) => false;
    protected override bool DoCrdResume(CrdType crd) => false;
    protected override bool DoCrdClr(CrdType crd) => false;
    protected override bool DoInputGetStatus() { foreach (var s in InputStates) s.Valid = true; return true; }
    protected override bool DoOutputGetStatus() { foreach (var s in OutputStates) s.Valid = true; return true; }
    protected override bool DoOutputSetStatus(BitStatus bit, bool value) { OutputCalls++; return true; }
}
