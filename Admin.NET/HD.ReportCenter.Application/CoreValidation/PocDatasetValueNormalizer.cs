using System.Globalization;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 把数据库驱动返回的 CLR 或提供程序专用值归一化为 JSON 和 Excel 都能稳定表达的标量。
/// </summary>
internal static class PocDatasetValueNormalizer
{
    /// <summary>将 PocDatasetValueNormalizer 中的 Normalize 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="field">参与映射、显示或数据写入的字段集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>将 PocDatasetValueNormalizer 中的 Normalize Scalar 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>将 PocDatasetValueNormalizer 的 To Invariant Text 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ToInvariantText(object value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>将 PocDatasetValueNormalizer 中的 Parse Number 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="field">参与映射、显示或数据写入的字段集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>将 PocDatasetValueNormalizer 中的 Parse Boolean 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="field">参与映射、显示或数据写入的字段集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>负责 PocDatasetValueNormalizer 的 Type Mismatch 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="field">参与映射、显示或数据写入的字段集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDatasetExecutionException TypeMismatch(PocDatasetFieldDefinition field) =>
        new(
            "DATASET_FIELD_TYPE_MISMATCH",
            $"查询结果字段 '{field.Code}' 无法转换为配置的数据类型 '{field.DataType}'。");
}
