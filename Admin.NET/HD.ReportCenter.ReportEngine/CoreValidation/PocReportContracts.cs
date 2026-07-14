namespace HD.ReportCenter.ReportEngine.CoreValidation;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
[Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
public enum PocPreviewMode
{
    Full,
    Paged
}

public sealed record PocPreviewSettings(
    PocPreviewMode Mode,
    int PageSize,
    bool ShowExactTotal);

public sealed record PocParameterDefinition(
    string Code,
    string DisplayName,
    string ControlType,
    string SourceType,
    string? DefaultValue,
    bool Visible);

public sealed record PocColumnDefinition(
    string Code,
    string Header,
    string DataType,
    int Width,
    string? FormulaTemplate = null);

public sealed record PocReportDefinition(
    string Code,
    string Name,
    string SheetName,
    PocPreviewSettings Preview,
    IReadOnlyList<PocParameterDefinition> Parameters,
    IReadOnlyList<PocColumnDefinition> Columns);

public sealed record PocExecuteRequest(
    int Page,
    string? HospitalId,
    string? DeptId,
    string? PersonId,
    string? StartDate,
    string? EndDate);

public sealed record PocResolvedParameters(
    string HospitalId,
    string DeptId,
    string PersonId,
    string StartDate,
    string EndDate);

public sealed record PocWorkloadRow(
    int Sequence,
    string DepartmentName,
    int OutpatientCount,
    int DischargeCount);

public sealed record PocExecuteResult(
    int Page,
    int PageSize,
    bool HasMore,
    PocResolvedParameters Parameters,
    IReadOnlyList<PocWorkloadRow> Rows);
