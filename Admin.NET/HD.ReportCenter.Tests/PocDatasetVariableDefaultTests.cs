using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 数据集变量默认值链路测试：ValidateDataset 的默认表达式合法性校验（仅日期类变量、
/// 长度上限、保存即试算）与 ResolveVariableFallback 的兜底取值优先级（表达式现算 >
/// 固定默认值 > null 走必填校验）——这是数据集预览与同步取数窗口共用的兜底语义。
/// </summary>
public sealed class PocDatasetVariableDefaultTests
{
    private static readonly DateTimeOffset Current = new(2026, 8, 18, 14, 35, 20, TimeSpan.FromHours(8));

    /// <summary>构造一份通过基础校验的最小数据集文档：单变量与 SQL 引用一一对应。</summary>
    /// <param name="variable">待测变量定义。</param>
    /// <returns>可直接交给 ValidateDataset 的数据集文档。</returns>
    private static PocDatasetDocument Document(PocDatasetVariableDefinition variable) =>
        new(
            "ds_window",
            "窗口演示数据集",
            "src_demo",
            "SELECT id FROM demo WHERE visit_date >= @startDate",
            [variable],
            [new PocDatasetFieldDefinition("id", "标识", "string")],
            30,
            10_000,
            Enabled: true,
            0);

    /// <summary>验证日期类变量的合法滚动表达式（嵌套偏移）能通过保存校验。</summary>
    /// <param name="expression">待校验的默认表达式。</param>
    [Theory]
    [InlineData("today()")]
    [InlineData("startOfMonth(addMonths(today(),-1))")]
    [InlineData("addDays(today(),-7)")]
    public void ValidateDataset_accepts_date_expression_on_date_variable(string expression)
    {
        var variable = new PocDatasetVariableDefinition(
            "startDate", "窗口开始", "date", false, DefaultExpression: expression);

        PocDataAssetService.ValidateDataset(Document(variable));
    }

    /// <summary>验证默认表达式只允许日期类变量：字符串变量配置表达式按变量定义无效拒绝。</summary>
    [Fact]
    public void ValidateDataset_rejects_expression_on_non_date_variable()
    {
        var variable = new PocDatasetVariableDefinition(
            "startDate", "窗口开始", "string", false, DefaultExpression: "today()");

        var exception = Assert.Throws<PocDatasetConfigurationException>(
            () => PocDataAssetService.ValidateDataset(Document(variable)));

        Assert.Equal("DATASET_VARIABLE_INVALID", exception.Code);
    }

    /// <summary>验证非法表达式在保存校验即被试算拒绝，错误码沿用求值器便于定位。</summary>
    [Fact]
    public void ValidateDataset_rejects_malformed_expression()
    {
        var variable = new PocDatasetVariableDefinition(
            "startDate", "窗口开始", "date", false, DefaultExpression: "startoflastmonth()");

        var exception = Assert.Throws<PocDatasetConfigurationException>(
            () => PocDataAssetService.ValidateDataset(Document(variable)));

        Assert.Equal("DEFAULT_EXPRESSION_INVALID", exception.Code);
    }

    /// <summary>验证默认值与表达式的长度上限：固定默认值超 1024、表达式超 256 都拒绝落库。</summary>
    [Fact]
    public void ValidateDataset_rejects_oversized_defaults()
    {
        var oversizedValue = new PocDatasetVariableDefinition(
            "startDate", "窗口开始", "date", false, DefaultValue: new string('a', 1025));
        var oversizedExpression = new PocDatasetVariableDefinition(
            "startDate", "窗口开始", "date", false, DefaultExpression: $"addDays(today(),{new string('1', 250)})");

        Assert.Throws<PocDatasetConfigurationException>(
            () => PocDataAssetService.ValidateDataset(Document(oversizedValue)));
        Assert.Throws<PocDatasetConfigurationException>(
            () => PocDataAssetService.ValidateDataset(Document(oversizedExpression)));
    }

    /// <summary>
    /// 验证兜底取值优先级：表达式现算结果优先于固定默认值。求值器内部取真实当前时间，
    /// 期望值用同一表达式对拍得出，只验证“表达式覆盖固定值”的优先级而不钉死日期。
    /// </summary>
    [Fact]
    public void ResolveVariableFallback_prefers_evaluated_expression()
    {
        var variable = new PocDatasetVariableDefinition(
            "startDate",
            "窗口开始",
            "date",
            false,
            DefaultValue: "2020-01-01",
            DefaultExpression: "yesterday()");

        var actual = PocDataAssetService.ResolveVariableFallback(variable);
        var expected = PocDefaultExpressionEvaluator.Evaluate("yesterday()", "date");

        Assert.Equal(expected, actual);
        Assert.NotEqual("2020-01-01", actual);
    }

    /// <summary>验证无表达式时回落固定默认值，两者皆无时返回 null 交由必填校验兜底。</summary>
    /// <param name="defaultValue">固定默认值（null 表示未配置）。</param>
    /// <param name="expected">期望的兜底结果。</param>
    [Theory]
    [InlineData("2020-01-01", "2020-01-01")]
    [InlineData(null, null)]
    public void ResolveVariableFallback_falls_back_to_fixed_value(string? defaultValue, string? expected)
    {
        var variable = new PocDatasetVariableDefinition(
            "startDate", "窗口开始", "date", false, DefaultValue: defaultValue);

        var actual = PocDataAssetService.ResolveVariableFallback(variable);

        Assert.Equal(expected, actual);
    }
}
