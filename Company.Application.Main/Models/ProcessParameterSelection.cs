/*
 * 文件职责与维护说明：
 * 工艺参数页的不可变选择快照，描述设备、上下文、配方、阶段和类别。
 * 异步申请以这份快照为依据，避免选择变化后把旧结果解释为新范围的数据。
 */
using Company.Application.Share.PLC.Configuration;

namespace Company.Application.Main.Models;

/// <summary>逻辑选择快照；地址和节点完全由配置绑定解析。</summary>
public sealed record ProcessParameterSelection(string DeviceId, PlcProtocol Protocol, string Context,
    string Recipe, string Phase, string ParameterCategory);
