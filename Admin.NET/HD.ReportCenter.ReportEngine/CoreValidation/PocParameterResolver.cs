using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public sealed class PocParameterResolutionException : Exception
{
    /// <summary>初始化 PocParameterResolutionException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="parameterCode">需要读取、校验或转换的业务值。</param>
    public PocParameterResolutionException(string code, string parameterCode)
        : base($"{code}: {parameterCode}")
    {
        Code = code;
        ParameterCode = parameterCode;
    }

    public string Code { get; }
    public string ParameterCode { get; }
}

public sealed record PocResolvedParameterSet(
    PocResolvedParameters DisplayValues,
    IReadOnlyDictionary<string, object?> DatabaseValues);

/// <summary>
/// 把浏览器字符串输入收敛为已定义参数。未知键被忽略，Fixed/System 参数不接受客户端覆盖。
/// </summary>
public static class PocParameterResolver
{
    /// <summary>解析并计算 PocParameterResolver 的 Resolve 结果，处理必要的边界条件。</summary>
    /// <param name="definitions">definitions 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="trustedInput">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static PocResolvedParameterSet Resolve(
        IReadOnlyList<PocParameterDefinition> definitions,
        IReadOnlyDictionary<string, string?>? input,
        IReadOnlyDictionary<string, string?>? trustedInput = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        input ??= new Dictionary<string, string?>();
        trustedInput ??= new Dictionary<string, string?>();

        var displayValues = new PocResolvedParameters();
        var databaseValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        // 第一阶段：构造 Resolve 当前步骤需要的中间数据和执行参数。
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.OrderBy(item => item.Code, StringComparer.Ordinal))
        {
            ValidateDefinition(definition, codes);
            var externalValue = definition.SourceType.Equals(
                "External",
                StringComparison.OrdinalIgnoreCase);
            var acceptsClientValue =
                !definition.SourceType.Equals("Fixed", StringComparison.OrdinalIgnoreCase) &&
                !definition.SourceType.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                !externalValue;
            var rawValue = externalValue && trustedInput.TryGetValue(definition.Code, out var resolved)
                ? resolved
                : acceptsClientValue && input.TryGetValue(definition.Code, out var supplied)
                    ? supplied
                    : null;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                rawValue = definition.SourceType.Equals("Fixed", StringComparison.OrdinalIgnoreCase)
                    ? definition.FixedValue ?? definition.DefaultValue
                    : definition.DefaultValue ?? definition.FixedValue;
            }

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                if (definition.Required)
                {
                    throw new PocParameterResolutionException("PARAM_REQUIRED", definition.Code);
                }

                // 第2阶段：根据 Resolve 当前分支转换输入数据，并保存该阶段产生的中间结果。
                displayValues[definition.Code] = string.Empty;
                databaseValues[definition.Code] = DBNull.Value;
                continue;
            }

            try
            {
                var normalizedValue = NormalizeControlValue(definition.ControlType, rawValue.Trim());
                var (display, database) = ConvertValue(definition.DataType, normalizedValue);
                displayValues[definition.Code] = display;
                databaseValues[definition.Code] = database;
            }
            catch (FormatException)
            {
                throw new PocParameterResolutionException("PARAM_INVALID", definition.Code);
            }
            catch (OverflowException)
            {
                throw new PocParameterResolutionException("PARAM_INVALID", definition.Code);
            }
        }

        return new PocResolvedParameterSet(displayValues, databaseValues);
    }

    /// <summary>将 PocParameterResolver 中的 Convert Value 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static (string Display, object Database) ConvertValue(string dataType, string value)
    {
        switch (dataType.Trim().ToLowerInvariant())
        {
            case "string":
            case "text":
                // 第一阶段：整理 ConvertValue 已计算的数据，并按方法契约返回最终结果。
                return (value, value);
            case "long":
            case "int64":
                var longValue = long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return (longValue.ToString(CultureInfo.InvariantCulture), longValue);
            case "int":
            case "int32":
            case "number":
                var intValue = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return (intValue.ToString(CultureInfo.InvariantCulture), intValue);
            case "decimal":
                var decimalValue = decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
                return (decimalValue.ToString(CultureInfo.InvariantCulture), decimalValue);
            case "date":
                var dateValue = DateTime.ParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                return (dateValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dateValue.Date);
            case "datetime":
                // 第2阶段：构造 ConvertValue 当前步骤需要的中间数据和执行参数。
                var dateTimeValue = DateTime.Parse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal);
                return (
                    dateTimeValue.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    dateTimeValue);
            case "bool":
            case "boolean":
                var boolValue = value switch
                {
                    "1" => true,
                    "0" => false,
                    _ => bool.Parse(value)
                };
                return (boolValue ? "true" : "false", boolValue);
            default:
                throw new FormatException($"Unsupported parameter data type '{dataType}'.");
        }
    }

    /// <summary>将 PocParameterResolver 中的 Normalize Control Value 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="controlType">controlType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string NormalizeControlValue(string controlType, string value)
    {
        switch (controlType.Trim().ToLowerInvariant())
        {
            case "date":
                // 第一阶段：整理 NormalizeControlValue 已计算的数据，并按方法契约返回最终结果。
                return DateTime.ParseExact(
                        value,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case "datetime":
                var dateTime = DateTime.ParseExact(
                    value,
                    [
                        "yyyy-MM-dd'T'HH:mm",
                        "yyyy-MM-dd'T'HH:mm:ss",
                        "yyyy-MM-dd HH:mm",
                        "yyyy-MM-dd HH:mm:ss"
                    ],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                return dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case "month":
                // 第2阶段：整理 NormalizeControlValue 已计算的数据，并按方法契约返回最终结果。
                return DateTime.ParseExact(
                        value,
                        "yyyy-MM",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None)
                    .ToString("yyyy-MM", CultureInfo.InvariantCulture);
            case "year":
                if (value.Length != 4 ||
                    !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
                    year is < 1 or > 9999)
                {
                    throw new FormatException("Year parameters must use yyyy format.");
                }
                return year.ToString("D4", CultureInfo.InvariantCulture);
            default:
                return value;
        }
    }

    /// <summary>校验 PocParameterResolver 的 Validate Definition 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <param name="codes">codes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void ValidateDefinition(
        PocParameterDefinition definition,
        ISet<string> codes)
    {
        if (string.IsNullOrWhiteSpace(definition.Code) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                definition.Code,
                "^[A-Za-z][A-Za-z0-9_]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !codes.Add(definition.Code))
        {
            throw new PocParameterResolutionException("PARAM_DEFINITION_INVALID", definition.Code);
        }
    }
}
