using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Company.Hardware.ControlCard;

/// <summary>设备内稳定的逻辑轴键；名称、总线及物理轴号由配置分别保存，不限制为四轴。</summary>
[JsonConverter(typeof(AxisIdJsonConverter))]
public readonly record struct AxisId
{
    public string Value { get; }
    public bool IsValid => MotionIdentifier.IsValid(Value);
    public AxisId(string value) { MotionIdentifier.Check(value); Value = value; }
    public override string ToString() => Value ?? string.Empty;
    // 兼容原来四轴调用的命名常量，不代表轴清单；清单只取当前配置。
    public static AxisId X => new("X"); public static AxisId Y => new("Y");
    public static AxisId Z => new("Z"); public static AxisId U => new("U");
}

/// <summary>逻辑插补组键，与控制器坐标系槽号分离。</summary>
[JsonConverter(typeof(CoordinateIdJsonConverter))]
public readonly record struct CoordinateId
{
    public string Value { get; }
    public bool IsValid => MotionIdentifier.IsValid(Value);
    public CoordinateId(string value) { MotionIdentifier.Check(value); Value = value; }
    public override string ToString() => Value ?? string.Empty;
    public static CoordinateId Coordinate0 => new("Coordinate0"); public static CoordinateId Coordinate1 => new("Coordinate1");
    public static CoordinateId Coordinate2 => new("Coordinate2"); public static CoordinateId Coordinate3 => new("Coordinate3");
}

internal static class MotionIdentifier
{
    internal static bool IsValid(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant);
    internal static void Check(string value) { if (!IsValid(value)) throw new ArgumentException("标识须为1～64位字母、数字、点、下划线或短横线，以字母开头。", nameof(value)); }
}

public sealed class AxisIdJsonConverter : JsonConverter<AxisId>
{
    public override AxisId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int old) && old is >= 0 and <= 3)
            return new AxisId(new[] { "X", "Y", "Z", "U" }[old]);
        try { return new AxisId(reader.GetString()!); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new JsonException("逻辑轴标识无效。", ex); }
    }
    public override void Write(Utf8JsonWriter writer, AxisId value, JsonSerializerOptions options)
    { if (!value.IsValid) throw new JsonException("逻辑轴标识为空或无效。"); writer.WriteStringValue(value.Value); }
    public override AxisId ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString()!);
    public override void WriteAsPropertyName(Utf8JsonWriter writer, AxisId value, JsonSerializerOptions options) => writer.WritePropertyName(value.Value);
}

public sealed class CoordinateIdJsonConverter : JsonConverter<CoordinateId>
{
    public override CoordinateId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int old) && old is >= 0 and <= 3) return new("Coordinate" + old);
        try { return new CoordinateId(reader.GetString()!); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new JsonException("插补组标识无效。", ex); }
    }
    public override void Write(Utf8JsonWriter writer, CoordinateId value, JsonSerializerOptions options)
    { if (!value.IsValid) throw new JsonException("插补组标识为空或无效。"); writer.WriteStringValue(value.Value); }
    public override CoordinateId ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString()!);
    public override void WriteAsPropertyName(Utf8JsonWriter writer, CoordinateId value, JsonSerializerOptions options) => writer.WritePropertyName(value.Value);
}
