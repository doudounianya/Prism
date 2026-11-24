using Company.PlcHmi.Shared.PLC.Configuration;
using Company.Core.Ioc;
using System.IO;
using System.Xml;

namespace Company.PlcHmi.Configuration.Services.PLC;

/// <summary>
/// 串行加载并校验 PLC 绑定工作簿，向运行时公开最近一次完整有效的配置快照。
/// 构造与容器注册不触发加载或硬件访问；加载失败只更新诊断结果，保留此前成功的 Current。
/// </summary>
[ExposedService(Lifetime.Singleton, typeof(IPlcBindingConfigProvider))]
public sealed class PlcBindingConfigProvider : IPlcBindingConfigProvider
{
    private readonly PlcBindingExcelLoader loader;
    private readonly PlcBindingValidator validator;
    // 同一时刻只解析一个工作簿，避免后发请求先完成而覆盖另一请求的发布顺序。
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private PlcBindingConfiguration? current;
    private PlcConfigurationLoadResult? lastLoadResult;
    /// <summary>最近成功发布的配置；首次成功加载前为空，后续失败不会将它清除。</summary>
    public PlcBindingConfiguration? Current => Volatile.Read(ref current);
    /// <summary>最近完成且未被取消的加载结果，可包含错误；读取时不需要等待加载门闩。</summary>
    public PlcConfigurationLoadResult? LastLoadResult => Volatile.Read(ref lastLoadResult);

    /// <summary>接入只读工作簿解析器和语义校验器，不在此处读取配置文件。</summary>
    public PlcBindingConfigProvider(PlcBindingExcelLoader loader, PlcBindingValidator validator)
    {
        this.loader = loader; this.validator = validator;
    }

    /// <summary>加载指定 xlsx，未指定路径时使用应用输出目录下的正式 PLC 配置文件。</summary>
    /// <remarks>读取及校验在后台执行；只有无错误诊断且未取消的结果才替换当前配置。</remarks>
    public async Task<PlcConfigurationLoadResult> LoadAsync(string? filePath = null, CancellationToken cancellationToken = default)
    {
        string path = filePath ?? Path.Combine(AppContext.BaseDirectory, "Config", "PLC", "CRT_HMI_PLC变量绑定配置.xlsx");
        // 等待和后台解析均可取消；拿到门闩后才进入 try/finally，确保只释放实际取得的许可。
        await loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => Load(path, cancellationToken), cancellationToken).ConfigureAwait(false);
            // 在发布前再次检查取消，避免调用方放弃请求后仍提交刚完成的快照或诊断。
            cancellationToken.ThrowIfCancellationRequested();
            // 原子发布完整对象引用；消费者不会看到正在构造或只通过部分校验的配置。
            if (result.Success) Volatile.Write(ref current, result.Configuration);
            Volatile.Write(ref lastLoadResult, result);
            return result;
        }
        finally { loadGate.Release(); }
    }

    private PlcConfigurationLoadResult Load(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
                return Failure(path, "FILE_FORMAT", "PLC配置文件必须为.xlsx格式。");
            var parsed = loader.Read(Path.GetFullPath(path), cancellationToken);
            // 字段格式/表结构诊断与跨表语义诊断汇总后统一判定；Warning 不阻止发布，Error 阻止发布。
            var diagnostics = parsed.Diagnostics.Concat(validator.Validate(parsed.Configuration)).ToArray();
            bool errors = diagnostics.Any(d => d.Severity == PlcDiagnosticSeverity.Error);
            return new(path, errors ? null : parsed.Configuration, Array.AsReadOnly(diagnostics));
        }
        // 取消维持任务取消语义；常见文件与工作簿故障才转换为可向用户展示的诊断。
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { return Failure(path, "FILE_NOT_FOUND", "找不到PLC配置文件，请检查部署目录。"); }
        catch (DirectoryNotFoundException) { return Failure(path, "FILE_NOT_FOUND", "找不到PLC配置目录。"); }
        catch (UnauthorizedAccessException) { return Failure(path, "FILE_ACCESS", "无权读取PLC配置文件，请检查文件权限。"); }
        catch (InvalidDataException e) { return Failure(path, "WORKBOOK_STRUCTURE", "Excel文件损坏或结构不符合模板。" + e.Message); }
        catch (XmlException) { return Failure(path, "WORKBOOK_STRUCTURE", "Excel内部结构损坏，无法读取。"); }
        catch (IOException) { return Failure(path, "FILE_ACCESS", "PLC配置文件无法读取，可能正被独占使用或保存中。"); }
        catch (ArgumentException) { return Failure(path, "WORKBOOK_STRUCTURE", "文件路径、重复结构或Excel数据格式无效。"); }
        catch (KeyNotFoundException) { return Failure(path, "WORKBOOK_STRUCTURE", "Excel内部关联或必要字段不完整。"); }
        catch (FormatException) { return Failure(path, "WORKBOOK_STRUCTURE", "Excel内部字段格式无效。"); }
        catch (OverflowException) { return Failure(path, "WORKBOOK_STRUCTURE", "Excel内部行列或数值超出范围。"); }
        catch (NotSupportedException) { return Failure(path, "WORKBOOK_FORMAT", "该Excel格式或编码不受支持。"); }
        catch (ExcelDataReader.Exceptions.ExcelReaderException) { return Failure(path, "WORKBOOK_FORMAT", "Excel读取失败，请使用未加密的正式.xlsx配置文件。"); }
    }

    // 文件级错误没有具体表/单元格位置，以空位置生成统一的 Error 结果，交给上层展示。
    private static PlcConfigurationLoadResult Failure(string path, string code, string message) =>
        new(path, null, Array.AsReadOnly(new[] { new PlcConfigurationDiagnostic(PlcDiagnosticSeverity.Error, code, "", 0, "", null, message) }));
}
