using System.Collections.ObjectModel;

namespace Company.PlcHmi.Shared.PLC.Configuration;

/// <summary>配置诊断等级；Error 阻止加载成功，Warning 保留供使用者检查。</summary>
public enum PlcDiagnosticSeverity { Warning, Error }

/// <summary>保留配置来源，便于直接定位原表中的问题。</summary>
/// <remarks>Sheet、Row 和字段列映射保留原表位置；File 使用空表名及零行号表示文件级问题。</remarks>
public sealed record PlcConfigurationSource(string Sheet, int Row, IReadOnlyDictionary<string, int> Columns)
{
    public static PlcConfigurationSource File { get; } = new("", 0,
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()));
}

/// <summary>可定位的加载诊断，包含稳定问题代码、表/行/字段/列与显示消息；文件级诊断可没有具体列。</summary>
public sealed record PlcConfigurationDiagnostic(
    PlcDiagnosticSeverity Severity, string Code, string Sheet, int Row,
    string Field, int? Column, string Message);

/// <summary>失败时 Configuration 为空；诊断与配置均不包含连接密码。</summary>
public sealed record PlcConfigurationLoadResult(
    string FilePath, PlcBindingConfiguration? Configuration,
    IReadOnlyList<PlcConfigurationDiagnostic> Diagnostics)
{
    /// <summary>只有生成配置且不存在 Error 诊断才算成功；不能只检查 Configuration 是否非空。</summary>
    public bool Success => Configuration is not null &&
        !Diagnostics.Any(d => d.Severity == PlcDiagnosticSeverity.Error);
}
