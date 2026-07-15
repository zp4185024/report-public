namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// Excel 与 Univer 的单项映射规则。规则逐条落库，便于在不重新编译的情况下修改、停用和排查。
/// </summary>
[SugarTable("RcPocExchangeMapping", "报表中心Excel与Univer映射验证表")]
[SugarIndex(
    "uk_RcPocExchangeMapping_Version_Rule",
    nameof(UniverVersion), OrderByType.Asc,
    nameof(MappingVersion), OrderByType.Asc,
    nameof(RuleCode), OrderByType.Asc,
    IsUnique = true)]
[SysTable]
public sealed class PocExchangeMapping : EntityBase
{
    [SugarColumn(ColumnDescription = "Univer版本", Length = 32)]
    public string UniverVersion { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "映射版本")]
    public int MappingVersion { get; set; }

    [SugarColumn(ColumnDescription = "规则编码", Length = 96)]
    public string RuleCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "转换方向", Length = 24)]
    public string Direction { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "规则分类", Length = 24)]
    public string Category { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "来源路径", Length = 128)]
    public string SourcePath { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "目标路径", Length = 128)]
    public string TargetPath { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "转换类型", Length = 32)]
    public string TransformType { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "来源枚举值", Length = 128, IsNullable = true)]
    public string? SourceValue { get; set; }

    [SugarColumn(ColumnDescription = "目标枚举值", Length = 128, IsNullable = true)]
    public string? TargetValue { get; set; }

    [SugarColumn(ColumnDescription = "数值换算系数", DecimalDigits = 8, IsNullable = true)]
    public decimal? Factor { get; set; }

    [SugarColumn(ColumnDescription = "是否必要")]
    public bool IsRequired { get; set; }

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; }

    [SugarColumn(ColumnDescription = "执行顺序")]
    public int Sort { get; set; }

    [SugarColumn(ColumnDescription = "调试说明", Length = 256, IsNullable = true)]
    public string? Remark { get; set; }
}
