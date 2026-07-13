// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 表唯一配置项
/// </summary>
public class TableUniqueConfigItem
{
    /// <summary>
    /// 字段列表
    /// </summary>
    public List<string> Columns { get; set; }

    /// <summary>
    /// 描述信息
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// 格式化查询条件
    /// </summary>
    /// <param name="separator">分隔符</param>
    /// <param name="format">模板字符串</param>
    /// <returns></returns>
    public string Format(string separator, string format) => string.Join(separator, Columns.Select(name => string.Format(format, name)));
}