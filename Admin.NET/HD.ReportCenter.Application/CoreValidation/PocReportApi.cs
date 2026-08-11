using Furion.FriendlyException;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;
using Microsoft.AspNetCore.Http;
using System.Data.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>导出当前编辑器快照的请求；导出不会隐式保存模板。</summary>
public sealed record PocTemplateXlsxExportRequest(JObject Snapshot);

/// <summary>
/// 报表中心核心验证 API。
/// 控制器只负责 HTTP 契约和错误映射，模板可信校验、查询构造和导出逻辑分别下沉到服务/引擎层。
/// </summary>
[AllowAnonymous]
[ApiDescriptionSettings("报表中心验证", Name = "ReportCenterPoc", Order = 901)]
public sealed class PocReportApi : IDynamicApiController
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // POC 使用进程内任务表验证异步大文件流程；生产化时应替换为持久化任务表和独立后台执行器。
    private static readonly PocLargeExportJobStore LargeExportJobs = new();

    private readonly PocTemplateService _templateService;
    private readonly PocTemplateExchangeService _templateExchangeService;
    private readonly PocTemplateDataExportService _templateDataExportService;
    private readonly PocReportConfigurationService? _configurationService;
    private readonly PocDataAssetService? _dataAssetService;
    private readonly PocSqlDatasetExecutor? _datasetExecutor;
    private readonly PocTemplateDataExporter _templateDataExporter = new();

    public PocReportApi(
        PocTemplateService templateService,
        PocTemplateExchangeService templateExchangeService,
        PocTemplateDataExportService templateDataExportService,
        PocReportConfigurationService? configurationService = null,
        PocSqlDatasetExecutor? datasetExecutor = null,
        PocDataAssetService? dataAssetService = null)
    {
        _templateService = templateService;
        _templateExchangeService = templateExchangeService;
        _templateDataExportService = templateDataExportService;
        _configurationService = configurationService;
        _datasetExecutor = datasetExecutor;
        _dataAssetService = dataAssetService;
    }

    /// <summary>读取数据库模板；未保存过时由服务层返回 revision 0 的种子模板。</summary>
    [HttpGet("/api/report-center/poc/{reportCode}/template")]
    public async Task<PocTemplateDocument> GetTemplate([FromRoute] string reportCode)
    {
        reportCode = EnsureReportCode(reportCode);
        return await HandleTemplateOperation(
            () => _templateService.GetEditableAsync(reportCode));
    }

    /// <summary>保存工作簿快照和绑定，使用请求中的 revision 做乐观并发检查。</summary>
    [HttpPut("/api/report-center/poc/{reportCode}/template")]
    public async Task<PocTemplateDocument> SaveTemplate(
        [FromRoute] string reportCode,
        [FromBody] PocSaveTemplateRequest request)
    {
        reportCode = EnsureReportCode(reportCode);
        return await HandleTemplateOperation(
            () => _templateService.SaveAsync(reportCode, request));
    }

    /// <summary>
    /// 把 XLSX 解析为经过后台校验的 Univer 草稿。
    /// 导入成功不会直接写数据库，设计人员仍需通过“保存模板”确认落库。
    /// </summary>
    [HttpPost("/api/report-center/poc/{reportCode}/template/import-xlsx")]
    public async Task<PocTemplateXlsxImportResult> ImportTemplateXlsx(
        [FromRoute] string reportCode,
        [FromForm] IFormFile file)
    {
        reportCode = EnsureReportCode(reportCode);
        if (file is null || file.Length == 0)
        {
            throw Oops.Oh("TEMPLATE_XLSX_FILE_REQUIRED").StatusCode(400);
        }

        await using var stream = file.OpenReadStream();
        var definition = await GetDefinitionAsync(reportCode);
        return await HandleTemplateOperation(
            () => _templateExchangeService.ImportAsync(stream, file.FileName, definition));
    }

    /// <summary>把页面当前快照导出为 XLSX；无需先保存，不会改动数据库模板。</summary>
    [HttpPost("/api/report-center/poc/{reportCode}/template/export-xlsx")]
    public async Task<FileStreamResult> ExportTemplateXlsx(
        [FromRoute] string reportCode,
        [FromBody] PocTemplateXlsxExportRequest request)
    {
        reportCode = EnsureReportCode(reportCode);
        var definition = await GetDefinitionAsync(reportCode);
        var exchange = await HandleTemplateOperation(
            () => _templateExchangeService.ExportAsync(
                request.Snapshot,
                definition.Name,
                definition));

        return new FileStreamResult(exchange.Content, ExcelContentType)
        {
            FileDownloadName = exchange.FileName
        };
    }

    /// <summary>返回参数、分页和数据集字段定义，供查询区、字段面板和绑定校验共同使用。</summary>
    [HttpGet("/api/report-center/poc/{reportCode}/definition")]
    public async Task<PocReportDefinition> Definition([FromRoute] string reportCode)
    {
        reportCode = EnsureReportCode(reportCode);
        return await GetDefinitionAsync(reportCode);
    }

    /// <summary>按当前查询参数执行一页预览数据，不在后台计算模板公式。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/{reportCode}/execute")]
    public async Task<PocExecuteResult> Execute(
        [FromRoute] string reportCode,
        [FromBody] PocExecuteRequest request,
        CancellationToken cancellationToken)
    {
        reportCode = EnsureReportCode(reportCode);
        if (_datasetExecutor is null)
        {
            return PocReportCatalog.Execute(request);
        }
        return await HandleDatasetOperation(
            () => _datasetExecutor.ExecuteAsync(reportCode, request, cancellationToken));
    }

    /// <summary>读取报表自身配置和可选择的全局数据集。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/{reportCode}/configuration")]
    public async Task<PocReportConfigurationDocument> Configuration(
        [FromRoute] string reportCode)
    {
        reportCode = EnsureReportCode(reportCode);
        return await RequireConfigurationService().GetDocumentAsync(reportCode);
    }

    /// <summary>原子保存报表定义、数据集引用和报表参数。</summary>
    [SuppressMonitor]
    [HttpPut("/api/report-center/poc/{reportCode}/configuration")]
    public async Task<PocReportConfigurationDocument> SaveConfiguration(
        [FromRoute] string reportCode,
        [FromBody] PocSaveReportConfigurationRequest request)
    {
        reportCode = EnsureReportCode(reportCode);
        return await HandleConfigurationOperation(
            () => RequireConfigurationService().SaveAsync(reportCode, request));
    }

    /// <summary>读取全部报表及其配置、模板修订状态。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/reports")]
    public async Task<IReadOnlyList<PocReportSummaryDocument>> Reports() =>
        await RequireConfigurationService().ListReportsAsync();

    /// <summary>读取创建报表时可选择的已启用数据集。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/reports/create-options")]
    public async Task<PocReportCreateOptionsDocument> ReportCreateOptions() =>
        await RequireConfigurationService().GetCreateOptionsAsync();

    /// <summary>创建报表配置；模板内容在首次进入编辑器时按报表定义生成。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/reports")]
    public async Task<PocReportConfigurationDocument> CreateReport(
        [FromBody] PocCreateReportRequest request) =>
        await HandleConfigurationOperation(
            () => RequireConfigurationService().CreateAsync(request));

    /// <summary>读取全部脱敏数据源。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/data-sources")]
    public async Task<IReadOnlyList<PocDataSourceDocument>> DataSources() =>
        await RequireDataAssetService().ListDataSourcesAsync();

    /// <summary>新增或更新一个全局数据源。</summary>
    [SuppressMonitor]
    [HttpPut("/api/report-center/poc/data-sources/{code}")]
    public async Task<PocDataSourceDocument> SaveDataSource(
        [FromRoute] string code,
        [FromBody] PocDataSourceInput request) =>
        await HandleConfigurationOperation(
            () => RequireDataAssetService().SaveDataSourceAsync(code, request));

    /// <summary>测试所选数据库连接；响应不包含连接串和密码。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/data-sources/test")]
    public async Task<PocDataSourceTestResult> TestDataSource(
        [FromBody] PocDataSourceInput request,
        CancellationToken cancellationToken) =>
        await HandleConfigurationOperation(
            () => RequireDataAssetService().TestDataSourceAsync(request, cancellationToken));

    /// <summary>读取全部全局数据集及其字段契约。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/datasets")]
    public async Task<IReadOnlyList<PocDatasetDocument>> Datasets() =>
        await RequireDataAssetService().ListDatasetsAsync();

    /// <summary>新增或更新一个全局数据集。</summary>
    [SuppressMonitor]
    [HttpPut("/api/report-center/poc/datasets/{code}")]
    public async Task<PocDatasetDocument> SaveDataset(
        [FromRoute] string code,
        [FromBody] PocDatasetDocument request) =>
        await HandleConfigurationOperation(
            () => RequireDataAssetService().SaveDatasetAsync(code, request));

    /// <summary>读取所有引用指定全局数据集的报表。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/datasets/{code}/references")]
    public async Task<IReadOnlyList<PocDatasetReferenceDocument>> DatasetReferences(
        [FromRoute] string code) =>
        await HandleConfigurationOperation(
            () => RequireDataAssetService().ListDatasetReferencesAsync(code));

    /// <summary>使用已保存的数据集和临时变量值执行最多 50 行只读预览。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/datasets/{code}/preview")]
    public async Task<PocDatasetPreviewResult> PreviewDataset(
        [FromRoute] string code,
        [FromBody] PocDatasetPreviewRequest request,
        CancellationToken cancellationToken) =>
        await HandleDatasetOperation(
            () => RequireDataAssetService().PreviewDatasetAsync(code, request, cancellationToken));

    /// <summary>使用数据库中指定 revision 的模板生成普通体量全量数据 Excel。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/{reportCode}/exports/normal")]
    public async Task<FileStreamResult> NormalExport(
        [FromRoute] string reportCode,
        [FromBody] PocDataExportRequest request,
        CancellationToken cancellationToken)
    {
        reportCode = EnsureReportCode(reportCode);
        var stream = new MemoryStream();
        try
        {
            var prepared = await HandleDataExportOperation(
                () => _templateDataExportService.CreatePreparedAsync(
                    reportCode,
                    request,
                    cancellationToken));
            var pages = prepared.RuntimePlan is null
                ? PocReportCatalog.ReadExportPages(request.Query, cancellationToken)
                : PocSqlDatasetExecutor.ReadExportPagesAsync(
                    prepared.RuntimePlan,
                    cancellationToken);
            await HandleDataExportOperation(async () =>
            {
                await _templateDataExporter.WriteNormalAsync(
                    stream,
                    prepared.TemplatePlan,
                    pages,
                    cancellationToken);
                return true;
            });
            stream.Position = 0;

            return new FileStreamResult(stream, ExcelContentType)
            {
                FileDownloadName = BuildExportFileName(prepared.TemplatePlan)
            };
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>校验并冻结已保存模板后创建异步流式导出任务，立即返回可轮询快照。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/{reportCode}/exports/large")]
    public async Task<PocExportJobSnapshot> StartLargeExport(
        [FromRoute] string reportCode,
        [FromBody] PocDataExportRequest request,
        CancellationToken cancellationToken)
    {
        reportCode = EnsureReportCode(reportCode);
        var prepared = await HandleDataExportOperation(
            () => _templateDataExportService.CreatePreparedAsync(
                reportCode,
                request,
                cancellationToken));
        return prepared.RuntimePlan is null
            ? LargeExportJobs.Start(prepared.TemplatePlan)
            : LargeExportJobs.Start(
                prepared.TemplatePlan,
                token => PocSqlDatasetExecutor.ReadExportPagesAsync(
                    prepared.RuntimePlan,
                    token));
    }

    /// <summary>读取异步导出任务的当前状态和已写行数。</summary>
    [HttpGet("/api/report-center/poc/export-jobs/{jobNo}")]
    public PocExportJobSnapshot ExportJob([FromRoute] string jobNo) =>
        HandleJobOperation(() => LargeExportJobs.Get(jobNo));

    /// <summary>请求取消尚未结束的导出任务。取消是协作式的，由写行循环观察 CancellationToken。</summary>
    [HttpPost("/api/report-center/poc/export-jobs/{jobNo}/cancel")]
    public PocExportJobSnapshot CancelExportJob([FromRoute] string jobNo) =>
        HandleJobOperation(() => LargeExportJobs.Cancel(jobNo));

    /// <summary>以顺序读取方式下载已完成的大文件；任务未完成时返回稳定的业务错误码。</summary>
    [HttpGet("/api/report-center/poc/export-jobs/{jobNo}/download")]
    public FileStreamResult DownloadExportJob([FromRoute] string jobNo)
    {
        var path = HandleJobOperation(() => LargeExportJobs.GetDownloadPath(jobNo));
        return new FileStreamResult(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan),
            ExcelContentType)
        {
            FileDownloadName = $"科室工作量_大文件_{jobNo}.xlsx"
        };
    }

    private static string BuildExportFileName(PocTemplateDataExportPlan plan)
    {
        var reportName = plan.Definition?.SheetName ?? "科室工作量";
        var parameters = plan.Parameters;
        if (parameters is not null &&
            parameters.TryGetValue("startDate", out var startDate) &&
            parameters.TryGetValue("endDate", out var endDate) &&
            !string.IsNullOrWhiteSpace(startDate) &&
            !string.IsNullOrWhiteSpace(endDate))
        {
            return $"{reportName}_{startDate.Replace("-", string.Empty)}_{endDate.Replace("-", string.Empty)}.xlsx";
        }
        return $"{reportName}.xlsx";
    }

    private static string EnsureReportCode(string reportCode)
    {
        if (string.IsNullOrWhiteSpace(reportCode) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                reportCode,
                "^[A-Za-z][A-Za-z0-9_-]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw Oops.Oh("REPORT_NOT_FOUND").StatusCode(404);
        }
        return reportCode.Equals(PocReportCatalog.ReportCode, StringComparison.OrdinalIgnoreCase)
            ? PocReportCatalog.ReportCode
            : reportCode.Trim();
    }

    private static T HandleJobOperation<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw Oops.Oh(exception.Message);
        }
    }

    private static async Task<T> HandleTemplateOperation<T>(Func<Task<T>> operation)
    {
        // 在一个入口集中转换领域异常，确保前端不依赖数据库/JSON 库的具体异常文本。
        try
        {
            return await operation();
        }
        catch (PocTemplateRevisionConflictException)
        {
            throw Oops.Oh("TEMPLATE_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocStoredTemplateCorruptedException)
        {
            throw Oops.Oh(PocStoredTemplateCorruptedException.ErrorCode).StatusCode(500);
        }
        catch (PocTemplateBindingMismatchException)
        {
            throw Oops.Oh(PocTemplateBindingMismatchException.ErrorCode).StatusCode(400);
        }
        catch (PocTemplateValidationException exception)
        {
            throw Oops.Oh("TEMPLATE_VALIDATION_FAILED: {0}", exception.Message).StatusCode(400);
        }
        catch (PocExchangeConversionException exception)
        {
            var error = string.IsNullOrWhiteSpace(exception.RuleCode)
                ? exception.Code
                : $"{exception.Code}: {exception.RuleCode}";
            throw Oops.Oh(error).StatusCode(400);
        }
        catch (JsonException)
        {
            throw Oops.Oh("TEMPLATE_SNAPSHOT_INVALID").StatusCode(400);
        }
    }

    private static async Task<T> HandleDataExportOperation<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (PocDataExportConflictException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(409);
        }
        catch (PocNormalExportLimitException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(413);
        }
    }

    private Task<PocReportDefinition> GetDefinitionAsync(string reportCode) =>
        _configurationService is null
            ? Task.FromResult(PocReportCatalog.GetDefinition())
            : _configurationService.GetDefinitionAsync(reportCode);

    private PocReportConfigurationService RequireConfigurationService() =>
        _configurationService
        ?? throw Oops.Oh("REPORT_CONFIGURATION_NOT_AVAILABLE").StatusCode(503);

    private PocDataAssetService RequireDataAssetService() =>
        _dataAssetService
        ?? throw Oops.Oh("DATA_ASSET_SERVICE_NOT_AVAILABLE").StatusCode(503);

    private static async Task<T> HandleConfigurationOperation<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (PocReportConfigurationRevisionConflictException)
        {
            throw Oops.Oh("REPORT_CONFIGURATION_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocReportAlreadyExistsException exception)
        {
            throw Oops.Oh($"REPORT_ALREADY_EXISTS: {exception.Message}").StatusCode(409);
        }
        catch (PocDataAssetRevisionConflictException)
        {
            throw Oops.Oh("DATA_ASSET_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocDatasetConfigurationException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(400);
        }
        catch (DbException)
        {
            throw Oops.Oh("DATA_SOURCE_CONNECTION_FAILED").StatusCode(400);
        }
        catch (SqlSugarException)
        {
            throw Oops.Oh("DATA_SOURCE_CONNECTION_FAILED").StatusCode(400);
        }
    }

    private static async Task<T> HandleDatasetOperation<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (PocParameterResolutionException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.ParameterCode}").StatusCode(400);
        }
        catch (PocDatasetConfigurationException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(400);
        }
        catch (PocDatasetExecutionException exception)
        {
            throw Oops.Oh(exception.Code).StatusCode(400);
        }
    }
}
