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

/// <summary>POC 导入上限，阻止异常工作簿在内存中无限展开。</summary>
public sealed record PocXlsxExchangeLimits(
    long MaxFileBytes,
    int MaxWorksheets,
    int MaxUsedCells);

/// <summary>非阻断兼容性提示；RuleCode 可以直接定位数据库中的映射记录。</summary>
public sealed record PocExchangeDiagnostic(
    string RuleCode,
    string Message,
    string? SheetName = null,
    string? CellAddress = null);

/// <summary>XLSX 导入产生的 Univer 快照和可观测信息。</summary>
public sealed record PocXlsxImportResult(
    Newtonsoft.Json.Linq.JObject Snapshot,
    int UsedCellCount,
    IReadOnlyList<PocExchangeDiagnostic> Warnings);

/// <summary>映射缺失、快照损坏或文件内容不受支持时使用的稳定领域异常。</summary>
public sealed class PocExchangeConversionException : Exception
{
    /// <summary>初始化 PocExchangeConversionException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    public PocExchangeConversionException(string code, string message, string? ruleCode = null)
        : base(message)
    {
        Code = code;
        RuleCode = ruleCode;
    }

    public string Code { get; }

    public string? RuleCode { get; }
}

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
