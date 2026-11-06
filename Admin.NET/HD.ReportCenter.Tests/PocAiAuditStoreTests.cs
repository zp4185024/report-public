using HD.ReportCenter.Application.CoreValidation.AiAssistant;

namespace HD.ReportCenter.Tests;

/// <summary>AI 工具审计存储单测：SQL 与错误摘要按列宽截断落库，成功与失败都保留完整元数据。</summary>
public sealed class PocAiAuditStoreTests
{
    /// <summary>验证审计写入：字段一一对应；超长 SQL 截断到 2000、错误摘要截断到 512。</summary>
    [Fact]
    public async Task WriteAsync_truncates_sql_and_error_summary()
    {
        using var host = new PocAiTestHost();
        var longSql = string.Concat(Enumerable.Repeat("SELECT 1 UNION ALL ", 400));
        var longError = string.Concat(Enumerable.Repeat("数据库错误详情-", 100));

        await host.AuditStore.WriteAsync("session-trunc", new PocAiToolExecutionResult(
            PocAiToolRegistry.RunReadonlySql,
            "ai_main",
            longSql,
            42,
            123,
            false,
            longError,
            "{}"));

        var rows = await host.Database.Queryable<PocAiAuditLog>()
            .Where(item => item.SessionId == "session-trunc")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(PocAiToolRegistry.RunReadonlySql, row.ToolName);
        Assert.Equal("ai_main", row.DataSourceCode);
        Assert.Equal(PocAiAuditStore.SqlTextMaxLength, row.SqlText!.Length);
        Assert.True(longSql.StartsWith(row.SqlText[..100], StringComparison.Ordinal));
        Assert.Equal(PocAiAuditStore.ErrorSummaryMaxLength, row.ErrorSummary!.Length);
        Assert.StartsWith("数据库错误详情-", row.ErrorSummary);
        Assert.Equal(42, row.RowCount);
        Assert.Equal(123, row.DurationMs);
        Assert.False(row.Success);
        Assert.True(row.Time <= DateTime.Now.AddSeconds(1), "审计时间应是写入时刻附近的服务器本地时间。");
    }

    /// <summary>验证空值写入：无 SQL 的工具（如 list_data_sources）落库时 SQL 与数据源字段为空。</summary>
    [Fact]
    public async Task WriteAsync_allows_null_sql_and_data_source()
    {
        using var host = new PocAiTestHost();
        await host.AuditStore.WriteAsync("session-empty", new PocAiToolExecutionResult(
            PocAiToolRegistry.ListDataSources,
            null,
            null,
            2,
            5,
            true,
            null,
            "{}"));

        var row = (await host.Database.Queryable<PocAiAuditLog>()
            .Where(item => item.SessionId == "session-empty")
            .ToListAsync()).Single();
        Assert.True(row.Success);
        Assert.Null(row.SqlText);
        Assert.Null(row.DataSourceCode);
        Assert.Null(row.ErrorSummary);
        Assert.Equal(2, row.RowCount);
    }
}
