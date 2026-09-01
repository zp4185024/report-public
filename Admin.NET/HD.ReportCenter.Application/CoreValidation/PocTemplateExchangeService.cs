using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>模板 XLSX 导入结果。快照和绑定已经过后台校验，可直接交给编辑器加载。</summary>
public sealed record PocTemplateXlsxImportResult(
    JObject Snapshot,
    PocTemplateBinding Binding,
    string UniverVersion,
    int MappingVersion,
    int UsedCellCount,
    IReadOnlyList<PocExchangeDiagnostic> Warnings);

/// <summary>模板 XLSX 导出结果。调用方负责释放内存流。</summary>
public sealed record PocTemplateXlsxExportResult(
    MemoryStream Content,
    string FileName,
    string UniverVersion,
    int MappingVersion) : IDisposable
{
    /// <summary>释放 PocTemplateExchangeService 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// 模板与 XLSX 的应用层交换服务。
/// 映射规则来自数据库；转换完成后仍经过模板校验器，避免导入文件绕过现有绑定约束。
/// </summary>
public sealed class PocTemplateExchangeService : ITransient
{
    public const int MaximumImportFileBytes = 10 * 1024 * 1024;
    public const int MaximumWorksheetCount = 10;
    public const int MaximumUsedCellCount = 200_000;

    private static readonly PocXlsxExchangeLimits ImportLimits = new(
        MaximumImportFileBytes,
        MaximumWorksheetCount,
        MaximumUsedCellCount);

    private readonly PocExchangeMappingStore _mappingStore;
    private readonly PocXlsxExchangeConverter _converter;

    /// <summary>运行时由依赖注入使用；转换器无状态，直接创建即可。</summary>
    public PocTemplateExchangeService(PocExchangeMappingStore mappingStore)
        : this(mappingStore, new PocXlsxExchangeConverter())
    {
    }

    /// <summary>测试可显式传入转换器，保持服务层依赖清晰。</summary>
    public PocTemplateExchangeService(
        PocExchangeMappingStore mappingStore,
        PocXlsxExchangeConverter converter)
    {
        _mappingStore = mappingStore;
        _converter = converter;
    }

    /// <summary>
    /// 把 XLSX 转为 Univer 快照。此操作只产生编辑器草稿，不会保存或覆盖数据库模板。
    /// </summary>
    public async Task<PocTemplateXlsxImportResult> ImportAsync(
        Stream input,
        string fileName,
        PocReportDefinition? definition = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            // 第一阶段：根据 ImportAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
            throw new PocExchangeConversionException(
                "TEMPLATE_XLSX_EXTENSION_REQUIRED",
                "只支持导入 .xlsx 格式的模板文件。");
        }

        if (input.CanSeek && input.Length > MaximumImportFileBytes)
        {
            throw new PocExchangeConversionException(
                "TEMPLATE_XLSX_FILE_TOO_LARGE",
                $"模板文件不能超过 {MaximumImportFileBytes / 1024 / 1024} MiB。");
        }

        var rules = await _mappingStore.GetCurrentAsync(PocExchangeMappingDefaults.CurrentUniverVersion);
        var workbookName = NormalizeWorkbookName(fileName);
        PocXlsxImportResult conversion;
        try
        {
            conversion = _converter.Import(input, rules, workbookName, ImportLimits);
        }
        catch (PocExchangeConversionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or FormatException)
        // 第2阶段：根据 ImportAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            throw new PocExchangeConversionException(
                "TEMPLATE_XLSX_INVALID",
                "无法读取该 XLSX 文件，请确认文件没有损坏。",
                null);
        }

        // 导入文件同样不可信。重新扫描 token 并生成规范绑定，前端不能自行声明绑定结果。
        var binding = PocTemplateValidator.Scan(
            conversion.Snapshot.ToString(Formatting.None),
            definition ?? PocReportCatalog.GetDefinition());
        return new PocTemplateXlsxImportResult(
            conversion.Snapshot,
            binding,
            rules.UniverVersion,
            rules.MappingVersion,
            conversion.UsedCellCount,
            conversion.Warnings);
    }

    /// <summary>把当前编辑器快照导出为 XLSX；不会读取或修改数据库中的模板正文。</summary>
    public async Task<PocTemplateXlsxExportResult> ExportAsync(
        JObject snapshot,
        string fileNameStem,
        PocReportDefinition? definition = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // 在转换前复用保存模板的可信校验，避免导出结构损坏或包含未知 token 的快照。
        _ = PocTemplateValidator.Scan(
            snapshot.ToString(Formatting.None),
            definition ?? PocReportCatalog.GetDefinition());

        var rules = await _mappingStore.GetCurrentAsync(PocExchangeMappingDefaults.CurrentUniverVersion);
        var output = new MemoryStream();
        try
        {
            _converter.Export(snapshot, rules, output);
            output.Position = 0;
            return new PocTemplateXlsxExportResult(
                output,
                $"{NormalizeWorkbookName(fileNameStem)}.xlsx",
                rules.UniverVersion,
                rules.MappingVersion);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>将 PocTemplateExchangeService 中的 Normalize Workbook Name 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string NormalizeWorkbookName(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value)?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "报表模板" : name;
    }
}
