using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Company.Hmi.Contracts;

internal sealed record SharedIoFixture(bool[] DiBefore, bool[] DoBefore, bool[] DiAfter, bool[] DoAfter)
{
    internal static (SharedIoFixture Data, string Sha256) Load()
    {
        using var resource = typeof(SharedIoFixture).Assembly.GetManifestResourceStream("SharedIoFixture.json")
            ?? throw new InvalidOperationException("The shared IO fixture resource is missing.");
        using var content = new MemoryStream();
        resource.CopyTo(content);
        byte[] bytes = content.ToArray();
        var fixture = JsonSerializer.Deserialize<SharedIoFixture>(bytes)
            ?? throw new InvalidOperationException("Could not read the shared IO fixture.");
        return (fixture, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    internal static bool Matches(IIoMonitor monitor, string direction, bool[] expected)
    {
        var rows = monitor.Items;
        int on = expected.Count(value => value);
        string summary = $"ON {on}    OFF {expected.Length - on}    未知 / 未配置 {16 - expected.Length}";
        if (monitor.SelectedGroup != "0" || monitor.SelectedSignalType != direction || rows.Count != 16 ||
            !monitor.Summary.StartsWith(summary, StringComparison.Ordinal)) return false;
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            string channel = index.ToString("00", CultureInfo.InvariantCulture);
            if (row.Channel != channel || row.LogicalId != $"IO.{direction}[0].{channel}") return false;
            if (index >= expected.Length)
            {
                if (row is not { Value: null, StateText: "未配置 / 禁用", QualityText: "未配置 / 禁用", UpdatedText: "—", LastValueText: "—" })
                    return false;
                continue;
            }
            bool value = expected[index];
            if (row.Value != value || row.StateText != (value ? "ON" : "OFF") || row.QualityText != "Valid / Good" ||
                row.Indicator != (value ? IoIndicator.On : IoIndicator.Neutral) || row.LastValueText != value.ToString() ||
                !TimeOnly.TryParseExact(row.UpdatedText, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                string.IsNullOrWhiteSpace(row.BindingDetails))
                return false;
        }
        return true;
    }
}
