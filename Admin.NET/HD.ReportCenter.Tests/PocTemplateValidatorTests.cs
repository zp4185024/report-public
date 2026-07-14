using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

public sealed class PocTemplateValidatorTests
{
    [Fact]
    public void Scan_ReturnsCanonicalBindingsForValidMinimalSnapshot()
    {
        var snapshot = CreateSnapshot();

        var binding = PocTemplateValidator.Scan(snapshot.ToString());

        Assert.Equal(1, binding.Version);
        var parameter = Assert.Single(binding.ParameterBindings);
        Assert.Equal(new PocParameterBinding("hospitalId", "report-sheet", 1, 0), parameter);

        var detail = Assert.IsType<PocDetailBinding>(binding.DetailBinding);
        Assert.Equal("detail", detail.DatasetCode);
        Assert.Equal("report-sheet", detail.SheetId);
        Assert.Equal(5, detail.TemplateRow);
        Assert.Equal(
            [
                new PocDetailFieldBinding("sequence", 0),
                new PocDetailFieldBinding("departmentName", 1),
                new PocDetailFieldBinding("outpatientCount", 2),
                new PocDetailFieldBinding("dischargeCount", 3)
            ],
            detail.Fields);
    }

    [Theory]
    [InlineData(1, 0, "{{params.unknown}}", "A2")]
    [InlineData(5, 0, "{{detail.unknown}}", "A6")]
    public void Scan_RejectsUnknownBindingCodes(int row, int column, string value, string address)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, row, column, value);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains(address, exception.Message);
    }

    [Fact]
    public void Scan_RejectsDetailTokenMixedWithLiteralText()
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 5, 0, "序号：{{detail.sequence}}");

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A6", exception.Message);
    }

    [Fact]
    public void Scan_RejectsDetailTokensOnDifferentRows()
    {
        var snapshot = CreateSnapshot();
        var cellData = (JObject)snapshot["sheets"]!["report-sheet"]!["cellData"]!;
        ((JObject)cellData["5"]!).Remove("3");
        cellData["6"] = new JObject
        {
            ["3"] = new JObject { ["v"] = "{{detail.dischargeCount}}" }
        };

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("D7", exception.Message);
    }

    [Fact]
    public void Scan_RejectsMergeThatTouchesDetailTemplateRow()
    {
        var snapshot = CreateSnapshot();
        snapshot["sheets"]!["report-sheet"]!["mergeData"] = new JArray
        {
            new JObject
            {
                ["startRow"] = 4,
                ["endRow"] = 5,
                ["startColumn"] = 0,
                ["endColumn"] = 1
            }
        };

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A6", exception.Message);
    }

    [Theory]
    [InlineData("{{params.hospitalId}")]
    [InlineData("{params.hospitalId}}")]
    [InlineData("{{params.}}")]
    [InlineData("{{params.hospitalId}} {{broken}}")]
    public void Scan_RejectsMalformedTokenSyntax(string value)
    {
        var snapshot = CreateSnapshot();
        SetCellValue(snapshot, 1, 0, value);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(snapshot.ToString()));

        Assert.Contains("report-sheet", exception.Message);
        Assert.Contains("A2", exception.Message);
    }

    [Fact]
    public void Scan_RejectsSnapshotLargerThanFourMebibytesBeforeParsing()
    {
        var oversizedSnapshot = new string('x', 4 * 1024 * 1024 + 1);

        var exception = Assert.Throws<PocTemplateValidationException>(
            () => PocTemplateValidator.Scan(oversizedSnapshot));

        Assert.Contains("4 MiB", exception.Message);
    }

    private static JObject CreateSnapshot() =>
        new()
        {
            ["sheets"] = new JObject
            {
                ["report-sheet"] = new JObject
                {
                    ["cellData"] = new JObject
                    {
                        ["1"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "医院：{{params.hospitalId}}" }
                        },
                        ["5"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "{{detail.sequence}}" },
                            ["1"] = new JObject { ["v"] = "{{detail.departmentName}}" },
                            ["2"] = new JObject { ["v"] = "{{detail.outpatientCount}}" },
                            ["3"] = new JObject { ["v"] = "{{detail.dischargeCount}}" },
                            ["4"] = new JObject { ["f"] = "=C6+D6" },
                            ["5"] = new JObject { ["f"] = "=AVERAGE(C6:D6)" }
                        }
                    },
                    ["mergeData"] = new JArray()
                }
            }
        };

    private static void SetCellValue(JObject snapshot, int row, int column, string value) =>
        snapshot["sheets"]!["report-sheet"]!["cellData"]![row.ToString()]![column.ToString()]!["v"] = value;
}
