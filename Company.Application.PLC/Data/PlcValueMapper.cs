using Company.Application.PLC.Configuration;
using Company.Application.Share.PLC.Configuration;
using Company.Application.Share.PLC.Runtime.Models;
using Company.Hardware.PLC.Models;
using Company.Core.Ioc;
using System.Globalization;

namespace Company.Application.PLC.Data;

/// <summary>将通讯结果映射为业务快照，集中处理工程量、质量、有效时间和重连后的首次有效标志。</summary>
[ExposedService(Lifetime.Singleton)]
public sealed class PlcValueMapper
{
    /// <summary>按绑定的数据类型选择通讯层期望的 CLR 类型；未知类型直接拒绝，避免隐式解释字节内容。</summary>
    internal static Type ClrType(PlcDataType type) => type switch
    {
        PlcDataType.Bool => typeof(bool), PlcDataType.Int16 => typeof(short), PlcDataType.UInt16 => typeof(ushort),
        PlcDataType.Int32 => typeof(int), PlcDataType.UInt32 => typeof(uint), PlcDataType.Int64 => typeof(long),
        PlcDataType.UInt64 => typeof(ulong), PlcDataType.Float => typeof(float), PlcDataType.Double => typeof(double),
        PlcDataType.String => typeof(string), PlcDataType.DateTime => typeof(DateTime),
        _ => throw new ArgumentException("不支持的 PLC 数据类型。")
    };
    internal PlcVariableSnapshot Map(PlcBoundPoint point, PlcReadResult result, PlcVariableSnapshot previous, long epoch, string version)
    {
        object? value = null;
        string? error = result.Error?.Message;
        var quality = result.Quality switch
        {
            PlcDataQuality.Good => PlcObservationQuality.Good, PlcDataQuality.Uncertain => PlcObservationQuality.Uncertain,
            PlcDataQuality.Bad => PlcObservationQuality.Bad, _ => PlcObservationQuality.Unknown
        };
        if (result.HasValue && error is null)
        {
            try
            {
                value = result.Value;
                // 布尔反相独立于数值换算；数值工程量 = 原始值 × Scale + Offset，未配置时分别取 1 和 0。
                if (value is bool boolean) value = point.Binding.InvertBoolean ? !boolean : boolean;
                else if (point.Binding.Scale.GetValueOrDefault(1) != 1 || point.Binding.Offset.GetValueOrDefault() != 0)
                    value = checked(Convert.ToDecimal(value, CultureInfo.InvariantCulture) * point.Binding.Scale.GetValueOrDefault(1) + point.Binding.Offset.GetValueOrDefault());
            }
            catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
            { error = "工程量换算失败，PLC 原始值保留。"; value = null; }
        }
        // 通讯成功且工程量换算无错误才更新最近有效值；失败保留历史值和历史有效会话，当前工程量设为空。
        var valid = result.Success && error is null;
        return previous with
        {
            RawValue = result.HasValue ? result.Value : null, EngineeringValue = valid ? value : null,
            LastValidValue = valid ? value : previous.LastValidValue,
            State = error is not null || !result.HasValue ? PlcValueState.Invalid : valid ? PlcValueState.Valid : PlcValueState.Uncertain,
            Quality = quality, Error = error, NativeStatusCode = result.NativeStatusCode,
            LastAttemptAt = result.ReceivedAt, LastValidAt = valid ? result.ReceivedAt : previous.LastValidAt,
            SourceTimestamp = result.SourceTimestamp, ServerTimestamp = result.ServerTimestamp,
            ConnectionEpoch = epoch, LastValidConnectionEpoch = valid ? epoch : previous.LastValidConnectionEpoch,
            ConfigurationVersion = version, IsInitialAfterReconnect = valid && previous.LastValidConnectionEpoch != epoch,
            IsRealtime = valid && point.Group.Mode == PlcAcquisitionMode.PeriodicBatch
        };
    }
}
