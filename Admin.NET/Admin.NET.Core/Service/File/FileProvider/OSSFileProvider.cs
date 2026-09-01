// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using OnceMi.AspNetCore.OSS;

namespace Admin.NET.Core.Service;

public class OSSFileProvider : ICustomFileProvider, ITransient
{
    private readonly IOSSService _OSSService;
    private readonly OSSProviderOptions _OSSProviderOptions;

    /// <summary>初始化 OSSFileProvider 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="oSSProviderOptions">控制本次处理行为的配置选项。</param>
    /// <param name="ossServiceFactory">当前方法执行时使用的 ossServiceFactory 输入值。</param>
    public OSSFileProvider(IOptions<OSSProviderOptions> oSSProviderOptions, IOSSServiceFactory ossServiceFactory)
    {
        _OSSProviderOptions = oSSProviderOptions.Value;
        if (_OSSProviderOptions.Enabled)
            _OSSService = ossServiceFactory.Create(Enum.GetName(_OSSProviderOptions.Provider));
    }

    /// <summary>移除 OSSFileProvider 中与 Delete File Async 对应的数据，并清理关联状态。</summary>
    /// <param name="file">需要读取、上传、导出或解析的文件内容。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task DeleteFileAsync(SysFile file)
    {
        await _OSSService.RemoveObjectAsync(file.BucketName, string.Concat(file.FilePath, "/", $"{file.Id}{file.Suffix}"));
    }

    /// <summary>整理 OSSFileProvider 的 Download File Base64 Async 数据，并生成可供调用方使用的输出。</summary>
    /// <param name="file">需要读取、上传、导出或解析的文件内容。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<string> DownloadFileBase64Async(SysFile file)
    {
        using var httpClient = new HttpClient();
        HttpResponseMessage response = await httpClient.GetAsync(file.Url);
        if (response.IsSuccessStatusCode)
        {
            // 读取文件内容并将其转换为 Base64 字符串
            byte[] fileBytes = await response.Content.ReadAsByteArrayAsync();
            return Convert.ToBase64String(fileBytes);
        }
        throw new HttpRequestException($"Request failed with status code: {response.StatusCode}");
    }

    /// <summary>查询或读取 OSSFileProvider 所需的 Get File Stream Result Async 数据，并按调用约定返回结果。</summary>
    /// <param name="file">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="fileName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<FileStreamResult> GetFileStreamResultAsync(SysFile file, string fileName)
    {
        var filePath = Path.Combine(file.FilePath ?? "", file.Id + file.Suffix);
        var httpRemoteService = App.GetRequiredService<IHttpRemoteService>();
        var stream = await httpRemoteService.GetAsStreamAsync(await _OSSService.PresignedGetObjectAsync(file.BucketName, filePath, 5));
        return new FileStreamResult(stream, "application/octet-stream") { FileDownloadName = fileName + file.Suffix };
    }

    /// <summary>接收并校验 OSSFileProvider 的 Upload File Async 输入，随后写入目标状态。</summary>
    /// <param name="file">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="path">目标资源、页面或接口使用的地址。</param>
    /// <param name="finalName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<SysFile> UploadFileAsync(IFormFile file, SysFile sysFile, string path, string finalName)
    {
        sysFile.Provider = Enum.GetName(_OSSProviderOptions.Provider);
        var filePath = string.Concat(path, "/", finalName);
        await _OSSService.PutObjectAsync(sysFile.BucketName, filePath, file.OpenReadStream());
        //  http://<你的bucket名字>.oss.aliyuncs.com/<你的object名字>
        //  生成外链地址 方便前端预览
        switch (_OSSProviderOptions.Provider)
        {
            case OSSProvider.Aliyun:
                sysFile.Url = $"{(_OSSProviderOptions.IsEnableHttps ? "https" : "http")}://{sysFile.BucketName}.{_OSSProviderOptions.Endpoint}/{filePath}";
                break;

            case OSSProvider.QCloud:
                var protocol = _OSSProviderOptions.IsEnableHttps ? "https" : "http";
                sysFile.Url = !string.IsNullOrWhiteSpace(_OSSProviderOptions.CustomHost)
                    ? $"{protocol}://{_OSSProviderOptions.CustomHost}/{filePath}"
                    : $"{protocol}://{sysFile.BucketName}-{_OSSProviderOptions.Endpoint}.cos.{_OSSProviderOptions.Region}.myqcloud.com/{filePath}";
                break;

            case OSSProvider.Minio:
                // 获取Minio文件的下载或者预览地址
                // newFile.Url = await GetMinioPreviewFileUrl(newFile.BucketName, filePath);// 这种方法生成的Url是有7天有效期的，不能这样使用
                // 需要在MinIO中的Buckets开通对 Anonymous 的readonly权限
                var customHost = _OSSProviderOptions.CustomHost;
                if (string.IsNullOrWhiteSpace(customHost))
                    customHost = _OSSProviderOptions.Endpoint;
                sysFile.Url = $"{(_OSSProviderOptions.IsEnableHttps ? "https" : "http")}://{customHost}/{sysFile.BucketName}/{filePath}";
                break;
        }
        return sysFile;
    }
}