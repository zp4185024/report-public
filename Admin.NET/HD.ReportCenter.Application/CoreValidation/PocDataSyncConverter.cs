using System.Data;
using System.Globalization;
using DynamicExpresso;
using Newtonsoft.Json;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 单个映射字段编译后的运行时形态：转换链委托已闭包所有参数，执行期零反射零表达式解析。
/// </summary>
/// <param name="SourceField">源数据集中的字段名（大小写不敏感匹配行键）。</param>
/// <param name="TargetField">目标库写入的列名。</param>
/// <param name="TargetType">目标列类型（string/number/decimal/datetime/bool），决定终值校验。</param>
/// <param name="Length">字符串目标列长度；null 表示建表时取方言默认值。</param>
/// <param name="Nullable">目标列是否允许空；false 时终值为 null 按失败行处理。</param>
/// <param name="Transform">编译完成的转换委托：原始值进、目标类型值出。</param>
/// <param name="IsKey">是否为 Upsert 冲突策略的合并键列；仅冲突策略为 Upsert 时被写入器消费。</param>
public sealed record PocDataSyncCompiledField(
    string SourceField,
    string TargetField,
    string TargetType,
    int? Length,
    bool Nullable,
    Func<object?, object?> Transform,
    bool IsKey = false);

/// <summary>
/// 字段转换链编译器：把映射里的转换器步骤编译成单一委托串。
/// 编译期完成语法与参数校验（表达式错误、未知转换器、缺参立即抛出），运行期单行失败抛
/// PocDataSyncTransformException 由执行器按失败行策略处理，整条链启动后零反射。
/// </summary>
public static class PocDataSyncConverter
{
    /// <summary>受支持的目标列类型集合；映射校验与终值归一都以它为白名单。</summary>
    public static readonly IReadOnlyList<string> TargetTypes =
        ["string", "number", "decimal", "datetime", "bool"];

    /// <summary>内置转换器名单：任务保存、包校验与编译器共用同一份白名单。</summary>
    public static readonly IReadOnlyList<string> SupportedConverters =
    [
        "toNumber", "toDecimal", "toDate", "toString", "trim",
        "emptyToNull", "nullToDefault", "required", "truncate", "dictionary", "expression"
    ];

    /// <summary>内置表达式解释器：变量只经 ParseAsDelegate 以 value 参数注入，不开放主机类型引用。</summary>
    private static readonly Interpreter ExpressionInterpreter = new();

    /// <summary>
    /// 编译一个映射字段：依次编译转换链，再追加目标类型终值校验与非空校验。
    /// 编译失败立即抛 PocDataSyncException（SYNC_MAPPING_INVALID），消息带字段名便于定位。
    /// </summary>
    /// <param name="mapping">待编译的字段映射定义。</param>
    /// <returns>可直接在执行器循环中调用的编译结果。</returns>
    public static PocDataSyncCompiledField CompileField(PocSyncColumnMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ValidateMappingShape(mapping);

        // 第一阶段：逐个编译转换步骤，闭包各自参数，形成值到值的委托链。
        Func<object?, object?> chain = BuildIdentityStep();
        foreach (var step in mapping.Converters)
        {
            var next = CompileStep(mapping.SourceField, step);
            var previous = chain;
            chain = value => next(previous(value));
        }

        // 第二阶段：追加目标类型终值校验，编译期锁死目标形态，运行期只做纯类型收敛。
        var targetType = NormalizeTargetType(mapping.TargetType);
        var length = mapping.Length;
        var field = mapping.SourceField;
        var typed = BuildTargetTypeStep(field, targetType, length);
        var nonNull = mapping.Nullable
            ? chain
            : BuildNonNullableStep(field, chain);
        return new PocDataSyncCompiledField(
            mapping.SourceField,
            mapping.TargetField,
            targetType,
            length,
            mapping.Nullable,
            value => typed(nonNull(value)),
            mapping.IsKey);
    }

