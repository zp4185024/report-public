using System.Globalization;
using System.Runtime.CompilerServices;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>
/// 固定“科室工作量统计”验证报表的定义和可重复测试数据源。
/// 正式迁移后这里将由数据库报表定义、数据源 SQL 和参数解析服务替换。
/// </summary>
public static class PocReportCatalog
{
    /// <summary>本轮所有 API 只接受这一报表编码。</summary>
    public const string ReportCode = "dept-workload";
    /// <summary>报表明确启用 Paged 时使用的默认预览页大小。</summary>
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
            new PocPreviewSettings(PocPreviewMode.Full, PageSize, false),
            new PocExportSettings(
                PocDataExportMode.Normal,
                NormalMaxRows: 20_000,
                NormalMaxCells: 500_000,
                NormalTimeoutSeconds: 60,
                LargeExportEnabled: true,
                RowsPerSheet: 1_000_000,
                FileRetentionHours: 24),
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

    /// <summary>按当前报表定义执行预览，并把空参数解析为可直接写入模板的非空显示值。</summary>
    public static PocExecuteResult Execute(PocExecuteRequest request, DateTimeOffset? now = null)
    {
        var definition = GetDefinition(now);
        return Execute(request, definition.Preview, definition);
    }

    /// <summary>
    /// 按显式预览设置执行固定数据。正式迁移后，数据库报表定义可以直接把保存的设置传入此入口。
    /// </summary>
    public static PocExecuteResult Execute(
        PocExecuteRequest request,
        PocPreviewSettings preview,
        DateTimeOffset? now = null)
    {
        return Execute(request, preview, GetDefinition(now));
    }

    private static PocExecuteResult Execute(
        PocExecuteRequest request,
        PocPreviewSettings preview,
        PocReportDefinition definition)
    {
        var fullPreview = preview.Mode == PocPreviewMode.Full;
        var page = fullPreview ? 1 : Math.Max(1, request.Page);
        var offset = fullPreview ? 0 : (page - 1) * preview.PageSize;
        // Full 一次返回完整结果；Paged 才按配置页大小截取当前批次。
        var count = fullPreview
            ? PreviewRowCount
            : Math.Max(0, Math.Min(preview.PageSize, PreviewRowCount - offset));
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
            fullPreview ? rows.Length : preview.PageSize,
            !fullPreview && offset + rows.Length < PreviewRowCount,
            parameters,
            rows);
    }

    /// <summary>
    /// 从第一批开始读取完整导出数据。Paged 只控制每批查询量，绝不把当前预览页当成导出范围。
    /// </summary>
    public static IAsyncEnumerable<IReadOnlyList<PocWorkloadRow>> ReadExportPages(
        PocExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReadExportPages(request, GetDefinition().Preview, cancellationToken);
    }

    /// <summary>按显式预览设置分批读取完整导出数据，最终行集不受 Full/Paged 选择影响。</summary>
    public static async IAsyncEnumerable<IReadOnlyList<PocWorkloadRow>> ReadExportPages(
        PocExecuteRequest request,
        PocPreviewSettings preview,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (preview.Mode == PocPreviewMode.Full)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return EnumerateRows(PreviewRowCount, request.DeptId).ToArray();
            yield break;
        }

        var page = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Execute(request with { Page = page }, preview);
            if (result.Rows.Count > 0)
            {
                yield return result.Rows;
            }
            if (!result.HasMore)
            {
                yield break;
            }

            page += 1;
            // 正式数据源会在这里执行异步 SQL；POC 主动让出线程以保持同一异步消费契约。
            await Task.Yield();
        }
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
