// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 代码生成字段配置表
/// </summary>
[SugarTable(null, "代码生成字段配置表")]
[SysTable]
public partial class SysCodeGenConfig : EntityBase
{
    /// <summary>
    /// 代码生成主表Id
    /// </summary>
    [SugarColumn(ColumnDescription = "主表Id")]
    public long CodeGenId { get; set; }

    /// <summary>
    /// 数据库字段名
    /// </summary>
    [SugarColumn(ColumnDescription = "字段名称", Length = 128)]
    [Required, MaxLength(128)]
    public virtual string ColumnName { get; set; }

    /// <summary>
    /// 主键
    /// </summary>
    [SugarColumn(ColumnDescription = "主键", Length = 8)]
    [MaxLength(8)]
    public string? ColumnKey { get; set; }

    /// <summary>
    /// 实体属性名
    /// </summary>
    [SugarColumn(ColumnDescription = "属性名称", Length = 128)]
    [Required, MaxLength(128)]
    public virtual string PropertyName { get; set; }

    /// <summary>
    /// 字段数据长度
    /// </summary>
    [SugarColumn(ColumnDescription = "字段数据长度", DefaultValue = "0")]
    public int ColumnLength { get; set; }

    /// <summary>
    /// 字段描述
    /// </summary>
    [SugarColumn(ColumnDescription = "字段描述", Length = 128)]
    [MaxLength(128)]
    public string? ColumnComment { get; set; }

    /// <summary>
    /// 数据库中类型（物理类型）
    /// </summary>
    [SugarColumn(ColumnDescription = "数据库中类型", Length = 64)]
    [MaxLength(64)]
    public string? DataType { get; set; }

    /// <summary>
    /// .NET数据类型
    /// </summary>
    [SugarColumn(ColumnDescription = "NET数据类型", Length = 64)]
    [MaxLength(64)]
    public string? NetType { get; set; }

    /// <summary>
    /// 字段数据默认值
    /// </summary>
    [SugarColumn(ColumnDescription = "默认值")]
    public string? DefaultValue { get; set; }

    /// <summary>
    /// 作用类型（字典）
    /// </summary>
    [SugarColumn(ColumnDescription = "作用类型", Length = 64)]
    [MaxLength(64)]
    public string? EffectType { get; set; }

    /// <summary>
    /// 外键库标识
    /// </summary>
    [SugarColumn(ColumnDescription = "外键库标识", Length = 20)]
    [MaxLength(20)]
    public string? FkConfigId { get; set; }

    /// <summary>
    /// 外键实体名称
    /// </summary>
    [SugarColumn(ColumnDescription = "外键实体名称", Length = 64)]
    [MaxLength(64)]
    public string? FkEntityName { get; set; }

    /// <summary>
    /// 外键表名称
    /// </summary>
    [SugarColumn(ColumnDescription = "外键表名称", Length = 128)]
    [MaxLength(128)]
    public string? FkTableName { get; set; }

    /// <summary>
    /// 外键显示字段
    /// </summary>
    [SugarColumn(ColumnDescription = "外键显示字段", Length = 64)]
    [MaxLength(64)]
    public string? FkDisplayColumns { get; set; }

    /// <summary>
    /// 外键链接字段
    /// </summary>
    [SugarColumn(ColumnDescription = "外键链接字段", Length = 64)]
    [MaxLength(64)]
    public string? FkLinkColumnName { get; set; }

    /// <summary>
    /// 外键显示字段.NET类型
    /// </summary>
    [SugarColumn(ColumnDescription = "外键显示字段.NET类型", Length = 64)]
    [MaxLength(64)]
    public string? FkColumnNetType { get; set; }

    /// <summary>
    /// 父级字段
    /// </summary>
    [SugarColumn(ColumnDescription = "父级字段", Length = 128)]
    [MaxLength(128)]
    public string? PidColumn { get; set; }

    /// <summary>
    /// 字典编码
    /// </summary>
    [SugarColumn(ColumnDescription = "字典编码", Length = 64)]
    [MaxLength(64)]
    public string? DictTypeCode { get; set; }

    /// <summary>
    /// 查询方式
    /// </summary>
    [SugarColumn(ColumnDescription = "查询方式", Length = 16)]
    [MaxLength(16)]
    public string? QueryType { get; set; }

    /// <summary>
    /// 是否是查询条件
    /// </summary>
    [SugarColumn(ColumnDescription = "是否是查询条件", Length = 8)]
    [MaxLength(8)]
    public string? WhetherQuery { get; set; }

    /// <summary>
    /// 列表是否缩进（字典）
    /// </summary>
    [SugarColumn(ColumnDescription = "列表是否缩进", Length = 8)]
    [MaxLength(8)]
    public string? WhetherRetract { get; set; }

    /// <summary>
    /// 是否必填（字典）
    /// </summary>
    [SugarColumn(ColumnDescription = "是否必填", Length = 8)]
    [MaxLength(8)]
    public string? WhetherRequired { get; set; }

    /// <summary>
    /// 是否可排序（字典）
    /// </summary>
    [SugarColumn(ColumnDescription = "是否可排序", Length = 8)]
    [MaxLength(8)]
    public string? WhetherSortable { get; set; }

    /// <summary>
    /// 列表显示
    /// </summary>
    [SugarColumn(ColumnDescription = "列表显示", Length = 8)]
    [MaxLength(8)]
    public string? WhetherTable { get; set; }

    /// <summary>
    /// 增改
    /// </summary>
    [SugarColumn(ColumnDescription = "增改", Length = 8)]
    [MaxLength(8)]
    public string? WhetherAddUpdate { get; set; }

    /// <summary>
    /// 导入
    /// </summary>
    [SugarColumn(ColumnDescription = "导入", Length = 8)]
    [MaxLength(8)]
    public string? WhetherImport { get; set; }

    /// <summary>
    /// 是否通用字段
    /// </summary>
    [SugarColumn(ColumnDescription = "是否通用字段", Length = 8)]
    [MaxLength(8)]
    public string? WhetherCommon { get; set; }

    /// <summary>
    /// 排序
    /// </summary>
    [SugarColumn(ColumnDescription = "排序")]
    public int OrderNo { get; set; } = 100;
}