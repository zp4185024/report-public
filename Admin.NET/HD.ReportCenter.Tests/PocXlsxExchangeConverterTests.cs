using ClosedXML.Excel;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

public sealed class PocXlsxExchangeConverterTests
{
    /// <summary>验证 Export writes the current template as a valid xlsx workbook 场景下的预期行为，并防止相关逻辑回归。</summary>
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

    /// <summary>验证 Export accepts css rgb colors 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="cssColor">cssColor 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="expectedColor">expectedColor 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("rgb(5,80,92)", "#05505C")]
    [InlineData("rgb(50, 54, 57)", "#323639")]
    [InlineData("rgba(255,255,255,0.8)", "#FFFFFF")]
    public void Export_accepts_css_rgb_colors(string cssColor, string expectedColor)
    {
        var snapshot = LoadSeedSnapshot();
        ((JObject)snapshot["styles"]!["header"]!)["cl"]!["rgb"] = cssColor;
        var converter = new PocXlsxExchangeConverter();
        using var output = new MemoryStream();

        converter.Export(snapshot, CurrentRules(), output);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var color = workbook.Worksheet("科室工作量").Cell("A5").Style.Font.FontColor.Color;
        Assert.Equal(
            expectedColor,
            $"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    /// <summary>验证 Export writes univer rich text cells 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Export_writes_univer_rich_text_cells()
    {
        var snapshot = LoadSeedSnapshot();
        var cellData = (JObject)snapshot["sheets"]!["report-sheet"]!["cellData"]!;
        ((JObject)snapshot["styles"]!)["ryHb-o"] = new JObject
        {
            ["ff"] = "Arial",
            ["fs"] = "16",
            ["bl"] = "1",
            ["ht"] = "2",
            ["vt"] = "2"
        };
        // 第一阶段：根据 Export_writes_univer_rich_text_cells 当前分支转换输入数据，并保存该阶段产生的中间结果。
        snapshot["sheets"]!["report-sheet"]!["mergeData"] = new JArray
        {
            new JObject
            {
                ["startRow"] = 1,
                ["endRow"] = 1,
                ["startColumn"] = 3,
                ["endColumn"] = 8
            }
        };
        cellData["1"] = new JObject
        {
            ["3"] = new JObject
            {
                ["s"] = "ryHb-o",
                ["p"] = new JObject
                {
                    ["body"] = new JObject
                    {
                        ["dataStream"] = "锦州市城镇职工医疗保险(异地医保本地就医)1\r\n",
                        ["textRuns"] = new JArray(new JObject
                        {
                            ["st"] = 0,
                            ["ed"] = 22,
                            ["ts"] = new JObject { ["ff"] = "Arial", ["fs"] = "16", ["bl"] = "1" }
                        })
                    }
                }
            }
        };
        // 第2阶段：根据 Export_writes_univer_rich_text_cells 当前分支转换输入数据，并保存该阶段产生的中间结果。
        cellData["4"]!["0"] = new JObject
        {
            ["p"] = new JObject
            {
                ["body"] = new JObject { ["dataStream"] = "               项目\r\r医疗类别\r\n" }
            }
        };
        var converter = new PocXlsxExchangeConverter();
        using var output = new MemoryStream();

        converter.Export(snapshot, CurrentRules(), output);

        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet("科室工作量");
        Assert.Equal("锦州市城镇职工医疗保险(异地医保本地就医)1", sheet.Cell("D2").GetString());
        Assert.True(sheet.Cell("D2").Style.Font.Bold);
        Assert.Equal(16d, sheet.Cell("D2").Style.Font.FontSize);
        Assert.Equal("               项目\n\n医疗类别", sheet.Cell("A5").GetString());
    }

    /// <summary>验证 Import creates a sparse univer 0251 snapshot 场景下的预期行为，并防止相关逻辑回归。</summary>
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
        Assert.Equal(1, titleStyle.Value<int>("tb"));
    }

    /// <summary>验证 Current template round trip preserves supported semantics 场景下的预期行为，并防止相关逻辑回归。</summary>
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

    /// <summary>验证 Import uses database target paths and scale factors 场景下的预期行为，并防止相关逻辑回归。</summary>
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

    /// <summary>查询或读取 PocXlsxExchangeConverterTests 所需的 Load Seed Snapshot 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject LoadSeedSnapshot()
    {
        const string resourceName =
            "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";
        using var stream = typeof(PocTemplateService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到测试模板资源 {resourceName}。");
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }

    /// <summary>根据 PocXlsxExchangeConverterTests 当前输入计算 Current Rules 结果，并按方法声明的类型返回。</summary>
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

    /// <summary>构建 PocXlsxExchangeConverterTests 所需的 Create Import Workbook 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
