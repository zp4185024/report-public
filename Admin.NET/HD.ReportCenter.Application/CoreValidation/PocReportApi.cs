using Furion.FriendlyException;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;

namespace HD.ReportCenter.Application.CoreValidation;

[AllowAnonymous]
[ApiDescriptionSettings("报表中心验证", Name = "ReportCenterPoc", Order = 901)]
public sealed class PocReportApi : IDynamicApiController
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly PocLargeExportJobStore LargeExportJobs = new();

    private readonly PocTemplateService _templateService;

    public PocReportApi(PocTemplateService templateService)
    {
        _templateService = templateService;
    }

    [HttpGet("/api/report-center/poc/{reportCode}/template")]
    public async Task<PocTemplateDocument> GetTemplate([FromRoute] string reportCode)
    {
        EnsureReportExists(reportCode);
        return await HandleTemplateOperation(
            () => _templateService.GetAsync(PocReportCatalog.ReportCode));
    }

    [HttpPut("/api/report-center/poc/{reportCode}/template")]
    public async Task<PocTemplateDocument> SaveTemplate(
        [FromRoute] string reportCode,
        [FromBody] PocSaveTemplateRequest request)
    {
        EnsureReportExists(reportCode);
        return await HandleTemplateOperation(
            () => _templateService.SaveAsync(PocReportCatalog.ReportCode, request));
    }

    [HttpGet("/api/report-center/poc/{reportCode}/definition")]
    public PocReportDefinition Definition([FromRoute] string reportCode)
    {
        EnsureReportExists(reportCode);
        return PocReportCatalog.GetDefinition();
    }

    [HttpPost("/api/report-center/poc/{reportCode}/execute")]
    public PocExecuteResult Execute(
        [FromRoute] string reportCode,
        [FromBody] PocExecuteRequest request)
    {
        EnsureReportExists(reportCode);
        return PocReportCatalog.Execute(request);
    }

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

    [HttpPost("/api/report-center/poc/{reportCode}/exports/large")]
    public PocExportJobSnapshot StartLargeExport(
        [FromRoute] string reportCode,
        [FromBody] PocExecuteRequest request)
    {
        EnsureReportExists(reportCode);
        return LargeExportJobs.Start(request);
    }

    [HttpGet("/api/report-center/poc/export-jobs/{jobNo}")]
    public PocExportJobSnapshot ExportJob([FromRoute] string jobNo) =>
        HandleJobOperation(() => LargeExportJobs.Get(jobNo));

    [HttpPost("/api/report-center/poc/export-jobs/{jobNo}/cancel")]
    public PocExportJobSnapshot CancelExportJob([FromRoute] string jobNo) =>
        HandleJobOperation(() => LargeExportJobs.Cancel(jobNo));

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
        try
        {
            return await operation();
        }
        catch (PocTemplateRevisionConflictException)
        {
            throw Oops.Oh("TEMPLATE_REVISION_CONFLICT").StatusCode(409);
        }
        catch (PocTemplateBindingMismatchException)
        {
            throw Oops.Oh(PocTemplateBindingMismatchException.ErrorCode).StatusCode(400);
        }
        catch (PocTemplateValidationException exception)
        {
            throw Oops.Oh("TEMPLATE_VALIDATION_FAILED: {0}", exception.Message).StatusCode(400);
        }
        catch (JsonException)
        {
            throw Oops.Oh("TEMPLATE_SNAPSHOT_INVALID").StatusCode(400);
        }
    }
}
