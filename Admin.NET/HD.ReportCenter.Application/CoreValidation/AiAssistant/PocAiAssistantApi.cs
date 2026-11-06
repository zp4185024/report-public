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

    /// <summary>初始化 PocAiAssistantApi 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="settingsService">AI 设置服务。</param>
    /// <param name="chatService">对话服务；依赖缺失时对话接口返回 503。</param>
    /// <param name="syncMatchService">同步智能匹配服务；依赖缺失时 sync-match 接口返回 503。</param>
    public PocAiAssistantApi(
        PocAiSettingsService settingsService,
        PocAiChatService? chatService = null,
        PocAiSyncMatchService? syncMatchService = null)
    {
        _settingsService = settingsService;
        _chatService = chatService;
        _syncMatchService = syncMatchService;
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

    /// <summary>保存 AI 设置；ApiKey 传空表示保留原值。保存本身不受 AI 开关限制，这是开启 AI 的唯一入口。</summary>
    /// <param name="request">设置输入。</param>
    /// <returns>保存后的打码设置文档。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/ai/settings")]
    public Task<PocAiSettingsDocument> SaveSettings([FromBody] PocAiSettingsInput request) =>
        HandleAiOperation(() => _settingsService.SaveAsync(request));

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

    /// <summary>显式结束会话并清空内存上下文；会话不存在时同样返回成功，保证幂等。</summary>
    /// <param name="id">会话编号。</param>
    /// <returns>关闭结果。</returns>
    [SuppressMonitor]
    [HttpDelete("/api/report-center/ai/sessions/{id}")]
    public Task<PocAiSessionClosedDocument> CloseSession([FromRoute] string id) =>
        HandleAiOperation(() => Task.FromResult(
            new PocAiSessionClosedDocument(id, _chatService?.CloseSession(id) ?? false)));

    /// <summary>AI 接口统一异常翻译：AI 关闭降级映射 403，配置与参数错误映射 400，LLM 失败映射 502 且保留会话语义。</summary>
    /// <param name="operation">待执行的 AI 操作。</param>
    /// <returns>操作结果，异常时抛出 FriendlyException。</returns>
    private static async Task<T> HandleAiOperation<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
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
    }
}

/// <summary>会话关闭回执；closed=false 表示会话已不存在或早已过期，同样视为关闭成功。</summary>
public sealed record PocAiSessionClosedDocument(string SessionId, bool Closed);
