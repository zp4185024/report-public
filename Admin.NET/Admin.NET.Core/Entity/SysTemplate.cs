// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 系统模板表
/// </summary>
[SysTable]
[SugarTable(null, "系统模板表")]
[SugarIndex("index_{table}_C", nameof(Code), OrderByType.Asc, IsUnique = true)]
[SugarIndex("index_{table}_G", nameof(GroupName), OrderByType.Asc)]
public partial class SysTemplate : EntityBaseTenant
{
    /// <summary>
    /// 名称
    /// </summary>
    [MaxLength(128)]
    [SugarColumn(ColumnDescription = "名称", Length = 128)]
    public virtual string Name { get; set; }

    /// <summary>
    /// 分组名称
    /// </summary>
    [SugarColumn(ColumnDescription = "分组名称")]
    public virtual TemplateTypeEnum Type { get; set; }

    /// <summary>
    /// 编码
    /// </summary>
    [MaxLength(128)]
    [SugarColumn(ColumnDescription = "编码", Length = 128)]
    public virtual string Code { get; set; }

    /// <summary>
    /// 分组名称
    /// </summary>
    [MaxLength(32)]
    [SugarColumn(ColumnDescription = "分组名称", Length = 32)]
    public virtual string GroupName { get; set; }

    /// <summary>
    /// 模板内容
    /// </summary>
    [SugarColumn(ColumnDescription = "模板内容", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public virtual string Content { get; set; }

    /// <summary>
    /// 备注
    /// </summary>
    [MaxLength(128)]
    [SugarColumn(ColumnDescription = "备注", Length = 128)]
    public virtual string? Remark { get; set; }

    /// <summary>
    /// 排序
    /// </summary>
    [SugarColumn(ColumnDescription = "排序")]
    public virtual int OrderNo { get; set; } = 100;
}