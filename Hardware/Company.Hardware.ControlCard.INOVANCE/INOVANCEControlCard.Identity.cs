using System.Diagnostics;
using System.IO;
using Inovance.InoMotionCotrollerShop.InoServiceContract.EtherCATConfigApi;

namespace Company.Hardware.ControlCard.INOVANCE;

public sealed partial class INOVANCEControlCard
{
    /// <summary>
    /// 原厂版本读取函数是没有长度参数的char*输出，资料没有给出最大容量，不能猜一个缓冲区传入。
    /// 此处只读取已经约定的本地DLL版本资源，不加载DLL；固件/FPGA身份仍明确保留Unknown。
    /// </summary>
    private void ReadLocalApiIdentity()
    {
        systemVersion = "Unknown"; fpgaVersion = "Unknown"; apiVersion = "Unknown";
        if (api is not InovanceNativeApi) { apiVersion = "Offline fake SDK"; return; }
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "IMC_API_x64.dll"));
        if (File.Exists(path)) apiVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Unknown";
    }

    private void ReadResourcesAndBindings(ulong handle)
    {
        Success(api.IMC_GetHwSysPara(handle, ref cycleTime, ref hardwareType), "读取硬件资源类型");
        if (hardwareType is < 0 or > 2) throw new InvalidOperationException("控制卡硬件资源类型未确认。");
        // pHwType只是ECAT+端子板/ECAT/端子板的组合，不是32/16/8轴型号。
        resources = default;
        Success(api.IMC_GetCardResource(handle, ref resources), "读取实际板卡资源");
        if (resources.terminalexist is < 0 or > 1 || resources.axNum is < 0 or > 32 ||
            resources.diNum < 0 || resources.doNum < 0 || resources.adNum < 0 || resources.daNum < 0 || resources.encNum < 0)
            throw new InvalidOperationException("SDK返回的硬件资源无效。");
        if ((long)Configuration.InputBankCount * 16 > resources.diNum || (long)Configuration.OutputBankCount * 16 > resources.doNum)
            throw new InvalidOperationException("配置的EtherCAT IO数量超过已读取的实际资源，未自动改写映射。");
        var physical = new HashSet<(short Type, short Channel)>();
        foreach (var axis in AxisIds)
        {
            short number = HardwareAxis(AxisConfig(axis));
            var binding = ReadBinding(handle, number);
            ValidateBinding(binding);
            var configuredBus = AxisConfig(axis).Bus;
            if ((configuredBus == MotionBusKind.Localbus && binding.AxisId != 0) ||
                (configuredBus == MotionBusKind.EtherCAT && binding.AxisId != 1))
                throw new InvalidOperationException("实际轴绑定与配置的总线类型不一致，未改写控制器绑定。");
            if (!physical.Add((binding.AxisId, binding.OutputChannel)))
                throw new InvalidOperationException("多个逻辑轴绑定到同一物理输出，拒绝初始化。");
            mappedAxes.Add(axis, number); axisBindings.Add(axis, binding);
        }
        maximumHardwareAxes = 32; // 仅协议地址上界；对外能力使用已确认映射数量。
        resourcesVerified = true;
    }

    private AxisBinding ReadBinding(ulong handle, short number)
    {
        short type = -1, output = -1, encoder = -1;
        Success(api.IMC_GetAxBondCfg(handle, number, ref type, ref output, ref encoder), "核对物理轴绑定");
        // 该字段对EtherCAT轴没有定义，不能把未写出的旧值当作绑定变化。
        return new AxisBinding(number, type, output, type == 0 ? encoder : (short)0);
    }

    private void ValidateBinding(AxisBinding binding)
    {
        if (binding.AxisNumber < 0 || binding.AxisNumber >= maximumHardwareAxes)
            throw new InvalidOperationException("轴号超出本协议支持范围。");
        if (binding.AxisId == 0)
        {
            if (hardwareType == 1 || resources.terminalexist != 1 || binding.OutputChannel is < 0 or > 5 || binding.LocalEncoderSource is < 0 or > 1)
                throw new InvalidOperationException("本地轴没有有效端子板/输出/编码器资源。");
        }
        else if (binding.AxisId == 1)
        {
            if (hardwareType == 2 || binding.OutputChannel < 0 || binding.OutputChannel >= resources.axNum)
                throw new InvalidOperationException("总线轴没有对应的实际EtherCAT通道。");
        }
        else throw new InvalidOperationException("配置轴未绑定实际输出或类型未知，不能用虚轴默认使能/到位表示就绪。");
    }

    private void ValidateCurrentBinding(ulong handle, AxisId axis)
    {
        if (!resourcesVerified || !axisBindings.TryGetValue(axis, out var expected))
            throw new InvalidOperationException("尚未确认当前轴资源。");
        var actual = ReadBinding(handle, expected.AxisNumber);
        if (actual != expected)
        {
            resourcesVerified = false;
            AxisStateBuffer.Single(s => s.Axis == axis).Mapped = false;
            throw new InvalidOperationException("轴绑定在会话中发生变化，必须停止并重新初始化，不能沿用旧映射。");
        }
    }
}
