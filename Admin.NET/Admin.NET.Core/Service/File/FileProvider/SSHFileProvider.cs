// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

public class SSHFileProvider : ICustomFileProvider, ITransient
{
    /// <summary>
    /// 创建SSH连接助手
    /// </summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private SSHHelper CreateSSHHelper()
    {
        return new SSHHelper(
            App.Configuration["SSHProvider:Host"],
            App.Configuration["SSHProvider:Port"].ToInt(),
            App.Configuration["SSHProvider:Username"],
            App.Configuration["SSHProvider:Password"]);
    }

    /// <summary>
    /// 构建文件完整路径
    /// </summary>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private string BuildFilePath(SysFile sysFile)
    {
        return string.Concat(sysFile.FilePath, "/", sysFile.Id + sysFile.Suffix);
    }

    /// <summary>移除 SSHFileProvider 中与 Delete File Async 对应的数据，并清理关联状态。</summary>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task DeleteFileAsync(SysFile sysFile)
    {
        var fullPath = BuildFilePath(sysFile);
        using var helper = CreateSSHHelper();
        helper.DeleteFile(fullPath);
        return Task.CompletedTask;
    }

    /// <summary>整理 SSHFileProvider 的 Download File Base64 Async 数据，并生成可供调用方使用的输出。</summary>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<string> DownloadFileBase64Async(SysFile sysFile)
    {
        using var helper = CreateSSHHelper();
        return Task.FromResult(Convert.ToBase64String(helper.ReadAllBytes(sysFile.FilePath)));
    }

    /// <summary>查询或读取 SSHFileProvider 所需的 Get File Stream Result Async 数据，并按调用约定返回结果。</summary>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="fileName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<FileStreamResult> GetFileStreamResultAsync(SysFile sysFile, string fileName)
    {
        var filePath = BuildFilePath(sysFile);
        using var helper = CreateSSHHelper();
        return Task.FromResult(new FileStreamResult(helper.OpenRead(filePath), "application/octet-stream")
        {
            FileDownloadName = fileName + sysFile.Suffix
        });
    }

    /// <summary>接收并校验 SSHFileProvider 的 Upload File Async 输入，随后写入目标状态。</summary>
    /// <param name="file">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="path">目标资源、页面或接口使用的地址。</param>
    /// <param name="finalName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<SysFile> UploadFileAsync(IFormFile file, SysFile sysFile, string path, string finalName)
    {
        var fullPath = string.Concat(path.StartsWith('/') ? path : "/" + path, "/", finalName);
        using var helper = CreateSSHHelper();
        helper.UploadFile(file.OpenReadStream(), fullPath);
        return Task.FromResult(sysFile);
    }
}