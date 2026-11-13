using Company.Application.Share.PLC.Configuration;
using System.Globalization;
using System.Text;

namespace Company.Application.PLC.Writing;

/// <summary>把用户输入的工程量文本转换为 PLC 原始类型，集中执行字节容量、配置范围、逆比例和整数精度校验。</summary>
public static class PlcWriteValueConverter
{
    /// <summary>文本按固定区域格式解析；返回绑定要求的原始 CLR 值，非法格式、溢出或不支持的类型直接拒绝。</summary>
    public static object ConvertValue(PlcVariableBinding binding, string input)
    {
        if (binding.DataType == PlcDataType.String)
        {
            // 当前 PLC 工程为 STRING(80)，避免按字符数通过却超过 PLC 字节容量。
            if (Encoding.UTF8.GetByteCount(input) > 80) throw new ArgumentException("名称最多80个UTF-8字节。");
            return input;
        }
        if (binding.DataType == PlcDataType.Bool)
        {
            if (!bool.TryParse(input, out var boolean)) throw new ArgumentException("布尔值应为True或False。");
            return binding.InvertBoolean ? !boolean : boolean;
        }
        // 数值输入使用 InvariantCulture，避免系统小数分隔符改变参数含义；decimal 解析同时拒绝 NaN 和无穷大。
        if (!decimal.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var engineering))
            throw new ArgumentException("请输入有效数值（不接受NaN或无穷大）。");
        if (binding.Minimum is { } min && engineering < min || binding.Maximum is { } max && engineering > max)
            throw new ArgumentException("数值超出Excel配置范围。");
        var scale = binding.Scale ?? 1;
        if (scale == 0) throw new ArgumentException("比例为0，无法进行写入换算。");
        // 先检查工程量上下限，再按 raw = (engineering - Offset) / Scale 逆换算；整数类型必须恰好为整数，不能隐式截断。
        var raw = checked((engineering - (binding.Offset ?? 0)) / scale);
        if (binding.DataType is PlcDataType.Int16 or PlcDataType.UInt16 or PlcDataType.Int32 or PlcDataType.UInt32 or PlcDataType.Int64 or PlcDataType.UInt64
            && decimal.Truncate(raw) != raw) throw new ArgumentException("换算后的整数参数不能包含小数。");
        return binding.DataType switch
        {
            PlcDataType.Int16 => (object)checked((short)raw), PlcDataType.UInt16 => (object)checked((ushort)raw),
            PlcDataType.Int32 => (object)checked((int)raw), PlcDataType.UInt32 => (object)checked((uint)raw),
            PlcDataType.Int64 => (object)checked((long)raw), PlcDataType.UInt64 => (object)checked((ulong)raw),
            PlcDataType.Float => (object)(float)raw, PlcDataType.Double => (object)(double)raw,
            _ => throw new ArgumentException("本次不支持此参数类型写入。")
        };
    }
}
