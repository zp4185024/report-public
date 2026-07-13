// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 语言基础输入参数
/// </summary>
public class SysLangBaseInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    public virtual long? Id { get; set; }

    /// <summary>
    /// 语言名称
    /// </summary>
    [Required(ErrorMessage = "语言名称不能为空")]
    public virtual string Name { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [Required(ErrorMessage = "语言代码不能为空")]
    public virtual string Code { get; set; }

    /// <summary>
    /// ISO 语言代码
    /// </summary>
    [Required(ErrorMessage = "ISO 语言代码不能为空")]
    public virtual string IsoCode { get; set; }

    /// <summary>
    /// URL 语言代码
    /// </summary>
    [Required(ErrorMessage = "URL 语言代码不能为空")]
    public virtual string UrlCode { get; set; }

    /// <summary>
    /// 书写方向
    /// </summary>
    [Required(ErrorMessage = "书写方向不能为空")]
    public virtual DirectionEnum Direction { get; set; }

    /// <summary>
    /// 日期格式
    /// </summary>
    [Required(ErrorMessage = "日期格式不能为空")]
    public virtual string DateFormat { get; set; }

    /// <summary>
    /// 时间格式
    /// </summary>
    [Required(ErrorMessage = "时间格式不能为空")]
    public virtual string TimeFormat { get; set; }

    /// <summary>
    /// 每周起始日
    /// </summary>
    [Required(ErrorMessage = "每周起始日不能为空")]
    public virtual WeekEnum? WeekStart { get; set; }

    /// <summary>
    /// 分组符号
    /// </summary>
    [Required(ErrorMessage = "分组符号不能为空")]
    public virtual string Grouping { get; set; }

    /// <summary>
    /// 小数点符号
    /// </summary>
    [Required(ErrorMessage = "小数点符号不能为空")]
    public virtual string DecimalPoint { get; set; }

    /// <summary>
    /// 千分位分隔符
    /// </summary>
    public virtual string? ThousandsSep { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [Required(ErrorMessage = "是否启用不能为空")]
    public virtual bool? Active { get; set; }
}

/// <summary>
/// 多语言分页查询输入参数
/// </summary>
public class PageSysLangInput : BasePageInput
{
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
    /// 是否启用
    /// </summary>
    public bool? Active { get; set; }

    /// <summary>
    /// 选中主键列表
    /// </summary>
    public List<long> SelectKeyList { get; set; }
}

/// <summary>
/// 多语言增加输入参数
/// </summary>
public class AddSysLangInput
{
    /// <summary>
    /// 语言名称
    /// </summary>
    [Required(ErrorMessage = "语言名称不能为空")]
    [MaxLength(255, ErrorMessage = "语言名称字符长度不能超过255")]
    public string Name { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [Required(ErrorMessage = "语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "语言代码字符长度不能超过255")]
    public string Code { get; set; }

    /// <summary>
    /// ISO 语言代码
    /// </summary>
    [Required(ErrorMessage = "ISO 语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "ISO 语言代码字符长度不能超过255")]
    public string IsoCode { get; set; }

    /// <summary>
    /// URL 语言代码
    /// </summary>
    [Required(ErrorMessage = "URL 语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "URL 语言代码字符长度不能超过255")]
    public string UrlCode { get; set; }

    /// <summary>
    /// 书写方向
    /// </summary>
    [Required(ErrorMessage = "书写方向不能为空")]
    public DirectionEnum Direction { get; set; }

    /// <summary>
    /// 日期格式
    /// </summary>
    [Required(ErrorMessage = "日期格式不能为空")]
    [MaxLength(255, ErrorMessage = "日期格式字符长度不能超过255")]
    public string DateFormat { get; set; }

    /// <summary>
    /// 时间格式
    /// </summary>
    [Required(ErrorMessage = "时间格式不能为空")]
    [MaxLength(255, ErrorMessage = "时间格式字符长度不能超过255")]
    public string TimeFormat { get; set; }

    /// <summary>
    /// 每周起始日
    /// </summary>
    [Required(ErrorMessage = "每周起始日不能为空")]
    public WeekEnum? WeekStart { get; set; }

    /// <summary>
    /// 分组符号
    /// </summary>
    [Required(ErrorMessage = "分组符号不能为空")]
    [MaxLength(255, ErrorMessage = "分组符号字符长度不能超过255")]
    public string Grouping { get; set; }

    /// <summary>
    /// 小数点符号
    /// </summary>
    [Required(ErrorMessage = "小数点符号不能为空")]
    [MaxLength(255, ErrorMessage = "小数点符号字符长度不能超过255")]
    public string DecimalPoint { get; set; }

    /// <summary>
    /// 千分位分隔符
    /// </summary>
    [MaxLength(255, ErrorMessage = "千分位分隔符字符长度不能超过255")]
    public string? ThousandsSep { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [Required(ErrorMessage = "是否启用不能为空")]
    public bool? Active { get; set; }
}

/// <summary>
/// 多语言删除输入参数
/// </summary>
public class DeleteSysLangInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    [Required(ErrorMessage = "主键Id不能为空")]
    public long? Id { get; set; }
}

/// <summary>
/// 多语言更新输入参数
/// </summary>
public class UpdateSysLangInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    [Required(ErrorMessage = "主键Id不能为空")]
    public long? Id { get; set; }

    /// <summary>
    /// 语言名称
    /// </summary>
    [Required(ErrorMessage = "语言名称不能为空")]
    [MaxLength(255, ErrorMessage = "语言名称字符长度不能超过255")]
    public string Name { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [Required(ErrorMessage = "语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "语言代码字符长度不能超过255")]
    public string Code { get; set; }

    /// <summary>
    /// ISO 语言代码
    /// </summary>
    [Required(ErrorMessage = "ISO 语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "ISO 语言代码字符长度不能超过255")]
    public string IsoCode { get; set; }

    /// <summary>
    /// URL 语言代码
    /// </summary>
    [Required(ErrorMessage = "URL 语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "URL 语言代码字符长度不能超过255")]
    public string UrlCode { get; set; }

    /// <summary>
    /// 书写方向
    /// </summary>
    [Required(ErrorMessage = "书写方向不能为空")]
    public DirectionEnum Direction { get; set; }

    /// <summary>
    /// 日期格式
    /// </summary>
    [Required(ErrorMessage = "日期格式不能为空")]
    [MaxLength(255, ErrorMessage = "日期格式字符长度不能超过255")]
    public string DateFormat { get; set; }

    /// <summary>
    /// 时间格式
    /// </summary>
    [Required(ErrorMessage = "时间格式不能为空")]
    [MaxLength(255, ErrorMessage = "时间格式字符长度不能超过255")]
    public string TimeFormat { get; set; }

    /// <summary>
    /// 每周起始日
    /// </summary>
    [Required(ErrorMessage = "每周起始日不能为空")]
    public WeekEnum? WeekStart { get; set; }

    /// <summary>
    /// 分组符号
    /// </summary>
    [Required(ErrorMessage = "分组符号不能为空")]
    [MaxLength(255, ErrorMessage = "分组符号字符长度不能超过255")]
    public string Grouping { get; set; }

    /// <summary>
    /// 小数点符号
    /// </summary>
    [Required(ErrorMessage = "小数点符号不能为空")]
    [MaxLength(255, ErrorMessage = "小数点符号字符长度不能超过255")]
    public string DecimalPoint { get; set; }

    /// <summary>
    /// 千分位分隔符
    /// </summary>
    [MaxLength(255, ErrorMessage = "千分位分隔符字符长度不能超过255")]
    public string? ThousandsSep { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [Required(ErrorMessage = "是否启用不能为空")]
    public bool? Active { get; set; }
}

/// <summary>
/// 多语言主键查询输入参数
/// </summary>
public class QueryByIdSysLangInput : DeleteSysLangInput
{
}

/// <summary>
/// 多语言数据导入实体
/// </summary>
[ExcelImporter(SheetIndex = 1, IsOnlyErrorRows = true)]
public class ImportSysLangInput : BaseImportInput
{
    /// <summary>
    /// 语言名称
    /// </summary>
    [ImporterHeader(Name = "*语言名称")]
    [ExporterHeader("*语言名称", Format = "", Width = 25, IsBold = true)]
    public string Name { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [ImporterHeader(Name = "*语言代码")]
    [ExporterHeader("*语言代码", Format = "", Width = 25, IsBold = true)]
    public string Code { get; set; }

    /// <summary>
    /// ISO 语言代码
    /// </summary>
    [ImporterHeader(Name = "*ISO 语言代码")]
    [ExporterHeader("*ISO 语言代码", Format = "", Width = 25, IsBold = true)]
    public string IsoCode { get; set; }

    /// <summary>
    /// URL 语言代码
    /// </summary>
    [ImporterHeader(Name = "*URL 语言代码")]
    [ExporterHeader("*URL 语言代码", Format = "", Width = 25, IsBold = true)]
    public string UrlCode { get; set; }

    /// <summary>
    /// 书写方向
    /// </summary>
    [ImporterHeader(Name = "*书写方向")]
    [ExporterHeader("*书写方向", Format = "", Width = 25, IsBold = true)]
    public DirectionEnum Direction { get; set; }

    /// <summary>
    /// 日期格式
    /// </summary>
    [ImporterHeader(Name = "*日期格式")]
    [ExporterHeader("*日期格式", Format = "", Width = 25, IsBold = true)]
    public string DateFormat { get; set; }

    /// <summary>
    /// 时间格式
    /// </summary>
    [ImporterHeader(Name = "*时间格式")]
    [ExporterHeader("*时间格式", Format = "", Width = 25, IsBold = true)]
    public string TimeFormat { get; set; }

    /// <summary>
    /// 每周起始日
    /// </summary>
    [ImporterHeader(Name = "*每周起始日")]
    [ExporterHeader("*每周起始日", Format = "", Width = 25, IsBold = true)]
    public WeekEnum? WeekStart { get; set; }

    /// <summary>
    /// 分组符号
    /// </summary>
    [ImporterHeader(Name = "*分组符号")]
    [ExporterHeader("*分组符号", Format = "", Width = 25, IsBold = true)]
    public string Grouping { get; set; }

    /// <summary>
    /// 小数点符号
    /// </summary>
    [ImporterHeader(Name = "*小数点符号")]
    [ExporterHeader("*小数点符号", Format = "", Width = 25, IsBold = true)]
    public string DecimalPoint { get; set; }

    /// <summary>
    /// 千分位分隔符
    /// </summary>
    [ImporterHeader(Name = "千分位分隔符")]
    [ExporterHeader("千分位分隔符", Format = "", Width = 25, IsBold = true)]
    public string? ThousandsSep { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [ImporterHeader(Name = "*是否启用")]
    [ExporterHeader("*是否启用", Format = "", Width = 25, IsBold = true)]
    public bool? Active { get; set; }
}