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

    /// <summary>验证 Evaluate Rolling Window Returns Expected Value 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="expression">expression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("startOfMonth(addMonths(today(),-1))", "date", "2026-07-01")]
    [InlineData("endOfMonth(addMonths(today(),-1))", "date", "2026-07-31")]
    [InlineData("addMonths(today(),-1)", "month", "2026-07")]
    [InlineData("addYears(today(),-1)", "year", "2025")]
    [InlineData("addDays(today(),-7)", "date", "2026-08-11")]
    [InlineData("startOfYear(addYears(today(),-1))", "date", "2025-01-01")]
    [InlineData("endOfYear(addYears(today(),-1))", "date", "2025-12-31")]
    [InlineData("startOfDay(addDays(today(),-7))", "datetime", "2026-08-11 00:00:00")]
    [InlineData("endOfDay(addDays(today(),-1))", "datetime", "2026-08-17 23:59:59")]
    [InlineData("addDays(today(),-30)", "date", "2026-07-19")]
    public void Evaluate_RollingWindow_ReturnsExpectedValue(
        string expression,
        string dataType,
        string expected)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate(expression, dataType, Current);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证周粒度函数按"周一为一周之始"求值：基准日 2026-08-18 是周二，
    /// 本周周一=08-17、本周周日=08-23；回退一周得上周一 08-10。
    /// </summary>
    /// <param name="expression">expression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("startofweek(today())", "2026-08-17")]
    [InlineData("endofweek(today())", "2026-08-23")]
    [InlineData("startofweek(adddays(today(),-7))", "2026-08-10")]
    [InlineData("endofweek(adddays(today(),-7))", "2026-08-16")]
    public void Evaluate_WeekFunctions_UseMondayAsWeekStart(string expression, string expected)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate(expression, "date", Current);

        Assert.Equal(expected, actual);
    }

    /// <summary>验证嵌套滚动表达式与历史词表在大小写、内部空白混杂时求值一致（兼容旧配置原文）。</summary>
    [Fact]
    public void Evaluate_IsInsensitiveToCaseAndWhitespace()
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate(
            "StartOfMonth( AddMonths( Today() , -1 ) )",
            "date",
            Current);

        Assert.Equal("2026-07-01", actual);
    }

    /// <summary>验证空白表达式返回 null，交由固定值或必填校验兜底。</summary>
    /// <param name="expression">expression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_BlankExpression_ReturnsNull(string? expression)
    {
        var actual = PocDefaultExpressionEvaluator.Evaluate(expression, "date", Current);

        Assert.Null(actual);
    }

    /// <summary>验证 Evaluate Rejects Malformed Expression 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="expression">expression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("now")]
    [InlineData("foo()")]
    [InlineData("today()today()")]
    [InlineData("adddays(today())")]
    [InlineData("adddays(today(),x)")]
    [InlineData("adddays(today(),1,2)")]
    [InlineData("startofmonth()")]
    [InlineData("today")]
    public void Evaluate_MalformedExpression_ThrowsInvalid(string expression)
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(
            () => PocDefaultExpressionEvaluator.Evaluate(expression, "date", Current));

        Assert.Equal("DEFAULT_EXPRESSION_INVALID", exception.Code);
    }
}
