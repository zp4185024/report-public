using Furion.FriendlyException;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.AspNetCore.Http;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>
/// AI 助手 API（路由前缀 /api/report-center/ai）。
/// 读取/保存设置是“开关的载体”，不按 AI_DISABLED 拦截，否则 AI 永远无法被启用、
/// 前端也无法从 /settings 拿到开关；其余调用型接口（测试连通性、对话）在 AI 关闭或配置
/// 不完整时统一 fail-fast 返回 AI_DISABLED，绝不发起 LLM 请求或执行查数工具。
/// </summary>
[AllowAnonymous]
[ApiDescriptionSettings("报表中心 AI", Name = "ReportCenterAi", Order = 902)]
public sealed class PocAiAssistantApi : IDynamicApiController
{
    private readonly PocAiSettingsService _settingsService;
    private readonly PocAiChatService? _chatService;
    private readonly PocAiSyncMatchService? _syncMatchService;
    private readonly PocAiToolExecutor? _toolExecutor;

    /// <summary>初始化 PocAiAssistantApi 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="settingsService">AI 设置服务。</param>
    /// <param name="chatService">对话服务；依赖缺失时对话接口返回 503。</param>
    /// <param name="syncMatchService">同步智能匹配服务；依赖缺失时 sync-match 接口返回 503。</param>
    /// <param name="toolExecutor">只读查数工具执行器；依赖缺失时数据源表清单接口返回 503。</param>
    public PocAiAssistantApi(
        PocAiSettingsService settingsService,
        PocAiChatService? chatService = null,
        PocAiSyncMatchService? syncMatchService = null,
        PocAiToolExecutor? toolExecutor = null)
    {
        _settingsService = settingsService;
        _chatService = chatService;
        _syncMatchService = syncMatchService;
        _toolExecutor = toolExecutor;
    }

    /// <summary>
    /// 同步 AI 智能字段匹配：三层策略（同名基线 → LLM 歧义补齐 → 白名单转换链建议），
    /// 输出与 SyncColumnMapping 同构的建议列表 + 置信度/依据标注；服务端绝不落库不保存。
    /// AI 未启用或配置不完整时按 AI_DISABLED fail-fast，不发起任何 LLM 请求。
    /// </summary>
    /// <param name="request">匹配请求（直接载荷或 taskCode）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>建议列表、降级标记与降级原因。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/ai/sync-match")]
    public Task<PocAiSyncMatchResult> SyncMatch(
        [FromBody] PocAiSyncMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (_syncMatchService is null)
        {
            throw Oops.Oh("AI_SYNC_MATCH_SERVICE_NOT_AVAILABLE").StatusCode(503);
        }
        return HandleAiOperation(() => _syncMatchService.MatchAsync(request, cancellationToken));
    }

    /// <summary>读取 AI 设置（ApiKey 打码）；未配置时返回默认关闭配置，前端据此控制入口渲染。</summary>
    /// <returns>打码后的设置文档。</returns>
    [SuppressMonitor]
    [HttpGet("/api/report-center/ai/settings")]
    public Task<PocAiSettingsDocument> GetSettings() => _settingsService.GetDocumentAsync();

    /// <summary>保存 AI 设置；ApiKey 传空表示保留原值。保存本身不受 AI 开关限制——这是开启 AI 的唯一入口，不走 AI_DISABLED 守卫，否则关闭态下将永远无法启用。</summary>
    /// <param name="request">设置输入。</param>
    /// <returns>保存后的打码设置文档。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/ai/settings")]
    public Task<PocAiSettingsDocument> SaveSettings([FromBody] PocAiSettingsInput request) => _settingsService.SaveAsync(request);

    /// <summary>对已保存配置做连通性测试；AI 未启用或配置不完整时按 AI_DISABLED fail-fast。</summary>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>测试结果。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/ai/settings/test")]
    public Task<PocAiConnectionTestResult> TestSettings(CancellationToken cancellationToken) =>
        HandleAiOperation(() => _settingsService.TestConnectionAsync(cancellationToken));

