using System.IO;
using System.Text.Json;

namespace Company.FlyScan.Runtime;

public readonly record struct ScanPoint(double X, double Y, double R1)
{
    internal const double Tolerance = 1e-7;
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(R1);
    internal bool Matches(ScanPoint point) => Math.Abs(X - point.X) <= Tolerance &&
        Math.Abs(Y - point.Y) <= Tolerance && Math.Abs(R1 - point.R1) <= Tolerance;

    /// <summary>沿实际插补片段求同一时刻的 XYR1 坐标，三个分量必须同时匹配。</summary>
    internal bool TryCross(ScanPoint end, ScanPoint target, out double fraction)
    {
        double dx = end.X - X, dy = end.Y - Y, dr = end.R1 - R1;
        double ax = Math.Abs(dx), ay = Math.Abs(dy), ar = Math.Abs(dr);
        double span = Math.Max(ax, Math.Max(ay, ar));
        fraction = 0;
        if (span <= Tolerance) return false;
        fraction = span == ax ? (target.X - X) / dx :
            span == ay ? (target.Y - Y) / dy : (target.R1 - R1) / dr;
        double bounded = Math.Clamp(fraction, 0, 1);
        var at = new ScanPoint(X + dx * bounded, Y + dy * bounded, R1 + dr * bounded);
        fraction = bounded;
        return at.Matches(target);
    }
}

public sealed record ScanTrigger(string Name, int Segment, ScanPoint Position);
public sealed record ScanGroup(string Name, string RobotPose, IReadOnlyList<ScanPoint> Waypoints,
    IReadOnlyList<ScanTrigger> Triggers);
public sealed record FlyScanRecipe(string Name, IReadOnlyList<ScanGroup> Groups)
{
    public static FlyScanRecipe Load(string path) => SimulationJson.Read<FlyScanRecipe>(path).Validate();

    public void Save(string path) => SimulationJson.Write(path, Validate());

    /// <summary>创建只读配方快照。段号从 1 开始，目标可以位于选定段的内部或终点。</summary>
    public FlyScanRecipe Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Groups is not { Count: 2 } || Groups.Any(group => group is null))
            throw new InvalidDataException("配方需要名称和两组飞拍任务。");
        if (Groups.Select(group => group.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            Groups.Select(group => group.RobotPose).Distinct(StringComparer.Ordinal).Count() != 2)
            throw new InvalidDataException("两组任务需要不同的组名和机器人示教位编号。");
        return new FlyScanRecipe(Name.Trim(), Array.AsReadOnly(Groups.Select(ValidateGroup).ToArray()));
    }

    private static ScanGroup ValidateGroup(ScanGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.Name) || string.IsNullOrWhiteSpace(group.RobotPose) ||
            group.Waypoints is not { Count: >= 2 and <= 1000 } || group.Triggers is not { Count: >= 1 and <= 1000 } ||
            group.Waypoints.Any(point => !point.IsFinite))
            throw new InvalidDataException("每组需要机器人示教位、至少两个有效轨迹点和一个触发点。");

        for (int i = 1; i < group.Waypoints.Count; i++)
            if (group.Waypoints[i - 1].Matches(group.Waypoints[i]))
                throw new InvalidDataException($"{group.Name} 第 {i} 段的起点与终点重合。");

        var names = new HashSet<string>(StringComparer.Ordinal);
        int lastSegment = 0;
        double lastFraction = 0;
        foreach (var trigger in group.Triggers)
        {
            if (trigger is null || string.IsNullOrWhiteSpace(trigger.Name) || !names.Add(trigger.Name) ||
                trigger.Segment < 1 || trigger.Segment >= group.Waypoints.Count || !trigger.Position.IsFinite)
                throw new InvalidDataException($"{group.Name} 的触发点编号、段号或坐标无效。");
            var start = group.Waypoints[trigger.Segment - 1];
            var end = group.Waypoints[trigger.Segment];
            if (start.Matches(trigger.Position) || !start.TryCross(end, trigger.Position, out double fraction) ||
                lastSegment > trigger.Segment || lastSegment == trigger.Segment && fraction <= lastFraction)
                throw new InvalidDataException($"{group.Name} 的 {trigger.Name} 必须位于指定段内或终点，并按经过顺序排列。");
            lastSegment = trigger.Segment;
            lastFraction = fraction;
        }
        return group with
        {
            Waypoints = Array.AsReadOnly(group.Waypoints.ToArray()),
            Triggers = Array.AsReadOnly(group.Triggers.ToArray())
        };
    }
}

internal static class SimulationJson
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Format)
        ?? throw new InvalidDataException("仿真 JSON 内容为空。");

    internal static void Write<T>(string path, T data)
    {
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, data, Format);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
