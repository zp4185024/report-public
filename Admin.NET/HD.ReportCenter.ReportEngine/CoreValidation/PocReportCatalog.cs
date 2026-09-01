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
        // 第2阶段：构造 GetDefinition 当前步骤需要的中间数据和执行参数。
        var startDate = new DateTimeOffset(current.Year, current.Month, 1, 0, 0, 0, current.Offset);

        // 第一阶段：整理 GetDefinition 已计算的数据，并按方法契约返回最终结果。
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
                new("hospitalId", "医院", "text", "Form", null, true, AllowUrlPrefill: true),
                new("deptId", "科室", "text", "Form", null, true, AllowUrlPrefill: true),
                new("personId", "人员", "text", "Form", null, true, AllowUrlPrefill: true),
                new(
                    "startDate",
                    "开始日期",
                    "date",
                    "Form",
                    FormatDate(startDate),
                    true,
                    "date",
                    "startOfMonth(today())",
                    AllowUrlPrefill: true),
                new(
                    "endDate",
                    "结束日期",
                    "date",
                    "Form",
                    FormatDate(current),
                    true,
                    "date",
                    "today()",
                    AllowUrlPrefill: true)
            ],
            [
                new("sequence", "序号", "number"),
                new("departmentName", "科室", "string"),
                new("outpatientCount", "门诊人次", "number"),
                new("dischargeCount", "出院人次", "number")
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

    /// <summary>执行 PocReportCatalog 的 Execute 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="preview">preview 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
            .Select(sequence => PocDataRow.FromWorkload(CreateRow(sequence, request.DeptId)))
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
    public static IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadExportPages(
        PocExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReadExportPages(request, GetDefinition().Preview, cancellationToken);
    }

    /// <summary>按显式预览设置分批读取完整导出数据，最终行集不受 Full/Paged 选择影响。</summary>
    public static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadExportPages(
        PocExecuteRequest request,
        PocPreviewSettings preview,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (preview.Mode == PocPreviewMode.Full)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return EnumerateRows(PreviewRowCount, request.DeptId)
                .Select(PocDataRow.FromWorkload)
                .ToArray();
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

    /// <summary>构建 PocReportCatalog 所需的 Create Row 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="sequence">sequence 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="deptId">需要定位的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>读取 PocReportCatalog 的目标值；值为空时返回调用方提供的默认内容。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="fallback">fallback 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>将 PocReportCatalog 中的 Format Date 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
