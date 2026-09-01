using System.Data;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocParameterResolverTests
{
    /// <summary>验证 Resolve Uses Only Definitions And Creates Typed Database Values 场景下的预期行为，并防止相关逻辑回归。</summary>
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

    /// <summary>验证 Resolve Ignores Client Value For Fixed And System Parameters 场景下的预期行为，并防止相关逻辑回归。</summary>
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

    /// <summary>验证 Resolve Only Accepts External Values From Trusted Input 场景下的预期行为，并防止相关逻辑回归。</summary>
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

    /// <summary>验证 Resolve Unifies Page Url And External Values For Template And Sql Consumers 场景下的预期行为，并防止相关逻辑回归。</summary>
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

    /// <summary>验证 Resolve Url Department Id Creates Named Sql Parameter With Url Value 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Resolve_UrlDepartmentId_CreatesNamedSqlParameterWithUrlValue()
    {
        var definitions = new[]
        {
            Parameter("hisDepartmentId", "string", required: true, sourceType: "Url")
        };
        var input = new Dictionary<string, string?>
        {
            ["hisDepartmentId"] = "D03"
        };

        var resolved = PocParameterResolver.Resolve(definitions, input);
        var referenced = PocReadOnlySqlValidator.Validate(
            "SELECT @hisDepartmentId AS URL_DEPARTMENT_ID FROM DUAL",
            definitions.Select(item => item.Code),
            PocPreviewMode.Full);
        var sqlParameter = PocDatabaseProvider.CreateParameter(
            "hisDepartmentId",
            definitions[0].DataType,
            resolved.DatabaseValues["hisDepartmentId"]);

        Assert.Contains("hisDepartmentId", referenced, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("@hisDepartmentId", sqlParameter.ParameterName);
        Assert.Equal("D03", sqlParameter.Value);
        Assert.Equal(DbType.String, sqlParameter.DbType);
    }

    /// <summary>验证 Resolve Rejects Required External Value When Trusted Input Is Missing 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Resolve_RejectsRequiredExternalValueWhenTrustedInputIsMissing()
    {
        var exception = Assert.Throws<PocParameterResolutionException>(() =>
            PocParameterResolver.Resolve(
                [Parameter("hisUserName", "string", required: true, sourceType: "External")],
                new Dictionary<string, string?> { ["hisUserName"] = "伪造用户" }));

        Assert.Equal("PARAM_REQUIRED", exception.Code);
    }

    /// <summary>验证 Resolve Rejects Missing Required Parameter 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Resolve_RejectsMissingRequiredParameter()
    {
        var exception = Assert.Throws<PocParameterResolutionException>(() =>
            PocParameterResolver.Resolve(
                [Parameter("hospitalId", "string", required: true)],
                new Dictionary<string, string?>()));

        Assert.Equal("PARAM_REQUIRED", exception.Code);
    }

    /// <summary>验证 Resolve Rejects Invalid Typed Value 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Resolve_RejectsInvalidTypedValue()
    {
        var exception = Assert.Throws<PocParameterResolutionException>(() =>
            PocParameterResolver.Resolve(
                [Parameter("startDate", "date")],
                new Dictionary<string, string?> { ["startDate"] = "not-a-date" }));

        Assert.Equal("PARAM_INVALID", exception.Code);
    }

    /// <summary>验证 Resolve Normalizes Calendar Control Formats 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="controlType">controlType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("date", "string", "2026-08-24", "2026-08-24")]
    [InlineData("datetime", "string", "2026-08-24T10:30", "2026-08-24 10:30:00")]
    [InlineData("month", "string", "2026-08", "2026-08")]
    [InlineData("year", "string", "2026", "2026")]
    public void Resolve_NormalizesCalendarControlFormats(
        string controlType,
        string dataType,
        string value,
        string expected)
    {
        var resolved = PocParameterResolver.Resolve(
            [Parameter("period", dataType, controlType: controlType)],
            new Dictionary<string, string?> { ["period"] = value });

        Assert.Equal(expected, resolved.DisplayValues["period"]);
        Assert.Equal(expected, resolved.DatabaseValues["period"]);
    }

    /// <summary>验证 Resolve Rejects Values Outside Calendar Control Format 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="controlType">controlType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    [Theory]
    [InlineData("date", "2026-08")]
    [InlineData("datetime", "2026-08-24")]
    [InlineData("month", "2026-08-24")]
    [InlineData("year", "26")]
    public void Resolve_RejectsValuesOutsideCalendarControlFormat(string controlType, string value)
    {
        var exception = Assert.Throws<PocParameterResolutionException>(() =>
            PocParameterResolver.Resolve(
                [Parameter("period", "string", controlType: controlType)],
                new Dictionary<string, string?> { ["period"] = value }));

        Assert.Equal("PARAM_INVALID", exception.Code);
    }

    /// <summary>负责 PocParameterResolverTests 的 Parameter 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="required">required 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sourceType">sourceType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="defaultValue">需要读取、校验或转换的业务值。</param>
    /// <param name="fixedValue">需要读取、校验或转换的业务值。</param>
    /// <param name="controlType">controlType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocParameterDefinition Parameter(
        string code,
        string dataType,
        bool required = false,
        string sourceType = "Form",
        string? defaultValue = null,
        string? fixedValue = null,
        string? controlType = null) =>
        new(
            code,
            code,
            controlType ?? (dataType == "date" ? "date" : "text"),
            sourceType,
            defaultValue,
            true,
            dataType,
            FixedValue: fixedValue,
            Required: required);
}
