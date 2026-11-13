using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Company.Hardware.ControlCard.AdTech;

/// <summary>只注册解析规则；注册时不加载 SDK、不探测设备，也不从工作目录搜索原生库。</summary>
internal static class AdtechNativeLibrary
{
    private static readonly object Sync = new();
    private static bool registered;

    internal static void RegisterResolver()
    {
        lock (Sync)
        {
            if (registered) return;
            NativeLibrary.SetDllImportResolver(typeof(AdtechNativeLibrary).Assembly, Resolve);
            registered = true;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, "adtemc.dll", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("当前 AdTech SDK 只提供 Windows x64 库，拒绝在其他进程架构下调用。");
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "adtemc.dll");
        if (!File.Exists(path)) throw new DllNotFoundException("未部署约定位置的 AdTech x64 原生库：" + path);
        // 绝对路径加载；失败直接上抛，禁止回退到当前目录或 PATH 中的同名 DLL。
        return NativeLibrary.Load(path, assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
    }
}
