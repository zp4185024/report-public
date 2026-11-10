using HD.ReportCenter.Application.CoreValidation.AiAssistant;

namespace HD.ReportCenter.Tests;

/// <summary>AI 会话存储单测：30 分钟空闲过期、50 条历史上限裁剪与显式关闭幂等。</summary>
public sealed class PocAiChatSessionStoreTests
{
    /// <summary>验证会话空闲 30 分钟后过期：Get 返回空、计数清零，过期会话不再可复用。</summary>
    [Fact]
    public void Session_expires_after_thirty_minutes_idle()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);
        var (session, created) = store.GetOrCreate(null, "ai_main");
        Assert.True(created);

        now = now.AddMinutes(29);
        Assert.NotNull(store.Get(session.Id));
        Assert.Equal(1, store.CountActive());

        now = now.AddMinutes(2);
        Assert.Null(store.Get(session.Id));
        Assert.Equal(0, store.CountActive());
    }

    /// <summary>验证活跃续聊滑动续期：只有 GetOrCreate（真实续聊路径）刷新活跃时间，探测类 Get 不续期。</summary>
    [Fact]
    public void Session_activity_slides_expiration_forward()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);
        var (session, _) = store.GetOrCreate(null, "ai_main");

        for (var step = 0; step < 5; step++)
        {
            now = now.AddMinutes(20);
            // 模拟一次续聊：GetOrCreate 刷新 LastActiveAt，使会话在 30 分钟边界不被清掉。
            var (sliding, created) = store.GetOrCreate(session.Id, "ai_main");
            Assert.False(created);
            Assert.NotNull(sliding);
        }
        Assert.Equal(1, store.CountActive());
    }

    /// <summary>验证探测访问不滑动过期：长期只读探测不会把死会话一直留在内存里。</summary>
    [Fact]
    public void Probe_access_does_not_slide_expiration()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);
        var (session, _) = store.GetOrCreate(null, "ai_main");

        for (var step = 0; step < 2; step++)
        {
            now = now.AddMinutes(10);
            Assert.NotNull(store.Get(session.Id));
        }

        now = now.AddMinutes(11);
        Assert.Null(store.Get(session.Id));
    }

    /// <summary>验证消息历史上限 50 条：超出裁剪最早的非系统消息，system 消息优先保留。</summary>
    [Fact]
    public void History_trimmed_to_fifty_messages_earliest_first()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);
        var (session, _) = store.GetOrCreate(null, "ai_main");

        session.AppendMessages([new PocAiChatMessage(PocAiChatRole.System, "系统提示词")]);
        for (var index = 1; index <= 55; index++)
        {
            session.AppendMessages([new PocAiChatMessage(PocAiChatRole.User, $"消息{index}")]);
        }

        Assert.Equal(PocAiChatSessionStore.MaxMessages, session.Messages.Count);
        // system 保留在首位，最早的用户消息（1-6）被裁剪，第 7 条起保留。
        Assert.Equal(PocAiChatRole.System, session.Messages[0].Role);
        Assert.Equal("消息7", session.Messages[1].Content);
        Assert.Equal("消息55", session.Messages[^1].Content);
    }

    /// <summary>验证全部为系统消息时的兜底裁剪：历史不会无限增长。</summary>
    [Fact]
    public void History_trims_system_messages_when_nothing_else_exists()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);
        var (session, _) = store.GetOrCreate(null, "ai_main");
        session.AppendMessages(
            Enumerable.Range(1, 60)
                .Select(index => new PocAiChatMessage(PocAiChatRole.System, $"系统{index}"))
                .ToArray());

        Assert.Equal(PocAiChatSessionStore.MaxMessages, session.Messages.Count);
        Assert.Equal("系统11", session.Messages[0].Content);
    }

    /// <summary>验证显式关闭与不存在会话的幂等语义，以及空编号直接按“不存在”处理。</summary>
    [Fact]
    public void Delete_and_get_are_idempotent()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);
        var (session, _) = store.GetOrCreate(null, "ai_main");

        Assert.True(store.Delete(session.Id));
        Assert.False(store.Delete(session.Id));
        Assert.Null(store.Get(string.Empty));
        Assert.Null(store.Get(null));
        Assert.Null(store.Get("never-created"));
    }

    /// <summary>
    /// 验证预创建入口（实时体验补强）：登记绑定数据源的空会话、循环状态空闲，
    /// 且可被既有 GetOrCreate（真实续聊路径）按 id 命中——首轮 /chat 因此可无缝续用预创建编号。
    /// </summary>
    [Fact]
    public void PreCreate_registers_empty_session_bound_to_data_source()
    {
        var now = DateTime.Now;
        var store = new PocAiChatSessionStore(() => now);

        var session = store.PreCreate("lis_db");

        Assert.StartsWith("ai-", session.Id);
        Assert.Equal("lis_db", session.DataSourceCode);
        Assert.Equal(now, session.LastActiveAt);
        Assert.Empty(session.Messages);
        Assert.Equal(PocAiLoopState.Idle, session.LoopState);
        // 预创建会话必须能被 GetOrCreate 按 id 复用而不是再建一个；空会话上的进度轮询快照安全（idle + 零增量）。
        var (reused, created) = store.GetOrCreate(session.Id, "lis_db");
        Assert.False(created);
        Assert.Same(session, reused);
        var (state, messages) = session.GetProgressSnapshot(0);
        Assert.Equal(PocAiLoopState.Idle, state);
        Assert.Empty(messages);
    }
}
