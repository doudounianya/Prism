/*
 * 文件职责与维护说明：
 * 生产日报入口，复用 ProductionDayViewModelBase 的查询、缓存和行同步行为。
 * 构造参数选择日报模式，具体统计口径仍由所选数据来源与生产日期决定。
 */
using Company.Application.Share.PLC.Capacity;
namespace Company.Application.Main.ViewModels;

public sealed class ProductionDailyViewModel(IProductionCapacityQueryService service) : ProductionDayViewModelBase(service, false);
