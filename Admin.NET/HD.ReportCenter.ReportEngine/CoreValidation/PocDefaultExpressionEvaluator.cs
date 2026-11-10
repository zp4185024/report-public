using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>
/// 日期类默认表达式求值器：封闭白名单的函数文法，支持一层嵌套组合与整数偏移，
/// 例如 <c>startofmonth(addmonths(today(),-1))</c> 表示"上月第一天"、<c>adddays(today(),-7)</c>
/// 表示"七天前"、<c>startofweek(adddays(today(),-7))</c> 表示"上周周一"（周按周一为一周之始）。
/// 每次求值都基于传入的基准时间现算，保证同一表达式随时间滚动——
/// 预览、导出、同步运行各自在发生时刻求值，而不是落库一个固定日期。
/// 词表只增不改：历史上按精确匹配支持的旧表达式在新文法下结果完全一致；
/// 非法表达式抛 DEFAULT_EXPRESSION_INVALID，配置保存时的试算与运行期求值共用同一入口。
/// 前端镜像实现见 ReportWeb/src/defaultExpressions.ts，新增函数必须两端同步。
/// </summary>
public static class PocDefaultExpressionEvaluator
{
    /// <summary>
    /// 求值默认表达式：空白表达式返回 null（交由固定值或必填校验兜底），
    /// 其余按函数文法基于基准时间现算，并按参数数据类型格式化为控件可接受的文本。
    /// </summary>
    /// <param name="expression">默认表达式原文，如 startOfMonth(today())；大小写与内部空白不敏感。</param>
    /// <param name="dataType">参数数据类型；date/month/year 分别格式化为 yyyy-MM-dd/yyyy-MM/yyyy，其余按 datetime 输出。</param>
    /// <param name="now">求值基准时间；缺省取当前时刻，测试与导入试算可显式传入固定时间。</param>
    /// <returns>格式化后的默认值文本；表达式为空白时返回 null。</returns>
    /// <exception cref="PocDatasetConfigurationException">表达式不满足函数文法（未知函数、参数个数或类型不符、多余内容）时抛出，错误码固定为 DEFAULT_EXPRESSION_INVALID。</exception>
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
        // 第一阶段：剔除全部空白并整体小写——文法不含字符串字面量，这样做与历史精确匹配行为一致。
        var normalized = string.Concat(expression.Where(character => !char.IsWhiteSpace(character)))
            .ToLowerInvariant();
        var parser = new ExpressionParser(normalized, expression.Trim(), current);
        var value = parser.ParseExpression();
        parser.ExpectEnd();

