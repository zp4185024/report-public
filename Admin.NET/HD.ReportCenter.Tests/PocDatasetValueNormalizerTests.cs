using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocDatasetValueNormalizerTests
{
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

    [Fact]
    public void Normalize_ConvertsClrIntegerToDecimal()
    {
        var field = new PocDatasetFieldDefinition("VISIT_ID", "就诊标识", "number");

        var result = PocDatasetValueNormalizer.Normalize(10000165072L, field);

        Assert.Equal(10000165072M, Assert.IsType<decimal>(result));
    }

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

    [Fact]
    public void Normalize_PreservesTextAndNull()
    {
        var field = new PocDatasetFieldDefinition("USER_NAME", "姓名", "string");

        Assert.Equal("张三", PocDatasetValueNormalizer.Normalize(new ProviderValue("张三"), field));
        Assert.Null(PocDatasetValueNormalizer.Normalize(DBNull.Value, field));
    }

    [Fact]
    public void Normalize_ConvertsClrNumberToConfiguredText()
    {
        var field = new PocDatasetFieldDefinition("CODE", "编码", "string");

        var result = PocDatasetValueNormalizer.Normalize(1001L, field);

        Assert.Equal("1001", Assert.IsType<string>(result));
    }

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
        public override string ToString() => value;
    }
}
