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
