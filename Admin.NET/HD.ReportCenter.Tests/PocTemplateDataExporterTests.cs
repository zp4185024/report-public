using ClosedXML.Excel;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

public sealed class PocTemplateDataExporterTests
{
    [Fact]
    public async Task WriteNormalAsync_ExportsAllRowsWithParametersStylesAndFormulas()
    {
        var snapshot = LoadSeedSnapshotWithSummaryRow();
        var binding = PocTemplateValidator.Scan(snapshot.ToString(Formatting.None));
        var query = new PocExecuteRequest(
            3,
            "H01",
            "D02",
            "P03",
            "2026-07-01",
            "2026-07-14");
        var plan = new PocTemplateDataExportPlan(
            PocReportCatalog.ReportCode,
            3,
            snapshot,
            binding,
            query,
            PocReportCatalog.GetDefinition().Export,
            CurrentRules());
        var exporter = new PocTemplateDataExporter();
        await using var output = new MemoryStream();

        await exporter.WriteNormalAsync(
            output,
            plan,
            PocReportCatalog.ReadExportPages(query, CancellationToken.None),
            CancellationToken.None);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet("科室工作量");
        Assert.Equal("医院：H01", sheet.Cell("A2").GetString());
        Assert.Equal("科室：D02", sheet.Cell("C2").GetString());
        Assert.Equal("人员：P03", sheet.Cell("E2").GetString());
        Assert.Equal("统计期间：2026-07-01 至 2026-07-14", sheet.Cell("A3").GetString());

        Assert.Equal(1d, sheet.Cell("A6").GetDouble());
        Assert.Equal(137d, sheet.Cell("A142").GetDouble());
        Assert.Equal("C6+D6", sheet.Cell("E6").FormulaA1);
        Assert.Equal("C142+D142", sheet.Cell("E142").FormulaA1);
        Assert.Equal("AVERAGE(C142:D142)", sheet.Cell("F142").FormulaA1);
        Assert.Equal("整体统计", sheet.Cell("A143").GetString());
        Assert.Equal("SUM(E6:E142)", sheet.Cell("E143").FormulaA1);
        Assert.Equal("AVERAGE(F6:F142)", sheet.Cell("F143").FormulaA1);

        Assert.Equal(
            sheet.Cell("E6").Style.Fill.BackgroundColor.Color,
            sheet.Cell("E142").Style.Fill.BackgroundColor.Color);
        Assert.Contains(sheet.MergedRanges, range => range.RangeAddress.ToString() == "A1:F1");
        Assert.Equal(5, sheet.SheetView.SplitRow);
        Assert.DoesNotContain(
            sheet.CellsUsed(XLCellsUsedOptions.Contents),
            cell => cell.GetString().Contains("{{", StringComparison.Ordinal));
    }

    private static JObject LoadSeedSnapshotWithSummaryRow()
    {
        const string resourceName =
            "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";
        using var stream = typeof(PocTemplateService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到测试模板资源 {resourceName}。");
        using var reader = new StreamReader(stream);
        var snapshot = JObject.Parse(reader.ReadToEnd());

        // 统计行紧跟明细模板行，验证插入 136 行后统计公式会覆盖完整导出数据区间。
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
}
