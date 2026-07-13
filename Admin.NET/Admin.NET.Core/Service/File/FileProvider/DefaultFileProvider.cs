// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

public class DefaultFileProvider : ICustomFileProvider, ITransient
{
    /// <summary>
    /// 构建文件的完整物理路径
    /// </summary>
    /// <param name="sysFile"></param>
    /// <returns></returns>
    private string BuildFullFilePath(SysFile sysFile)
    {
        return Path.Combine(App.WebHostEnvironment.WebRootPath, sysFile.FilePath ?? "", $"{sysFile.Id}{sysFile.Suffix}");
    }

    /// <summary>
    /// 构建目录的完整物理路径
    /// </summary>
    /// <param name="relativePath"></param>
    /// <returns></returns>
    private string BuildFullDirectoryPath(string relativePath)
    {
        return Path.Combine(App.WebHostEnvironment.WebRootPath, relativePath);
    }

    /// <summary>
    /// 确保目录存在
    /// </summary>
    /// <param name="directoryPath"></param>
    private void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);
    }

    public Task DeleteFileAsync(SysFile sysFile)
    {
        var filePath = BuildFullFilePath(sysFile);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }

    public async Task<string> DownloadFileBase64Async(SysFile sysFile)
    {
        var realFile = BuildFullFilePath(sysFile);
        if (!File.Exists(realFile))
        {
            Log.Error($"DownloadFileBase64:文件[{realFile}]不存在");
            throw Oops.Oh($"文件[{sysFile.FilePath}]不存在");
        }

        byte[] fileBytes = await File.ReadAllBytesAsync(realFile);
        return Convert.ToBase64String(fileBytes);
    }

    public Task<FileStreamResult> GetFileStreamResultAsync(SysFile sysFile, string fileName)
    {
        var fullPath = BuildFullFilePath(sysFile);
        return Task.FromResult(new FileStreamResult(new FileStream(fullPath, FileMode.Open), "application/octet-stream")
        {
            FileDownloadName = fileName + sysFile.Suffix
        });
    }

    public async Task<SysFile> UploadFileAsync(IFormFile file, SysFile newFile, string path, string finalName)
    {
        newFile.Provider = ""; // 本地存储 Provider 显示为空

        var directoryPath = BuildFullDirectoryPath(path);
        EnsureDirectoryExists(directoryPath);

        var realFile = Path.Combine(directoryPath, finalName);
        await using var stream = File.Create(realFile);
        await file.CopyToAsync(stream);

        newFile.Url = $"{newFile.FilePath}/{newFile.Id + newFile.Suffix}";
        return newFile;
    }
}