using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using Xunit;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 透明化改造（方案 A/B）单测：数据源表清单公共取数（tables 接口与工具同源）、
/// 工具循环 Seq 单调递增、loopState 流转（running→completed/failed）、progress 按 afterSeq 截取、
/// AI 关闭 fail-fast 与 50 条裁剪后 Seq 不回退。
/// </summary>
public sealed class PocAiTransparencyTests
{
    /// <summary>验证表清单公共取数：基表与视图都带 kind 标记；工具口径（includeViews=false）只列基表。</summary>
    [Fact]
    public async Task LoadDataSourceTables_lists_tables_and_views_with_kind()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 1);
        host.Database.Ado.ExecuteCommand("CREATE VIEW V_PatientInfo AS SELECT Id, PatientName FROM PatientInfo");
        await host.EnableAiAsync();

        var withViews = await host.ToolExecutor.LoadDataSourceTablesAsync("ai_main", includeViews: true);
        var tablesOnly = await host.ToolExecutor.LoadDataSourceTablesAsync("ai_main", includeViews: false);

        Assert.Contains(withViews, item => item.Name == "PatientInfo" && item.Kind == "table");
        Assert.Contains(withViews, item => item.Name == "V_PatientInfo" && item.Kind == "view");
        Assert.DoesNotContain(withViews, item => item.Name == "V_PatientInfo" && item.Kind == "table");
        // 工具 list_tables 保持既有口径：只列基表，前端清单接口才附带视图。
        Assert.Contains(tablesOnly, item => item.Name == "PatientInfo");
        Assert.DoesNotContain(tablesOnly, item => item.Name == "V_PatientInfo");
    }

    /// <summary>验证表清单接口 fail-fast：AI 关闭时公共取数直接抛 AI_DISABLED，且不触碰目标库。</summary>
    [Fact]
    public async Task LoadDataSourceTables_when_ai_disabled_fails_fast()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 1);

        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(
            () => host.ToolExecutor.LoadDataSourceTablesAsync("ai_main", includeViews: true));

        Assert.Contains(PocAiDisabledException.ErrorCode, exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
    }

    /// <summary>验证表清单目标数据源校验：不存在或停用的数据源按 DATA_SOURCE_NOT_FOUND 拒绝。</summary>
    [Fact]
    public async Task LoadDataSourceTables_rejects_missing_data_source()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();

        var exception = await Assert.ThrowsAsync<PocAiToolRejectException>(
            () => host.ToolExecutor.LoadDataSourceTablesAsync("no_such_source", includeViews: true));

        Assert.Equal("DATA_SOURCE_NOT_FOUND", exception.Code);
    }

    /// <summary>
    /// 验证循环实时化主干（方案 B）：脚本化两轮工具返回，第二轮 LLM 响应延迟挂起——
    /// 挂起期间 progress 已能看到 running 状态与逐条追加的用户/工具消息（不再等循环结束统一落）；
    /// 循环结束后状态 completed，Seq 在会话内单调递增；afterSeq 截取只返回其后消息。
    /// </summary>
    [Fact]
    public async Task Tool_loop_streams_running_state_and_increasing_seqs_then_completes()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 2);
        await host.EnableAiAsync();
        // 预建会话，让中间态观察有稳定的会话引用（真实前端在第二轮起同样持有会话号）。
        var (session, _) = host.Sessions.GetOrCreate("preset-streaming", "ai_main");
        // 第一轮：模型请求查 SQL；第二轮：延迟 800ms 的最终结论，为轮询观察留出窗口。
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_1",
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT COUNT(1) AS Total FROM PatientInfo"}"""));
        host.LlmHandler.EnqueueDelayedJson(
            PocAiLlmFixtures.ContentResponse("共 2 行，结论如下……"), TimeSpan.FromMilliseconds(800));

        var chatTask = host.ChatService.ChatAsync(new PocAiChatRequest("preset-streaming", "ai_main", "数一下行数"));

        // 轮询等待：工具结果已入会话且循环仍在进行——验证“逐条追加”而非收尾一次性落。
        var midLoop = await WaitForRunningWithMessagesAsync(session, minMessages: 3, TimeSpan.FromSeconds(10));
        Assert.NotNull(midLoop);
        Assert.Equal(3, midLoop.Value.Messages.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, midLoop.Value.Messages.Select(item => item.Seq).ToArray());
        Assert.Equal(PocAiChatRole.Tool, midLoop.Value.Messages[2].Role);

        var result = await chatTask;
        Assert.Equal(PocAiLoopState.Completed, session.LoopState);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, session.Messages.Select(item => item.Seq).ToArray());
        Assert.Equal("共 2 行，结论如下……", session.Messages[^1].Content);

        // afterSeq 截取：afterSeq=2 只回 Seq 3/4；不存在的会话按 idle 空列表返回（不报错）。
        var progressTail = await host.ChatService.GetProgressAsync(result.SessionId, 2);
        Assert.Equal("completed", progressTail.LoopState);
        Assert.Equal(new long[] { 3, 4 }, progressTail.Messages.Select(item => item.Seq).ToArray());
        var progressAll = await host.ChatService.GetProgressAsync(result.SessionId, 0);
        Assert.Equal(4, progressAll.Messages.Count);
        var progressGhost = await host.ChatService.GetProgressAsync("ghost-session", 0);
        Assert.Equal("idle", progressGhost.LoopState);
        Assert.Empty(progressGhost.Messages);
    }

    /// <summary>验证循环异常结束：LLM 失败时 loopState=failed，会话与用户消息保留可重试。</summary>
    [Fact]
    public async Task Tool_loop_marks_failed_when_llm_errors_and_keeps_session()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var (session, _) = host.Sessions.GetOrCreate("preset-failed", "ai_main");
        host.LlmHandler.EnqueueError(500, "boom");

        await Assert.ThrowsAsync<PocAiLlmException>(() => host.ChatService.ChatAsync(
            new PocAiChatRequest("preset-failed", "ai_main", "第一次提问")));

        Assert.Equal(PocAiLoopState.Failed, session.LoopState);
        var (state, messages) = session.GetProgressSnapshot(0);
        Assert.Equal(PocAiLoopState.Failed, state);
        Assert.Single(messages);
        Assert.Equal(1, messages[0].Seq);
    }

    /// <summary>验证进度接口 fail-fast：AI 关闭时 GetProgressAsync 与 chat 同闸抛 AI_DISABLED。</summary>
    [Fact]
    public async Task GetProgress_when_ai_disabled_fails_fast()
    {
        using var host = new PocAiTestHost();

        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(
            () => host.ChatService.GetProgressAsync("any-session", 0));

        Assert.Contains(PocAiDisabledException.ErrorCode, exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
    }

    /// <summary>验证 Seq 与 50 条裁剪共存：裁剪只移除元素不回退计数器，新消息 Seq 持续递增。</summary>
    [Fact]
    public void Seq_keeps_increasing_after_history_trimming()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);
        var (session, _) = store.GetOrCreate(null, "ai_main");

        for (var index = 1; index <= 55; index++)
        {
            session.AppendMessages([new PocAiChatMessage(PocAiChatRole.User, $"消息{index}")]);
        }

        Assert.Equal(PocAiChatSessionStore.MaxMessages, session.Messages.Count);
        Assert.Equal(55, session.Messages[^1].Seq);
        session.AppendMessages([new PocAiChatMessage(PocAiChatRole.User, "消息56")]);
        Assert.Equal(50, session.Messages.Count);
        Assert.Equal(56, session.Messages[^1].Seq);
        // 裁剪后按 afterSeq 读取仍只回其后消息：Seq 是稳定游标，不受列表位移影响。
        var (_, afterTrim) = session.GetProgressSnapshot(55);
        Assert.Single(afterTrim);
        Assert.Equal(56, afterTrim[0].Seq);
    }

    /// <summary>验证轮询进度视图的形状：Seq 随消息下发，工具消息 content 保持折叠（与 chat 响应同构）。</summary>
    [Fact]
    public async Task Progress_view_carries_seq_and_keeps_tool_content_collapsed()
    {
        using var host = new PocAiTestHost();
        host.SeedDataTable("PatientInfo", 1);
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_v",
            PocAiToolRegistry.RunReadonlySql,
            """{"dataSourceCode":"ai_main","sql":"SELECT COUNT(1) AS Total FROM PatientInfo"}"""));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("结论……"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "查一下"));
        var progress = await host.ChatService.GetProgressAsync(result.SessionId, 0);

        Assert.Equal("completed", progress.LoopState);
        Assert.Equal(4, progress.Messages.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, progress.Messages.Select(item => item.Seq).ToArray());
        var toolView = progress.Messages[2];
        Assert.Equal("tool", toolView.Role);
        Assert.Null(toolView.Content);
        Assert.Equal(1, toolView.RowCount);
        Assert.NotNull(toolView.SqlSummary);
    }

    /// <summary>轮询等待循环进入 running 且已产出至少 minMessages 条消息；超时返回 null，避免测试死等。</summary>
    /// <param name="session">被观察的会话。</param>
    /// <param name="minMessages">期望至少可见的消息数。</param>
    /// <param name="timeout">总等待上限。</param>
    /// <returns>条件成立时的（状态, 消息）快照；超时返回 null。</returns>
    private static async Task<(PocAiLoopState State, IReadOnlyList<PocAiChatMessage> Messages)?> WaitForRunningWithMessagesAsync(
        PocAiChatSession session,
        int minMessages,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (state, messages) = session.GetProgressSnapshot(0);
            if (state == PocAiLoopState.Running && messages.Count >= minMessages)
            {
                return (state, messages);
            }
            await Task.Delay(25);
        }
        return null;
    }
}
