// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 翻译表基础输入参数
/// </summary>
public class SysLangTextBaseInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    public virtual long? Id { get; set; }

    /// <summary>
    /// 所属实体名
    /// </summary>
    [Required(ErrorMessage = "所属实体名不能为空")]
    public virtual string EntityName { get; set; }

    /// <summary>
    /// 所属实体ID
    /// </summary>
    [Required(ErrorMessage = "所属实体ID不能为空")]
    public virtual long? EntityId { get; set; }

    /// <summary>
    /// 字段名
    /// </summary>
    [Required(ErrorMessage = "字段名不能为空")]
    public virtual string FieldName { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [Required(ErrorMessage = "语言代码不能为空")]
    public virtual string LangCode { get; set; }

    /// <summary>
    /// 翻译内容
    /// </summary>
    [Required(ErrorMessage = "翻译内容不能为空")]
    public virtual string Content { get; set; }
}

/// <summary>
/// 翻译表分页查询输入参数
/// </summary>
public class PageSysLangTextInput : BasePageInput
{
    /// <summary>
    /// 所属实体名
    /// </summary>
    public string EntityName { get; set; }

    /// <summary>
    /// 所属实体ID
    /// </summary>
    public long? EntityId { get; set; }

    /// <summary>
    /// 字段名
    /// </summary>
    public string FieldName { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    public string LangCode { get; set; }

    /// <summary>
    /// 翻译内容
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// 选中主键列表
    /// </summary>
    public List<long> SelectKeyList { get; set; }
}

/// <summary>
/// 翻译表增加输入参数
/// </summary>
public class AddSysLangTextInput
{
    /// <summary>
    /// 所属实体名
    /// </summary>
    [Required(ErrorMessage = "所属实体名不能为空")]
    [MaxLength(255, ErrorMessage = "所属实体名字符长度不能超过255")]
    public string EntityName { get; set; }

    /// <summary>
    /// 所属实体ID
    /// </summary>
    [Required(ErrorMessage = "所属实体ID不能为空")]
    public long? EntityId { get; set; }

    /// <summary>
    /// 字段名
    /// </summary>
    [Required(ErrorMessage = "字段名不能为空")]
    [MaxLength(255, ErrorMessage = "字段名字符长度不能超过255")]
    public string FieldName { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [Required(ErrorMessage = "语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "语言代码字符长度不能超过255")]
    public string LangCode { get; set; }

    /// <summary>
    /// 翻译内容
    /// </summary>
    [Required(ErrorMessage = "翻译内容不能为空")]
    public string Content { get; set; }
}

/// <summary>
/// 翻译表输入参数
/// </summary>
public class ListSysLangTextInput
{
    /// <summary>
    /// 所属实体名
    /// </summary>
    [Required(ErrorMessage = "所属实体名不能为空")]
    public string EntityName { get; set; }

    /// <summary>
    /// 所属实体ID
    /// </summary>
    [Required(ErrorMessage = "所属实体ID不能为空")]
    public long? EntityId { get; set; }

    /// <summary>
    /// 字段名
    /// </summary>
    [Required(ErrorMessage = "字段名不能为空")]
    public string FieldName { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    public string LangCode { get; set; }
}

/// <summary>
/// 翻译表删除输入参数
/// </summary>
public class DeleteSysLangTextInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    [Required(ErrorMessage = "主键Id不能为空")]
    public long? Id { get; set; }
}

/// <summary>
/// 翻译表更新输入参数
/// </summary>
public class UpdateSysLangTextInput
{
    /// <summary>
    /// 主键Id
    /// </summary>
    [Required(ErrorMessage = "主键Id不能为空")]
    public long? Id { get; set; }

    /// <summary>
    /// 所属实体名
    /// </summary>
    [Required(ErrorMessage = "所属实体名不能为空")]
    [MaxLength(255, ErrorMessage = "所属实体名字符长度不能超过255")]
    public string EntityName { get; set; }

    /// <summary>
    /// 所属实体ID
    /// </summary>
    [Required(ErrorMessage = "所属实体ID不能为空")]
    public long? EntityId { get; set; }

    /// <summary>
    /// 字段名
    /// </summary>
    [Required(ErrorMessage = "字段名不能为空")]
    [MaxLength(255, ErrorMessage = "字段名字符长度不能超过255")]
    public string FieldName { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [Required(ErrorMessage = "语言代码不能为空")]
    [MaxLength(255, ErrorMessage = "语言代码字符长度不能超过255")]
    public string LangCode { get; set; }

    /// <summary>
    /// 翻译内容
    /// </summary>
    [Required(ErrorMessage = "翻译内容不能为空")]
    public string Content { get; set; }
}

/// <summary>
/// 翻译表主键查询输入参数
/// </summary>
public class QueryByIdSysLangTextInput : DeleteSysLangTextInput
{
}

/// <summary>
/// 翻译表数据导入实体
/// </summary>
[ExcelImporter(SheetIndex = 1, IsOnlyErrorRows = true)]
public class ImportSysLangTextInput : BaseImportInput
{
    /// <summary>
    /// 所属实体名
    /// </summary>
    [ImporterHeader(Name = "*所属实体名")]
    [ExporterHeader("*所属实体名", Format = "", Width = 25, IsBold = true)]
    public string EntityName { get; set; }

    /// <summary>
    /// 所属实体ID
    /// </summary>
    [ImporterHeader(Name = "*所属实体ID")]
    [ExporterHeader("*所属实体ID", Format = "", Width = 25, IsBold = true)]
    public long? EntityId { get; set; }

    /// <summary>
    /// 字段名
    /// </summary>
    [ImporterHeader(Name = "*字段名")]
    [ExporterHeader("*字段名", Format = "", Width = 25, IsBold = true)]
    public string FieldName { get; set; }

    /// <summary>
    /// 语言代码
    /// </summary>
    [ImporterHeader(Name = "*语言代码")]
    [ExporterHeader("*语言代码", Format = "", Width = 25, IsBold = true)]
    public string LangCode { get; set; }

    /// <summary>
    /// 翻译内容
    /// </summary>
    [ImporterHeader(Name = "*翻译内容")]
    [ExporterHeader("*翻译内容", Format = "", Width = 25, IsBold = true)]
    public string Content { get; set; }
}

/// <summary>
///
/// </summary>
public class AiTranslateTextInput
{
    /// <summary>
    /// 原文
    /// </summary>
    public string OriginalText { get; set; }

    /// <summary>
    /// 目标语言
    /// </summary>
    public string TargetLang { get; set; }
}