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

    [Fact]
    public void Import_creates_a_sparse_univer_0251_snapshot()
    {
        using var input = CreateImportWorkbook();
        var converter = new PocXlsxExchangeConverter();

        var result = converter.Import(
            input,
            CurrentRules(),
            "导入验证模板",
            new PocXlsxExchangeLimits(10 * 1024 * 1024, 10, 200_000));

        var snapshot = result.Snapshot;
        Assert.Equal("0.25.1", snapshot.Value<string>("appVersion"));
        Assert.Equal("导入验证模板", snapshot.Value<string>("name"));
        Assert.Equal(2, snapshot["sheetOrder"]!.Count());
        var firstSheetId = snapshot["sheetOrder"]![0]!.Value<string>()!;
        var firstSheet = (JObject)snapshot["sheets"]![firstSheetId]!;
        Assert.Equal("导入模板", firstSheet.Value<string>("name"));
        Assert.Equal("测试标题", firstSheet["cellData"]!["0"]!["0"]!["v"]!.Value<string>());
        Assert.Equal("{{params.hospitalId}}", firstSheet["cellData"]!["1"]!["0"]!["v"]!.Value<string>());
        Assert.Equal(12d, firstSheet["cellData"]!["3"]!["2"]!["v"]!.Value<double>());
        Assert.Equal("=C4+D4", firstSheet["cellData"]!["3"]!["4"]!["f"]!.Value<string>());
        // Univer 的 WHEN_EMPTY 初始计算模式会重算没有缓存值的公式。设计态仍是 token，
        // 因此导入公式必须携带数值缓存，避免一打开模板就把 token 计算成 #VALUE!。
        Assert.Equal(0d, firstSheet["cellData"]!["3"]!["4"]!["v"]!.Value<double>());
        Assert.Equal(2, firstSheet["cellData"]!["3"]!["4"]!["t"]!.Value<int>());
        Assert.Null(firstSheet["cellData"]!["20"]);
        Assert.Single(firstSheet["mergeData"]!);
        Assert.Equal(3, firstSheet["freeze"]!["ySplit"]!.Value<int>());
        Assert.InRange(firstSheet["rowData"]!["0"]!["h"]!.Value<double>(), 39.9d, 40.1d);
        Assert.InRange(firstSheet["columnData"]!["0"]!["w"]!.Value<double>(), 144d, 146d);

        var titleStyleId = firstSheet["cellData"]!["0"]!["0"]!["s"]!.Value<string>()!;
        var titleStyle = (JObject)snapshot["styles"]![titleStyleId]!;
        Assert.Equal("Microsoft YaHei", titleStyle.Value<string>("ff"));
        Assert.Equal(14d, titleStyle.Value<double>("fs"), 3);
        Assert.Equal(1, titleStyle.Value<int>("bl"));
        Assert.Equal(2, titleStyle.Value<int>("ht"));
    }

    [Fact]
    public void Current_template_round_trip_preserves_supported_semantics()
    {
        var original = LoadSeedSnapshot();
        var converter = new PocXlsxExchangeConverter();
        using var xlsx = new MemoryStream();
        converter.Export(original, CurrentRules(), xlsx);

        var imported = converter.Import(
            xlsx,
            CurrentRules(),
            "往返模板",
            new PocXlsxExchangeLimits(10 * 1024 * 1024, 10, 200_000)).Snapshot;

        var sheetId = imported["sheetOrder"]![0]!.Value<string>()!;
        var sheet = (JObject)imported["sheets"]![sheetId]!;
        Assert.Equal("=C6+D6", sheet["cellData"]!["5"]!["4"]!["f"]!.Value<string>());
        Assert.Equal("=AVERAGE(C6:D6)", sheet["cellData"]!["5"]!["5"]!["f"]!.Value<string>());
        Assert.Equal(0d, sheet["cellData"]!["5"]!["4"]!["v"]!.Value<double>());
        Assert.Equal(2, sheet["cellData"]!["5"]!["4"]!["t"]!.Value<int>());
        Assert.Equal(0d, sheet["cellData"]!["5"]!["5"]!["v"]!.Value<double>());
        Assert.Equal(2, sheet["cellData"]!["5"]!["5"]!["t"]!.Value<int>());
        Assert.Equal(5, sheet["mergeData"]!.Count());
        Assert.InRange(sheet["rowData"]!["0"]!["h"]!.Value<double>(), 41.9d, 42.1d);
        Assert.InRange(sheet["columnData"]!["0"]!["w"]!.Value<double>(), 69d, 71d);
        Assert.Equal(5, sheet["freeze"]!["ySplit"]!.Value<int>());

        var titleStyleId = sheet["cellData"]!["0"]!["0"]!["s"]!.Value<string>()!;
        var titleStyle = (JObject)imported["styles"]![titleStyleId]!;
        Assert.Equal("Microsoft YaHei", titleStyle.Value<string>("ff"));
        Assert.Equal(16d, titleStyle.Value<double>("fs"), 3);
        Assert.Equal(1, titleStyle.Value<int>("bl"));
    }

    [Fact]
    public void Import_uses_database_target_paths_and_scale_factors()
    {
        using var input = CreateImportWorkbook();
        var current = CurrentRules();
        var changedRules = current.Rules.Select(rule => rule.RuleCode switch
        {
            "xlsx-to-univer-cell-formula" => rule with { TargetPath = "custom.formula" },
            "xlsx-to-univer-row-height" => rule with { Factor = 2m },
            _ => rule
        }).ToArray();
        var converter = new PocXlsxExchangeConverter();

        var snapshot = converter.Import(
            input,
            current with { Rules = changedRules },
            "映射配置验证",
            new PocXlsxExchangeLimits(10 * 1024 * 1024, 10, 200_000)).Snapshot;

        var sheetId = snapshot["sheetOrder"]![0]!.Value<string>()!;
        var sheet = (JObject)snapshot["sheets"]![sheetId]!;
        Assert.Null(sheet["cellData"]!["3"]!["4"]!["f"]);
        Assert.Equal("=C4+D4", sheet["cellData"]!["3"]!["4"]!["custom"]!["formula"]!.Value<string>());
        Assert.Equal(60d, sheet["rowData"]!["0"]!["h"]!.Value<double>(), 3);
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

    private static MemoryStream CreateImportWorkbook()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("导入模板");
        sheet.Cell("A1").Value = "测试标题";
        sheet.Cell("A1").Style.Font.FontName = "Microsoft YaHei";
        sheet.Cell("A1").Style.Font.FontSize = 14;
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range("A1:C1").Merge();
        sheet.Cell("A2").Value = "{{params.hospitalId}}";
        sheet.Cell("A4").Value = "{{detail.departmentName}}";
        sheet.Cell("C4").Value = 12;
        sheet.Cell("D4").Value = 3;
        sheet.Cell("E4").FormulaA1 = "C4+D4";
        sheet.Row(1).Height = 30;
        sheet.Column(1).Width = 20;
        sheet.Row(3).Hide();
        sheet.Column(6).Hide();
        sheet.SheetView.FreezeRows(3);
        workbook.Worksheets.Add("空工作表");

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
