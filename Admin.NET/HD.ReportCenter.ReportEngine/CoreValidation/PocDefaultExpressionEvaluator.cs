using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public static class PocDefaultExpressionEvaluator
{
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
        var value = normalized switch
        {
            "now()" => current,
            "today()" => StartOfDay(current),
            "startofday(now())" => StartOfDay(current),
            "endofday(today())" => StartOfDay(current).AddDays(1).AddTicks(-1),
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

        return dataType.Equals("date", StringComparison.OrdinalIgnoreCase)
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
}
