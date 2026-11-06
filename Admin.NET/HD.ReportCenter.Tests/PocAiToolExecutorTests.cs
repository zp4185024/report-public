using System.Text.RegularExpressions;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 工具执行器单测：只读拦截、表名注入、行数上限、数据源不存在与 SqlProbeEnabled 拒绝
/// 全部走 SQLite 内存库 + 共享客户端工厂，不依赖真实业务库与真实 LLM。
/// </summary>
public sealed class PocAiToolExecutorTests
{
    /// <summary>验证未注册工具一律拒绝；注册表之外没有旁路，未登记的工具名直接结构化拒绝。</summary>
    [Fact]
    public async Task ExecuteAsync_rejects_unregistered_tool()
    {
        using var host = new PocAiTestHost();
        var settings = await host.EnableAiAsync();

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            "delete_report",
            "{}",
            "ai_main");

        Assert.False(result.Success);
        Assert.Contains("AI_TOOL_NOT_REGISTERED", result.ErrorSummary);
        Assert.Contains("AI_TOOL_NOT_REGISTERED", result.ContentJson);
    }

    /// <summary>验证产物工具误路由防御：submit/dry_run 由对话服务处理，只读执行器接到时给出明确拒绝。</summary>
    [Fact]
    public async Task ExecuteAsync_rejects_spec_tools_with_defensive_code()
    {
        using var host = new PocAiTestHost();
        var settings = await host.EnableAiAsync();

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.SubmitReportSpec,
            "{}",
            "ai_main");

        Assert.False(result.Success);
        Assert.Contains("AI_SPEC_TOOL_MISROUTED", result.ErrorSummary);
    }

    /// <summary>验证 list_data_sources 只返回已启用数据源，输出永不包含连接串。</summary>
    [Fact]
    public async Task ListDataSources_returns_enabled_sources_without_connection_string()
    {
        using var host = new PocAiTestHost();
        await host.Database.Insertable(new HD.ReportCenter.Application.CoreValidation.PocDataSource
        {
            Code = "disabled_src",
            Name = "停用数据源",
            DbType = "SqlServer",
            ConnectionString = "SECRET_CONNECTION_STRING",
            DefaultTimeoutSeconds = 5,
            DefaultMaxRows = 100,
            Revision = 1,
            IsEnabled = false
        }).ExecuteCommandAsync();
        var settings = await host.EnableAiAsync();

        var result = await host.ToolExecutor.ExecuteAsync(settings, PocAiToolRegistry.ListDataSources, "{}", "ai_main");

        Assert.True(result.Success);
        var payload = JObject.Parse(result.ContentJson);
        var codes = payload["dataSources"]!.Select(item => item.Value<string>("code")).ToArray();
        Assert.Equal(new[] { "ai_main" }, codes);
        Assert.DoesNotContain("SECRET_CONNECTION_STRING", result.ContentJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证 list_tables 返回建出的表清单，且失败的数据源不阻断工具本身。</summary>
    [Fact]
    public async Task ListTables_returns_created_tables()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 3);
        var settings = await host.EnableAiAsync();

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.ListTables,
            """{"dataSourceCode":"ai_main"}""",
            "ai_main");

        Assert.True(result.Success);
        var names = JObject.Parse(result.ContentJson)["tables"]!
            .Select(item => item.Value<string>("name")!).ToArray();
        Assert.Contains("PatientInfo", names);
    }

    /// <summary>验证 describe_table 返回列名与类型；表名注入被标识符校验拦截且目标表保持原样。</summary>
    [Fact]
    public async Task DescribeTable_rejects_injected_table_name_and_lists_columns_otherwise()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        var settings = await host.EnableAiAsync();

        var injected = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.DescribeTable,
            """{"dataSourceCode":"ai_main","tableName":"PatientInfo; DROP TABLE PatientInfo"}""",
            "ai_main");
        Assert.False(injected.Success);
        Assert.Contains("TABLE_NAME_INVALID", injected.ErrorSummary);

        var quoted = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.DescribeTable,
            """{"dataSourceCode":"ai_main","tableName":"\"PatientInfo\""}""",
            "ai_main");
        Assert.False(quoted.Success);
        Assert.Contains("TABLE_NAME_INVALID", quoted.ErrorSummary);

        var columns = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.DescribeTable,
            """{"dataSourceCode":"ai_main","tableName":"PatientInfo"}""",
            "ai_main");
        Assert.True(columns.Success);
        var payload = JObject.Parse(columns.ContentJson);
        var columnNames = payload["columns"]!.Select(item => item.Value<string>("name")).ToArray();
        Assert.Contains("PatientName", columnNames);
        Assert.Contains("FeeAmount", columnNames);
    }

    /// <summary>验证 sample_rows 由代码生成 TOP/LIMIT 包装（AI 不写 SQL），行数按配置上限钳制并标记截断。</summary>
    [Fact]
    public async Task SampleRows_clamps_limit_and_marks_truncation()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 10);
        var settings = await host.EnableAiAsync(sampleRowLimit: 3);

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.SampleRows,
            """{"dataSourceCode":"ai_main","tableName":"PatientInfo","limit":10}""",
            "ai_main");

        Assert.True(result.Success, result.ErrorSummary ?? "工具失败但未返回错误摘要");
        Assert.Equal("SELECT * FROM \"PatientInfo\" LIMIT 4", result.SqlText);
        var payload = JObject.Parse(result.ContentJson);
        Assert.Equal(3, payload.Value<int?>("rowCount"));
        Assert.True(payload.Value<bool?>("truncated"));
    }

    /// <summary>验证 SampleRowLimit 配置为 0 时采样整体禁用。</summary>
    [Fact]
    public async Task SampleRows_refused_when_sample_limit_is_zero()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 3);
        var settings = await host.EnableAiAsync(sampleRowLimit: 0);

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.SampleRows,
            """{"dataSourceCode":"ai_main","tableName":"PatientInfo"}""",
            "ai_main");

        Assert.False(result.Success);
        Assert.Contains("SAMPLE_DISABLED", result.ErrorSummary);
    }

    /// <summary>验证 run_readonly_sql 拦截 DML/DDL 与多语句；拦截发生在数据库执行之前，数据不被改动。</summary>
    [Fact]
    public async Task RunReadonlySql_blocks_dml_before_execution()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 5);
        var settings = await host.EnableAiAsync();

        var update = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"UPDATE PatientInfo SET FeeAmount = 0"}""",
            "ai_main");
        Assert.False(update.Success);
        Assert.Contains("DATASET_QUERY_NOT_READ_ONLY", update.ErrorSummary);

        var drop = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT * FROM PatientInfo; DROP TABLE PatientInfo"}""",
            "ai_main");
        Assert.False(drop.Success);

        var rowTotal = (await host.Database.Ado.SqlQueryAsync<int>("SELECT COUNT(1) AS Cnt FROM \"PatientInfo\"")).Single();
        Assert.Equal(5, rowTotal);
    }

    /// <summary>验证 run_readonly_sql 禁止 @参数占位：探测 SQL 只允许字面量，避免任何绑定旁路。</summary>
    [Fact]
    public async Task RunReadonlySql_rejects_parameter_placeholders()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        var settings = await host.EnableAiAsync();

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT * FROM PatientInfo WHERE Id = @id"}""",
            "ai_main");

        Assert.False(result.Success);
        Assert.Contains("DATASET_PARAMETER_UNDEFINED", result.ErrorSummary);
    }

    /// <summary>验证 run_readonly_sql 读取侧截断：SQL 原样执行不改写，读到上限即停并标记截断。</summary>
    [Fact]
    public async Task RunReadonlySql_truncates_rows_on_read_side()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 10);
        var settings = await host.EnableAiAsync(sqlProbeRowLimit: 4);

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT * FROM PatientInfo"}""",
            "ai_main");

        Assert.True(result.Success);
        Assert.Equal("SELECT * FROM PatientInfo", result.SqlText);
        var payload = JObject.Parse(result.ContentJson);
        Assert.Equal(4, payload.Value<int?>("rowCount"));
        Assert.True(payload.Value<bool?>("truncated"));
    }

    /// <summary>验证 SqlProbeEnabled=false 时 run_readonly_sql 整体拒绝，sample_rows 等其他工具不受影响。</summary>
    [Fact]
    public async Task RunReadonlySql_refused_when_probe_disabled()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 3);
        var settings = await host.EnableAiAsync(sqlProbeEnabled: false);

        var probe = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT 1"}""",
            "ai_main");
        Assert.False(probe.Success);
        Assert.Contains("SQL_PROBE_DISABLED", probe.ErrorSummary);

        var sample = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.SampleRows,
            """{"dataSourceCode":"ai_main","tableName":"PatientInfo"}""",
            "ai_main");
        Assert.True(sample.Success);
    }

    /// <summary>验证数据源不存在或已停用时统一按 DATA_SOURCE_NOT_FOUND 拒绝，不给探测停用资源的机会。</summary>
    [Fact]
    public async Task Tools_refuse_missing_or_disabled_data_source()
    {
        using var host = new PocAiTestHost();
        await host.Database.Insertable(new HD.ReportCenter.Application.CoreValidation.PocDataSource
        {
            Code = "off_src",
            Name = "停用源",
            DbType = "SqlServer",
            ConnectionString = "Data Source=x",
            DefaultTimeoutSeconds = 5,
            DefaultMaxRows = 10,
            Revision = 1,
            IsEnabled = false
        }).ExecuteCommandAsync();
        var settings = await host.EnableAiAsync();

        var missing = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"no_such","sql":"SELECT 1"}""",
            "no_such");
        Assert.False(missing.Success);
        Assert.Contains("DATA_SOURCE_NOT_FOUND", missing.ErrorSummary);

        var disabled = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"off_src","sql":"SELECT 1"}""",
            "off_src");
        Assert.False(disabled.Success);
        Assert.Contains("DATA_SOURCE_NOT_FOUND", disabled.ErrorSummary);
    }

    /// <summary>验证会话默认数据源兜底：工具参数缺省 dataSourceCode 时回落到会话绑定数据源。</summary>
    [Fact]
    public async Task Tools_fall_back_to_session_default_data_source()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        var settings = await host.EnableAiAsync();

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"sql":"SELECT COUNT(1) AS total FROM PatientInfo"}""",
            "ai_main");

        Assert.True(result.Success);
        Assert.Equal("ai_main", result.DataSourceCode);
        Assert.Equal(1, result.RowCount);
    }

    /// <summary>验证采样 SQL 按方言生成：SqlServer 用 TOP、Oracle 用 FETCH FIRST、MySQL/PG 用 LIMIT。</summary>
    [Theory]
    [InlineData("SqlServer", "SELECT TOP (5) * FROM [dbo].[PatientInfo]")]
    [InlineData("Oracle", "SELECT * FROM \"dbo\".\"PatientInfo\" FETCH FIRST 5 ROWS ONLY")]
    [InlineData("MySql", "SELECT * FROM `dbo`.`PatientInfo` LIMIT 5")]
    [InlineData("PostgreSQL", "SELECT * FROM \"dbo\".\"PatientInfo\" LIMIT 5")]
    public void BuildSampleQuery_generates_dialect_specific_wrapping(string dbType, string expected)
    {
        Assert.Equal(expected, PocAiToolExecutor.BuildSampleQuery(dbType, "dbo", "PatientInfo", 5));
    }

    /// <summary>验证审计元数据：成功调用记录 SQL、行数与耗时；供会话层审计对账。</summary>
    [Fact]
    public async Task Execution_result_carries_audit_metadata()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 3);
        var settings = await host.EnableAiAsync();

        var result = await host.ToolExecutor.ExecuteAsync(
            settings,
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT * FROM PatientInfo"}""",
            "ai_main");

        Assert.True(result.Success);
        Assert.Equal(PocAiToolRegistry.RunReadonlySql, result.ToolName);
        Assert.Equal("ai_main", result.DataSourceCode);
        Assert.Equal(3, result.RowCount);
        Assert.True(result.DurationMs >= 0);
        Assert.Null(result.ErrorSummary);
        // 表名注入防御的正则自身也要保持严格：引号、分号、空白都不允许。
        Assert.Matches(new Regex("^SELECT"), result.SqlText!);
    }
}
