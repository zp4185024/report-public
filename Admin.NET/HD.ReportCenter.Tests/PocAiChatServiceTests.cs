using Furion.FriendlyException;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 对话服务单测：重点覆盖“AI 可关闭”硬约束（IsEnabled=false / 配置不完整时零 LLM 请求、零会话、零查数）、
/// 工具循环收敛、MaxToolRounds 超限强制结论、LLM 失败保留会话、30 分钟过期提示，
/// 以及超时治理的发送前上下文压缩（早期轮次 tool 结果截断、最近 2 轮全文、user/assistant 不截断）。
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

    /// <summary>验证会话预创建的关闭守卫：AI 关闭时抛 AI_DISABLED 且零会话创建、零 LLM 请求。</summary>
    [Fact]
    public async Task PreCreateSessionAsync_when_disabled_fails_fast_without_session()
    {
        using var host = new PocAiTestHost();
        // 默认配置即 IsEnabled=false；预创建必须与 chat 同闸，绝不留下“AI 关闭还能建号”的旁路。
        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(() => host.ChatService.PreCreateSessionAsync(
            new PocAiSessionPreCreateRequest("ai_main")));

        Assert.Contains("AI_DISABLED", exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
        Assert.Equal(0, host.Sessions.CountActive());
    }

    /// <summary>验证会话预创建的参数边界：数据源为空或不存在/停用时按 400 语义拒绝，不留半截会话。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no_such_source")]
    public async Task PreCreateSessionAsync_with_invalid_source_is_rejected(string dataSourceCode)
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            host.ChatService.PreCreateSessionAsync(new PocAiSessionPreCreateRequest(dataSourceCode)));

        // 空编码与未知编码分别对应 AI_DATA_SOURCE_REQUIRED / AI_DATA_SOURCE_INVALID 两个稳定错误码，
        // API 层据此映射 400；会话绝不留下半截。
        Assert.Contains("AI_DATA_SOURCE", exception.Code);
        Assert.Equal(0, host.Sessions.CountActive());
    }

    /// <summary>验证会话预创建主链路：产出绑定数据源的空会话，且首轮 /chat 可无缝续用、不判过期。</summary>
    [Fact]
    public async Task PreCreateSessionAsync_creates_empty_session_and_first_chat_reuses_it()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        await host.EnableAiAsync();

        var preCreated = await host.ChatService.PreCreateSessionAsync(new PocAiSessionPreCreateRequest("ai_main"));

        // 空会话契约：编号非空、绑定数据源、零消息历史、循环状态空闲（progress 轮询安全返回 idle）。
        Assert.StartsWith("ai-", preCreated.SessionId);
        Assert.Equal("ai_main", preCreated.DataSourceCode);
        var session = host.Sessions.Get(preCreated.SessionId);
        Assert.NotNull(session);
        Assert.Empty(session!.Messages);
        Assert.Equal(PocAiLoopState.Idle, session.LoopState);
        var idleProgress = await host.ChatService.GetProgressAsync(preCreated.SessionId, 0);
        Assert.Equal("idle", idleProgress.LoopState);
        Assert.Empty(idleProgress.Messages);

        // 首轮对话携带预创建编号：会话命中不重建，expired=false，消息从该会话继续累积。
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("你好，请描述你的需求。"));
        var chat = await host.ChatService.ChatAsync(
            new PocAiChatRequest(preCreated.SessionId, "ai_main", "你好"));

        Assert.False(chat.Expired);
        Assert.Equal(preCreated.SessionId, chat.SessionId);
        Assert.Equal("user", chat.Messages[0].Role);
        Assert.Equal(1, chat.Messages[0].Seq);
    }

    // ===== 超时治理：发送前上下文压缩（早期轮次 tool 结果截断/最近 2 轮全文/user·assistant 不截断） =====

    /// <summary>
    /// 构造带指定轮数的工具调用历史：每轮为 assistant(tool_calls) + 指定长度的 tool 结果，
    /// 尾接一条指定长度的 assistant 结论，开头一条用户消息；供压缩与插话用例复用。
    /// </summary>
    /// <param name="rounds">工具调用轮数。</param>
    /// <param name="toolContentLength">每条 tool 结果内容总字符数（前缀 T{n}: 占 3 字符）。</param>
    /// <param name="assistantContentLength">结尾 assistant 结论的字符数。</param>
    /// <returns>完整历史列表（元素下标固定：2/4/6…为 tool 消息）。</returns>
    private static List<PocAiChatMessage> BuildToolRoundHistory(
        int rounds,
        int toolContentLength,
        int assistantContentLength)
    {
        var history = new List<PocAiChatMessage> { new(PocAiChatRole.User, "第一问") };
        for (var round = 1; round <= rounds; round++)
        {
            history.Add(new PocAiChatMessage(
                PocAiChatRole.Assistant,
                null,
                [new PocLlmToolCall($"call_{round}", "run_readonly_sql", "{}")]));
            history.Add(new PocAiChatMessage(
                PocAiChatRole.Tool,
                $"T{round}:" + new string('X', toolContentLength - 3),
                null,
                $"call_{round}",
                "run_readonly_sql"));
        }
        history.Add(new PocAiChatMessage(PocAiChatRole.Assistant, new string('A', assistantContentLength)));
        return history;
    }

    /// <summary>验证发送前压缩边界：3 轮历史中只有第 1 轮（超出最近 2 轮的早期轮次）超长结果被截断并追加标记，会话内原对象不动。</summary>
    [Fact]
    public void CompressHistoryForLlm_truncates_only_older_than_recent_two_rounds()
    {
        var history = BuildToolRoundHistory(3, toolContentLength: 1000, assistantContentLength: 30);
        var firstTool = history[2];

        var compressed = PocAiChatService.CompressHistoryForLlm(history);

        // 第 1 轮属早期轮次：截为前 600 字符并追加截断标记；第 2、3 轮在“最近 2 轮”保护内保持原对象引用。
        var compressedFirst = compressed[2];
        Assert.NotSame(firstTool, compressedFirst);
        Assert.StartsWith("T1:", compressedFirst.Content);
        Assert.EndsWith(PocAiChatService.CompressedToolContentSuffix, compressedFirst.Content);
        Assert.Equal(
            PocAiChatService.CompressedToolContentLength + PocAiChatService.CompressedToolContentSuffix.Length,
            compressedFirst.Content!.Length);
        Assert.Same(history[4], compressed[4]);
        Assert.Same(history[6], compressed[6]);
        // 压缩不得改动会话内对象：原 tool 消息内容仍为全文（存储与前端展示语义不变）。
        Assert.Equal(1000, firstTool.Content!.Length);
    }

    /// <summary>验证 user/assistant 一律不截断：长用户输入、带 tool_calls 的 assistant 与长结论在压缩后原样保留。</summary>
    [Fact]
    public void CompressHistoryForLlm_never_truncates_user_or_assistant_messages()
    {
        var history = BuildToolRoundHistory(3, toolContentLength: 1000, assistantContentLength: 2000);
        // 在历史中部插入一条 2000 字符的用户消息，验证任何位置的非 tool 消息都不参与压缩。
        history.Insert(1, new PocAiChatMessage(PocAiChatRole.User, new string('U', 2000)));

        var compressed = PocAiChatService.CompressHistoryForLlm(history);

        for (var index = 0; index < history.Count; index++)
        {
            if (history[index].Role != PocAiChatRole.Tool)
            {
                Assert.Same(history[index], compressed[index]);
            }
        }
        // 带 tool_calls 的 assistant 保留调用结构（role/tool_call_id 配对不被压缩破坏）。
        var assistantWithCalls = compressed.First(item => item.ToolCalls is { Count: > 0 });
        Assert.Equal("call_1", assistantWithCalls.ToolCalls![0].Id);
        Assert.Equal(2000, compressed.Last(item => item.Role == PocAiChatRole.Assistant).Content!.Length);
    }

    /// <summary>验证长度未超限的早期工具结果保持原样：不截断也不追加误导性的“已截断”标记。</summary>
    [Fact]
    public void CompressHistoryForLlm_keeps_short_old_tool_results_verbatim()
    {
        var history = BuildToolRoundHistory(4, toolContentLength: 100, assistantContentLength: 10);
        var firstTool = history[2];

        var compressed = PocAiChatService.CompressHistoryForLlm(history);

        Assert.Same(firstTool, compressed[2]);
        Assert.DoesNotContain(PocAiChatService.CompressedToolContentSuffix, compressed[2].Content);
    }

    /// <summary>验证总轮数未超出“最近 2 轮”保护窗口时不做任何压缩：直接返回原列表，避免无谓复制。</summary>
    [Fact]
    public void CompressHistoryForLlm_returns_original_when_rounds_within_recent_window()
    {
        var history = BuildToolRoundHistory(2, toolContentLength: 1000, assistantContentLength: 30);

        Assert.Same(history, PocAiChatService.CompressHistoryForLlm(history));
    }

    /// <summary>
    /// 验证压缩作用边界（线格式层面）：LLM 请求里早期轮次工具结果被截断、最近两轮全文，
    /// 而会话内存储保持全文不动，本轮结论照常返回。
    /// </summary>
    [Fact]
    public async Task ChatAsync_sends_compressed_old_tool_results_while_session_keeps_full()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        // 直接为预置会话注入 3 轮工具历史（每轮 tool 结果 1000 字符），不经过 LLM 逐步构造。
        var (session, _) = host.Sessions.GetOrCreate("compress-session", "ai_main");
        session.AppendMessages(BuildToolRoundHistory(3, 1000, 30));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("已基于历史给出结论。"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest("compress-session", "ai_main", "继续"));

        // 请求侧：只有第 1 轮（早期）tool 结果被截断，第 2、3 轮保持全文。
        var request = JObject.Parse(host.LlmHandler.RequestBodies.Single());
        var toolContents = request["messages"]!
            .Where(item => item.Value<string>("role") == "tool")
            .Select(item => item.Value<string>("content")!)
            .ToArray();
        Assert.Equal(3, toolContents.Length);
        Assert.Equal("T1:" + new string('X', 597) + PocAiChatService.CompressedToolContentSuffix, toolContents[0]);
        Assert.Equal("T2:" + new string('X', 997), toolContents[1]);
        Assert.Equal("T3:" + new string('X', 997), toolContents[2]);

        // 存储侧：会话内 tool 消息仍为全文，本轮结论照常追加。
        var storedTools = session.Messages.Where(item => item.Role == PocAiChatRole.Tool).ToArray();
        Assert.Equal(1000, storedTools[0].Content!.Length);
        Assert.Equal(1000, storedTools[1].Content!.Length);
        Assert.Equal("已基于历史给出结论。", result.Messages[^1].Content);
    }

    // ===== 插话纠正（中途方向修正）：guidance 追加 + 运行中插话下一轮生效 + 与压缩正交 =====

    /// <summary>验证插话基础语义：正常入会话并分配递增 Seq；AI 关闭 fail-fast 零写入；会话不存在报 AI_SESSION_NOT_FOUND；空内容拒绝。</summary>
    [Fact]
    public async Task AppendGuidanceAsync_appends_user_message_with_seq_and_guards()
    {
        using var host = new PocAiTestHost();
        // AI 默认关闭：fail-fast 且零会话写入。
        var disabled = await Assert.ThrowsAsync<PocAiDisabledException>(
            () => host.ChatService.AppendGuidanceAsync("any-session", "纠正方向"));
        Assert.StartsWith(PocAiDisabledException.ErrorCode, disabled.Message);
        Assert.Equal(0, host.Sessions.CountActive());

        await host.EnableAiAsync();
        var missing = await Assert.ThrowsAsync<PocDatasetConfigurationException>(
            () => host.ChatService.AppendGuidanceAsync("ghost-session", "纠正方向"));
        // 稳定错误码在 Code 属性上（API 层据此映射 400），消息只承载中文说明。
        Assert.Equal("AI_SESSION_NOT_FOUND", missing.Code);

        var (session, _) = host.Sessions.GetOrCreate("live-session", "ai_main");
        session.AppendMessages([new PocAiChatMessage(PocAiChatRole.User, "第一问")]);
        var guidance = await host.ChatService.AppendGuidanceAsync("live-session", "改查费用类型");

        Assert.Equal("user", guidance.Role);
        Assert.Equal("改查费用类型", guidance.Content);
        Assert.Equal(2, guidance.Seq);
        Assert.Equal(2, session.Messages.Count);
        // 空内容与纯空白都被拒绝，不留下半截插话。
        await Assert.ThrowsAsync<PocDatasetConfigurationException>(
            () => host.ChatService.AppendGuidanceAsync("live-session", "   "));
    }

    /// <summary>
    /// 验证循环运行中插话生效：工具循环挂起等待下一轮响应期间追加的用户插话，
    /// 正常入会话并分配递增 Seq，且下一轮组装 llmMessages 时被带上（user 角色、位于工具结果之后）。
    /// </summary>
    [Fact]
    public async Task Guidance_during_running_loop_is_included_in_next_llm_request()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_1",
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT COUNT(1) AS Total FROM PatientInfo"}"""));
        // 第二轮响应手动放行：请求已发出但模型“迟迟未回”，为插话制造确定性时序窗口。
        var releaseRound2 = host.LlmHandler.EnqueueDeferredJson(PocAiLlmFixtures.ToolCallResponse(
            "call_2",
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT 1 AS One"}"""));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("已按纠正方向给出结论。"));

        var preCreated = await host.ChatService.PreCreateSessionAsync(new PocAiSessionPreCreateRequest("ai_main"));
        var chatTask = host.ChatService.ChatAsync(
            new PocAiChatRequest(preCreated.SessionId, "ai_main", "先查一下行数"));
        await WaitForConditionAsync(() => host.LlmHandler.RequestCount >= 2);

        // 循环挂起期间插话：应正常入会话，Seq 排在第一轮工具结果之后。
        var guidance = await host.ChatService.AppendGuidanceAsync(preCreated.SessionId, "重点看费用类型，不要查行数了");
        var session = host.Sessions.Get(preCreated.SessionId)!;
        var firstTool = session.Messages.Single(item => item.Role == PocAiChatRole.Tool);
        Assert.True(guidance.Seq > firstTool.Seq);

        releaseRound2();
        var result = await chatTask;

        // 第三轮请求包含插话：user 角色、内容原文，且紧跟在工具结果之后进入消息序列。
        var thirdRequest = JObject.Parse(host.LlmHandler.RequestBodies[2]);
        var messages = thirdRequest["messages"]!;
        var guidancePosition = -1;
        for (var index = 0; index < messages.Count(); index++)
        {
            if ((messages[index].Value<string>("content") ?? string.Empty) == "重点看费用类型，不要查行数了")
            {
                guidancePosition = index;
                break;
            }
        }
        Assert.True(guidancePosition > 0);
        Assert.Equal("user", messages[guidancePosition]!.Value<string>("role"));
        Assert.Equal("tool", messages[guidancePosition - 1]!.Value<string>("role"));
        Assert.Equal("已按纠正方向给出结论。", result.Messages[^1].Content);
    }

    /// <summary>验证压缩与插话正交：同一请求里早期轮次 tool 结果被截断，而插话（user 消息）全文保留。</summary>
    [Fact]
    public async Task ChatAsync_compression_and_guidance_are_orthogonal()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var (session, _) = host.Sessions.GetOrCreate("compress-guidance", "ai_main");
        session.AppendMessages(BuildToolRoundHistory(3, 1000, 30));
        var guidance = await host.ChatService.AppendGuidanceAsync("compress-guidance", "请把重心放在列口径上");
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("收到纠正，已调整方向。"));

        await host.ChatService.ChatAsync(new PocAiChatRequest("compress-guidance", "ai_main", "继续"));

        var request = JObject.Parse(host.LlmHandler.RequestBodies.Single());
        var contents = request["messages"]!
            .Select(item => item.Value<string>("content") ?? string.Empty)
            .ToArray();
        // 压缩照常生效：第 1 轮早期 tool 结果为前缀 + 截断标记。
        Assert.Contains("T1:" + new string('X', 597) + PocAiChatService.CompressedToolContentSuffix, contents);
        // 插话不受压缩影响：user 消息全文出现。
        Assert.Contains("请把重心放在列口径上", contents);
        Assert.Equal(guidance.Seq, session.Messages.Single(item => item.Content == "请把重心放在列口径上").Seq);
    }

    /// <summary>
    /// 钉住“客户端中止优雅收口”：浏览器关闭或停止按钮 abort fetch 后，RequestAborted 从 LLM await 点
    /// 抛出取消异常，ChatAsync 必须正常返回部分结果（不逃逸 OperationCanceledException）、
    /// 会话与用户消息保留、循环状态记 failed 供前端重试——这是生产崩溃排查后的回归防线。
    /// </summary>
    [Fact]
    public async Task ChatAsync_client_cancel_returns_gracefully_without_escaping_oce()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        using var cts = new CancellationTokenSource();
        // 延迟 60 秒且观察取消令牌的 LLM 响应：保证取消一定命中 await 点，与生产“取消打断在途请求”同构。
        host.LlmHandler.EnqueueDelayedJson(PocAiLlmFixtures.ContentResponse("永远不会返回"), TimeSpan.FromSeconds(60));

        var chatTask = host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "帮我数行数"), cts.Token);
        cts.Cancel();

        // 关键断言：await 不抛 OperationCanceledException，而是拿到可控的部分结果。
        var result = await chatTask;
        Assert.Equal(new[] { "user" }, result.Messages.Select(item => item.Role).ToArray());
        var session = host.Sessions.Get(result.SessionId);
        Assert.NotNull(session);
        Assert.Equal(PocAiLoopState.Failed, session!.LoopState);
        Assert.Single(session.Messages);
    }

    /// <summary>
    /// 钉住“审计旁路失败不响应对话”：审计存储指向一个没有任何表的独立内存库（任何插入必失败），
    /// 对话主流程必须照常完成（LLM 工具调用 + 最终结论），同时 stderr 留一行审计失败痕迹供事后排查。
    /// </summary>
    [Fact]
    public async Task ChatAsync_audit_failure_does_not_break_chat()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.SeedDataTable("PatientInfo", 3);
        // 独立的空内存库客户端：RcAiAuditLog 表不存在，任何审计写入都会失败。
        var auditStore = new PocAiAuditStore(new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute
        }));
        var chatService = new PocAiChatService(
            host.SettingsService,
            host.Sessions,
            host.ToolExecutor,
            auditStore,
            host.LlmClient,
            null,
            host.DataAssets,
            (_, _, _, cancellationToken) =>
            {
                // 系统提示词表清单读取走本地 SQLite 空库：返回空清单且不触网，与主链路降级语义一致。
                var client = new SqlSugarClient(new ConnectionConfig
                {
                    ConnectionString = "Data Source=:memory:",
                    DbType = SqlSugar.DbType.Sqlite,
                    IsAutoCloseConnection = false,
                    InitKeyType = InitKeyType.Attribute
                });
                client.Ado.CancellationToken = cancellationToken;
                return client;
            },
            new PocAiSpecCompiler(),
            host.PackageService);

        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_1",
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT COUNT(1) AS Total FROM PatientInfo"}"""));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("共 3 行数据。"));

        var stderr = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(stderr);
        try
        {
            var result = await chatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "数一下行数"));
            Assert.Equal("共 3 行数据。", result.Messages[^1].Content);
            // 审计失败被就地消化：对话正常返回，stderr 只有一行观测痕迹。
            Assert.Contains("[AI] audit write failed", stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// 钉住“AI API 层兜住取消异常”：客户端中止时（连通性测试带上已取消令牌），
    /// HandleAiOperation 必须把 OperationCanceledException 翻译为受控 FriendlyException(499)，
    /// 不让取消异常以裸 500/原始堆栈穿透到 Furion 管道制造错误日志风暴。
    /// </summary>
    [Fact]
    public async Task Ai_api_translates_client_cancel_to_friendly_499()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueDelayedJson(PocAiLlmFixtures.ContentResponse("ok"), TimeSpan.FromSeconds(60));
        var api = new PocAiAssistantApi(host.SettingsService, host.ChatService);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAsync<AppFriendlyException>(() => api.TestSettings(cts.Token));
        Assert.Contains("AI_REQUEST_ABORTED", exception.Message);
        Assert.Equal(499, exception.StatusCode);
    }

    /// <summary>轮询等待条件成立（每 10ms 一次，最长 5 秒），用于异步工具循环的确定性时序编排；超时抛出失败。</summary>
    /// <param name="condition">待满足的条件。</param>
    /// <returns>条件成立后返回。</returns>
    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待工具循环推进超时，测试编排失败。");
            }
            await Task.Delay(10);
        }
    }
}
