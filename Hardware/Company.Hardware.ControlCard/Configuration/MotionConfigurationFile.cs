using System.Text.Json;
using System.Text.Json.Serialization;

namespace Company.Hardware.ControlCard;

/// <summary>只读取/校验数据，不创建控制器、不连接板卡、不下发参数。</summary>
public static class MotionConfigurationFile
{
    private static JsonSerializerOptions Options() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static MotionConfiguration Load(string path) => Parse(File.ReadAllText(path));
    public static MotionConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 1_048_576) throw new ArgumentException("运动配置超过1MB。");
        var configuration = JsonSerializer.Deserialize<MotionConfiguration>(json, Options()) ?? throw new JsonException("运动配置为空。");
        configuration.Validate(); return configuration;
    }
    public static string Serialize(MotionConfiguration configuration)
    { ArgumentNullException.ThrowIfNull(configuration); configuration.Validate(); return JsonSerializer.Serialize(configuration, Options()); }
}

public enum MotionBusKind { Unspecified, Localbus, EtherCAT }
public enum MotionAxisKind { Linear, Rotary }
public enum MotionAxisUnit { Millimetre, Degree }
public enum SynchronizationMode { ElectronicGear }
public enum SynchronizationMasterSource { CommandPosition }

/// <summary>以建立跟随时的位置为基准，SlaveDelta = MasterDelta × Ratio；不强制两个绝对零点相同。</summary>
public sealed class SynchronizationConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public AxisId MasterAxis { get; set; }
    public AxisId SlaveAxis { get; set; }
    public double Ratio { get; set; } = 1;
    public SynchronizationMode Mode { get; set; } = SynchronizationMode.ElectronicGear;
    public SynchronizationMasterSource MasterSource { get; set; } = SynchronizationMasterSource.CommandPosition;
    public SynchronizationConfiguration Snapshot() => (SynchronizationConfiguration)MemberwiseClone();
}