        // 第2阶段：按参数数据类型格式化输出，格式必须与对应控件的可输入格式一致。
        return dataType.Trim().ToLowerInvariant() switch
        {
            "date" => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "month" => value.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "year" => value.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>把时间截断到当天零点，保留原时区偏移，是各区间截断函数的公共底座。</summary>
    /// <param name="value">待截断的时间。</param>
    /// <returns>当天 00:00:00（保留原 Offset）。</returns>
    private static DateTimeOffset StartOfDay(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);

    /// <summary>把时间截断到当月一号零点，保留原时区偏移，供 startofmonth/endofmonth 使用。</summary>
    /// <param name="value">待截断的时间。</param>
    /// <returns>当月 1 日 00:00:00（保留原 Offset）。</returns>
    private static DateTimeOffset StartOfMonth(DateTimeOffset value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, value.Offset);

    /// <summary>把时间截断到当年一月一号零点，保留原时区偏移，供 startofyear/endofyear 使用。</summary>
    /// <param name="value">待截断的时间。</param>
    /// <returns>当年 1 月 1 日 00:00:00（保留原 Offset）。</returns>
    private static DateTimeOffset StartOfYear(DateTimeOffset value) =>
        new(value.Year, 1, 1, 0, 0, 0, value.Offset);

    /// <summary>
    /// 把时间回退到所在周的周一零点，保留原时区偏移，供 startofweek/endofweek 使用。
    /// 周按中国习惯以周一为一周之始：DayOfWeek 里 Monday=1、Sunday=0，
    /// 统一换算为"距周一的天数"（周一 0、周日 6）后回退。
    /// </summary>
    /// <param name="value">待截断的时间。</param>
    /// <returns>所在周周一 00:00:00（保留原 Offset）。</returns>
    private static DateTimeOffset StartOfWeek(DateTimeOffset value) =>
        StartOfDay(value).AddDays(((int)value.DayOfWeek + 6) % 7 * -1);

    /// <summary>
    /// 默认表达式文法的递归下降解析器：词法仅含小写标识符、括号、逗号与整数，
    /// 函数表封闭（基础时刻、区间截断、日/月/年偏移三类），不存在任意表达式执行面。
    /// 解析与求值同步进行；任何不满足文法的位置都抛 DEFAULT_EXPRESSION_INVALID。
    /// </summary>
    /// <param name="text">已剔除空白并小写的规范化表达式文本。</param>
    /// <param name="source">用户输入的原始表达式，仅用于拼装错误消息。</param>
    /// <param name="now">求值基准时间。</param>
    private sealed class ExpressionParser(string text, string source, DateTimeOffset now)
    {
        private readonly string _text = text;
        private readonly string _source = source;
        private readonly DateTimeOffset _now = now;
        private int _position;

        /// <summary>解析并求值一个表达式（函数调用）；嵌套参数在对应分支内递归进入本方法。</summary>
        /// <returns>该子表达式基于基准时间算出的时间值。</returns>
        public DateTimeOffset ParseExpression()
        {
            // 第一阶段：读取函数名与左括号，随后按封闭白名单分发到各函数的参数形态。
            var name = ReadIdentifier();
            Expect('(');
            DateTimeOffset value;
            switch (name)
            {
                case "now":
                    value = _now;
                    break;
                case "today":
                    value = StartOfDay(_now);
                    break;
                case "yesterday":
                    value = StartOfDay(_now).AddDays(-1);
                    break;
                case "tomorrow":
                    value = StartOfDay(_now).AddDays(1);
                    break;
                case "startofday":
                    value = StartOfDay(ParseSingleArgument(name));
                    break;
                case "endofday":
                    value = StartOfDay(ParseSingleArgument(name)).AddDays(1).AddTicks(-1);
                    break;
                case "startofmonth":
                    value = StartOfMonth(ParseSingleArgument(name));
                    break;
                case "endofmonth":
                    value = StartOfMonth(ParseSingleArgument(name)).AddMonths(1).AddTicks(-1);
                    break;
                case "startofweek":
                    value = StartOfWeek(ParseSingleArgument(name));
                    break;
                case "endofweek":
                    value = StartOfWeek(ParseSingleArgument(name)).AddDays(7).AddTicks(-1);
                    break;
                case "startofyear":
                    value = StartOfYear(ParseSingleArgument(name));
                    break;
                case "endofyear":
                    value = StartOfYear(ParseSingleArgument(name)).AddYears(1).AddTicks(-1);
                    break;
                case "adddays":
                    var (daysBase, daysOffset) = ParseOffsetArguments(name);
                    value = daysBase.AddDays(daysOffset);
                    break;
                case "addmonths":
                    var (monthsBase, monthsOffset) = ParseOffsetArguments(name);
                    value = monthsBase.AddMonths(monthsOffset);
                    break;
                case "addyears":
                    var (yearsBase, yearsOffset) = ParseOffsetArguments(name);
                    value = yearsBase.AddYears(yearsOffset);
                    break;
                default:
                    throw Invalid($"不支持的函数 '{name}'。");
            }

            // 第2阶段：闭合右括号；参数多余或缺失在此处统一暴露为文法错误。
            Expect(')');
            return value;
        }

        /// <summary>顶层解析结束后要求输入已耗尽，拒绝"today()today()"之类的多余尾部内容。</summary>
        public void ExpectEnd()
        {
            if (_position < _text.Length)
            {
                throw Invalid($"表达式末尾存在多余内容 '{_text[_position..]}'。");
            }
        }

        /// <summary>解析恰好一个时间型参数（区间截断函数）；多余的参数会在外层括号校验处暴露。</summary>
        /// <param name="name">当前函数名，用于错误消息定位。</param>
        /// <returns>参数子表达式的求值结果。</returns>
        private DateTimeOffset ParseSingleArgument(string name)
        {
            if (Peek() == ')')
            {
                throw Invalid($"函数 '{name}' 需要一个时间参数。");
            }

            return ParseExpression();
        }

        /// <summary>解析"时间参数, 整数偏移"两个参数（日/月/年偏移函数）。</summary>
        /// <param name="name">当前函数名，用于错误消息定位。</param>
        /// <returns>基准时间与整数偏移量。</returns>
        private (DateTimeOffset Base, int Offset) ParseOffsetArguments(string name)
        {
            if (Peek() == ')')
            {
                throw Invalid($"函数 '{name}' 需要时间参数和整数偏移两个参数。");
            }

            var baseValue = ParseExpression();
            Expect(',');
            return (baseValue, ReadInteger(name));
        }

        /// <summary>读取一个小写字母开头的标识符（函数名）。</summary>
        /// <returns>标识符文本。</returns>
        private string ReadIdentifier()
        {
            if (_position >= _text.Length || !char.IsLetter(_text[_position]))
            {
                throw Invalid("此处应为函数名。");
            }

            var start = _position;
            while (_position < _text.Length &&
                   (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
            {
                _position++;
            }

            return _text[start.._position];
        }

        /// <summary>读取一个整数常量（允许负号），供偏移函数的第二个参数使用。</summary>
        /// <param name="name">当前函数名，用于错误消息定位。</param>
        /// <returns>解析出的整数偏移量。</returns>
        private int ReadInteger(string name)
        {
            var start = _position;
            if (_position < _text.Length && _text[_position] == '-')
            {
                _position++;
            }

            while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
            {
                _position++;
            }

            if (_position == start ||
                (_position == start + 1 && _text[start] == '-') ||
                !int.TryParse(_text[start.._position], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                throw Invalid($"函数 '{name}' 的偏移量必须是整数常量。");
            }

            return value;
        }

        /// <summary>消费下一个字符，不匹配则抛文法错误（含原始表达式便于定位）。</summary>
        /// <param name="character">期望出现的字符。</param>
        private void Expect(char character)
        {
            if (_position >= _text.Length || _text[_position] != character)
            {
                throw Invalid(character == '('
                    ? "函数名后缺少左括号。"
                    : $"此处应为 '{character}'。");
            }

            _position++;
        }

        /// <summary>只读查看当前位置字符；耗尽时返回右括号，使参数缺失统一走括号校验报错。</summary>
        /// <returns>当前位置字符；输入耗尽时返回 ')'。</returns>
        private char Peek() => _position < _text.Length ? _text[_position] : ')';

        /// <summary>构造统一的配置异常：错误码固定 DEFAULT_EXPRESSION_INVALID，消息带原始表达式。</summary>
        /// <param name="detail">具体文法错误描述。</param>
        /// <returns>可直接抛出的配置异常。</returns>
        private PocDatasetConfigurationException Invalid(string detail) =>
            new("DEFAULT_EXPRESSION_INVALID", $"不支持默认表达式 '{_source}'：{detail}");
    }
}
