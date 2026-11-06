using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 对话服务单测：重点覆盖“AI 可关闭”硬约束（IsEnabled=false / 配置不完整时零 LLM 请求、零会话、零查数）、
/// 工具循环收敛、MaxToolRounds 超限强制结论、LLM 失败保留会话与 30 分钟过期提示。
/// </summary>
public sealed class PocAiChatServiceTests
{
    /// <summary>验证 AI 关闭降级：IsEnabled=false 时 chat 抛 AI_DISABLED，LLM 零请求、会话零创建、审计零写入。</summary>
    [Fact]
    public async Task ChatAsync_when_disabled_fails_fast_without_llm_session_or_tools()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 3);
        // 默认配置即 IsEnabled=false；“AI 可关闭”是最高优先级约束，先钉住默认态。
        var settings = await host.SettingsStore.GetAsync();
        Assert.False(settings.IsEnabled);

        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(() => host.ChatService.ChatAsync(
            new PocAiChatRequest(null, "ai_main", "帮我看看患者费用表")));

        Assert.StartsWith(PocAiDisabledException.ErrorCode, exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
        Assert.Equal(0, host.Sessions.CountActive());
        Assert.Equal(0, await host.CountAuditAsync("any"));
    }

    /// <summary>验证配置不完整同样按 AI_DISABLED fail-fast：缺 BaseUrl 与缺 Model 各自拦截，且不外发任何请求。</summary>
    [Theory]
    [InlineData("", "test-model")]
    [InlineData("http://llm.test/v1", "")]
    public async Task ChatAsync_with_incomplete_config_is_disabled(string baseUrl, string model)
    {
        using var host = new PocAiTestHost();
        // 直接种子存储层（绕过保存校验），模拟手工改库造成的“启用但配置不完整”状态。
        await host.SettingsStore.SaveAsync(new PocAiSettings
        {
            IsEnabled = true,
            BaseUrl = baseUrl,
            ApiKey = "sk-x",
            Model = model,
            SampleRowLimit = 20,
            SqlProbeEnabled = true,
            SqlProbeRowLimit = 100,
            MaxToolRounds = 8
        });

        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(() => host.ChatService.ChatAsync(
            new PocAiChatRequest(null, "ai_main", "你好")));

        Assert.Contains("AI_DISABLED", exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
    }

    /// <summary>验证连通性测试同样受 AI_DISABLED 守卫：关闭时不发起测试请求。</summary>
    [Fact]
    public async Task TestConnection_when_disabled_fails_fast()
    {
        using var host = new PocAiTestHost();
        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(
            () => host.SettingsService.TestConnectionAsync());
        Assert.Contains("AI_DISABLED", exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
    }

    /// <summary>验证工具循环收敛：模型请求工具 → 执行并回传 → 模型给结论；审计逐工具落行。</summary>
    [Fact]
    public async Task ChatAsync_tool_loop_converges_and_audits_tools()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 4);
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_1",
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT COUNT(1) AS Total FROM PatientInfo"}"""));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("共 4 行数据，结论如下……"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "数一下行数"));

        Assert.False(result.Expired);
        var roles = result.Messages.Select(item => item.Role).ToArray();
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant" }, roles);
        Assert.Equal("共 4 行数据，结论如下……", result.Messages[^1].Content);
        var toolView = result.Messages[2];
        Assert.Equal(PocAiToolRegistry.RunReadonlySql, toolView.ToolName);
        Assert.Equal(1, toolView.RowCount);
        Assert.True(toolView.Success);
        Assert.NotNull(toolView.SqlSummary);
        // 展示视图里工具消息只暴露摘要，原始 JSON 不直接下发前端。
        Assert.Null(toolView.Content);
        Assert.Equal(1, await host.CountAuditAsync(result.SessionId));
        // 会话内保存了完整历史（含工具消息），续聊可直接复用。
        var session = host.Sessions.Get(result.SessionId);
        Assert.NotNull(session);
        Assert.Equal(4, session!.Messages.Count);
    }

    /// <summary>验证工具执行失败转为结构化结果继续会话：AI 收到错误后可给出解释性结论，不中断对话。</summary>
    [Fact]
    public async Task ChatAsync_tool_failure_returns_structured_error_and_continues()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_bad",
            PocAiToolRegistry.SampleRows,
            """{"tableName":"NoSuchTable"}"""));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("该表查询失败，请确认表名。"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "看看NoSuchTable"));

        var toolView = result.Messages.Single(item => item.Role == "tool");
        Assert.False(toolView.Success);
        Assert.Equal("该表查询失败，请确认表名。", result.Messages[^1].Content);
        // 结构化错误 JSON 完整写入了会话与审计，供追溯。
        var toolMessage = host.Sessions.Get(result.SessionId)!
            .Messages.Single(item => item.Role == PocAiChatRole.Tool);
        Assert.Contains("QUERY_FAILED", toolMessage.Content);
        Assert.Equal(1, await host.CountAuditAsync(result.SessionId));
        var audit = (await host.Database.Queryable<PocAiAuditLog>()
            .Where(item => item.SessionId == result.SessionId)
            .ToListAsync()).Single();
        Assert.False(audit.Success);
        Assert.Contains("QUERY_FAILED", audit.ErrorSummary);
    }

    /// <summary>验证 MaxToolRounds 超限：第 N+1 次请求不再携带 tools 且注入强制结论指令，模型只能给结论。</summary>
    [Fact]
    public async Task ChatAsync_forces_conclusion_after_max_tool_rounds()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        await host.EnableAiAsync(maxToolRounds: 1);
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_1",
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT 1 AS One"}"""));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("已达轮数上限，我的结论是……"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "继续查"));

        Assert.Equal("已达轮数上限，我的结论是……", result.Messages[^1].Content);
        Assert.Equal(2, host.LlmHandler.RequestCount);
        // 第二次请求：不携带 tools，且包含强制结论指令与首轮工具结果，模型无法继续调用工具。
        var secondRequest = JObject.Parse(host.LlmHandler.RequestBodies[1]);
        Assert.Null(secondRequest["tools"]);
        var contents = secondRequest["messages"]!
            .Select(item => item.Value<string>("content") ?? string.Empty)
            .ToArray();
        Assert.Contains(PocAiChatServicePrompts.ForceConclusion.Content, contents);
        Assert.Contains("\"rowCount\":1", string.Join('\n', contents));
    }

    /// <summary>验证 LLM 失败保留会话：用户与工具消息都已入会话，客户端可原样重试直到成功。</summary>
    [Fact]
    public async Task ChatAsync_llm_failure_keeps_session_for_retry()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        await host.EnableAiAsync();
        var (session, _) = host.Sessions.GetOrCreate("preset-session", "ai_main");

        host.LlmHandler.EnqueueError(500, "boom");
        await Assert.ThrowsAsync<PocAiLlmException>(() => host.ChatService.ChatAsync(
            new PocAiChatRequest("preset-session", "ai_main", "第一次提问")));
        Assert.Single(session.Messages);

        host.LlmHandler.EnqueueError(502, "still down");
        await Assert.ThrowsAsync<PocAiLlmException>(() => host.ChatService.ChatAsync(
            new PocAiChatRequest("preset-session", "ai_main", "重试一次")));
        Assert.Equal(2, session.Messages.Count);

        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("好了，分析如下……"));
        var result = await host.ChatService.ChatAsync(
            new PocAiChatRequest("preset-session", "ai_main", "再试"));
        Assert.Equal("好了，分析如下……", result.Messages[^1].Content);
        Assert.False(result.Expired);
    }

    /// <summary>验证过期重建：前端携带已过期会话号时按新会话重建（复用编号），并把 expired 标记回传提示。</summary>
    [Fact]
    public async Task ChatAsync_reports_expired_when_session_idle_expires()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("第一次"));
        var first = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "你好"));

        var now = host.Clock();
        host.Clock = () => now.AddMinutes(31);
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("新会话"));
        var second = await host.ChatService.ChatAsync(new PocAiChatRequest(first.SessionId, "ai_main", "还在吗"));

        Assert.True(second.Expired);
        // 过期会话被清空后复用同一编号重建，前端据此提示“会话已过期，AI 上下文已清空”。
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(2, second.Messages.Count);
        Assert.Equal("还在吗", second.Messages[0].Content);
        Assert.Equal("新会话", second.Messages[^1].Content);
    }

    /// <summary>验证空消息或未选数据源被参数校验拒绝（发生在守卫之后、任何副作用之前）。</summary>
    [Theory]
    [InlineData("", "ai_main", "AI_MESSAGE_REQUIRED")]
    [InlineData("  ", "ai_main", "AI_MESSAGE_REQUIRED")]
    [InlineData("你好", "", "AI_DATA_SOURCE_REQUIRED")]
    public async Task ChatAsync_rejects_invalid_request(string message, string dataSourceCode, string expectedCode)
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            host.ChatService.ChatAsync(new PocAiChatRequest(null, dataSourceCode, message)));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(0, host.LlmHandler.RequestCount);
        Assert.Equal(0, host.Sessions.CountActive());
    }

    /// <summary>验证系统提示词：嵌入资源存在、包含硬规则段落，并动态注入数据源表清单摘要。</summary>
    [Fact]
    public async Task System_prompt_injects_selected_source_table_summary()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 1);
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("ok"));

        await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "你好"));

        var request = JObject.Parse(host.LlmHandler.RequestBodies.Single());
        var systemMessage = request["messages"]!.First().Value<string>("content");
        Assert.NotNull(systemMessage);
        Assert.Contains("list_data_sources", systemMessage!);
        Assert.Contains("只读", systemMessage);
        Assert.Contains("ai_main", systemMessage);
        Assert.Contains("PatientInfo", systemMessage);
        // 阶段二：提示词必须包含产物契约（submit/dry_run 工具与 3 次熔断语义），
        // 保证模型知道提交格式与失败自修流程。
        Assert.Contains("submit_report_spec", systemMessage);
        Assert.Contains("dry_run_report_spec", systemMessage);
        Assert.Contains("3 次", systemMessage);
    }

    /// <summary>验证显式关闭会话：清空内存上下文，重复关闭幂等成功。</summary>
    [Fact]
    public async Task CloseSession_clears_context_idempotently()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("ok"));
        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "你好"));

        Assert.True(host.ChatService.CloseSession(result.SessionId));
        // 重复关闭返回 closed=false，语义上仍视为关闭成功（幂等），前端按成功提示。
        Assert.False(host.ChatService.CloseSession(result.SessionId));
        Assert.Equal(0, host.Sessions.CountActive());
    }
}
