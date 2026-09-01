using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;

namespace HD.ReportCenter.Tests;

public sealed class PocStreamingTemplateDataExporterTests
{
    /// <summary>验证 Write Large Async Streams Paged Rows Through The Saved Template 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task WriteLargeAsync_StreamsPagedRowsThroughTheSavedTemplate()
    {
        var snapshot = LoadSeedSnapshotWithSummaryRow();
        var query = new PocExecuteRequest(
            1,
            "H01",
            "D02",
            "P03",
            "2026-07-01",
            "2026-07-14");
        // 第一阶段：构造 WriteLargeAsync_StreamsPagedRowsThroughTheSavedTemplate 当前步骤需要的中间数据和执行参数。
        var plan = new PocTemplateDataExportPlan(
            PocReportCatalog.ReportCode,
            3,
            snapshot,
            PocTemplateValidator.Scan(snapshot.ToString(Newtonsoft.Json.Formatting.None)),
            query,
            PocReportCatalog.GetDefinition().Export,
            CurrentRules());
        var progress = new List<long>();
        var exporter = new PocStreamingTemplateDataExporter();
        await using var output = new MemoryStream();

        await exporter.WriteLargeAsync(
            output,
            plan,
            ReadPages(2_001, 500, query.DeptId, CancellationToken.None),
            progress.Add,
            CancellationToken.None);

        output.Position = 0;
        using (var document = SpreadsheetDocument.Open(output, false))
        {
            // 第2阶段：根据 WriteLargeAsync_StreamsPagedRowsThroughTheSavedTemplate 当前分支转换输入数据，并保存该阶段产生的中间结果。
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet("科室工作量");
        Assert.Equal("医院：H01", sheet.Cell("A2").GetString());
        Assert.Equal(2_001d, sheet.Cell("A2006").GetDouble());
        Assert.Equal("C2006+D2006", sheet.Cell("E2006").FormulaA1);
        Assert.Equal("整体统计", sheet.Cell("A2007").GetString());
        Assert.Equal("SUM(E6:E2006)", sheet.Cell("E2007").FormulaA1);
        Assert.Equal(
            sheet.Cell("E6").Style.Fill.BackgroundColor.Color,
            sheet.Cell("E2006").Style.Fill.BackgroundColor.Color);
        Assert.DoesNotContain(
            sheet.CellsUsed(XLCellsUsedOptions.Contents),
            cell => cell.GetString().Contains("{{", StringComparison.Ordinal));
        Assert.Equal([500L, 1_000L, 1_500L, 2_000L, 2_001L], progress);
    }

    /// <summary>验证 Write Large Async Writes One Hundred Thousand Rows Without Materializing All Data 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task WriteLargeAsync_WritesOneHundredThousandRowsWithoutMaterializingAllData()
    {
        var snapshot = LoadSeedSnapshotWithSummaryRow();
        var query = new PocExecuteRequest(
            1,
            "H01",
            "D02",
            "P03",
            "2026-07-01",
            "2026-07-14");
        // 第一阶段：构造 WriteLargeAsync_WritesOneHundredThousandRowsWithoutMaterializingAllData 当前步骤需要的中间数据和执行参数。
        var plan = new PocTemplateDataExportPlan(
            PocReportCatalog.ReportCode,
            3,
            snapshot,
            PocTemplateValidator.Scan(snapshot.ToString(Newtonsoft.Json.Formatting.None)),
            query,
            PocReportCatalog.GetDefinition().Export,
            CurrentRules());
        var exporter = new PocStreamingTemplateDataExporter();
        var progress = new List<long>();
        var path = Path.Combine(Path.GetTempPath(), $"report-stream-{Guid.NewGuid():N}.xlsx");

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var memoryBefore = GC.GetTotalMemory(true);
            await using (var output = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.SequentialScan))
            {
                // 第2阶段：调用 WriteLargeAsync_WritesOneHundredThousandRowsWithoutMaterializingAllData 依赖的异步能力，等待并校验返回数据。
                await exporter.WriteLargeAsync(
                    output,
                    plan,
                    ReadPages(100_000, 1_000, query.DeptId, CancellationToken.None),
                    progress.Add,
                    CancellationToken.None);
            }
            var memoryAfter = GC.GetTotalMemory(true);

            var inspected = InspectWorksheet(path, 100_005, 100_006);
            Assert.Equal("C100005+D100005", inspected.DetailFormula);
            Assert.Equal("SUM(E6:E100005)", inspected.SummaryFormula);
            Assert.Equal(100_000L, progress[^1]);
            Assert.InRange(memoryAfter - memoryBefore, -16L * 1024 * 1024, 96L * 1024 * 1024);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>查询或读取 PocStreamingTemplateDataExporterTests 所需的 Read Pages 数据，并按调用约定返回结果。</summary>
    /// <param name="rowCount">控制数据范围或位置的数值。</param>
    /// <param name="pageSize">控制数据范围或位置的数值。</param>
    /// <param name="deptId">需要定位的业务记录标识。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadPages(
        int rowCount,
        int pageSize,
        string? deptId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = new List<PocDataRow>(pageSize);
        foreach (var row in PocReportCatalog.EnumerateRows(rowCount, deptId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            page.Add(PocDataRow.FromWorkload(row));
            if (page.Count == pageSize)
            {
                yield return page.ToArray();
                page.Clear();
                await Task.Yield();
            }
        }
        if (page.Count > 0)
        {
            yield return page.ToArray();
        }
    }

    /// <summary>查询或读取 PocStreamingTemplateDataExporterTests 所需的 Load Seed Snapshot With Summary Row 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject LoadSeedSnapshotWithSummaryRow()
    {
        const string resourceName =
            "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";
        using var stream = typeof(PocTemplateService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到测试模板资源 {resourceName}。");
        using var reader = new StreamReader(stream);
        var snapshot = JObject.Parse(reader.ReadToEnd());
        var cellData = (JObject)snapshot["sheets"]!["report-sheet"]!["cellData"]!;
        cellData["6"] = new JObject
        {
            ["0"] = new JObject { ["v"] = "整体统计", ["s"] = "header" },
            ["4"] = new JObject { ["f"] = "=SUM(E6:E6)", ["s"] = "formula" },
            ["5"] = new JObject { ["f"] = "=AVERAGE(F6:F6)", ["s"] = "formula" }
        };
        return snapshot;
    }

    /// <summary>根据 PocStreamingTemplateDataExporterTests 当前输入计算 Current Rules 结果，并按方法声明的类型返回。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocExchangeRuleSet CurrentRules()
    {
        var rules = PocExchangeMappingDefaults.Create()
            .Select(rule => new PocExchangeRule(
                rule.RuleCode,
                rule.Direction,
                rule.Category,
                rule.SourcePath,
                rule.TargetPath,
                rule.TransformType,
                rule.SourceValue,
                rule.TargetValue,
                rule.Factor,
                rule.IsRequired,
                rule.Sort))
            .ToArray();
        return new PocExchangeRuleSet(
            PocExchangeMappingDefaults.CurrentUniverVersion,
            PocExchangeMappingDefaults.CurrentMappingVersion,
            rules);
    }

    /// <summary>检查 PocStreamingTemplateDataExporterTests 的 Inspect Worksheet 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="path">path 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="detailRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="summaryRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static (string DetailFormula, string SummaryFormula) InspectWorksheet(
        string path,
        int detailRow,
        int summaryRow)
    {
        using var archive = ZipFile.OpenRead(path);
        var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException("导出文件缺少 sheet1.xml。");
        using var stream = worksheet.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true
        });
        var detailCell = $"E{detailRow}";
        var summaryCell = $"E{summaryRow}";
        string? currentCell = null;
        string? detailFormula = null;
        string? summaryFormula = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                currentCell = reader.GetAttribute("r");
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "f")
            {
                var formula = reader.ReadElementContentAsString();
                if (currentCell == detailCell)
                {
                    detailFormula = formula;
                }
                else if (currentCell == summaryCell)
                {
                    summaryFormula = formula;
                }
            }
        }
        return (
            detailFormula ?? throw new InvalidDataException($"缺少 {detailCell} 公式。"),
            summaryFormula ?? throw new InvalidDataException($"缺少 {summaryCell} 公式。"));
    }
}
