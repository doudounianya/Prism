using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using Inovance.InoMotionCotrollerShop.InoServiceContract.EtherCATConfigApi;

namespace Company.Hardware.ControlCard.INOVANCE;

/// <summary>只在实际 P/Invoke 时从约定目录加载 x64 DLL，不搜索当前工作目录中的同名库。</summary>
internal static class InovanceNativeLibrary
{
    private static readonly object Sync = new();
    private static bool registered;
    internal static void RegisterResolver()
    {
        lock (Sync)
        {
            if (registered) return;
            NativeLibrary.SetDllImportResolver(typeof(ImcApi).Assembly, Resolve);
            registered = true;
        }
    }
    internal static void EnsureSupportedProcess()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("汇川驱动只允许 Windows x64 进程，不会尝试加载32位或其他架构库。");
    }
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals("IMC_API_x64.dll", StringComparison.OrdinalIgnoreCase) && !name.Equals("IMC_API_x86.dll", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        EnsureSupportedProcess();
        if (!name.Equals("IMC_API_x64.dll", StringComparison.OrdinalIgnoreCase)) throw new DllNotFoundException("当前进程不允许解析32位汇川库。");
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "IMC_API_x64.dll"));
        if (!File.Exists(path)) throw new DllNotFoundException("缺少已部署的汇川 x64 库：" + path);
        // 依赖仅从原生库所在目录和系统目录解析，不回退到裸文件名和当前工作目录。
        return NativeLibrary.Load(path, assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
    }
}
