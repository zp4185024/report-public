namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 报表模板的数据库实体。
/// 快照与绑定分列保存：快照用于完整恢复 Univer，绑定用于快速审计和执行前一致性校验。
/// </summary>
[SugarTable("RcReportTemplate", "报表中心模板表")]
[SugarIndex("uk_RcReportTemplate_ReportCode", nameof(ReportCode), OrderByType.Asc, IsUnique = true)]
[SysTable]
public sealed class PocReportTemplate : EntityBase
{
    /// <summary>报表唯一编码，也是模板记录的业务唯一键。</summary>
    [SugarColumn(ColumnDescription = "报表编码", Length = 64)]
    public string ReportCode { get; set; } = string.Empty;

    /// <summary>未经裁剪的 Univer IWorkbookData JSON，用于无损恢复样式、公式、合并区域和工作表设置。</summary>
    [SugarColumn(ColumnDescription = "Univer工作簿快照", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>由后台重新扫描快照得到的规范化绑定 JSON，不直接信任浏览器提交值。</summary>
    [SugarColumn(ColumnDescription = "模板绑定", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string BindingJson { get; set; } = string.Empty;

    /// <summary>乐观并发修订号。每次成功保存加一，防止两个编辑页面静默互相覆盖。</summary>
    [SugarColumn(ColumnDescription = "修订号")]
    public int Revision { get; set; }
}