    /// <summary>模板 AI 对话：一次回复内完成工具循环；LLM 失败保留会话，前端可原样重试。</summary>
    /// <param name="request">对话请求（sessionId 可空表示新会话）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>本轮新增消息与会话号。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/ai/chat")]
    public Task<PocAiChatResult> Chat([FromBody] PocAiChatRequest request, CancellationToken cancellationToken)
    {
        if (_chatService is null)
        {
            throw Oops.Oh("AI_CHAT_SERVICE_NOT_AVAILABLE").StatusCode(503);
        }
        return HandleAiOperation(() => _chatService.ChatAsync(request, cancellationToken));
    }

    /// <summary>
    /// 预创建空 AI 会话（实时体验补强）：前端在面板打开且选定数据源后立即建号，
    /// 此后每一轮对话（含首轮）从发消息瞬间开始按 Seq 轮询 progress，
    /// 消除“新会话首轮不可轮询”导致的长时间无反馈。AI 关闭或配置不完整时按
    /// AI_DISABLED fail-fast，不建会话；数据源缺失/停用返回 400。
    /// </summary>
    /// <param name="request">预创建请求（数据源编码）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>会话编号与绑定数据源。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/ai/sessions")]
    public Task<PocAiSessionPreCreateDocument> CreateSession(
        [FromBody] PocAiSessionPreCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (_chatService is null)
        {
            throw Oops.Oh("AI_CHAT_SERVICE_NOT_AVAILABLE").StatusCode(503);
        }
        return HandleAiOperation(() => _chatService.PreCreateSessionAsync(request, cancellationToken));
    }

    /// <summary>显式结束会话并清空内存上下文；会话不存在时同样返回成功，保证幂等。</summary>
    /// <param name="id">会话编号。</param>
    /// <returns>关闭结果。</returns>
    [SuppressMonitor]
    [HttpDelete("/api/report-center/ai/sessions/{id}")]
    public Task<PocAiSessionClosedDocument> CloseSession([FromRoute] string id) =>
        HandleAiOperation(() => Task.FromResult(
            new PocAiSessionClosedDocument(id, _chatService?.CloseSession(id) ?? false)));

    /// <summary>
    /// 插话纠正（中途方向修正）：向指定会话追加一条用户消息。运行中的工具循环在下一轮
    /// 组装 llmMessages 时自然读到该插话——模型即收到方向纠正，不必等本轮失败后从头重来；
    /// 循环未运行时同样允许追加，作为下一轮对话的既有上下文。AI 关闭 fail-fast；
    /// 会话不存在/已过期按 AI_SESSION_NOT_FOUND 映射 400，内容为空按 AI_MESSAGE_REQUIRED 映射 400。
    /// </summary>
    /// <param name="id">目标会话编号。</param>
    /// <param name="request">插话请求（message 为方向纠正文本）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>追加后的消息视图（含 Seq，供前端对齐消息流）。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/ai/sessions/{id}/guidance")]
    public Task<PocAiChatMessageView> AppendSessionGuidance(
        [FromRoute] string id,
        [FromBody] PocAiSessionGuidanceRequest request,
        CancellationToken cancellationToken)
    {
        if (_chatService is null)
        {
            throw Oops.Oh("AI_CHAT_SERVICE_NOT_AVAILABLE").StatusCode(503);
        }
        return HandleAiOperation(() => _chatService.AppendGuidanceAsync(id, request.Message, cancellationToken));
    }

    /// <summary>
    /// 数据源表清单（方案 A 透明化）：把静默注入给模型的“AI 眼中的数据源”直接展示给用户。
    /// 复用 PocAiToolExecutor 的 list_tables 公共取数（含 AI 启用门控），返回名称/类型/注释；
    /// 元数据只读、不落审计，会话不存在也能查（数据源维度而非会话维度）。
    /// </summary>
    /// <param name="code">目标数据源编码。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表/视图条目列表。</returns>
    [SuppressMonitor]
    [HttpGet("/api/report-center/ai/data-sources/{code}/tables")]
    public Task<IReadOnlyList<PocAiDataSourceTable>> ListDataSourceTables(
        [FromRoute] string code,
        CancellationToken cancellationToken)
    {
        if (_toolExecutor is null)
        {
            throw Oops.Oh("AI_TOOL_SERVICE_NOT_AVAILABLE").StatusCode(503);
        }
        return HandleAiOperation(() => _toolExecutor.LoadDataSourceTablesAsync(code, includeViews: true, cancellationToken));
    }

