using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocReportCatalogTests
{
    /// <summary>验证 Get Definition Returns Full Preview And Defaults 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void GetDefinition_ReturnsFullPreviewAndDefaults()
    {
        var now = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.FromHours(8));

        var definition = PocReportCatalog.GetDefinition(now);

        Assert.Equal("dept-workload", definition.Code);
        Assert.Equal(PocPreviewMode.Full, definition.Preview.Mode);
        Assert.Equal(20, definition.Preview.PageSize);
        Assert.Equal(PocDataExportMode.Normal, definition.Export.DefaultMode);
        Assert.Equal(20_000, definition.Export.NormalMaxRows);
        Assert.Equal(500_000, definition.Export.NormalMaxCells);
        Assert.True(definition.Export.LargeExportEnabled);
        Assert.Equal("2026-07-01", definition.Parameters.Single(x => x.Code == "startDate").DefaultValue);
        Assert.Equal("2026-07-14", definition.Parameters.Single(x => x.Code == "endDate").DefaultValue);
    }

    /// <summary>验证 Execute Full Preview Returns All Rows And Normalizes Page 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Execute_FullPreviewReturnsAllRowsAndNormalizesPage()
    {
        var request = new PocExecuteRequest(2, "H01", "D02", "P03", "2026-07-01", "2026-07-14");

        var result = PocReportCatalog.Execute(request);

        Assert.Equal(1, result.Page);
        Assert.Equal(137, result.PageSize);
        Assert.Equal(137, result.Rows.Count);
        Assert.Equal(1, result.Rows[0].Sequence);
        Assert.Equal(137, result.Rows[^1].Sequence);
        Assert.False(result.HasMore);
    }

    /// <summary>验证 Execute Paged Preview Uses Configured Page Size And Stable Rows 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Execute_PagedPreviewUsesConfiguredPageSizeAndStableRows()
    {
        var request = new PocExecuteRequest(2, "H01", "D02", "P03", "2026-07-01", "2026-07-14");
        var preview = new PocPreviewSettings(PocPreviewMode.Paged, 20, false);

        var result = PocReportCatalog.Execute(request, preview);

        Assert.Equal(2, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(20, result.Rows.Count);
        Assert.Equal(21, result.Rows[0].Sequence);
        Assert.True(result.HasMore);
    }

    /// <summary>验证 Read Export Pages Full Preview Returns One Complete Batch 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ReadExportPages_FullPreviewReturnsOneCompleteBatch()
    {
        var request = new PocExecuteRequest(3, "H01", "D02", "P03", "2026-07-01", "2026-07-14");
        var pages = new List<IReadOnlyList<PocDataRow>>();

        await foreach (var page in PocReportCatalog.ReadExportPages(request, CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Single(pages);
        Assert.Equal(137, pages.Sum(page => page.Count));
        Assert.Equal(
            Enumerable.Range(1, 137),
            pages.SelectMany(page => page).Select(row => row.Sequence));
    }

    /// <summary>验证 Read Export Pages Paged Preview Returns All Batches In Stable Order 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ReadExportPages_PagedPreviewReturnsAllBatchesInStableOrder()
    {
        // 导出不能继承页面当前页码。即使用户正在看第 3 页，也必须从第 1 页开始顺序读取全部数据。
        var request = new PocExecuteRequest(3, "H01", "D02", "P03", "2026-07-01", "2026-07-14");
        var preview = new PocPreviewSettings(PocPreviewMode.Paged, 20, false);
        var pages = new List<IReadOnlyList<PocDataRow>>();

        await foreach (var page in PocReportCatalog.ReadExportPages(request, preview, CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Equal(7, pages.Count);
        Assert.Equal(137, pages.Sum(page => page.Count));
        Assert.Equal(
            Enumerable.Range(1, 137),
            pages.SelectMany(page => page).Select(row => row.Sequence));
    }
}
