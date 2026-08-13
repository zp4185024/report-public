using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocParameterResolverTests
{
    [Fact]
    public void Resolve_UsesOnlyDefinitionsAndCreatesTypedDatabaseValues()
    {
        var definitions = new[]
        {
            Parameter("hospitalId", "string", required: true),
            Parameter("startDate", "date", defaultValue: "2026-08-01"),
            Parameter("includeDisabled", "bool", defaultValue: "false")
        };
        var request = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hospitalId"] = " H01 ",
            ["unknown"] = "must-not-flow-to-sql"
        };

        var resolved = PocParameterResolver.Resolve(definitions, request);

        Assert.Equal("H01", resolved.DisplayValues["hospitalId"]);
        Assert.Equal("2026-08-01", resolved.DisplayValues["startDate"]);
        Assert.Equal("false", resolved.DisplayValues["includeDisabled"]);
        Assert.DoesNotContain("unknown", resolved.DatabaseValues.Keys);
        Assert.Equal("H01", resolved.DatabaseValues["hospitalId"]);
        Assert.Equal(new DateTime(2026, 8, 1), resolved.DatabaseValues["startDate"]);
        Assert.Equal(false, resolved.DatabaseValues["includeDisabled"]);
    }

    [Fact]
    public void Resolve_IgnoresClientValueForFixedAndSystemParameters()
    {
        var definitions = new[]
        {
            Parameter("tenant", "string", sourceType: "Fixed", fixedValue: "internal"),
            Parameter("today", "date", sourceType: "System", defaultValue: "2026-08-10")
        };
        var request = new Dictionary<string, string?>
        {
            ["tenant"] = "attacker-value",
            ["today"] = "1999-01-01"
        };

        var resolved = PocParameterResolver.Resolve(definitions, request);

        Assert.Equal("internal", resolved.DisplayValues["tenant"]);
        Assert.Equal("2026-08-10", resolved.DisplayValues["today"]);
    }

    [Fact]
    public void Resolve_OnlyAcceptsExternalValuesFromTrustedInput()
    {
        var definitions = new[]
        {
            Parameter("hisOrganizeId", "string", required: true, sourceType: "External")
        };
        var clientInput = new Dictionary<string, string?>
        {
            ["hisOrganizeId"] = "forged-organ"
        };
        var trustedInput = new Dictionary<string, string?>
        {
            ["hisOrganizeId"] = "organ1001"
        };

        var resolved = PocParameterResolver.Resolve(definitions, clientInput, trustedInput);

        Assert.Equal("organ1001", resolved.DisplayValues["hisOrganizeId"]);
        Assert.Equal("organ1001", resolved.DatabaseValues["hisOrganizeId"]);
    }

    [Fact]
    public void Resolve_UnifiesPageUrlAndExternalValuesForTemplateAndSqlConsumers()
    {
        var definitions = new[]
        {
            Parameter("queryDate", "date", required: true, sourceType: "Url"),
            Parameter("hisOrganizeName", "string", sourceType: "External"),
            Parameter("hisDepartmentName", "string", sourceType: "External"),
            Parameter("hisUserName", "string", sourceType: "External")
        };
        var pageOrUrlInput = new Dictionary<string, string?>
        {
            ["queryDate"] = "2026-08-13"
        };
        var externalInput = new Dictionary<string, string?>
        {
            ["hisOrganizeName"] = "锦州市凌河区医院",
            ["hisDepartmentName"] = "财务科",
            ["hisUserName"] = "super"
        };

        var resolved = PocParameterResolver.Resolve(
            definitions,
            pageOrUrlInput,
            externalInput);

        Assert.Equal("2026-08-13", resolved.DisplayValues["queryDate"]);
        Assert.Equal("锦州市凌河区医院", resolved.DisplayValues["hisOrganizeName"]);
        Assert.Equal("财务科", resolved.DisplayValues["hisDepartmentName"]);
        Assert.Equal("super", resolved.DisplayValues["hisUserName"]);
        Assert.Equal(new DateTime(2026, 8, 13), resolved.DatabaseValues["queryDate"]);
        Assert.Equal("锦州市凌河区医院", resolved.DatabaseValues["hisOrganizeName"]);
        Assert.Equal("财务科", resolved.DatabaseValues["hisDepartmentName"]);
        Assert.Equal("super", resolved.DatabaseValues["hisUserName"]);
    }

    [Fact]
    public void Resolve_RejectsRequiredExternalValueWhenTrustedInputIsMissing()
    {
        var exception = Assert.Throws<PocParameterResolutionException>(() =>
            PocParameterResolver.Resolve(
                [Parameter("hisUserName", "string", required: true, sourceType: "External")],
                new Dictionary<string, string?> { ["hisUserName"] = "伪造用户" }));

        Assert.Equal("PARAM_REQUIRED", exception.Code);
    }

    [Fact]
    public void Resolve_RejectsMissingRequiredParameter()
    {
        var exception = Assert.Throws<PocParameterResolutionException>(() =>
            PocParameterResolver.Resolve(
                [Parameter("hospitalId", "string", required: true)],
                new Dictionary<string, string?>()));

        Assert.Equal("PARAM_REQUIRED", exception.Code);
    }

    [Fact]
    public void Resolve_RejectsInvalidTypedValue()
    {
        var exception = Assert.Throws<PocParameterResolutionException>(() =>
            PocParameterResolver.Resolve(
                [Parameter("startDate", "date")],
                new Dictionary<string, string?> { ["startDate"] = "not-a-date" }));

        Assert.Equal("PARAM_INVALID", exception.Code);
    }

    private static PocParameterDefinition Parameter(
        string code,
        string dataType,
        bool required = false,
        string sourceType = "Form",
        string? defaultValue = null,
        string? fixedValue = null) =>
        new(
            code,
            code,
            dataType == "date" ? "date" : "text",
            sourceType,
            defaultValue,
            true,
            dataType,
            FixedValue: fixedValue,
            Required: required);
}
