using System.Collections.Concurrent;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>AI 报表草稿的生命周期状态；failed 表示最近一次编译或校验失败，等待模型自修。</summary>
public enum PocAiDraftStatus
{
    /// <summary>模型已提交 spec，编译进行中或编译刚通过。</summary>
    Submitted,

    /// <summary>编译 + 全链校验通过，草稿卡片可进入校验/试跑/导入流程。</summary>
    Validated,

    /// <summary>已通过一次受限试跑（最多 50 行 / 30 秒）。</summary>
    DryRunOk,

    /// <summary>最近一次提交或校验失败；错误列表已回传模型。</summary>
    Failed
}

/// <summary>
/// 会话上的报表草稿：spec 原文、编译产物、全链校验结果与状态。
/// 草稿只存在于内存会话中，随会话过期/关闭一并消失；真正落库由用户通过前端
/// 草稿卡片复用 reportPackageApi（validate/dryRun/import）完成，不经过 /ai/* 接口。
/// </summary>
/// <param name="SpecJson">模型提交的 spec JSON 原文。</param>
/// <param name="Package">C# 编译器产出的报表包。</param>
/// <param name="ValidateResult">最近一次全链校验报告；编译失败时为 null。</param>
/// <param name="Status">草稿状态。</param>
/// <param name="DryRun">最近一次试跑结果；未试跑时为 null。</param>
public sealed record PocAiChatDraft(
    string SpecJson,
    PocReportPackage Package,
    PocReportPackageValidateResult? ValidateResult,
    PocAiDraftStatus Status,
    PocReportPackageDryRunResult? DryRun = null);

/// <summary>AI 消息角色；tool 消息承载工具回传结果，assistant 消息可能携带 tool_calls。</summary>
public enum PocAiChatRole
{
    /// <summary>系统提示词；不落会话历史，每次请求按当前配置重建。</summary>
    System,

    /// <summary>用户输入。</summary>
    User,

    /// <summary>模型回复；可携带 tool_calls。</summary>
    Assistant,

    /// <summary>工具执行结果回传。</summary>
    Tool
}

/// <summary>会话历史中的单条消息；工具消息附加 ToolCallId/ToolName 与展示用元数据。</summary>
public sealed record PocAiChatMessage(
    PocAiChatRole Role,
    string? Content,
    IReadOnlyList<PocLlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? DataSourceCode = null,
    string? SqlText = null,
    int? RowCount = null,
    bool Success = true)
{
    /// <summary>
    /// 会话内单调递增序号（从 1 开始，由会话在追加时分配）：前端轮询进度按它做“取其后消息”与去重。
    /// 历史裁剪只移除列表元素，不回退计数器，保证跨轮次不出现重复 Seq。
    /// </summary>
    public long Seq { get; internal set; }
}

/// <summary>会话工具循环的实时状态：前端据此在 POST /chat 返回前轮询渲染增量消息。</summary>
public enum PocAiLoopState
{
    /// <summary>当前没有进行中的工具循环（空闲或循环尚未开始）。</summary>
    Idle,

    /// <summary>工具循环进行中：模型调用与工具结果正逐条写入会话。</summary>
    Running,

    /// <summary>本轮工具循环正常结束（模型给出结论性回复）。</summary>
    Completed,

    /// <summary>本轮工具循环异常结束（LLM 失败或取消）；会话保留，可原样重试。</summary>
    Failed
}

/// <summary>内存 AI 会话：绑定发起时的数据源，保存裁剪后的消息历史与活跃时间。</summary>
public sealed class PocAiChatSession
{
    private readonly object _messageLock = new();

    /// <summary>会话编号；由会话存储生成，前端按它延续同一上下文。</summary>
    public string Id { get; }

    /// <summary>发起会话时选择的数据源编码；决定上下文注入与工具默认数据源。</summary>
    public string DataSourceCode { get; }

    /// <summary>最近一次活跃时间（本地时间）；空闲超过上限即过期清理。</summary>
    public DateTime LastActiveAt { get; internal set; }

    /// <summary>会话消息历史（已裁剪）；system 提示词不入历史。</summary>
    public List<PocAiChatMessage> Messages { get; } = [];

    /// <summary>下一个待分配的消息序号；只增不减，裁剪历史不影响它。</summary>
    private long _nextSeq = 1;

    /// <summary>当前工具循环状态；与消息共用同一把锁，保证轮询读到状态与消息的一致快照。</summary>
    private PocAiLoopState _loopState = PocAiLoopState.Idle;

