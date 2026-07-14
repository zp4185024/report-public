using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public static class PocReportCatalog
{
    public const string ReportCode = "dept-workload";
    public const int PageSize = 20;
    public const int PreviewRowCount = 137;

    public static PocReportDefinition GetDefinition(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var startDate = new DateTimeOffset(current.Year, current.Month, 1, 0, 0, 0, current.Offset);

        return new PocReportDefinition(
            ReportCode,
            "科室工作量统计",
            "科室工作量",
            new PocPreviewSettings(PocPreviewMode.Paged, PageSize, false),
            [
                new("hospitalId", "医院", "text", "url", null, true),
                new("deptId", "科室", "text", "url", null, true),
                new("personId", "人员", "text", "url", null, true),
                new("startDate", "开始日期", "date", "default-function", FormatDate(startDate), true),
                new("endDate", "结束日期", "date", "default-function", FormatDate(current), true)
            ],
            [
                new("sequence", "序号", "number", 70),
                new("departmentName", "科室", "text", 180),
                new("outpatientCount", "门诊人次", "number", 110),
                new("dischargeCount", "出院人次", "number", 110),
                new("totalWorkload", "合计", "formula", 110, "=C{row}+D{row}"),
                new("averageWorkload", "平均值", "formula", 110, "=AVERAGE(C{row}:D{row})")
            ]);
    }

    public static PocExecuteResult Execute(PocExecuteRequest request, DateTimeOffset? now = null)
    {
        var definition = GetDefinition(now);
        var page = Math.Max(1, request.Page);
        var offset = (page - 1) * definition.Preview.PageSize;
        var count = Math.Max(0, Math.Min(definition.Preview.PageSize, PreviewRowCount - offset));
        var rows = Enumerable.Range(offset + 1, count)
            .Select(sequence => CreateRow(sequence, request.DeptId))
            .ToArray();

        var parameters = new PocResolvedParameters(
            ValueOrDefault(request.HospitalId, "全部医院"),
            ValueOrDefault(request.DeptId, "全部科室"),
            ValueOrDefault(request.PersonId, "全部人员"),
            ValueOrDefault(request.StartDate, definition.Parameters.Single(x => x.Code == "startDate").DefaultValue!),
            ValueOrDefault(request.EndDate, definition.Parameters.Single(x => x.Code == "endDate").DefaultValue!));

        return new PocExecuteResult(
            page,
            definition.Preview.PageSize,
            offset + rows.Length < PreviewRowCount,
            parameters,
            rows);
    }

    public static IEnumerable<PocWorkloadRow> EnumerateRows(int rowCount, string? deptId = null)
    {
        for (var sequence = 1; sequence <= rowCount; sequence++)
        {
            yield return CreateRow(sequence, deptId);
        }
    }

    private static PocWorkloadRow CreateRow(int sequence, string? deptId)
    {
        var department = ValueOrDefault(deptId, "科室");
        return new PocWorkloadRow(
            sequence,
            $"{department}-{((sequence - 1) % 12) + 1:D2}",
            80 + sequence * 17 % 121,
            20 + sequence * 11 % 61);
    }

    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
