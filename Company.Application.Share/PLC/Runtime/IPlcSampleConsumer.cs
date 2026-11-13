using Company.Application.Share.PLC.Runtime.Models;
namespace Company.Application.Share.PLC.Runtime;

/// <summary>后台数据接收契约，与 OPC UA 订阅无关；实现必须响应取消且不得修改帧。</summary>
/// <remarks>消费者按组串行接收业务帧与缺口；应验证配置和会话边界，不能用界面通知替代该逐帧输入。</remarks>
public interface IPlcSampleConsumer
{
    /// <summary>处理一轮后台采样；逐项检查质量，且首次重连样本只能用于重新建立基线。</summary>
    Task OnFrameAsync(PlcAcquisitionFrame frame, CancellationToken cancellationToken);
    /// <summary>处理无法证明观测连续性的边界；缺口期间不能推定报警未发生或产能未变化。</summary>
    Task OnGapAsync(PlcMonitoringGap gap, CancellationToken cancellationToken);
}