    /// <summary>当前工具循环状态（只读快照）；线程安全，供进度轮询接口读取。</summary>
    public PocAiLoopState LoopState
    {
        get
        {
            lock (_messageLock)
            {
                return _loopState;
            }
        }
    }

    /// <summary>
    /// 会话累积的参考材料（附件解析产物）：只存结构化描述，附件原始内容永不落库，
    /// 随会话 30 分钟空闲过期一并消失。超出上限裁剪最早的材料，防止提示词无限膨胀。
    /// </summary>
    private readonly List<PocAiReferenceMaterial> _referenceMaterials = [];

    /// <summary>追加参考材料并按会话上限裁剪最早的材料；加锁保证并发回复时不撕裂。</summary>
    /// <param name="materials">本次消息附件解析出的参考材料。</param>
    internal void AppendReferenceMaterials(IEnumerable<PocAiReferenceMaterial> materials)
    {
        lock (_messageLock)
        {
            _referenceMaterials.AddRange(materials);
            while (_referenceMaterials.Count > PocAiReferenceMaterial.MaxMaterialsPerSession)
            {
                _referenceMaterials.RemoveAt(0);
            }
        }
    }

    /// <summary>读取当前会话的全部参考材料快照；为系统提示词注入提供只读视图。</summary>
    /// <returns>参考材料列表快照。</returns>
    public IReadOnlyList<PocAiReferenceMaterial> GetReferenceMaterials()
    {
        lock (_messageLock)
        {
            return _referenceMaterials.ToList();
        }
    }

    /// <summary>当前挂载的报表草稿；未提交过或会话重建时为 null。</summary>
    public PocAiChatDraft? Draft { get; private set; }

    /// <summary>本会话内 submit_report_spec 连续失败的次数；达到 3 次触发熔断，提示人工介入。</summary>
    public int FailedSubmitCount { get; private set; }

    /// <summary>初始化会话实体：登记编号、绑定数据源并记录创建时刻作为首个活跃时间。</summary>
    /// <param name="id">会话编号。</param>
    /// <param name="dataSourceCode">会话绑定的数据源编码。</param>
    /// <param name="now">会话创建时间（由会话存储的时钟提供）。</param>
    internal PocAiChatSession(string id, string dataSourceCode, DateTime now)
    {
        Id = id;
        DataSourceCode = dataSourceCode;
        LastActiveAt = now;
    }

    /// <summary>追加一批消息并按上限裁剪最早的非系统消息；加锁保证同一会话并发回复时历史不撕裂。</summary>
    /// <param name="messages">本次追加的消息序列。</param>
    internal void AppendMessages(IEnumerable<PocAiChatMessage> messages)
    {
        lock (_messageLock)
        {
            // 逐条分配会话内单调递增 Seq：轮询接口与前端去重都以它为准；裁剪只移除元素，计数器不回退。
            foreach (var message in messages)
            {
                message.Seq = _nextSeq++;
                Messages.Add(message);
            }
            while (Messages.Count > PocAiChatSessionStore.MaxMessages)
            {
                // 裁剪最早的非系统消息；全部为系统消息时退化为裁剪第一条，保证历史不会无限增长。
                var index = Messages.FindIndex(item => item.Role != PocAiChatRole.System);
                Messages.RemoveAt(index < 0 ? 0 : index);
            }
        }
    }

    /// <summary>标记本轮工具循环开始：此后工具循环内产生的消息可被进度轮询接口实时读到。</summary>
    internal void BeginLoop()
    {
        lock (_messageLock)
        {
            _loopState = PocAiLoopState.Running;
        }
    }

    /// <summary>标记本轮工具循环正常结束；状态保持到下一轮开始，重复轮询可安全读到 completed。</summary>
    internal void CompleteLoop()
    {
        lock (_messageLock)
        {
            _loopState = PocAiLoopState.Completed;
        }
    }

    /// <summary>标记本轮工具循环异常结束（LLM 失败/取消）；会话与已产生的消息保留，前端展示 failed 态。</summary>
    internal void FailLoop()
    {
        lock (_messageLock)
        {
            _loopState = PocAiLoopState.Failed;
        }
    }

