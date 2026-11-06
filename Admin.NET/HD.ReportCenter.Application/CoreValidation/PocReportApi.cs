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
    private readonly PocExternalParameterSourceService? _externalParameterSourceService;
    private readonly PocSqlDatasetExecutor? _datasetExecutor;
    private readonly PocReportPackageService? _reportPackageService;
    private readonly PocTemplateDataExporter _templateDataExporter = new();

    /// <summary>初始化 PocReportApi 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="templateService">templateService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="templateExchangeService">templateExchangeService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="templateDataExportService">templateDataExportService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="configurationService">configurationService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="datasetExecutor">datasetExecutor 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dataAssetService">dataAssetService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="externalParameterSourceService">externalParameterSourceService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="reportPackageService">报表包导出/导入服务，缺失时包接口返回 503。</param>
    public PocReportApi(
        PocTemplateService templateService,
        PocTemplateExchangeService templateExchangeService,
        PocTemplateDataExportService templateDataExportService,
        PocReportConfigurationService? configurationService = null,
        PocSqlDatasetExecutor? datasetExecutor = null,
        PocDataAssetService? dataAssetService = null,
        PocExternalParameterSourceService? externalParameterSourceService = null,
        PocReportPackageService? reportPackageService = null)
    {
        _templateService = templateService;
        _templateExchangeService = templateExchangeService;
        _templateDataExportService = templateDataExportService;
        _configurationService = configurationService;
        _datasetExecutor = datasetExecutor;
        _dataAssetService = dataAssetService;
        _externalParameterSourceService = externalParameterSourceService;
        _reportPackageService = reportPackageService;
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

    /// <summary>校验共享页的解除保护密码；响应不暴露密码摘要或具体失败原因。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/{reportCode}/share-protection/unlock")]
    public async Task<PocShareProtectionUnlockResult> UnlockShareProtection(
        [FromRoute] string reportCode,
        [FromBody] PocShareProtectionUnlockRequest request)
    {
        reportCode = EnsureReportCode(reportCode);
        var valid = request is not null &&
            await RequireConfigurationService().VerifyShareProtectionPasswordAsync(
                reportCode,
                request.Password);
        return new PocShareProtectionUnlockResult(valid);
    }

    /// <summary>读取全部报表及其配置、模板修订状态。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/reports")]
    public async Task<IReadOnlyList<PocReportSummaryDocument>> Reports() =>
        await RequireConfigurationService().ListReportsAsync();

    /// <summary>按关键字分页读取报表管理列表，避免客户端一次加载全部模板。</summary>
    /// <param name="page">从一开始的页码。</param>
    /// <param name="pageSize">每页记录数。</param>
    /// <param name="query">可选的模板名称、编码、工作表或数据集搜索词。</param>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/reports/page")]
    public async Task<PocPagedDocument<PocReportSummaryDocument>> ReportsPage(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 15,
        [FromQuery] string? query = null) =>
        await RequireConfigurationService().ListReportsPageAsync(page, pageSize, query);

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

    /// <summary>删除报表及其参数、模板快照等全部配套数据；编码不存在时返回 404。</summary>
    [SuppressMonitor]
    [HttpDelete("/api/report-center/poc/reports/{reportCode}")]
    public async Task<PocDeletedDocument> DeleteReport(
        [FromRoute] string reportCode)
    {
        reportCode = EnsureReportCode(reportCode);
        return await HandleConfigurationOperation(
            () => RequireConfigurationService().DeleteReportAsync(reportCode));
    }

    /// <summary>
    /// 把已保存的报表（配置 + 参数 + 模板 + 数据集定义）聚合导出为报表包 JSON；
    /// 包内不含连接串/密码，也不含分享保护口令摘要。报表尚未保存模板时返回业务错误。
    /// </summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/reports/{reportCode}/package")]
    public Task<PocReportPackage> ExportReportPackage([FromRoute] string reportCode)
    {
        reportCode = EnsureReportCode(reportCode);
        return HandleReportPackageOperation(
            () => RequireReportPackageService().ExportAsync(reportCode));
    }

    /// <summary>
    /// 只读校验上传的报表包：结构、编码、参数、SQL、变量绑定、模板一致性逐项检查，
    /// 并探测与当前库的编码冲突；响应不写库、不执行 SQL。
    /// </summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/report-packages/validate")]
    public Task<PocReportPackageValidateResult> ValidateReportPackage(
        [FromBody] PocReportPackageValidateRequest request) =>
        HandleReportPackageOperation(
            () => RequireReportPackageService().ValidateAsync(request?.Package));

    /// <summary>
    /// 在用户选定的目标数据源上受限试跑报表包：SQL 重过只读校验、参数按现有规则解析，
    /// 最多返回 50 行并比对实际列与包内字段契约。
    /// </summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/report-packages/dry-run")]
    public Task<PocReportPackageDryRunResult> DryRunReportPackage(
        [FromBody] PocReportPackageDryRunRequest request,
        CancellationToken cancellationToken) =>
        HandleReportPackageOperation(
            () => RequireReportPackageService().DryRunAsync(request, cancellationToken));

    /// <summary>
    /// 导入报表包：服务端重跑全部校验后按冲突策略落库（数据集新建/复用/替换，报表 Create/Replace），
    /// 模板经服务端重扫绑定保存，导入后报表必须处于已保存配置 + 已保存模板状态。
    /// </summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/report-packages/import")]
    public Task<PocReportPackageImportResult> ImportReportPackage(
        [FromBody] PocReportPackageImportRequest request) =>
        HandleReportPackageOperation(
            () => RequireReportPackageService().ImportAsync(request));

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

    /// <summary>删除未被数据集引用的全局数据源；仍被引用时返回 409 和引用数据集清单。</summary>
    [SuppressMonitor]
    [HttpDelete("/api/report-center/poc/data-sources/{code}")]
    public async Task<PocDeletedDocument> DeleteDataSource([FromRoute] string code) =>
        await HandleConfigurationOperation(
            () => RequireDataAssetService().DeleteDataSourceAsync(code));

    /// <summary>读取可跨报表复用的外部参数接口源；响应不包含运行时凭据。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/external-parameter-sources")]
    public async Task<IReadOnlyList<PocExternalParameterSourceDocument>> ExternalParameterSources() =>
        await RequireExternalParameterSourceService().ListAsync();

    /// <summary>新增或更新一个外部参数接口源及其返回字段映射。</summary>
    [SuppressMonitor]
    [HttpPut("/api/report-center/poc/external-parameter-sources/{code}")]
    public async Task<PocExternalParameterSourceDocument> SaveExternalParameterSource(
        [FromRoute] string code,
        [FromBody] PocExternalParameterSourceInput request) =>
        await HandleConfigurationOperation(
            () => RequireExternalParameterSourceService().SaveAsync(code, request));

    /// <summary>删除未被引用的外部参数源；报表参数或数据集变量仍绑定时返回 409 和引用清单。</summary>
    [SuppressMonitor]
    [HttpDelete("/api/report-center/poc/external-parameter-sources/{code}")]
    public async Task<PocDeletedDocument> DeleteExternalParameterSource(
        [FromRoute] string code) =>
        await HandleConfigurationOperation(
            () => RequireExternalParameterSourceService().DeleteAsync(code));

    /// <summary>使用临时上下文测试外部参数源，响应只返回已配置字段。</summary>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/external-parameter-sources/{code}/test")]
    public async Task<PocExternalParameterSourceTestResult> TestExternalParameterSource(
        [FromRoute] string code,
        [FromBody] PocExternalParameterSourceTestRequest request,
        CancellationToken cancellationToken) =>
        await HandleDatasetOperation(
            () => RequireExternalParameterSourceService().TestAsync(
                code,
                request,
                cancellationToken));

    /// <summary>读取全部全局数据集及其字段契约。</summary>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/datasets")]
    public async Task<IReadOnlyList<PocDatasetDocument>> Datasets() =>
        await RequireDataAssetService().ListDatasetsAsync();

    /// <summary>按关键字分页读取数据集管理列表，避免客户端一次加载全部数据集。</summary>
    /// <param name="page">从一开始的页码。</param>
    /// <param name="pageSize">每页记录数。</param>
    /// <param name="query">可选的数据集名称、编码或数据源搜索词。</param>
    [SuppressMonitor]
    [HttpGet("/api/report-center/poc/datasets/page")]
    public async Task<PocPagedDocument<PocDatasetDocument>> DatasetsPage(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 15,
        [FromQuery] string? query = null) =>
        await RequireDataAssetService().ListDatasetsPageAsync(page, pageSize, query);

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

    /// <summary>删除未被报表引用的全局数据集；仍被引用时返回 409 和引用报表清单。</summary>
    [SuppressMonitor]
    [HttpDelete("/api/report-center/poc/datasets/{code}")]
    public async Task<PocDeletedDocument> DeleteDataset([FromRoute] string code) =>
        await HandleConfigurationOperation(
            () => RequireDataAssetService().DeleteDatasetAsync(code));

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
            // 第一阶段：调用 NormalExport 依赖的异步能力，等待并校验返回数据。
            var prepared = await HandleDataExportOperation(
                () => _templateDataExportService.CreatePreparedAsync(
                    reportCode,
                    request,
                    cancellationToken));
            var rawPages = prepared.RuntimePlan is null
                ? PocReportCatalog.ReadExportPages(request.Query, cancellationToken)
                : PocSqlDatasetExecutor.ReadExportPagesAsync(
                    prepared.RuntimePlan,
                    cancellationToken);
            var pages = PocLocalDataProjection.ProjectPages(
                rawPages,
                prepared.TemplatePlan.Definition ?? PocReportCatalog.GetDefinition(),
                request.Query,
                cancellationToken);
            await HandleDataExportOperation(async () =>
            {
                await _templateDataExporter.WriteNormalAsync(
                    stream,
                    prepared.TemplatePlan,
                    pages,
                    cancellationToken);
                // 第2阶段：整理 NormalExport 已计算的数据，并按方法契约返回最终结果。
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
                token => PocLocalDataProjection.ProjectPages(
                    PocSqlDatasetExecutor.ReadExportPagesAsync(
                        prepared.RuntimePlan,
                        token),
                    prepared.TemplatePlan.Definition ?? PocReportCatalog.GetDefinition(),
                    request.Query,
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
        var plan = HandleJobOperation(() => LargeExportJobs.GetPlan(jobNo));
        return new FileStreamResult(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan),
            ExcelContentType)
        {
            FileDownloadName = BuildExportFileName(plan)
        };
    }

    /// <summary>构建 PocReportApi 所需的 Build Export File Name 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string BuildExportFileName(PocTemplateDataExportPlan plan)
    {
        var reportName = plan.Definition?.Name ?? plan.Definition?.SheetName ?? "科室工作量";
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            reportName = reportName.Replace(invalidCharacter, '_');
        }
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

    /// <summary>校验 PocReportApi 的 Ensure Report Code 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>响应 PocReportApi 的 Handle Job Operation 事件，并协调相关状态和后续操作。</summary>
    /// <param name="operation">operation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>响应 PocReportApi 的 Handle Template Operation 事件，并协调相关状态和后续操作。</summary>
    /// <param name="operation">operation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>响应 PocReportApi 的 Handle Data Export Operation 事件，并协调相关状态和后续操作。</summary>
    /// <param name="operation">operation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>查询或读取 PocReportApi 所需的 Get Definition Async 数据，并按调用约定返回结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private Task<PocReportDefinition> GetDefinitionAsync(string reportCode) =>
        _configurationService is null
            ? Task.FromResult(PocReportCatalog.GetDefinition())
            : _configurationService.GetDefinitionAsync(reportCode);

    /// <summary>执行 PocReportApi 的 Require Configuration Service 核心流程，并保持错误和结果语义稳定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private PocReportConfigurationService RequireConfigurationService() =>
        _configurationService
        ?? throw Oops.Oh("REPORT_CONFIGURATION_NOT_AVAILABLE").StatusCode(503);

    /// <summary>执行 PocReportApi 的 Require Data Asset Service 核心流程，并保持错误和结果语义稳定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private PocDataAssetService RequireDataAssetService() =>
        _dataAssetService
        ?? throw Oops.Oh("DATA_ASSET_SERVICE_NOT_AVAILABLE").StatusCode(503);

    /// <summary>执行 PocReportApi 的 Require External Parameter Source Service 核心流程，并保持错误和结果语义稳定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private PocExternalParameterSourceService RequireExternalParameterSourceService() =>
        _externalParameterSourceService
        ?? throw Oops.Oh("EXTERNAL_PARAMETER_SOURCE_SERVICE_NOT_AVAILABLE").StatusCode(503);

    /// <summary>获取报表包服务；依赖未注册时返回稳定的 503 业务错误而不是空引用。</summary>
    /// <returns>可用的报表包导出/导入服务实例。</returns>
    private PocReportPackageService RequireReportPackageService() =>
        _reportPackageService
        ?? throw Oops.Oh("REPORT_PACKAGE_SERVICE_NOT_AVAILABLE").StatusCode(503);

    /// <summary>
    /// 报表包接口的统一异常翻译：包业务错误按错误码区分 409（DATASET_IN_USE、REPORT_ALREADY_EXISTS）
    /// 与 400；同时覆盖导入编排触达的配置、模板、参数、执行和存储层既有异常，语义与其他入口一致。
    /// </summary>
    /// <param name="operation">待执行的包操作委托。</param>
    /// <returns>操作结果，异常时抛出 FriendlyException。</returns>
    private static async Task<T> HandleReportPackageOperation<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        // 第一层：报表包自身的业务拒绝码。资源被占用与撞码属于冲突语义映射 409，其余按请求错误映射 400。
        catch (PocReportPackageException exception)
        {
            var statusCode = exception.Code is "DATASET_IN_USE" or "REPORT_ALREADY_EXISTS" ? 409 : 400;
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(statusCode);
        }
        // 第二层：乐观并发冲突。导入用修订号 0 新建，只有并发改动才会触发，统一按 409 返回。
        catch (PocReportConfigurationRevisionConflictException)
        {
            throw Oops.Oh("REPORT_CONFIGURATION_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocTemplateRevisionConflictException)
        {
            throw Oops.Oh("TEMPLATE_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocDataAssetRevisionConflictException)
        {
            throw Oops.Oh("DATA_ASSET_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocReportAlreadyExistsException exception)
        {
            throw Oops.Oh($"REPORT_ALREADY_EXISTS: {exception.Message}").StatusCode(409);
        }
        // 第三层：导入编排触达的既有领域异常，翻译逻辑与配置/模板/数据集入口保持一致。
        catch (PocTemplateBindingMismatchException)
        {
            throw Oops.Oh(PocTemplateBindingMismatchException.ErrorCode).StatusCode(400);
        }
        catch (PocTemplateValidationException exception)
        {
            throw Oops.Oh("TEMPLATE_VALIDATION_FAILED: {0}", exception.Message).StatusCode(400);
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
        catch (PocExternalParameterException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(400);
        }
        catch (KeyNotFoundException exception)
        {
            throw Oops.Oh($"REPORT_NOT_FOUND: {exception.Message}").StatusCode(404);
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

    /// <summary>响应 PocReportApi 的 Handle Configuration Operation 事件，并协调相关状态和后续操作。</summary>
    /// <param name="operation">operation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
        catch (PocResourceInUseException exception)
        {
            // 删除被引用资源属于业务冲突，回传错误码和引用清单让管理员先处理引用方。
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(409);
        }
        catch (KeyNotFoundException exception)
        {
            // 删除目标在保存与删除之间被别人先删掉时按 404 处理，与 REPORT_NOT_FOUND 语义一致。
            throw Oops.Oh($"REPORT_NOT_FOUND: {exception.Message}").StatusCode(404);
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

    /// <summary>响应 PocReportApi 的 Handle Dataset Operation 事件，并协调相关状态和后续操作。</summary>
    /// <param name="operation">operation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
        catch (PocExternalParameterException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(400);
        }
    }
}
