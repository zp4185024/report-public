using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocDefaultExpressionEvaluatorTests
{
    private static readonly DateTimeOffset Current = new(2026, 8, 18, 14, 35, 20, TimeSpan.FromHours(8));

    /// <summary>验证 Evaluate Date Presets Returns Expected Date 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="expression">expression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("today()", "2026-08-18")]
    [InlineData("yesterday()", "2026-08-17")]
    [InlineData("tomorrow()", "2026-08-19")]
    [InlineData("startOfMonth(today())", "2026-08-01")]
    [InlineData("endOfMonth(today())", "2026-08-31")]
    public void Evaluate_DatePresets_ReturnsExpectedDate(string expression, string expected)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate(expression, "date", Current);

        Assert.Equal(expected, actual);
    }

    /// <summary>验证 Evaluate Date Time Presets Returns Expected Date Time 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="expression">expression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("startOfDay(yesterday())", "2026-08-17 00:00:00")]
    [InlineData("endOfDay(yesterday())", "2026-08-17 23:59:59")]
    [InlineData("now()", "2026-08-18 14:35:20")]
    public void Evaluate_DateTimePresets_ReturnsExpectedDateTime(string expression, string expected)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate(expression, "datetime", Current);

        Assert.Equal(expected, actual);
    }

    /// <summary>验证 Evaluate Calendar Period Returns Control Format 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("month", "2026-08")]
    [InlineData("year", "2026")]
    public void Evaluate_CalendarPeriod_ReturnsControlFormat(string dataType, string expected)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate("today()", dataType, Current);

        Assert.Equal(expected, actual);
    }
}
