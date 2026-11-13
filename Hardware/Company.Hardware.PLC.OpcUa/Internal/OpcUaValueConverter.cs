using Company.Hardware.PLC.Models;

namespace Company.Hardware.PLC.OpcUa.Internal;

internal static class OpcUaValueConverter
{
    internal static bool IsSupported(Type? type) => type == typeof(bool) ||
        type == typeof(short) || type == typeof(ushort) || type == typeof(int) ||
        type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(string) || type == typeof(DateTime);

    internal static PlcError? ValidateWrite(PlcWriteRequest request)
    {
        if (!IsSupported(request.Point.ExpectedType))
            return new(PlcErrorKind.TypeMismatch, "不支持的 PLC 原生数据类型。");
        if (request.Value is null || request.Value.GetType() != request.Point.ExpectedType)
            return new(PlcErrorKind.TypeMismatch, "写入值必须与预期原生类型完全匹配，禁止隐式转换。");
        return null;
    }

    internal static (object? Value, PlcError? Error) Read(object? raw, Type expectedType, bool indexed)
    {
        // UA 范围读取返回一个元素的原生数组，不可强制转换成 object[]。
        if (indexed)
        {
            if (raw is not Array array || array.Rank != 1 || array.Length != 1)
                return (null, new(PlcErrorKind.InvalidResponse, "数组范围读取未返回一个元素。"));
            raw = array.GetValue(array.GetLowerBound(0));
        }
        if (raw is null || raw.GetType() != expectedType)
            return (null, new(PlcErrorKind.TypeMismatch, "返回的 PLC 原生类型与点位配置不一致。"));
        return (raw, null);
    }

    internal static object WriteValue(PlcWriteRequest request)
    {
        if (request.Point.Address is Models.OpcUaAddress { ArrayIndex: not null })
        {
            var value = Array.CreateInstance(request.Point.ExpectedType, 1);
            value.SetValue(request.Value, 0);
            return value;
        }
        return request.Value!;
    }
}
