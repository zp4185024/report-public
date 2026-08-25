using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocDefaultExpressionEvaluatorTests
{
    private static readonly DateTimeOffset Current = new(2026, 8, 18, 14, 35, 20, TimeSpan.FromHours(8));

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

    [Theory]
    [InlineData("startOfDay(yesterday())", "2026-08-17 00:00:00")]
    [InlineData("endOfDay(yesterday())", "2026-08-17 23:59:59")]
    [InlineData("now()", "2026-08-18 14:35:20")]
    public void Evaluate_DateTimePresets_ReturnsExpectedDateTime(string expression, string expected)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate(expression, "datetime", Current);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("month", "2026-08")]
    [InlineData("year", "2026")]
    public void Evaluate_CalendarPeriod_ReturnsControlFormat(string dataType, string expected)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate("today()", dataType, Current);

        Assert.Equal(expected, actual);
    }
}
