using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>
/// 固定“科室工作量统计”验证报表的定义和可重复测试数据源。
/// 正式迁移后这里将由数据库报表定义、数据源 SQL 和参数解析服务替换。
/// </summary>
public static class PocReportCatalog
{
    /// <summary>本轮所有 API 只接受这一报表编码。</summary>
    public const string ReportCode = "dept-workload";
    /// <summary>模板定义的固定预览页大小。</summary>
    public const int PageSize = 20;
    /// <summary>验证数据总行数；选择非整页数字以覆盖最后一页逻辑。</summary>
    public const int PreviewRowCount = 137;

    /// <summary>
    /// 生成报表元数据。日期默认函数在后台解析，保证 URL 未传日期时不同客户端获得同一规则。
    /// </summary>
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
                // default-function 表示值由后台函数解析，而不是写死在模板或浏览器代码中。
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

    /// <summary>执行一页固定数据，并把空参数解析为可直接写入模板的非空显示值。</summary>
    public static PocExecuteResult Execute(PocExecuteRequest request, DateTimeOffset? now = null)
    {
        var definition = GetDefinition(now);
        var page = Math.Max(1, request.Page);
        var offset = (page - 1) * definition.Preview.PageSize;
        // 不做精确总数查询的通用模拟：当前页最多 PageSize 行，超出固定数据集后返回空页。
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

    /// <summary>
    /// 惰性产生指定行数，供普通和大文件导出复用；不会先构造十万行集合占用内存。
    /// </summary>
    public static IEnumerable<PocWorkloadRow> EnumerateRows(int rowCount, string? deptId = null)
    {
        for (var sequence = 1; sequence <= rowCount; sequence++)
        {
            yield return CreateRow(sequence, deptId);
        }
    }

    private static PocWorkloadRow CreateRow(int sequence, string? deptId)
    {
        // 使用确定性算式生成数据，使分页、导出首尾行和公式结果都可在自动化测试中稳定断言。
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
