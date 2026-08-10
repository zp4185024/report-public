namespace HD.ReportCenter.Application.CoreValidation;

[SugarTable("RcReport", "报表定义表")]
[SugarIndex("uk_RcReport_Code", nameof(Code), OrderByType.Asc, IsUnique = true)]
[SysTable]
public sealed class PocReport : EntityBase
{
    [SugarColumn(ColumnDescription = "报表编码", Length = 64)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "报表名称", Length = 128)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "工作表名称", Length = 128)]
    public string SheetName { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "预览设置", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string PreviewSettingsJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "导出设置", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string ExportSettingsJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "配置修订号")]
    public int Revision { get; set; }

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; } = true;
}

[SugarTable("RcDataSource", "报表数据源表")]
[SugarIndex("uk_RcDataSource_Code", nameof(Code), OrderByType.Asc, IsUnique = true)]
[SysTable]
public sealed class PocDataSource : EntityBase
{
    [SugarColumn(ColumnDescription = "数据源编码", Length = 64)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "数据源名称", Length = 128)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "数据库类型", Length = 32)]
    public string DbType { get; set; } = "SqlServer";

    [SugarColumn(ColumnDescription = "连接字符串", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string ConnectionString { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "默认查询超时秒数")]
    public int DefaultTimeoutSeconds { get; set; } = 30;

    [SugarColumn(ColumnDescription = "默认最大返回行数")]
    public int DefaultMaxRows { get; set; } = 20_000;

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; } = true;
}

[SugarTable("RcDataset", "报表数据集表")]
[SugarIndex(
    "uk_RcDataset_ReportCode_Code",
    nameof(ReportCode),
    OrderByType.Asc,
    nameof(Code),
    OrderByType.Asc,
    IsUnique = true)]
[SysTable]
public sealed class PocReportDataset : EntityBase
{
    [SugarColumn(ColumnDescription = "报表编码", Length = 64)]
    public string ReportCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "数据源编码", Length = 64)]
    public string DataSourceCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "数据集编码", Length = 64)]
    public string Code { get; set; } = "detail";

    [SugarColumn(ColumnDescription = "数据集名称", Length = 128)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "只读参数化SQL", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string QueryText { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "字段定义", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string FieldSchemaJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "查询超时秒数")]
    public int TimeoutSeconds { get; set; } = 30;

    [SugarColumn(ColumnDescription = "最大返回行数")]
    public int MaxRows { get; set; } = 20_000;

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; } = true;
}

[SugarTable("RcReportParameter", "报表参数表")]
[SugarIndex(
    "uk_RcReportParameter_ReportCode_Code",
    nameof(ReportCode),
    OrderByType.Asc,
    nameof(Code),
    OrderByType.Asc,
    IsUnique = true)]
[SysTable]
public sealed class PocReportParameter : EntityBase
{
    [SugarColumn(ColumnDescription = "报表编码", Length = 64)]
    public string ReportCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "参数编码", Length = 64)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "显示名称", Length = 128)]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "数据类型", Length = 32)]
    public string DataType { get; set; } = "string";

    [SugarColumn(ColumnDescription = "控件类型", Length = 32)]
    public string ControlType { get; set; } = "text";

    [SugarColumn(ColumnDescription = "来源类型", Length = 16)]
    public string SourceType { get; set; } = "Form";

    [SugarColumn(ColumnDescription = "URL参数名", Length = 64, IsNullable = true)]
    public string? UrlKey { get; set; }

    [SugarColumn(ColumnDescription = "默认表达式", Length = 256, IsNullable = true)]
    public string? DefaultExpression { get; set; }

    [SugarColumn(ColumnDescription = "固定值", Length = 1024, IsNullable = true)]
    public string? FixedValue { get; set; }

    [SugarColumn(ColumnDescription = "是否显示")]
    public bool IsVisible { get; set; } = true;

    [SugarColumn(ColumnDescription = "是否只读")]
    public bool IsReadOnly { get; set; }

    [SugarColumn(ColumnDescription = "是否必填")]
    public bool IsRequired { get; set; }

    [SugarColumn(ColumnDescription = "是否允许URL预填")]
    public bool AllowUrlPrefill { get; set; }

    [SugarColumn(ColumnDescription = "显示顺序")]
    public int DisplayOrder { get; set; }
}