    /// <summary>
    /// 工具循环进度轮询（方案 B 实时化）：POST /chat 语义不变，前端并行轮询本接口按 Seq 渲染增量消息。
    /// AI 关闭 fail-fast；会话不存在/已过期返回 idle 空列表，由 POST /chat 的最终响应校正。
    /// </summary>
    /// <param name="sessionId">会话编号。</param>
    /// <param name="afterSeq">前端已渲染的最大 Seq，传 0 表示取全部。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>循环状态与增量消息。</returns>
    [SuppressMonitor]
    [HttpGet("/api/report-center/ai/chat/progress/{sessionId}")]
    public Task<PocAiChatProgressDocument> ChatProgress(
        [FromRoute] string sessionId,
        [FromQuery] long afterSeq = 0,
        CancellationToken cancellationToken = default)
    {
        if (_chatService is null)
        {
            throw Oops.Oh("AI_CHAT_SERVICE_NOT_AVAILABLE").StatusCode(503);
        }
        return HandleAiOperation(() => _chatService.GetProgressAsync(sessionId, afterSeq, cancellationToken));
    }

    /// <summary>AI 接口统一异常翻译：AI 关闭降级映射 403，配置与参数错误映射 400，LLM 失败映射 502 且保留会话语义；客户端中止映射受控 499。</summary>
    /// <param name="operation">待执行的 AI 操作。</param>
    /// <returns>操作结果，异常时抛出 FriendlyException。</returns>
    private static async Task<T> HandleAiOperation<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            // 客户端中止（浏览器关闭或“停止按钮”abort fetch）带来的取消异常不能以原始形态穿透到
            // Furion 异常管道：否则会被当 500 记 Error 日志（含完整堆栈），并连带触发错误邮件等
            // 后台旁路。这里统一翻译为受控 FriendlyException(499)，按 BahLogLevel（Information）
            // 记日志；连接已断开所以客户端不可感知，服务端只留下安静的可控翻译。
            throw ClientAborted();
        }
        catch (PocAiDisabledException exception)
        {
            // AI 关闭是配置态而非故障：返回 403 + 稳定错误码 AI_DISABLED，前端零感知地隐藏入口。
            throw Oops.Oh(exception.Message).StatusCode(403);
        }
        catch (PocAiLlmException exception)
        {
            // LLM 侧失败按上游网关错误处理；会话已保留，前端提示用户重试。
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(502);
        }
        catch (PocDatasetConfigurationException exception)
        {
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(400);
        }
        catch (PocAiToolRejectException exception)
        {
            // 工具拒绝类失败（数据源不存在/停用等）是请求侧错误：映射 400 并保留稳定错误码，避免裸 500。
            throw Oops.Oh($"{exception.Code}: {exception.Message}").StatusCode(400);
        }
    }

    /// <summary>
    /// 构造“客户端中止”的受控业务异常：499 语义（连接已断，客户端不可感知），错误码 AI_REQUEST_ABORTED。
    /// 直接构造 AppFriendlyException 而不是走 Oops.Oh，保证无 Furion 宿主环境的单测也能构造与断言，
    /// 运行时行为（MVC 管道按 StatusCode 返回、按 BahLogLevel 记日志）与 Oops.Oh+StatusCode 完全一致。
    /// </summary>
    /// <returns>StatusCode=499 的 FriendlyException 实例。</returns>
    internal static AppFriendlyException ClientAborted() =>
        new("AI_REQUEST_ABORTED", "AI_REQUEST_ABORTED") { StatusCode = 499 };
}

/// <summary>会话关闭回执；closed=false 表示会话已不存在或早已过期，同样视为关闭成功。</summary>
public sealed record PocAiSessionClosedDocument(string SessionId, bool Closed);