    /// <summary>校验映射形状：源/目标字段、目标类型与转换器步骤的基础合法性。</summary>
    /// <param name="mapping">待校验的字段映射。</param>
    private static void ValidateMappingShape(PocSyncColumnMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.SourceField) ||
            string.IsNullOrWhiteSpace(mapping.TargetField) ||
            mapping.TargetField.Length > 128 ||
            !TargetTypes.Contains(
                mapping.TargetType.Trim(),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段映射无效：源字段 '{mapping.SourceField}' → 目标列 '{mapping.TargetField}'（类型 '{mapping.TargetType}'）不在支持范围内。");
        }
        if ((mapping.Length ?? 0) < 0)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"目标列 '{mapping.TargetField}' 的长度不能为负数。");
        }
    }

    /// <summary>返回直通委托，作为转换链起点，避免每字段额外判空分支。</summary>
    /// <returns>原样返回输入值的委托。</returns>
    private static Func<object?, object?> BuildIdentityStep() => value => value;

    /// <summary>把目标类型文本归一为白名单内的小写形态，非法类型在编译期拒绝。</summary>
    /// <param name="targetType">映射声明的目标类型。</param>
    /// <returns>归一化后的小写类型名。</returns>
    private static string NormalizeTargetType(string targetType) =>
        TargetTypes.First(item =>
            item.Equals(targetType.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>构造非空校验步骤：nullable=false 且终值为空时按失败行抛出。</summary>
    /// <param name="field">字段名，用于异常定位。</param>
    /// <param name="inner">前级转换委托。</param>
    /// <returns>包装后的委托。</returns>
    private static Func<object?, object?> BuildNonNullableStep(
        string field,
        Func<object?, object?> inner) =>
        value =>
        {
            var result = inner(value);
            if (result is null || result is DBNull)
            {
                throw new PocDataSyncTransformException(
                    field,
                    "该列配置为不允许空值，但转换结果为空。",
                    Describe(value));
            }
            return result;
        };

    /// <summary>构造目标类型终值校验步骤：把链尾输出收敛为 string/long/double/decimal/DateTime/bool。</summary>
    /// <param name="field">字段名，用于异常定位。</param>
    /// <param name="targetType">归一化后的目标类型。</param>
    /// <param name="length">字符串列长度上限；null 表示不限制。</param>
    /// <returns>终值校验委托。</returns>
    private static Func<object?, object?> BuildTargetTypeStep(
        string field,
        string targetType,
        int? length)
    {
        return targetType switch
        {
            "number" => value => NormalizeNumber(field, value),
            "decimal" => value => NormalizeDecimal(field, value, scale: null),
            "datetime" => value => NormalizeDateTime(field, value),
            "bool" => value => NormalizeBool(field, value),
            _ => value => NormalizeString(field, value, length)
        };
    }

    /// <summary>把转换步骤编译为委托；未知转换器或参数缺失在编译期即抛出。</summary>
    /// <param name="field">字段名，用于异常消息定位。</param>
    /// <param name="step">待编译的转换步骤定义。</param>
    /// <returns>编译完成的单步委托。</returns>
    private static Func<object?, object?> CompileStep(string field, PocSyncConverterStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (string.IsNullOrWhiteSpace(step.Type) ||
            !SupportedConverters.Contains(step.Type.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 '{step.Type}' 不是受支持的内置转换器。");
        }

        var args = step.Args ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var type = step.Type.Trim();
        return type.ToLowerInvariant() switch
        {
            "tonumber" => CompileToNumber(field),
            "todecimal" => CompileToDecimal(field, args),
            "todate" => CompileToDate(field, args),
            "tostring" => CompileToString(field),
            "trim" => CompileTrim(field),
            "emptytonull" => CompileEmptyToNull(field),
            "nulltodefault" => CompileNullToDefault(field, args),
            "required" => CompileRequired(field, args),
            "truncate" => CompileTruncate(field, args),
            "dictionary" => CompileDictionary(field, args),
            "expression" => CompileExpression(field, args),
            _ => throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 '{step.Type}' 不是受支持的内置转换器。")
        };
    }

    /// <summary>取参数值；参数缺失时抛带字段名的编译期异常。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数字典。</param>
    /// <param name="key">参数键。</param>
    /// <param name="converter">转换器名，用于错误消息。</param>
    /// <returns>非空参数文本。</returns>
    private static string RequireArg(
        string field,
        IReadOnlyDictionary<string, string?> args,
        string key,
        string converter)
    {
        if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 '{converter}' 缺少必填参数 '{key}'。");
        }
        return value.Trim();
    }

    /// <summary>编译 toNumber：文本或数值 → 整数优先（无小数部分）否则 double，解析失败按失败行抛出。</summary>
    /// <param name="field">字段名。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileToNumber(string field) =>
        value =>
        {
            if (value is null or DBNull)
            {
                return null;
            }
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integral))
            {
                return integral;
            }
            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var real))
            {
                return real;
            }
            throw new PocDataSyncTransformException(field, $"无法把 '{text}' 解析为数字。", text);
        };

    /// <summary>编译 toDecimal：按 scale 四舍五入到 decimal，解析失败按失败行抛出。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数（scale 可选，默认 2）。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileToDecimal(string field, IReadOnlyDictionary<string, string?> args)
    {
        var scale = ResolveScale(field, args, "toDecimal");
        return value =>
        {
            var normalized = NormalizeDecimal(field, value, scale);
            return normalized;
        };
    }

    /// <summary>解析 scale 参数：默认 2，允许 0..28。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数。</param>
    /// <param name="converter">转换器名。</param>
    /// <returns>合法的小数位。</returns>
    private static int ResolveScale(
        string field,
        IReadOnlyDictionary<string, string?> args,
        string converter)
    {
        if (!args.TryGetValue("scale", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return 2;
        }
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale) ||
            scale is < 0 or > 28)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 '{converter}' 参数 scale '{raw}' 必须是 0 到 28 的整数。");
        }
        return scale;
    }

    /// <summary>把链尾输出收敛为 decimal；输入为空返回空，非法文本抛失败行异常。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="value">输入值。</param>
    /// <param name="scale">小数位；null 表示不四舍五入。</param>
    /// <returns>decimal 终值或 null。</returns>
    private static object? NormalizeDecimal(string field, object? value, int? scale)
    {
        if (value is null or DBNull)
        {
            return null;
        }
        decimal decimalValue;
        if (value is decimal direct)
        {
            decimalValue = direct;
        }
        else if (value is bool)
        {
            throw new PocDataSyncTransformException(field, "布尔值不能转换为 decimal。", Describe(value));
        }
        else
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            if (!decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimalValue))
            {
                throw new PocDataSyncTransformException(field, $"无法把 '{text}' 解析为 decimal。", text);
            }
        }
        return scale is null ? decimalValue : Math.Round(decimalValue, scale.Value, MidpointRounding.AwayFromZero);
    }

    /// <summary>编译 toDate：按指定格式解析日期，成功返回 DateTime，失败按失败行抛出。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数（format 可选，默认 yyyy-MM-dd）。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileToDate(string field, IReadOnlyDictionary<string, string?> args)
    {
        var format = args.TryGetValue("format", out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : "yyyy-MM-dd";
        // 第一阶段：用多组样例字面量探测格式合法性，兼容带分隔符与时间部分的格式；
        // 格式错误属于编译期问题，启动即失败而不是留给数据撞出来。
        var formatProbes = new[]
        {
            "20000101", "2000-01-01", "2000/01/01",
            "20000101000000", "2000-01-01 00:00:00", "2000/01/01 00:00:00",
            "000000", "00:00:00"
        };
        var formatValid = false;
        foreach (var probe in formatProbes)
        {
            try
            {
                _ = DateTime.ParseExact(probe, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces);
                formatValid = true;
                break;
            }
            catch (FormatException)
            {
                // 逐一试探，全部失败才判定格式非法。
            }
        }
        if (!formatValid)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 toDate 的 format '{format}' 不是合法日期格式。");
        }

        // 第二阶段：生成运行期解析委托，空输入透传为空，非格式匹配文本按失败行抛出。
        return value =>
        {
            if (value is null or DBNull)
            {
                return null;
            }
            if (value is DateTime direct)
            {
                return direct;
            }
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            if (DateTime.TryParseExact(
                    text,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
            {
                return parsed;
            }
            throw new PocDataSyncTransformException(field, $"无法按格式 '{format}' 解析日期 '{text}'。", text);
        };
    }

    /// <summary>把链尾输出收敛为 DateTime；输入为空返回空，无法解析时抛失败行异常。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="value">输入值。</param>
    /// <returns>DateTime 终值或 null。</returns>
    private static object? NormalizeDateTime(string field, object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }
        if (value is DateTime direct)
        {
            return direct;
        }
        if (value is DateTimeOffset offset)
        {
            return offset.DateTime;
        }
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        // 常见业务日期形态显式枚举，其余交给宽松解析，保证 HIS 数字日期（如 20240101）可直接入库。
        var formats = new[]
        {
            "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd",
            "yyyyMMddHHmmss", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss"
        };
        if (DateTime.TryParseExact(
                text,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var exact))
        {
            return exact;
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed;
        }
        throw new PocDataSyncTransformException(field, $"无法把 '{text}' 解析为日期时间。", text);
    }

    /// <summary>编译 toString：统一转换为不变文化文本，空输入保持为空。</summary>
    /// <param name="field">字段名。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileToString(string field) =>
        value =>
        {
            if (value is null or DBNull)
            {
                return null;
            }
            return NormalizeString(field, value, length: null);
        };

    /// <summary>编译 trim：去除两端空白；非文本值先转文本再裁剪。</summary>
    /// <param name="field">字段名。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileTrim(string field) =>
        value =>
        {
            if (value is null or DBNull)
            {
                return null;
            }
            var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            return text?.Trim();
        };

    /// <summary>编译 emptyToNull：空白文本归一为 null，便于走数据库 NULL 而不是空串。</summary>
    /// <param name="field">字段名。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileEmptyToNull(string field) =>
        value =>
        {
            if (value is null or DBNull)
            {
                return null;
            }
            if (value is string text && string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            return value;
        };

    /// <summary>编译 nullToDefault：空值替换为参数 value 指定的默认文本。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数（value 必填）。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileNullToDefault(string field, IReadOnlyDictionary<string, string?> args)
    {
        var fallback = RequireArg(field, args, "value", "nullToDefault");
        return value =>
        {
            if (value is null or DBNull ||
                (value is string text && string.IsNullOrWhiteSpace(text)))
            {
                return fallback;
            }
            return value;
        };
    }

    /// <summary>编译 required：空值按策略处理——useDefault 填默认值，failRow 记失败行。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数（strategy 可选，defaultValue 用于 useDefault）。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileRequired(string field, IReadOnlyDictionary<string, string?> args)
    {
        var strategy = args.TryGetValue("strategy", out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : "failRow";
        var useDefault = strategy.Equals("useDefault", StringComparison.OrdinalIgnoreCase);
        var defaultValue = args.TryGetValue("defaultValue", out var fallbackRaw) ? fallbackRaw : null;
        if (useDefault && string.IsNullOrEmpty(defaultValue))
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 required 采用 useDefault 策略时必须提供 defaultValue。");
        }
        if (!useDefault && !strategy.Equals("failRow", StringComparison.OrdinalIgnoreCase))
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 required 的 strategy '{strategy}' 无效，仅支持 failRow 或 useDefault。");
        }
        return value =>
        {
            var isEmpty = value is null or DBNull ||
                (value is string text && string.IsNullOrWhiteSpace(text));
            if (!isEmpty)
            {
                return value;
            }
            if (useDefault)
            {
                return defaultValue;
            }
            throw new PocDataSyncTransformException(field, "必填字段取值为空。", Describe(value));
        };
    }

    /// <summary>编译 truncate：把文本截断到参数 length 指定的上限，防止超长写库失败。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数（length 必填）。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileTruncate(string field, IReadOnlyDictionary<string, string?> args)
    {
        var raw = RequireArg(field, args, "length", "truncate");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length <= 0)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 truncate 参数 length '{raw}' 必须是正整数。");
        }
        return value =>
        {
            if (value is null or DBNull)
            {
                return null;
            }
            var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
            if (text is null || text.Length <= length)
            {
                return value;
            }
            return text[..length];
        };
    }

    /// <summary>编译 dictionary：按 args.items（JSON 对象文本）构建源值→目标值映射表。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数（items 必填，形如 {"1":"男","2":"女"}）。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileDictionary(string field, IReadOnlyDictionary<string, string?> args)
    {
        var itemsJson = RequireArg(field, args, "items", "dictionary");
        Dictionary<string, string> table;
        try
        {
            table = JsonConvert.DeserializeObject<Dictionary<string, string>>(itemsJson)
                ?? throw new JsonException("items 反序列化为空。");
        }
        catch (JsonException exception)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的转换器 dictionary 的 items 不是合法 JSON 对象：{exception.Message}");
        }

        var lookup = new Dictionary<string, string>(table, StringComparer.Ordinal);
        return value =>
        {
            if (value is null or DBNull)
            {
                return null;
            }
            var key = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (lookup.TryGetValue(key, out var mapped))
            {
                return mapped;
            }
            throw new PocDataSyncTransformException(field, $"值 '{key}' 不在字典映射表中。", key);
        };
    }

    /// <summary>编译 expression：用受限解释器把表达式编译成以 value 为入参的委托。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="args">转换器参数（expression 必填）。</param>
    /// <returns>编译完成的委托。</returns>
    private static Func<object?, object?> CompileExpression(string field, IReadOnlyDictionary<string, string?> args)
    {
        var expressionText = RequireArg(field, args, "expression", "expression");
        try
        {
            // 编译期锁定 Func<object,object> 签名：表达式内可用 value 引用原始值，返回任意可转换对象。
            var compiled = ExpressionInterpreter.ParseAsDelegate<Func<object, object>>(
                expressionText,
                "value");
            return value =>
            {
                try
                {
                    // 空值按 null 传入表达式（object 形参可空），由表达式自行判空处理，不做 DBNull 替换。
                    return compiled(value!);
                }
                catch (Exception exception) when (exception is not PocDataSyncTransformException)
                {
                    throw new PocDataSyncTransformException(
                        field,
                        $"表达式执行失败：{exception.Message}",
                        Describe(value));
                }
            };
        }
        catch (DynamicExpresso.Exceptions.ParseException exception)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INVALID",
                $"字段 '{field}' 的表达式语法无效：{exception.Message}");
        }
    }

    /// <summary>把链尾输出收敛为文本；超出声明长度时抛失败行异常（与 truncate 转换器互补）。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="value">输入值。</param>
    /// <param name="length">长度上限；null 表示不限制。</param>
    /// <returns>文本终值或 null。</returns>
    private static object? NormalizeString(string field, object? value, int? length)
    {
        if (value is null or DBNull)
        {
            return null;
        }
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (length is > 0 && text.Length > length.Value)
        {
            throw new PocDataSyncTransformException(
                field,
                $"值长度 {text.Length} 超过目标列长度 {length.Value}，请先配置 truncate 转换器。",
                text.Length > 64 ? text[..64] : text);
        }
        return text;
    }

    /// <summary>把链尾输出收敛为整数优先的数字：整数值返回 long，否则 double；无法收敛抛失败行异常。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="value">输入值。</param>
    /// <returns>long/double 终值或 null。</returns>
    private static object? NormalizeNumber(string field, object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        // 第一阶段：数值原值直接收敛——整数型保持 long，浮点型保持 double，decimal 按有无小数分流。
        var numberType = value.GetType();
        if (numberType == typeof(long) || numberType == typeof(int) || numberType == typeof(short) ||
            numberType == typeof(byte) || numberType == typeof(sbyte))
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (numberType == typeof(double) || numberType == typeof(float))
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        if (numberType == typeof(decimal))
        {
            var raw = (decimal)value;
            // 无小数部分且不溢出时收敛为整数，写库更稳；否则保留 double 以支持浮点口径。
            if (decimal.Equals(raw, decimal.Truncate(raw)) &&
                raw is >= long.MinValue and <= long.MaxValue)
            {
                return (long)raw;
            }
            return (double)raw;
        }
        if (numberType == typeof(bool))
        {
            return (bool)value ? 1L : 0L;
        }

        // 第二阶段：文本值按整数优先解析，失败时按失败行抛出并携带原始文本。
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integral))
        {
            return integral;
        }
        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var real))
        {
            return real;
        }
        throw new PocDataSyncTransformException(field, $"无法把 '{text}' 收敛为数字目标类型。", text);
    }

    /// <summary>把链尾输出收敛为 bool：识别 1/0、true/false 与布尔原值，其余抛失败行异常。</summary>
    /// <param name="field">字段名。</param>
    /// <param name="value">输入值。</param>
    /// <returns>bool 终值或 null。</returns>
    private static object? NormalizeBool(string field, object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }
        if (value is bool direct)
        {
            return direct;
        }
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }
        if (text is "1" or "Y" or "y")
        {
            return true;
        }
        if (text is "0" or "N" or "n")
        {
            return false;
        }
        throw new PocDataSyncTransformException(field, $"无法把 '{text}' 收敛为布尔目标类型。", text);
    }

    /// <summary>把任意原始值压缩为短文本，供异常明细记录，超长截断到 64 字符。</summary>
    /// <param name="value">原始值。</param>
    /// <returns>可直接进 JSON 明细的短文本；null 保持 null。</returns>
    private static string? Describe(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.Length > 64 ? text[..64] : text;
    }
}
