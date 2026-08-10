using System.Text;
using System.Text.RegularExpressions;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public sealed class PocDatasetConfigurationException : Exception
{
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

    public static IReadOnlyList<string> Validate(
        string queryText,
        IEnumerable<string> parameterCodes,
        PocPreviewMode previewMode)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw Unsafe("数据集 SQL 不能为空。");
        }

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

    private static string Sanitize(string queryText)
    {
        var output = new StringBuilder(queryText.Length);
        var state = SqlLexState.Normal;
        for (var index = 0; index < queryText.Length; index++)
        {
            var current = queryText[index];
            var next = index + 1 < queryText.Length ? queryText[index + 1] : '\0';

            switch (state)
            {
                case SqlLexState.Normal:
                    if ((current == '-' && next == '-') || (current == '/' && next == '*'))
                    {
                        throw Unsafe("数据集 SQL 不允许注释。");
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

    private static bool sanitizedSystemVariable(StringBuilder output) =>
        output.ToString().Contains("@@", StringComparison.Ordinal);

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

    private static PocDatasetConfigurationException Unsafe(string message) =>
        new("DATASET_QUERY_NOT_READ_ONLY", message);

    private enum SqlLexState
    {
        Normal,
        String,
        QuotedIdentifier,
        BracketIdentifier
    }

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("(?<!@)@(?<code>[A-Za-z][A-Za-z0-9_]{0,63})", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterRegex();
}
