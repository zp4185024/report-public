// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

[SugarTable(null, "语言配置")]
[SysTable]
[SugarIndex("index_{table}_N", nameof(Name), OrderByType.Asc)]
[SugarIndex("index_{table}_C", nameof(Code), OrderByType.Asc)]
public class SysLang : EntityBase
{
    /// <summary>
    /// 语言名称
    /// </summary>
    [SugarColumn(ColumnDescription = "语言名称")]
    public string Name { get; set; }

    /// <summary>
    /// 语言代码（如 zh-CN）
    /// </summary>
    [SugarColumn(ColumnDescription = "语言代码")]
    public string Code { get; set; }

    /// <summary>
    /// ISO 语言代码
    /// </summary>
    [SugarColumn(ColumnDescription = "ISO 语言代码")]
    public string IsoCode { get; set; }

    /// <summary>
    /// URL 语言代码
    /// </summary>
    [SugarColumn(ColumnDescription = "URL 语言代码")]
    public string UrlCode { get; set; }

    /// <summary>
    /// 书写方向（1=从左到右，2=从右到左）
    /// </summary>
    [SugarColumn(ColumnDescription = "书写方向", DefaultValue = "1")]
    public DirectionEnum Direction { get; set; } = DirectionEnum.Ltr;

    /// <summary>
    /// 日期格式（如 YYYY-MM-DD）
    /// </summary>
    [SugarColumn(ColumnDescription = "日期格式")]
    public string DateFormat { get; set; }

    /// <summary>
    /// 时间格式（如 HH:MM:SS）
    /// </summary>
    [SugarColumn(ColumnDescription = "时间格式")]
    public string TimeFormat { get; set; }

    /// <summary>
    /// 每周起始日（如 0=星期日，1=星期一）
    /// </summary>
    [SugarColumn(ColumnDescription = "每周起始日", DefaultValue = "7")]
    public WeekEnum WeekStart { get; set; } = WeekEnum.Sunday;

    /// <summary>
    /// 分组符号（如 ,）
    /// </summary>
    [SugarColumn(ColumnDescription = "分组符号")]
    public string Grouping { get; set; }

    /// <summary>
    /// 小数点符号
    /// </summary>
    [SugarColumn(ColumnDescription = "小数点符号")]
    public string DecimalPoint { get; set; }

    /// <summary>
    /// 千分位分隔符
    /// </summary>
    [SugarColumn(ColumnDescription = "千分位分隔符")]
    public string? ThousandsSep { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [SugarColumn(ColumnDescription = "是否启用")]
    public bool Active { get; set; }
}