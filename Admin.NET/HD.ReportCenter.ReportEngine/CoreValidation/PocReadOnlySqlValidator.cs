using System.Text;
using System.Text.RegularExpressions;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public sealed class PocDatasetConfigurationException : Exception
{
    /// <summary>初始化 PocDatasetConfigurationException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocDatasetConfigurationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// SQL 文本的附加保护。最终安全边界仍是只读数据库账号、超时和最大行数。
/// </summary>
public static partial class PocReadOnlySqlValidator
{
    private static readonly HashSet<string> ForbiddenTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER", "BACKUP", "BULK", "CREATE", "DBCC", "DELETE", "DENY", "DROP",
        "EXEC", "EXECUTE", "GRANT", "INSERT", "INTO", "MERGE", "OPENQUERY",
        "OPENROWSET", "OPENDATASOURCE", "RECONFIGURE", "RESTORE", "REVOKE",
        "TRUNCATE", "UPDATE", "USE"
    };

    /// <summary>校验 PocReadOnlySqlValidator 的 Validate 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <param name="parameterCodes">parameterCodes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="previewMode">previewMode 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static IReadOnlyList<string> Validate(
        string queryText,
        IEnumerable<string> parameterCodes,
        PocPreviewMode previewMode)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw Unsafe("数据集 SQL 不能为空。");
        }

        // 第一阶段：构造 Validate 当前步骤需要的中间数据和执行参数。
        var sanitized = Sanitize(queryText);
        var tokens = TokenRegex().Matches(sanitized)
            .Select(match => match.Value)
            .ToArray();
        if (tokens.Length == 0 ||
            (!tokens[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase) &&
             !tokens[0].Equals("WITH", StringComparison.OrdinalIgnoreCase)) ||
            tokens.Any(ForbiddenTokens.Contains))
        {
            throw Unsafe("数据集只允许单条只读 SELECT 或 WITH 查询。");
        }

        if (previewMode == PocPreviewMode.Paged && !ContainsOrderBy(tokens))
        {
            throw new PocDatasetConfigurationException(
                "PAGING_ORDER_REQUIRED",
                "分页数据集必须包含稳定的 ORDER BY。" );
        }

        var knownCodes = new HashSet<string>(parameterCodes, StringComparer.OrdinalIgnoreCase);
        var referenced = new List<string>();
        var referencedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 第2阶段：遍历 Validate 当前数据集合，逐项转换并维护处理结果。
        foreach (Match match in ParameterRegex().Matches(sanitized))
        {
            var code = match.Groups["code"].Value;
            if (!knownCodes.Contains(code))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_PARAMETER_UNDEFINED",
                    $"SQL 参数 '@{code}' 没有对应的报表参数定义。" );
            }
            if (referencedCodes.Add(code))
            {
                referenced.Add(knownCodes.First(item => item.Equals(code, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return referenced;
    }

    /// <summary>按 PocReadOnlySqlValidator 的安全规则清洗输入文本，同时保留后续解析需要的字符位置。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string Sanitize(string queryText)
    {
        var output = new StringBuilder(queryText.Length);
        var state = SqlLexState.Normal;
        for (var index = 0; index < queryText.Length; index++)
        {
            var current = queryText[index];
            var next = index + 1 < queryText.Length ? queryText[index + 1] : '\0';

            // 第一阶段：检查 Sanitize 当前条件，并按匹配分支执行对应处理。
            switch (state)
            {
                case SqlLexState.Normal:
                    if (current == '-' && next == '-')
                    {
                        output.Append("  ");
                        index += 2;
                        while (index < queryText.Length &&
                               queryText[index] is not '\r' and not '\n')
                        {
                            output.Append(' ');
                            index++;
                        }
                        continue;
                    }
                    if (current == '/' && next == '*')
                    {
                        var executableComment = index + 2 < queryText.Length &&
                            (queryText[index + 2] == '!' ||
                             (queryText[index + 2] is 'M' or 'm' &&
                              index + 3 < queryText.Length &&
                              queryText[index + 3] == '!'));
                        if (executableComment)
                        {
                            throw Unsafe("数据集 SQL 不允许 MySQL 可执行注释。");
                        }
                        output.Append("  ");
                        index += 2;
                        var closed = false;
                        while (index < queryText.Length)
                        {
                            // 第2阶段：更新 Sanitize 当前分支对应的业务状态，并保持关联数据一致。
                            current = queryText[index];
                            next = index + 1 < queryText.Length ? queryText[index + 1] : '\0';
                            if (current == '*' && next == '/')
                            {
                                output.Append("  ");
                                index++;
                                closed = true;
                                break;
                            }
                            output.Append(current is '\r' or '\n' ? current : ' ');
                            index++;
                        }
                        if (!closed)
                        {
                            throw Unsafe("数据集 SQL 包含未闭合的块注释。");
                        }
                        continue;
                    }
                    if (current == ';' || current == '#')
                    {
                        throw Unsafe("数据集 SQL 不允许多语句或临时表。");
                    }
                    if (current == '\'' || current == '"' || current == '[')
                    {
                        state = current switch
                        {
                            '\'' => SqlLexState.String,
                            '"' => SqlLexState.QuotedIdentifier,
                            _ => SqlLexState.BracketIdentifier
                        };
                        output.Append(' ');
                        // 第3阶段：根据 Sanitize 当前分支转换输入数据，并保存该阶段产生的中间结果。
                        continue;
                    }
                    output.Append(current);
                    break;

                case SqlLexState.String:
                    output.Append(' ');
                    if (current == '\'' && next == '\'')
                    {
                        output.Append(' ');
                        index++;
                    }
                    else if (current == '\'')
                    {
                        state = SqlLexState.Normal;
                    }
                    break;

                case SqlLexState.QuotedIdentifier:
                    output.Append(' ');
                    if (current == '"' && next == '"')
                    {
                        output.Append(' ');
                        index++;
                    }
                    else if (current == '"')
                    {
                        state = SqlLexState.Normal;
                    }
                    break;

                case SqlLexState.BracketIdentifier:
                    // 第4阶段：根据 Sanitize 当前分支转换输入数据，并保存该阶段产生的中间结果。
                    output.Append(' ');
                    if (current == ']' && next == ']')
                    {
                        output.Append(' ');
                        index++;
                    }
                    else if (current == ']')
                    {
                        state = SqlLexState.Normal;
                    }
                    break;
            }
        }

        if (state != SqlLexState.Normal)
        {
            throw Unsafe("数据集 SQL 包含未闭合的字符串或标识符。");
        }
        if (sanitizedSystemVariable(output))
        {
            throw Unsafe("数据集 SQL 不允许服务器级变量。");
        }
        return output.ToString();
    }

    /// <summary>负责 PocReadOnlySqlValidator 的 sanitized System Variable 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="output">output 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool sanitizedSystemVariable(StringBuilder output) =>
        output.ToString().Contains("@@", StringComparison.Ordinal);

    /// <summary>尝试或判断 PocReadOnlySqlValidator 的 Contains Order By 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="tokens">tokens 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool ContainsOrderBy(IReadOnlyList<string> tokens)
    {
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (tokens[index].Equals("ORDER", StringComparison.OrdinalIgnoreCase) &&
                tokens[index + 1].Equals("BY", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>构造 PocReadOnlySqlValidator 使用的只读安全异常，并附带稳定错误编码。</summary>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDatasetConfigurationException Unsafe(string message) =>
        new("DATASET_QUERY_NOT_READ_ONLY", message);

    private enum SqlLexState
    {
        Normal,
        String,
        QuotedIdentifier,
        BracketIdentifier
    }

    /// <summary>负责 PocReadOnlySqlValidator 的 Token Regex 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>负责 PocReadOnlySqlValidator 的 Parameter Regex 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [GeneratedRegex("(?<!@)@(?<code>[A-Za-z][A-Za-z0-9_]{0,63})", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterRegex();
}
