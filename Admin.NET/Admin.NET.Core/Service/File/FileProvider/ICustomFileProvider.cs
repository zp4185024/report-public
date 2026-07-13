// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 自定义文件提供器接口
/// </summary>
public interface ICustomFileProvider
{
    /// <summary>
    /// 获取文件流
    /// </summary>
    /// <param name="sysFile"></param>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public Task<FileStreamResult> GetFileStreamResultAsync(SysFile sysFile, string fileName);

    /// <summary>
    /// 下载指定文件Base64格式
    /// </summary>
    /// <param name="sysFile"></param>
    /// <returns></returns>
    public Task<string> DownloadFileBase64Async(SysFile sysFile);

    /// <summary>
    /// 删除文件
    /// </summary>
    /// <param name="sysFile"></param>
    /// <returns></returns>
    public Task DeleteFileAsync(SysFile sysFile);

    /// <summary>
    /// 上传文件
    /// </summary>
    /// <param name="file">文件</param>
    /// <param name="sysFile"></param>
    /// <param name="path">文件存储位置</param>
    /// <param name="finalName">文件最终名称</param>
    /// <returns></returns>
    public Task<SysFile> UploadFileAsync(IFormFile file, SysFile sysFile, string path, string finalName);
}