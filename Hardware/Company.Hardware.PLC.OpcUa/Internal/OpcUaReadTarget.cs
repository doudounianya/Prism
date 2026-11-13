using System.Globalization;
using Opc.Ua;

namespace Company.Hardware.PLC.OpcUa.Internal;

/// <summary>只合并已申请的连续下标，不跨过未申请的页面变量。</summary>
internal sealed record OpcUaReadTarget(NodeId NodeId, int? Start, int Length, IReadOnlyList<(int Input, int Offset)> Inputs)
{
    internal ReadValueId ToReadValue() => new()
    {
        NodeId = NodeId, AttributeId = Attributes.Value,
        IndexRange = Start is not { } start ? null : Length == 1
            ? start.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{start}:{(long)start + Length - 1}")
    };

    internal IEnumerable<OpcUaReadTarget> Split(bool individual)
    {
        if (Start is not { } start || Length <= 1) throw new InvalidOperationException("读取范围不能继续拆分。");
        int step = individual ? 1 : (Length + 1) / 2;
        for (int offset = 0; offset < Length; offset += step)
        {
            int count = Math.Min(step, Length - offset);
            yield return new(NodeId, start + offset, count, Inputs.Where(i => i.Offset >= offset && i.Offset < offset + count)
                .Select(i => (i.Input, i.Offset - offset)).ToArray());
        }
    }

    internal static IReadOnlyList<OpcUaReadTarget> Build(IEnumerable<(NodeId Node, int? Index, int Input)> points)
    {
        var targets = new List<OpcUaReadTarget>();
        foreach (var node in points.GroupBy(p => p.Node))
        {
            var scalars = node.Where(p => p.Index is null).Select(p => (p.Input, 0)).ToArray();
            if (scalars.Length != 0) targets.Add(new(node.Key, null, 1, scalars));
            var indexed = node.Where(p => p.Index is not null).GroupBy(p => p.Index!.Value).OrderBy(g => g.Key).ToArray();
            for (int first = 0; first < indexed.Length;)
            {
                int end = first + 1;
                while (end < indexed.Length && (long)indexed[end].Key == (long)indexed[end - 1].Key + 1) end++;
                int start = indexed[first].Key;
                targets.Add(new(node.Key, start, end - first, indexed.Skip(first).Take(end - first)
                    .SelectMany(g => g.Select(p => (p.Input, g.Key - start))).ToArray()));
                first = end;
            }
        }
        return targets.OrderBy(t => t.Inputs.Min(i => i.Input)).ToArray();
    }
}
