using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public sealed class PocParameterResolutionException : Exception
{
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

    private static (string Display, object Database) ConvertValue(string dataType, string value)
    {
        switch (dataType.Trim().ToLowerInvariant())
        {
            case "string":
            case "text":
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

    private static string NormalizeControlValue(string controlType, string value)
    {
        switch (controlType.Trim().ToLowerInvariant())
        {
            case "date":
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
