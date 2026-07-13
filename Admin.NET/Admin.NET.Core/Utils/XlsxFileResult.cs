// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// Excel文件ActionResult
/// </summary>
/// <typeparam name="T"></typeparam>
public class XlsxFileResult<T> : XlsxFileResultBase where T : class, new()
{
    public string FileDownloadName { get; }
    public ICollection<T> Data { get; }

    /// <summary>
    ///
    /// </summary>
    /// <param name="data"></param>
    /// <param name="fileDownloadName"></param>
    public XlsxFileResult(ICollection<T> data, string fileDownloadName = null)
    {
        FileDownloadName = fileDownloadName;
        Data = data;
    }

    public override async Task ExecuteResultAsync(ActionContext context)
    {
        var exporter = new ExcelExporter();
        var bytes = await exporter.ExportAsByteArray(Data);
        var fs = new MemoryStream(bytes);
        await DownloadExcelFileAsync(context, fs, FileDownloadName);
    }
}

/// <summary>
///
/// </summary>
public class XlsxFileResult : XlsxFileResultBase
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="fileDownloadName"></param>
    public XlsxFileResult(Stream stream, string fileDownloadName = null)
    {
        Stream = stream;
        FileDownloadName = fileDownloadName;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="bytes"></param>
    /// <param name="fileDownloadName"></param>

    public XlsxFileResult(byte[] bytes, string fileDownloadName = null)
    {
        Stream = new MemoryStream(bytes);
        FileDownloadName = fileDownloadName;
    }

    public Stream Stream { get; protected set; }
    public string FileDownloadName { get; protected set; }

    public override async Task ExecuteResultAsync(ActionContext context)
    {
        await DownloadExcelFileAsync(context, Stream, FileDownloadName);
    }
}

/// <summary>
/// 基类
/// </summary>
public class XlsxFileResultBase : ActionResult
{
    /// <summary>
    /// 下载Excel文件
    /// </summary>
    /// <param name="context"></param>
    /// <param name="stream"></param>
    /// <param name="downloadFileName"></param>
    /// <returns></returns>
    protected virtual async Task DownloadExcelFileAsync(ActionContext context, Stream stream, string downloadFileName)
    {
        var response = context.HttpContext.Response;
        response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        downloadFileName ??= Guid.NewGuid().ToString("N") + ".xlsx";

        if (string.IsNullOrEmpty(Path.GetExtension(downloadFileName))) downloadFileName += ".xlsx";

        context.HttpContext.Response.Headers.Append("Content-Disposition", new[] { "attachment; filename=" + HttpUtility.UrlEncode(downloadFileName) });
        await stream.CopyToAsync(context.HttpContext.Response.Body);
    }
}