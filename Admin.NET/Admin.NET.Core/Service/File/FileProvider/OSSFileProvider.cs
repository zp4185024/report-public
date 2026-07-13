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

    public OSSFileProvider(IOptions<OSSProviderOptions> oSSProviderOptions, IOSSServiceFactory ossServiceFactory)
    {
        _OSSProviderOptions = oSSProviderOptions.Value;
        if (_OSSProviderOptions.Enabled)
            _OSSService = ossServiceFactory.Create(Enum.GetName(_OSSProviderOptions.Provider));
    }

    public async Task DeleteFileAsync(SysFile file)
    {
        await _OSSService.RemoveObjectAsync(file.BucketName, string.Concat(file.FilePath, "/", $"{file.Id}{file.Suffix}"));
    }

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

    public async Task<FileStreamResult> GetFileStreamResultAsync(SysFile file, string fileName)
    {
        var filePath = Path.Combine(file.FilePath ?? "", file.Id + file.Suffix);
        var httpRemoteService = App.GetRequiredService<IHttpRemoteService>();
        var stream = await httpRemoteService.GetAsStreamAsync(await _OSSService.PresignedGetObjectAsync(file.BucketName, filePath, 5));
        return new FileStreamResult(stream, "application/octet-stream") { FileDownloadName = fileName + file.Suffix };
    }

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