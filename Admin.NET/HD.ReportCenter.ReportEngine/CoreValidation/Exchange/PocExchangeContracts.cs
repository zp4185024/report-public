namespace HD.ReportCenter.ReportEngine.CoreValidation.Exchange;

/// <summary>一条可由数据库调整的 Excel/Univer 转换规则。</summary>
public sealed record PocExchangeRule(
    string RuleCode,
    string Direction,
    string Category,
    string SourcePath,
    string TargetPath,
    string TransformType,
    string? SourceValue,
    string? TargetValue,
    decimal? Factor,
    bool IsRequired,
    int Sort);

/// <summary>同一 Univer 版本、同一映射修订下的有序规则集合。</summary>
public sealed record PocExchangeRuleSet(
    string UniverVersion,
    int MappingVersion,
    IReadOnlyList<PocExchangeRule> Rules);

public static class PocExchangeDirections
{
    public const string ExcelToUniver = "ExcelToUniver";
    public const string UniverToExcel = "UniverToExcel";
}

public static class PocExchangeTransformTypes
{
    public const string Direct = "Direct";
    public const string Boolean01 = "Boolean01";
    public const string Enum = "Enum";
    public const string Scale = "Scale";
    public const string Color = "Color";
    public const string AddPrefix = "AddPrefix";
    public const string RemovePrefix = "RemovePrefix";
    public const string StyleReference = "StyleReference";
    public const string CellType = "CellType";
    public const string Border = "Border";
    public const string ColumnWidth = "ColumnWidth";
}
