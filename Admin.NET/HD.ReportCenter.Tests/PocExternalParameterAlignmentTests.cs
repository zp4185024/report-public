using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocExternalParameterAlignmentTests
{
    private static readonly PocExternalParameterSourceDefinition His = new(
        "his",
        "HIS 登录信息",
        true,
        [
            new("hisUserName", "人员姓名", "string", "content.userName"),
            new("hisOrganizeId", "医院ID", "string", "content.organId"),
            new("hisOrganizeName", "医院名称", "string", "content.organName")
        ],
        ["token"]);

    /// <summary>验证 Align External Parameters Overrides Sql Parameter And Adds Template Fields 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void AlignExternalParameters_OverridesSqlParameterAndAddsTemplateFields()
    {
        var stored = new[]
        {
            Parameter("hisOrganizeId", "旧医院参数", "Form"),
            Parameter("PATIENT_SEX", "性别", "Form")
        };
        var variables = new[]
        {
            new PocDatasetVariableDefinition(
                "hisOrganizeId",
                "医院ID",
                "string",
                true,
                "External",
                "his",
                "hisOrganizeId")
        };

        var aligned = PocReportConfigurationService.AlignExternalParameters(
            stored,
            variables,
            [His]);

        var sqlParameter = aligned.Single(item => item.Code == "hisOrganizeId");
        Assert.Equal("External", sqlParameter.SourceType);
        Assert.Equal("his", sqlParameter.SourceCode);
        Assert.Equal("hisOrganizeId", sqlParameter.SourceFieldCode);
        Assert.False(sqlParameter.Visible);
        Assert.True(sqlParameter.ReadOnly);
        Assert.False(sqlParameter.AllowUrlPrefill);
        Assert.True(sqlParameter.Required);
        Assert.Contains(aligned, item =>
            item.Code == "hisUserName" && item.SourceFieldCode == "hisUserName");
        Assert.Contains(aligned, item =>
            item.Code == "hisOrganizeName" && item.SourceFieldCode == "hisOrganizeName");
        Assert.Equal("Form", aligned.Single(item => item.Code == "PATIENT_SEX").SourceType);
    }

    /// <summary>验证 Align External Parameters Does Not Add Source Fields Without External Dataset Binding 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void AlignExternalParameters_DoesNotAddSourceFieldsWithoutExternalDatasetBinding()
    {
        var parameters = new[] { Parameter("deptId", "科室", "System") };
        var variables = new[]
        {
            new PocDatasetVariableDefinition("deptId", "科室", "string", false, "Form")
        };

        var aligned = PocReportConfigurationService.AlignExternalParameters(
            parameters,
            variables,
            [His]);

        Assert.Single(aligned);
        Assert.Equal("System", aligned[0].SourceType);
    }

    /// <summary>验证 Align External Parameters Keeps External Binding When Source Definition Is Missing 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void AlignExternalParameters_KeepsExternalBindingWhenSourceDefinitionIsMissing()
    {
        var parameters = new[]
        {
            Parameter("hisOrganizeId", "医院标识", "Form")
        };
        var variables = new[]
        {
            new PocDatasetVariableDefinition(
                "hisOrganizeId",
                "医院标识",
                "string",
                true,
                "External",
                "his",
                "hisOrganizeId")
        };

        var aligned = PocReportConfigurationService.AlignExternalParameters(
            parameters,
            variables,
            []);

        var parameter = Assert.Single(aligned);
        Assert.Equal("External", parameter.SourceType);
        Assert.True(parameter.ReadOnly);
        Assert.Equal("his", parameter.SourceCode);
        Assert.Equal("hisOrganizeId", parameter.SourceFieldCode);
    }

    /// <summary>负责 PocExternalParameterAlignmentTests 的 Parameter 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="displayName">需要读取、校验或转换的业务值。</param>
    /// <param name="sourceType">sourceType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocParameterDefinition Parameter(
        string code,
        string displayName,
        string sourceType) =>
        new(code, displayName, "text", sourceType, null, true);
}
