using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocDatasetValueNormalizerTests
{
    /// <summary>验证 Normalize Converts Provider Numeric Text To Decimal 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("12345678901", "12345678901")]
    [InlineData("12.5", "12.5")]
    [InlineData("1.25E+3", "1250")]
    public void Normalize_ConvertsProviderNumericTextToDecimal(string input, string expected)
    {
        var field = new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "number");

        var result = PocDatasetValueNormalizer.Normalize(new ProviderValue(input), field);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    /// <summary>验证 Normalize Converts Clr Integer To Decimal 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_ConvertsClrIntegerToDecimal()
    {
        var field = new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "number");

        var result = PocDatasetValueNormalizer.Normalize(10000165072L, field);

        Assert.Equal(10000165072M, Assert.IsType<decimal>(result));
    }

    /// <summary>验证 Normalize Converts Provider Boolean Text 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Normalize_ConvertsProviderBooleanText(string input, bool expected)
    {
        var field = new PocDatasetFieldDefinition("ENABLED", "启用", "bool");

        var result = PocDatasetValueNormalizer.Normalize(new ProviderValue(input), field);

        Assert.Equal(expected, result);
    }

    /// <summary>验证 Normalize Preserves Text And Null 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_PreservesTextAndNull()
    {
        var field = new PocDatasetFieldDefinition("USER_NAME", "姓名", "string");

        Assert.Equal("张三", PocDatasetValueNormalizer.Normalize(new ProviderValue("张三"), field));
        Assert.Null(PocDatasetValueNormalizer.Normalize(DBNull.Value, field));
    }

    /// <summary>验证 Normalize Converts Clr Number To Configured Text 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_ConvertsClrNumberToConfiguredText()
    {
        var field = new PocDatasetFieldDefinition("CODE", "编码", "string");

        var result = PocDatasetValueNormalizer.Normalize(1001L, field);

        Assert.Equal("1001", Assert.IsType<string>(result));
    }

    /// <summary>验证 Normalize Rejects Provider Value That Violates Number Contract 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normalize_RejectsProviderValueThatViolatesNumberContract()
    {
        var field = new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "number");

        var exception = Assert.Throws<PocDatasetExecutionException>(
            () => PocDatasetValueNormalizer.Normalize(new ProviderValue("not-a-number"), field));

        Assert.Equal("DATASET_FIELD_TYPE_MISMATCH", exception.Code);
        Assert.Contains("VISIT_ID", exception.Message);
    }

    private sealed class ProviderValue(string value)
    {
        /// <summary>将 PocDatasetValueNormalizerTests 的当前值转换为稳定文本，供日志或界面展示使用。</summary>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public override string ToString() => value;
    }
}
