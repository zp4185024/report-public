using System.Globalization;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 把数据库驱动返回的 CLR 或提供程序专用值归一化为 JSON 和 Excel 都能稳定表达的标量。
/// </summary>
internal static class PocDatasetValueNormalizer
{
    public static object? Normalize(object value, PocDatasetFieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var scalar = NormalizeScalar(value);
        if (scalar is null)
        {
            return null;
        }

        return field.DataType.Trim().ToLowerInvariant() switch
        {
            "number" or "int" or "long" or "decimal" => ParseNumber(ToInvariantText(scalar), field),
            "bool" or "boolean" => scalar is bool boolean
                ? boolean
                : ParseBoolean(ToInvariantText(scalar), field),
            "string" or "text" => ToInvariantText(scalar),
            _ => scalar
        };
    }

    private static object? NormalizeScalar(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid guid => guid.ToString("D"),
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        DateTime dateTime => dateTime,
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string ToInvariantText(object value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal ParseNumber(string value, PocDatasetFieldDefinition field)
    {
        if (decimal.TryParse(
                value,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number;
        }

        throw TypeMismatch(field);
    }

    private static bool ParseBoolean(string value, PocDatasetFieldDefinition field)
    {
        if (bool.TryParse(value, out var boolean))
        {
            return boolean;
        }
        if (value == "1")
        {
            return true;
        }
        if (value == "0")
        {
            return false;
        }

        throw TypeMismatch(field);
    }

    private static PocDatasetExecutionException TypeMismatch(PocDatasetFieldDefinition field) =>
        new(
            "DATASET_FIELD_TYPE_MISMATCH",
            $"查询结果字段 '{field.Code}' 无法转换为配置的数据类型 '{field.DataType}'。");
}
