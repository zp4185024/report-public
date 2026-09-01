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
    /// <summary>查询或读取 ICustomFileProvider 所需的 Get File Stream Result Async 数据，并按调用约定返回结果。</summary>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="fileName">需要读取、校验、比较或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<FileStreamResult> GetFileStreamResultAsync(SysFile sysFile, string fileName);

    /// <summary>
    /// 下载指定文件Base64格式
    /// </summary>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<string> DownloadFileBase64Async(SysFile sysFile);

    /// <summary>移除 ICustomFileProvider 中与 Delete File Async 对应的数据，并清理关联状态。</summary>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task DeleteFileAsync(SysFile sysFile);

    /// <summary>接收并校验 ICustomFileProvider 的 Upload File Async 输入，随后写入目标状态。</summary>
    /// <param name="file">文件</param>
    /// <param name="sysFile">需要读取、上传、导出或解析的文件内容。</param>
    /// <param name="path">文件存储位置</param>
    /// <param name="finalName">文件最终名称</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<SysFile> UploadFileAsync(IFormFile file, SysFile sysFile, string path, string finalName);
}