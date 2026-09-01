using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public static class PocDefaultExpressionEvaluator
{
    /// <summary>解析并计算 PocDefaultExpressionEvaluator 的 Evaluate 结果，处理必要的边界条件。</summary>
    /// <param name="expression">expression 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="now">now 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static string? Evaluate(
        string? expression,
        string dataType,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var current = now ?? DateTimeOffset.Now;
        var normalized = string.Concat(expression.Where(character => !char.IsWhiteSpace(character)))
            .ToLowerInvariant();
        // 第一阶段：构造 Evaluate 当前步骤需要的中间数据和执行参数。
        var value = normalized switch
        {
            "now()" => current,
            "today()" => StartOfDay(current),
            "yesterday()" => StartOfDay(current).AddDays(-1),
            "tomorrow()" => StartOfDay(current).AddDays(1),
            "startofday(now())" => StartOfDay(current),
            "endofday(today())" => StartOfDay(current).AddDays(1).AddTicks(-1),
            "startofday(yesterday())" => StartOfDay(current).AddDays(-1),
            "endofday(yesterday())" => StartOfDay(current).AddTicks(-1),
            "startofday(tomorrow())" => StartOfDay(current).AddDays(1),
            "endofday(tomorrow())" => StartOfDay(current).AddDays(2).AddTicks(-1),
            "startofmonth(today())" => new DateTimeOffset(
                current.Year,
                current.Month,
                1,
                0,
                0,
                0,
                current.Offset),
            "endofmonth(today())" => new DateTimeOffset(
                    current.Year,
                    current.Month,
                    1,
                    0,
                    0,
                    0,
                    current.Offset)
                .AddMonths(1)
                .AddTicks(-1),
            "startofyear(today())" => new DateTimeOffset(
                current.Year,
                1,
                1,
                0,
                0,
                0,
                current.Offset),
            "endofyear(today())" => new DateTimeOffset(
                    current.Year,
                    1,
                    1,
                    0,
                    0,
                    0,
                    current.Offset)
                .AddYears(1)
                .AddTicks(-1),
            _ => throw new PocDatasetConfigurationException(
                "DEFAULT_EXPRESSION_INVALID",
                $"不支持默认表达式 '{expression}'。")
        };

        // 第2阶段：整理 Evaluate 已计算的数据，并按方法契约返回最终结果。
        return dataType.Trim().ToLowerInvariant() switch
        {
            "date" => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "month" => value.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "year" => value.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>启动 PocDefaultExpressionEvaluator 的 Start Of Day 流程，并建立运行期间所需状态。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static DateTimeOffset StartOfDay(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
}
