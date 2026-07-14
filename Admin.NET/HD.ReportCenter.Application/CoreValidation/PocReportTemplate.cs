namespace HD.ReportCenter.Application.CoreValidation;

[SugarTable("RcPocReportTemplate", "报表中心模板核心验证表")]
[SugarIndex("uk_RcPocReportTemplate_ReportCode", nameof(ReportCode), OrderByType.Asc, IsUnique = true)]
[SysTable]
public sealed class PocReportTemplate : EntityBase
{
    [SugarColumn(ColumnDescription = "报表编码", Length = 64)]
    public string ReportCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "Univer工作簿快照", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string SnapshotJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "模板绑定", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string BindingJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "修订号")]
    public int Revision { get; set; }
}
