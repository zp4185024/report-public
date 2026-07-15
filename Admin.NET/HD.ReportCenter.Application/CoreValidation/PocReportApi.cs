using Furion.FriendlyException;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Microsoft.AspNetCore.Http;
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

    public PocReportApi(
        PocTemplateService templateService,
        PocTemplateExchangeService templateExchangeService)
    {
        _templateService = templateService;
        _templateExchangeService = templateExchangeService;
    }

    /// <summary>读取数据库模板；未保存过时由服务层返回 revision 0 的种子模板。</summary>
    [HttpGet("/api/report-center/poc/{reportCode}/template")]
    public async Task<PocTemplateDocument> GetTemplate([FromRoute] string reportCode)
    {
        EnsureReportExists(reportCode);
        return await HandleTemplateOperation(
            () => _templateService.GetAsync(PocReportCatalog.ReportCode));
    }

    /// <summary>保存工作簿快照和绑定，使用请求中的 revision 做乐观并发检查。</summary>
    [HttpPut("/api/report-center/poc/{reportCode}/template")]
    public async Task<PocTemplateDocument> SaveTemplate(
        [FromRoute] string reportCode,
        [FromBody] PocSaveTemplateRequest request)
    {
        EnsureReportExists(reportCode);
        return await HandleTemplateOperation(
            () => _templateService.SaveAsync(PocReportCatalog.ReportCode, request));
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
        EnsureReportExists(reportCode);
        if (file is null || file.Length == 0)
        {
            throw Oops.Oh("TEMPLATE_XLSX_FILE_REQUIRED").StatusCode(400);
        }

        await using var stream = file.OpenReadStream();
        return await HandleTemplateOperation(
            () => _templateExchangeService.ImportAsync(stream, file.FileName));
    }

    /// <summary>把页面当前快照导出为 XLSX；无需先保存，不会改动数据库模板。</summary>
    [HttpPost("/api/report-center/poc/{reportCode}/template/export-xlsx")]
    public async Task<FileStreamResult> ExportTemplateXlsx(
        [FromRoute] string reportCode,
        [FromBody] PocTemplateXlsxExportRequest request)
    {
        EnsureReportExists(reportCode);
        var exchange = await HandleTemplateOperation(
            () => _templateExchangeService.ExportAsync(
                request.Snapshot,
                PocReportCatalog.GetDefinition().Name));

        return new FileStreamResult(exchange.Content, ExcelContentType)
        {
            FileDownloadName = exchange.FileName
        };
    }

    /// <summary>返回参数、分页和数据集字段定义，供查询区、字段面板和绑定校验共同使用。</summary>
    [HttpGet("/api/report-center/poc/{reportCode}/definition")]
    public PocReportDefinition Definition([FromRoute] string reportCode)
    {
        EnsureReportExists(reportCode);
        return PocReportCatalog.GetDefinition();
    }

    /// <summary>按当前查询参数执行一页预览数据，不在后台计算模板公式。</summary>
    [HttpPost("/api/report-center/poc/{reportCode}/execute")]
    public PocExecuteResult Execute(
        [FromRoute] string reportCode,
        [FromBody] PocExecuteRequest request)
    {
        EnsureReportExists(reportCode);
        return PocReportCatalog.Execute(request);
    }

    /// <summary>同步生成普通体量 Excel；公式写入文件，由 Excel/WPS 打开时计算。</summary>
    [HttpPost("/api/report-center/poc/{reportCode}/exports/normal")]
    public FileStreamResult NormalExport(
        [FromRoute] string reportCode,
        [FromBody] PocExecuteRequest request)
    {
        EnsureReportExists(reportCode);
        var stream = new MemoryStream();
        PocExcelExporter.Write(
            stream,
            PocReportCatalog.EnumerateRows(PocReportCatalog.PreviewRowCount, request.DeptId));
        stream.Position = 0;

        return new FileStreamResult(stream, ExcelContentType)
        {
            FileDownloadName = $"科室工作量_{DateTimeOffset.Now:yyyyMMddHHmmss}.xlsx"
        };
    }

    /// <summary>创建十万行异步导出任务，立即返回可轮询的任务快照。</summary>
    [HttpPost("/api/report-center/poc/{reportCode}/exports/large")]
    public PocExportJobSnapshot StartLargeExport(
        [FromRoute] string reportCode,
        [FromBody] PocExecuteRequest request)
    {
        EnsureReportExists(reportCode);
        return LargeExportJobs.Start(request);
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

    private static void EnsureReportExists(string reportCode)
    {
        if (!string.Equals(reportCode, PocReportCatalog.ReportCode, StringComparison.OrdinalIgnoreCase))
        {
            throw Oops.Oh("REPORT_NOT_FOUND");
        }
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
}