    /// <summary>
    /// 按 Seq 截取其后消息的只读快照：进度轮询的核心读法，返回 (Seq &gt; afterSeq) 的消息。
    /// 与 LoopState 在同一把锁下读取，轮询拿到的是“状态 + 消息”的一致快照。
    /// </summary>
    /// <param name="afterSeq">已读取到的最大 Seq；传 0 表示从头取。</param>
    /// <returns>循环状态与 Seq 之后的消息列表快照。</returns>
    public (PocAiLoopState State, IReadOnlyList<PocAiChatMessage> Messages) GetProgressSnapshot(long afterSeq)
    {
        lock (_messageLock)
        {
            var messages = Messages.Where(item => item.Seq > afterSeq).ToList();
            return (_loopState, messages);
        }
    }

    /// <summary>挂载或替换会话草稿；成功提交（validated/dryRunOk）会清零失败计数，给模型自修重来的机会。</summary>
    /// <param name="draft">新草稿；传 null 表示清空。</param>
    internal void SetDraft(PocAiChatDraft? draft)
    {
        lock (_messageLock)
        {
            Draft = draft;
            if (draft is not null && draft.Status is not PocAiDraftStatus.Failed)
            {
                FailedSubmitCount = 0;
            }
        }
    }

    /// <summary>登记一次失败的 spec 提交；由对话服务在编译或校验失败时调用，驱动 3 次熔断。</summary>
    internal void RegisterSubmitFailure()
    {
        lock (_messageLock)
        {
            FailedSubmitCount++;
        }
    }
}

/// <summary>
/// AI 聊天会话的进程内存储（ConcurrentDictionary）：会话不落库，空闲 30 分钟过期，
/// 消息历史上限 50 条。服务重启即全部丢失，属阶段一已知边界。
/// </summary>
public sealed class PocAiChatSessionStore : ISingleton
{
    /// <summary>单会话消息历史上限；超出裁剪最早的非系统消息。</summary>
    public const int MaxMessages = 50;

    /// <summary>会话空闲过期时长；前端提示“会话已过期，AI 上下文已清空”。</summary>
    public static readonly TimeSpan IdleExpiration = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, PocAiChatSession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;

    /// <summary>初始化 PocAiChatSessionStore 实例；生产使用系统时钟，测试注入假时钟驱动过期。</summary>
    /// <param name="clock">时间来源委托，缺省取 DateTime.Now。</param>
    public PocAiChatSessionStore(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>按编号取会话；命中但已过期的会话被移除并返回空，使调用方按“过期”分支处理。</summary>
    /// <param name="sessionId">会话编号，可为空（等价于不存在）。</param>
    /// <returns>活跃会话；过期或不存在返回 null。</returns>
    public PocAiChatSession? Get(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        RemoveExpiredSessions();
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }
        if (_clock() - session.LastActiveAt > IdleExpiration)
        {
            _ = _sessions.TryRemove(sessionId, out _);
            return null;
        }
        return session;
    }

    /// <summary>取既有会话或创建新会话；创建时必须给定数据源编码，返回是否新建，供前端感知“过期重建”。</summary>
    /// <param name="sessionId">前端携带的会话编号，可为空表示新会话。</param>
    /// <param name="dataSourceCode">新会话绑定的数据源编码。</param>
    /// <returns>会话实体与是否新建的标记。</returns>
    public (PocAiChatSession Session, bool Created) GetOrCreate(string? sessionId, string dataSourceCode)
    {
        RemoveExpiredSessions();
        var existing = Get(sessionId);
        if (existing is not null)
        {
            existing.LastActiveAt = _clock();
            return (existing, false);
        }

        var id = sessionId;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"ai-{Guid.NewGuid():N}";
        }

        var session = new PocAiChatSession(id, dataSourceCode, _clock());
        _sessions[id] = session;
        return (session, true);
    }

    /// <summary>显式结束会话：从内存移除上下文；编号不存在时同样返回成功，保证幂等。</summary>
    /// <param name="sessionId">待关闭的会话编号。</param>
    /// <returns>是否关闭了现存会话。</returns>
    public bool Delete(string sessionId)
    {
        RemoveExpiredSessions();
        return _sessions.TryRemove(sessionId, out _);
    }

    /// <summary>当前存活的会话数量；仅测试与诊断使用。</summary>
    /// <returns>会话计数。</returns>
    public int CountActive()
    {
        RemoveExpiredSessions();
        return _sessions.Count;
    }

    /// <summary>清理已过期的全部会话；每次访问机会式触发，避免引入后台定时器。</summary>
    private void RemoveExpiredSessions()
    {
        var now = _clock();
        foreach (var pair in _sessions)
        {
            if (now - pair.Value.LastActiveAt > IdleExpiration)
            {
                _ = _sessions.TryRemove(pair.Key, out _);
            }
        }
    }
}
