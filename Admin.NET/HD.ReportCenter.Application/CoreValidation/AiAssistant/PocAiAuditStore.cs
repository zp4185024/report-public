using HD.ReportCenter.Application.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>AI 工具调用审计存储：每次工具调用写一行 RcAiAuditLog，SQL 与错误摘要在写入前截断。</summary>
public sealed class PocAiAuditStore : ITransient
{
    /// <summary>审计 SQL 文本的截断长度；与建表脚本 RcAiAuditLog.SqlText 列宽一致。</summary>
    public const int SqlTextMaxLength = 2000;

    /// <summary>审计错误摘要的截断长度；与建表脚本 RcAiAuditLog.ErrorSummary 列宽一致。</summary>
    public const int ErrorSummaryMaxLength = 512;

    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocAiAuditStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="database">报表中心主库的 SqlSugar 客户端。</param>
    public PocAiAuditStore(ISqlSugarClient database)
    {
        _database = database;
    }

    /// <summary>写入一次工具调用的审计行；审计失败不影响会话主流程，异常只上报给日志层调用方自行决定。</summary>
    /// <param name="sessionId">AI 会话编号。</param>
    /// <param name="result">工具执行结果（含 SQL、行数、耗时与错误摘要）。</param>
    public async Task WriteAsync(string sessionId, PocAiToolExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        await _database.Insertable(new PocAiAuditLog
        {
            Time = DateTime.Now,
            SessionId = Truncate(sessionId, 64) ?? string.Empty,
            ToolName = Truncate(result.ToolName, 64) ?? string.Empty,
            DataSourceCode = Truncate(result.DataSourceCode, 64),
            SqlText = Truncate(result.SqlText, SqlTextMaxLength),
            RowCount = result.RowCount,
            DurationMs = result.DurationMs,
            Success = result.Success,
            ErrorSummary = Truncate(result.ErrorSummary, ErrorSummaryMaxLength)
        }).ExecuteCommandAsync();
    }

    /// <summary>按长度截断文本；空值原样返回 null，超长值截断到指定上限。</summary>
    /// <param name="value">原文。</param>
    /// <param name="maxLength">最大保留长度。</param>
    /// <returns>截断后的文本。</returns>
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
