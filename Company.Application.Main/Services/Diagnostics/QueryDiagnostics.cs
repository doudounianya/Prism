using Company.Database.PLC.Models;
using Company.Application.Share.PLC.Capacity.Models;
using Company.Logger;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Company.Application.Main.Services.Diagnostics;

/// <summary>
/// 应用层查询诊断。上下文只接受明确列出的筛选范围，不序列化整个查询、异常或数据库连接对象。
/// 关键词、SQL、异常消息和连接串均不进入此日志；界面与日志用随机编号关联。
/// </summary>
public static class QueryDiagnostics
{
    public sealed record Scope(string Device, string From, string To, int? Page = null, int? PageSize = null,
        string Source = "", string Detail = "");

    public static Scope Alarm(AlarmHistoryQuery query) => new(Id(query.DeviceId), query.FromUtc.ToString("O"),
        query.ToUtc.ToString("O"), query.Page, query.PageSize);
    public static Scope Detail(string eventId) => new("", "", "", Detail: Id(eventId));
    public static Scope Day(ProductionDayQuery query, ProductionCapacityDataSource source) => new(Id(query.DeviceId),
        query.ProductionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "", Source: source.ToString() + ":" + Id(query.SourceId));
    public static Scope Trend(ProductionTrendQuery query, ProductionCapacityDataSource source) => new(Id(query.DeviceId),
        query.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), query.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Source: source.ToString() + ":" + Id(query.SourceId));

    /// <summary>只记录真正失败；即使日志系统异常，原页面仍可报告编号并继续响应操作。</summary>
    public static string Failure(string page, Scope scope, long startedAt, Exception exception)
    {
        var errorId = "Q-" + Guid.NewGuid().ToString("N")[..12];
        if (exception is OperationCanceledException) return errorId;
        try
        {
            var types = new List<string>(); var codes = new List<int>();
            // 限定遍历深度和已知数值属性；不读取 Message、Data、StackTrace 或任意文本属性。
            for (Exception? current = exception; current is not null && types.Count < 8; current = current.InnerException)
            {
                types.Add(current.GetType().FullName ?? current.GetType().Name);
                codes.Add(current.HResult);
                if (current.GetType().FullName is "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException" &&
                    current.GetType().GetProperty("Number")?.GetValue(current) is int number)
                    codes.Add(number);
            }
            Logs.LogError("查询失败 " + JsonSerializer.Serialize(new
            {
                ErrorId = errorId, Page = page, Scope = scope, ElapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                ExceptionTypes = types, NumericCodes = codes
            }));
        }
        catch { /* 诊断失败不能覆盖原查询结果或阻断界面。 */ }
        return errorId;
    }

    /// <summary>
    /// 取消页面等待而不擅自释放仍在执行 SQL 的串行门闩。调用者须提前捕获 Token，
    /// 后台任务独立负责 finally 释放门闩；取消后迟到的异常只观察，不产生重复错误日志。
    /// </summary>
    public static async Task<T> AwaitAsync<T>(Task<T> pending, CancellationToken token)
    {
        try { return await pending.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    // 设备和来源应为配置标识。非标准输入仅标记存在，避免有人把连接串/SQL粘贴进输入框后被日志带出。
    private static string Id(string? value) => string.IsNullOrEmpty(value) ? "" : value.Length <= 128 &&
        value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '@') ? value : "[非标准标识]";
}
