using Company.Core.Cache;
using Company.Core.Config;
using Company.Core.Helper;
using Company.Hardware.ControlCard;
using Company.Hardware.ControlCard.None;
using System.Diagnostics;

// 运行在无 WPF Application、无 Prism 容器的普通控制台，验证可独立复用的真实路径。
var reads = 0;
using var factory = new MotionControllerFactory(kind =>
{
    if (kind != MotionDriverKind.Simulation) throw new NotSupportedException();
    reads++;
    return new NoneControlCard();
});
var card = factory.Get(MotionDriverKind.Simulation);
if (!ReferenceEquals(card, factory.Get(MotionDriverKind.Simulation)) || reads != 1)
    throw new Exception("工厂未保留每驱动单例语义");
if ((int)MotionDriverKind.Inovance != 2 || Enum.IsDefined((MotionDriverKind)1))
    throw new Exception("驱动移除后改变了已有配置编号");
try { factory.Get((MotionDriverKind)1); throw new Exception("旧驱动编号未被拒绝"); }
catch (ArgumentOutOfRangeException) { }
if (reads != 1) throw new Exception("无效驱动编号不应触发驱动解析");
if (card.IsInitialized) throw new Exception("解析驱动不应初始化设备");
if (!card.Initialize(new MotionConfiguration { Enabled = true, InputBankCount = 1, OutputBankCount = 1 }))
    throw new Exception("离线仿真无法初始化");
factory.CloseAll();
if (card.IsInitialized) throw new Exception("关闭未释放运动会话");
if (typeof(ConfigKey).FullName != "Company.Core.Config.ConfigKey" || typeof(CacheKey).FullName != "Company.Core.Cache.CacheKey")
    throw new Exception("持久化配置键身份改变");
if (JsonHelper.Deserialize<Dictionary<string,int>>(JsonHelper.Serialize(new Dictionary<string,int>{{"value",3}}))["value"] != 3)
    throw new Exception("JSON 往返失败");
var framework = AppContext.GetData("APP_CONTEXT_DEPS_FILES")?.ToString() ?? "";
foreach (var file in framework.Split(';',StringSplitOptions.RemoveEmptyEntries))
    if (File.ReadAllText(file).Contains("Microsoft.WindowsDesktop.App"))
        throw new Exception("后台测试引入了 WindowsDesktop 运行时");
var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetName().Name ?? "").ToArray();
if (assemblies.Any(x => x.StartsWith("Presentation") || x is "Company.Desktop" or "Prism.Wpf"))
    throw new Exception("后台路径加载了桌面程序集");
if (Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName is "IMC_API_x64.dll" or "IMC_API_x86.dll"))
    throw new Exception("离线测试加载了真实 SDK");
Console.WriteLine("Headless smoke passed: simulation, factory lifetime, config identity, JSON; no WPF or vendor SDK.");
