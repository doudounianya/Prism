namespace Company.Application.Main.Models;

/// <summary>供只读日志表展示的安全投影；不携带原始 JSON、密码、协议错误消息或连接配置。</summary>
public sealed record OperationLogEntry(string Id, DateTimeOffset StartedUtc, string UserName, string Device,
    string ComponentName, string Operation, string Changes, string Result)
{
    public string Time => StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
}

/// <summary>结束时间不包含；界面结束日期会转换为下一天零点，确保包含整日。</summary>
public sealed record OperationLogQuery(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string UserName = "", string Keyword = "");
public sealed record OperationLogResult(IReadOnlyList<OperationLogEntry> Items, bool Truncated, int SkippedFiles, int InvalidRecords);
