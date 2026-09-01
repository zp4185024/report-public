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

    /// <summary>初始化 XlsxFileResult 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="data">符合当前接口契约的请求输入数据。</param>
    /// <param name="fileDownloadName">需要读取、校验或转换的业务值。</param>
    public XlsxFileResult(ICollection<T> data, string fileDownloadName = null)
    {
        FileDownloadName = fileDownloadName;
        Data = data;
    }

    /// <summary>执行 XlsxFileResult 的 Execute Result Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
    /// <summary>初始化 XlsxFileResult 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="stream">stream 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="fileDownloadName">需要读取、校验或转换的业务值。</param>
    public XlsxFileResult(Stream stream, string fileDownloadName = null)
    {
        Stream = stream;
        FileDownloadName = fileDownloadName;
    }

    /// <summary>初始化 XlsxFileResult 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="bytes">bytes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="fileDownloadName">需要读取、校验或转换的业务值。</param>

    public XlsxFileResult(byte[] bytes, string fileDownloadName = null)
    {
        Stream = new MemoryStream(bytes);
        FileDownloadName = fileDownloadName;
    }

    public Stream Stream { get; protected set; }
    public string FileDownloadName { get; protected set; }

    /// <summary>执行 XlsxFileResult 的 Execute Result Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="stream">stream 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="downloadFileName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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