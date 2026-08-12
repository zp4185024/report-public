using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocLocalDataProjectionTests
{
    [Fact]
    public void Projection_keeps_parameter_options_separate_from_field_display_mappings()
    {
        var definition = Definition("select");
        var request = new PocExecuteRequest(1, new Dictionary<string, string?>
        {
            ["GENDER_CODE"] = "F"
        });
        var source = Rows();

        var rows = PocLocalDataProjection.ApplyRows(definition, request, source);
        var parameters = PocLocalDataProjection.ApplyParameterLabels(
            definition,
            new PocResolvedParameters { ["GENDER_CODE"] = "F" });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("女", row["GENDER_CODE"]));
        Assert.Equal("女性条件", parameters["GENDER_CODE"]);
        Assert.Equal("F", source[0]["GENDER_CODE"]);
    }

    [Fact]
    public void Projection_supports_multi_select_values_without_sql_changes()
    {
        var definition = Definition("multiSelect");
        var request = new PocExecuteRequest(1, new Dictionary<string, string?>
        {
            ["GENDER_CODE"] = "[\"M\",\"F\"]"
        });

        var rows = PocLocalDataProjection.ApplyRows(definition, request, Rows());

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => Equals(row["GENDER_CODE"], "男"));
        Assert.Contains(rows, row => Equals(row["GENDER_CODE"], "女"));
    }

    private static PocReportDefinition Definition(string controlType) => new(
        "generic-people",
        "人员明细",
        "人员明细",
        new PocPreviewSettings(PocPreviewMode.Full, 20, false),
        new PocExportSettings(PocDataExportMode.Normal, 20_000, 500_000, 60, true, 1_000_000, 24),
        [new PocParameterDefinition(
            "GENDER_CODE",
            "人员类别",
            controlType,
            "Form",
            null,
            true,
            Options:
            [
                new PocValueOption("M", "男性条件"),
                new PocValueOption("F", "女性条件")
            ])],
        [new PocDatasetFieldDefinition("GENDER_CODE", "人员类别", "string")],
        [new PocFieldPresentationDefinition(
            "GENDER_CODE",
            [new PocValueOption("M", "男"), new PocValueOption("F", "女")])]);

    private static PocDataRow[] Rows() =>
    [
        new PocDataRow { ["GENDER_CODE"] = "F" },
        new PocDataRow { ["GENDER_CODE"] = "M" },
        new PocDataRow { ["GENDER_CODE"] = "F" }
    ];
}
