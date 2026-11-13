/*
 * 文件职责与维护说明：
 * 生产日按小时/区间显示的行模型，转换区间状态、数量和时间，并复用现有集合行。
 * 缺记录、待开始和已记录零产量含义不同，显示与合计必须沿用查询结果的判定。
 */
using Company.Application.Main.Presentation;
using Company.Application.Share.PLC.Capacity.Models;
using ReactiveUI;
using System.Collections.ObjectModel;
namespace Company.Application.Main.Models;

public sealed class ProductionPeriodRow(string period, string quantity, string state, string source, DateTime? startUtc = null) : ReactiveObject
{
    private string _period = period, _quantity = quantity, _state = state, _source = source;
    public DateTime? StartUtc { get; } = startUtc;
    public string Period { get => _period; private set => this.RaiseAndSetIfChanged(ref _period, value); }
    public string Quantity { get => _quantity; private set => this.RaiseAndSetIfChanged(ref _quantity, value); }
    public string State { get => _state; private set => this.RaiseAndSetIfChanged(ref _state, value); }
    public string Source { get => _source; private set => this.RaiseAndSetIfChanged(ref _source, value); }

    // 按实际小时复用行；刷新同一区间时不重置 ItemsSource 或替换 DataGridRow。
    internal static void Synchronize(ObservableCollection<ProductionPeriodRow> rows, IReadOnlyList<ProductionPeriodRow> incoming)
    {
        for (int i = 0; i < incoming.Count; i++)
        {
            var next = incoming[i];
            int found = -1;
            for (int j = i; j < rows.Count; j++) if (rows[j].StartUtc == next.StartUtc && rows[j].Period == next.Period) { found = j; break; }
            if (found < 0) rows.Insert(i, next);
            else
            {
                if (found != i) rows.Move(found, i);
                var row = rows[i];
                row.Period = next.Period; row.Quantity = next.Quantity; row.State = next.State; row.Source = next.Source;
            }
        }
        while (rows.Count > incoming.Count) rows.RemoveAt(rows.Count - 1);
    }

    public static IReadOnlyList<ProductionPeriodRow> Build(ProductionCapacityDayResult result, int shift) =>
        result.Shifts.Single(s => s.Code == (shift == 0 ? "Day" : "Night")).Periods.Select(p => new ProductionPeriodRow(
            ProductionDisplayFormatter.Period(p.StartUtc, p.EndUtc, result.DataSource, result.Query.SourceId),
            p.State == "/" ? "/" : ProductionDisplayFormatter.Quantity(p.Quantity), p.State, p.Source, p.StartUtc)).ToArray();
}
