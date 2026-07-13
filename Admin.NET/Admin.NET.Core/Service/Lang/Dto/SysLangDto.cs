// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 语言输出参数
/// </summary>
public class SysLangDto
{
    /// <summary>
    /// 主键Id
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 语言名称
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    public string Code { get; set; }

    /// <summary>
    /// ISO 语言代码
    /// </summary>
    public string IsoCode { get; set; }

    /// <summary>
    /// URL 语言代码
    /// </summary>
    public string UrlCode { get; set; }

    /// <summary>
    /// 书写方向
    /// </summary>
    public DirectionEnum Direction { get; set; }

    /// <summary>
    /// 日期格式
    /// </summary>
    public string DateFormat { get; set; }

    /// <summary>
    /// 时间格式
    /// </summary>
    public string TimeFormat { get; set; }

    /// <summary>
    /// 每周起始日
    /// </summary>
    public WeekEnum WeekStart { get; set; }

    /// <summary>
    /// 分组符号
    /// </summary>
    public string Grouping { get; set; }

    /// <summary>
    /// 小数点符号
    /// </summary>
    public string DecimalPoint { get; set; }

    /// <summary>
    /// 千分位分隔符
    /// </summary>
    public string? ThousandsSep { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Active { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime? CreateTime { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime? UpdateTime { get; set; }

    /// <summary>
    /// 创建者Id
    /// </summary>
    public long? CreateUserId { get; set; }

    /// <summary>
    /// 创建者姓名
    /// </summary>
    public string? CreateUserName { get; set; }

    /// <summary>
    /// 修改者Id
    /// </summary>
    public long? UpdateUserId { get; set; }

    /// <summary>
    /// 修改者姓名
    /// </summary>
    public string? UpdateUserName { get; set; }
}