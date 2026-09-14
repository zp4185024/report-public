using System.Data.Common;
using System.Globalization;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 把数据库驱动返回的 CLR 或提供程序专用值归一化为 JSON 和 Excel 都能稳定表达的标量。
/// </summary>
internal static class PocDatasetValueNormalizer
{
    /// <summary>
    /// 从数据读取器当前行按列序号安全取值。Oracle NUMBER 超出 .NET decimal 范围时，驱动在
    /// GetValue 内部转 decimal 会抛 InvalidCastException（内层 OverflowException）；此处降级为
    /// 提供程序专用值的文本形式（如 OracleDecimal 的全精度字符串），保证单行超大数值不会让整个查询失败。
    /// </summary>
    /// <param name="reader">正在遍历结果集的数据读取器，调用前必须已 Read 到当前行。</param>
    /// <param name="ordinal">目标列序号，由调用方按字段解析结果传入。</param>
    /// <returns>归一化前的原始列值；列为数据库 NULL 时返回 null；溢出且连文本兜底也失败时返回 null，避免整报表中断。</returns>
    public static object? ReadColumnValue(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetValue(ordinal);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException)
        {
            // 溢出场景改走提供程序专用值并转文本；仍失败则置空，单元格缺失优于整张报表执行失败。
            try
            {
                return reader.IsDBNull(ordinal)
                    ? null
                    : Convert.ToString(reader.GetProviderSpecificValue(ordinal), CultureInfo.InvariantCulture);
            }
            catch (Exception fallback) when (fallback is InvalidCastException or OverflowException)
            {
                return null;
            }
        }
    }
    /// <summary>
    /// 把单个数据库列值按字段契约归一化为 JSON/Excel 可稳定表达的对象；ReadColumnValue 溢出兜底
    /// 可能返回 null，直接映射为空单元格，与数据库 NULL 同等处理。
    /// </summary>
    /// <param name="value">数据库列原始值，允许为 null（视为数据库 NULL）。</param>
    /// <param name="field">目标字段定义，决定数值/布尔/文本的转换方式。</param>
    /// <returns>归一化后的标量值；输入为 null 时返回 null。</returns>
    public static object? Normalize(object? value, PocDatasetFieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (value is null)
        {
            return null;
        }

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

    /// <summary>
    /// 把数值文本按字段契约解析为 decimal。文本是合法数值但超出 decimal 表示范围
    /// （如 Oracle NUMBER 的超大 SUM 值）时降级为原始字符串返回，与 ReadColumnValue 的
    /// 溢出兜底衔接；只有确实不是数值时才抛字段类型不匹配异常。
    /// </summary>
    /// <param name="value">数据库列的数值文本表示。</param>
    /// <param name="field">目标字段定义，用于异常信息定位。</param>
    /// <returns>范围内的 decimal；超出 decimal 范围的合法数值返回原始字符串。</returns>
    private static object ParseNumber(string value, PocDatasetFieldDefinition field)
    {
        if (decimal.TryParse(
                value,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number;
        }

        // 数值文本有效但超出 decimal 范围（double 可解析）时降级为字符串，避免单格超大数值导致整张报表失败。
        if (double.TryParse(
                value,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out _))
        {
            return value;
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
