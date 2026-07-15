using ClosedXML.Excel;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

public sealed class PocXlsxExchangeConverterTests
{
    [Fact]
    public void Export_writes_the_current_template_as_a_valid_xlsx_workbook()
    {
        var snapshot = LoadSeedSnapshot();
        var headerStyle = (JObject)snapshot["styles"]!["header"]!;
        headerStyle["bd"] = new JObject
        {
            ["b"] = new JObject
            {
                ["s"] = 1,
                ["cl"] = new JObject { ["rgb"] = "#2563eb" }
            }
        };
        var converter = new PocXlsxExchangeConverter();
        using var output = new MemoryStream();

        converter.Export(snapshot, CurrentRules(), output);

        Assert.True(output.Length > 0);
        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet("科室工作量");
        Assert.Equal("科室工作量统计（可编辑模板）", sheet.Cell("A1").GetString());
        Assert.Equal("C6+D6", sheet.Cell("E6").FormulaA1);
        Assert.Equal("AVERAGE(C6:D6)", sheet.Cell("F6").FormulaA1);
        Assert.Contains(sheet.MergedRanges, range => range.RangeAddress.ToString() == "A1:F1");
        Assert.True(sheet.Cell("A1").Style.Font.Bold);
        Assert.Equal("Microsoft YaHei", sheet.Cell("A1").Style.Font.FontName);
        Assert.Equal(16d, sheet.Cell("A1").Style.Font.FontSize, 3);
        Assert.Equal(XLAlignmentHorizontalValues.Center, sheet.Cell("A1").Style.Alignment.Horizontal);
        Assert.Equal(XLAlignmentVerticalValues.Center, sheet.Cell("A1").Style.Alignment.Vertical);
        Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell("A5").Style.Border.BottomBorder);
        Assert.Equal(31.5d, sheet.Row(1).Height, 2);
        Assert.InRange(sheet.Column(1).Width, 9d, 10d);
    }

    private static JObject LoadSeedSnapshot()
    {
        const string resourceName =
            "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";
        using var stream = typeof(PocTemplateService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到测试模板资源 {resourceName}。");
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
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
