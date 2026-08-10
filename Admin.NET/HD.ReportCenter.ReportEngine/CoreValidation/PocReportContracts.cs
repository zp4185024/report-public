namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>
/// 报表预览的数据装载方式。Full 表示一次返回全部数据，Paged 表示按模板配置分页。
/// 当前 POC 固定使用 Paged，用于验证大数据量下浏览器只渲染当前页的技术路线。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
[Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
public enum PocPreviewMode
{
    Full,
    Paged
}

/// <summary>
/// 模板设计人员为当前报表选择的预览策略。
/// PageSize 控制一次查询和一次前端渲染的行数；ShowExactTotal 用于决定是否执行可能昂贵的总数查询。
/// </summary>
public sealed record PocPreviewSettings(
    PocPreviewMode Mode,
    int PageSize,
    bool ShowExactTotal);

/// <summary>模板定义的数据导出方式。Normal 直接响应文件，Large 通过后台任务生成。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
[Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
public enum PocDataExportMode
{
    Normal,
    Large
}

/// <summary>
/// 模板拥有的数据导出限制。预览分页与导出方式是两个维度：Paged 报表也可以选择 Normal 导出。
/// </summary>
public sealed record PocExportSettings(
    PocDataExportMode DefaultMode,
    int NormalMaxRows,
    int NormalMaxCells,
    int NormalTimeoutSeconds,
    bool LargeExportEnabled,
    int RowsPerSheet,
    int FileRetentionHours);

/// <summary>
/// 查询参数元数据。前端根据该契约生成查询控件，并区分 URL 参数与后台默认函数。
/// DefaultValue 是后台解析后的稳定值，不要求前端再次实现日期等默认值算法。
/// </summary>
public sealed record PocParameterDefinition(
    string Code,
    string DisplayName,
    string ControlType,
    string SourceType,
    string? DefaultValue,
    bool Visible,
    string DataType = "string",
    string? DefaultExpression = null,
    string? FixedValue = null,
    bool ReadOnly = false,
    bool Required = false,
    bool AllowUrlPrefill = false,
    string? UrlKey = null);

/// <summary>
/// 明细数据集的列定义。FormulaTemplate 只描述公式列模板，普通数据列由后台结果直接提供。
/// </summary>
public sealed record PocColumnDefinition(
    string Code,
    string Header,
    string DataType,
    int Width,
    string? FormulaTemplate = null);

/// <summary>
/// 一个可执行报表的完整元数据，包括查询参数、分页方式和数据集字段。
/// 模板绑定校验和前端字段面板都以此定义作为允许字段的唯一来源。
/// </summary>
public sealed record PocReportDefinition(
    string Code,
    string Name,
    string SheetName,
    PocPreviewSettings Preview,
    PocExportSettings Export,
    IReadOnlyList<PocParameterDefinition> Parameters,
    IReadOnlyList<PocColumnDefinition> Columns);

/// <summary>
/// 浏览器提交的预览/导出查询条件。可空字符串由报表目录统一解析为业务默认值。
/// </summary>
public sealed record PocExecuteRequest(
    int Page,
    IReadOnlyDictionary<string, string?> Parameters)
{
    public PocExecuteRequest(
        int page,
        string? hospitalId,
        string? deptId,
        string? personId,
        string? startDate,
        string? endDate)
        : this(page, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hospitalId"] = hospitalId,
            ["deptId"] = deptId,
            ["personId"] = personId,
            ["startDate"] = startDate,
            ["endDate"] = endDate
        })
    {
    }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string? HospitalId => Get("hospitalId");

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string? DeptId => Get("deptId");

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string? PersonId => Get("personId");

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string? StartDate => Get("startDate");

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string? EndDate => Get("endDate");

    private string? Get(string code) =>
        Parameters.TryGetValue(code, out var value) ? value : null;
}

/// <summary>
/// 数据导出必须绑定一个已经保存的模板修订号。Query 只描述查询条件，不接受浏览器传入的模板快照。
/// </summary>
public sealed record PocDataExportRequest(
    int TemplateRevision,
    PocExecuteRequest Query);

/// <summary>
/// 后台解析后的查询参数。返回给前端后可直接替换模板中的 params token，字段不会再出现 null。
/// </summary>
public sealed class PocResolvedParameters : Dictionary<string, string>
{
    public PocResolvedParameters()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public PocResolvedParameters(IEnumerable<KeyValuePair<string, string>> values)
        : base(values, StringComparer.OrdinalIgnoreCase)
    {
    }

    public PocResolvedParameters(
        string hospitalId,
        string deptId,
        string personId,
        string startDate,
        string endDate)
        : this()
    {
        this["hospitalId"] = hospitalId;
        this["deptId"] = deptId;
        this["personId"] = personId;
        this["startDate"] = startDate;
        this["endDate"] = endDate;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string HospitalId => this["hospitalId"];

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string DeptId => this["deptId"];

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string PersonId => this["personId"];

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string StartDate => this["startDate"];

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string EndDate => this["endDate"];
}

/// <summary>
/// 科室工作量明细数据集的一行。公式列不在这里计算，而是在 Univer 或导出的 Excel/WPS 中计算。
/// </summary>
public sealed record PocWorkloadRow(
    int Sequence,
    string DepartmentName,
    int OutpatientCount,
    int DischargeCount);

/// <summary>
/// 数据集动态行。键必须来自已保存字段定义；值只允许 JSON/Excel 可表达的标量或 null。
/// 兼容属性仅供现有验证测试使用，不参与 JSON 序列化。
/// </summary>
public sealed class PocDataRow : Dictionary<string, object?>
{
    public PocDataRow()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public PocDataRow(IEnumerable<KeyValuePair<string, object?>> values)
        : base(values, StringComparer.OrdinalIgnoreCase)
    {
    }

    public static PocDataRow FromWorkload(PocWorkloadRow row) => new()
    {
        ["sequence"] = row.Sequence,
        ["departmentName"] = row.DepartmentName,
        ["outpatientCount"] = row.OutpatientCount,
        ["dischargeCount"] = row.DischargeCount
    };

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public int Sequence => Convert.ToInt32(this["sequence"], System.Globalization.CultureInfo.InvariantCulture);

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string DepartmentName => Convert.ToString(this["departmentName"], System.Globalization.CultureInfo.InvariantCulture)!;

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public int OutpatientCount => Convert.ToInt32(this["outpatientCount"], System.Globalization.CultureInfo.InvariantCulture);

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public int DischargeCount => Convert.ToInt32(this["dischargeCount"], System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 单页执行结果。HasMore 让前端在不查询精确总数的情况下控制“下一页”按钮。
/// </summary>
public sealed record PocExecuteResult(
    int Page,
    int PageSize,
    bool HasMore,
    PocResolvedParameters Parameters,
    IReadOnlyList<PocDataRow> Rows);
