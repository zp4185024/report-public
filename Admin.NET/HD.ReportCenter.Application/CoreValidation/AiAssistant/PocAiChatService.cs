using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>一次 AI 对话请求：会话号为空表示新建会话；数据源决定上下文注入与工具默认数据源；
/// 附件（base64 内联，≤3 个）解析为「参考材料」注入系统提示词，内容不落库。</summary>
public sealed record PocAiChatRequest(
    string? SessionId,
    string DataSourceCode,
    string Message,
    IReadOnlyList<PocAiChatAttachment>? Attachments = null);

/// <summary>回传前端展示的单条消息；工具消息携带工具名/SQL 摘要/行数，供折叠条渲染；Seq 供进度轮询去重。</summary>
public sealed record PocAiChatMessageView(
    string Role,
    string? Content,
    string? ToolName,
    string? ToolCallId,
    string? SqlSummary,
    int? RowCount,
    bool Success,
    bool Truncated,
    long Seq = 0);

/// <summary>会话预创建请求：仅携带数据源编码；服务端创建空会话（无消息历史）并返回编号。</summary>
public sealed record PocAiSessionPreCreateRequest(string DataSourceCode);

/// <summary>会话预创建回执：空会话编号与绑定数据源；前端后续每轮 /chat 直接携带该编号。</summary>
public sealed record PocAiSessionPreCreateDocument(string SessionId, string DataSourceCode);

/// <summary>会话插话纠正请求：一条以用户身份追加的方向纠正文本；运行中的工具循环下一轮即读到。</summary>
public sealed record PocAiSessionGuidanceRequest(string Message);

/// <summary>一次对话的响应：会话号、本轮新增消息、会话是否刚被判定过期重建，以及当前草稿卡片（可为空）。</summary>
public sealed record PocAiChatResult(
    string SessionId,
    IReadOnlyList<PocAiChatMessageView> Messages,
    bool Expired,
    PocAiChatDraftView? Draft);

/// <summary>
/// 工具循环进度轮询文档：循环状态 + afterSeq 之后的新增消息。
/// 会话不存在/已过期时返回 idle 空列表（不报错），前端以 POST /chat 的最终响应为准。
/// </summary>
/// <param name="LoopState">idle / running / completed / failed（小写字符串，与前端契约一致）。</param>
/// <param name="Messages">Seq 大于 afterSeq 的展示消息（按 Seq 升序）。</param>
public sealed record PocAiChatProgressDocument(
    string LoopState,
    IReadOnlyList<PocAiChatMessageView> Messages);

/// <summary>
/// 下发给前端草稿卡片的草稿视图：spec 原文、编译产物包、最近一次全链校验结果与状态。
/// 校验/试跑/导入由前端直接复用 reportPackageApi 完成，不经任何 /ai/* 接口。
/// </summary>
/// <param name="Status">submitted / validated / dryRunOk / failed。</param>
/// <param name="Spec">spec 原文（JSON 对象）。</param>
/// <param name="Package">编译产物报表包（JSON 对象）。</param>
/// <param name="ValidateResult">最近一次校验报告；编译失败时为 null。</param>
public sealed record PocAiChatDraftView(
    string Status,
    object? Spec,
    object? Package,
    PocReportPackageValidateResult? ValidateResult);

/// <summary>
/// 模板 AI 对话服务：维护“LLM 调用 ↔ 工具执行”循环，轮数上限 MaxToolRounds，
/// 超限强制要求模型给出结论性回复；LLM 请求失败保留会话并抛可重试业务错误。
/// 对话链路不设每调用硬超时（仅由调用方取消令牌中止），并支持运行中插话纠正：
/// 每轮从会话快照重建 llmMessages，guidance 追加的用户消息下一轮即被模型读到；
/// 发送前对早期轮次工具结果做上下文压缩，控制长会话的请求体积。
/// </summary>
public sealed class PocAiChatService : ITransient
{
    /// <summary>同一会话允许的连续失败提交次数上限；达到后熔断 submit 工具并提示人工介入。</summary>
    public const int MaxSubmitFailures = 3;

    /// <summary>上下文压缩保持全文回传的最近工具轮数；更早轮次的超长 tool 结果截为前缀摘要。</summary>
    internal const int FullToolResultRounds = 2;

    /// <summary>早期工具结果在 LLM 请求中保留的字符数；只影响发送历史，会话存储与前端展示不动。</summary>
    internal const int CompressedToolContentLength = 600;

    /// <summary>早期工具结果被截断后追加的标记文本，明确告知模型该结果不完整。</summary>
    internal const string CompressedToolContentSuffix = "\n…(早期工具结果已截断)";

    private readonly PocAiSettingsService _settingsService;
    private readonly PocAiChatSessionStore _sessionStore;
    private readonly PocAiToolExecutor _toolExecutor;
    private readonly PocAiAuditStore _auditStore;
    private readonly PocLlmClient _llmClient;
    private readonly PocAiVisionAnalyzer _visionAnalyzer;
    private readonly PocDataAssetStore _dataAssetStore;
    private readonly CreateDatasetClient _clientFactory;
    private readonly PocAiSpecCompiler _specCompiler;
    private readonly PocReportPackageService? _packageService;

    /// <summary>初始化 PocAiChatService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="settingsService">AI 设置服务，负责启用守卫与配置读取。</param>
    /// <param name="sessionStore">内存会话存储。</param>
    /// <param name="toolExecutor">只读查数工具执行器。</param>
    /// <param name="auditStore">工具审计存储。</param>
    /// <param name="llmClient">OpenAI 兼容 LLM 客户端。</param>
    /// <param name="visionAnalyzer">图片附件视觉分析器（阶段四附件参考）；缺省按主链路客户端自动构造。</param>
    /// <param name="dataAssetStore">全局数据源存储，用于构建上下文注入摘要。</param>
    /// <param name="clientFactory">提示词表清单读取用的连接工厂；缺省走 PocDatabaseProvider，测试可注入内存库。</param>
    /// <param name="specCompiler">spec→报表包编译器（阶段二 submit/dry_run 工具依赖）。</param>
    /// <param name="packageService">报表包服务，提供全链校验与受限试跑；缺失时产物工具返回结构化错误。</param>
    public PocAiChatService(
        PocAiSettingsService settingsService,
        PocAiChatSessionStore sessionStore,
        PocAiToolExecutor toolExecutor,
        PocAiAuditStore auditStore,
        PocLlmClient llmClient,
        PocAiVisionAnalyzer? visionAnalyzer,
        PocDataAssetStore dataAssetStore,
        CreateDatasetClient? clientFactory = null,
        PocAiSpecCompiler? specCompiler = null,
        PocReportPackageService? packageService = null)
    {
        _settingsService = settingsService;
        _sessionStore = sessionStore;
        _toolExecutor = toolExecutor;
        _auditStore = auditStore;
        _llmClient = llmClient;
        _visionAnalyzer = visionAnalyzer ?? new PocAiVisionAnalyzer(llmClient);
        _dataAssetStore = dataAssetStore;
        _clientFactory = clientFactory ?? PocDatabaseProvider.CreateClient;
        _specCompiler = specCompiler ?? new PocAiSpecCompiler();
        _packageService = packageService;
    }

    /// <summary>处理一轮对话：守卫 AI 开关 → 维护会话 → 工具循环（≤MaxToolRounds）→ 返回本轮新增消息。</summary>
    /// <param name="request">对话请求。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>本轮新增的展示消息与会话号。</returns>
    public async Task<PocAiChatResult> ChatAsync(PocAiChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // AI 启用守卫放在一切副作用之前：未启用/配置不完整时不建会话、不查数、不发 LLM 请求。
        var settings = await _settingsService.RequireEnabledAsync();
        var message = (request.Message ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            throw new PocDatasetConfigurationException("AI_MESSAGE_REQUIRED", "对话内容不能为空。");
        }
        var dataSourceCode = (request.DataSourceCode ?? string.Empty).Trim();
        if (dataSourceCode.Length == 0)
        {
            throw new PocDatasetConfigurationException("AI_DATA_SOURCE_REQUIRED", "发起对话必须选择数据源。");
        }

        // 会话重建：前端携带的会话号已不存在（空闲过期或服务重启）时按新会话重建，
        // 复用同一编号保持前端连续性，并把 expired 标记回传前端提示“上下文已清空”。
        var providedSessionId = !string.IsNullOrWhiteSpace(request.SessionId);
        var existing = _sessionStore.Get(request.SessionId ?? string.Empty);
        var (session, _) = _sessionStore.GetOrCreate(request.SessionId, dataSourceCode);
        var expired = providedSessionId && existing is null;

        // 附件参考（阶段四）：数量/类型/大小校验 → 按类型分发解析 → 参考材料随会话存储（不落库）→ 记元信息审计。
        if (request.Attachments is { Count: > 0 })
        {
            if (request.Attachments.Count > PocAiReferenceMaterial.MaxAttachmentsPerMessage)
            {
                throw new PocDatasetConfigurationException(
                    "AI_ATTACHMENT_INVALID",
                    $"单条消息最多 {PocAiReferenceMaterial.MaxAttachmentsPerMessage} 个附件。");
            }
            var materials = await ParseAttachmentsAsync(settings, request.Attachments, cancellationToken);
            session.AppendReferenceMaterials(materials);
            await WriteAttachmentAuditAsync(session.Id, request.Attachments, materials);
        }

        // 同一条消息实例既入会话又进 appended：Seq 由会话在追加时统一分配，保证响应视图与轮询视图同源。
        var userMessage = new PocAiChatMessage(PocAiChatRole.User, message);
        session.AppendMessages([userMessage]);
        var appended = new List<PocAiChatMessage> { userMessage };

        try
        {
            // 循环状态实时化（方案 B）：开始即置 running，工具循环内每条消息立即追加到会话，
            // 前端经 progress 接口轮询读取；正常结束 completed，异常结束 failed（会话保留可重试）。
            session.BeginLoop();
            await RunToolLoopAsync(settings, session, appended, cancellationToken);
            session.CompleteLoop();
            return new PocAiChatResult(
                session.Id,
                appended.Select(ToView).ToArray(),
                expired,
                BuildDraftView(session.Draft));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 客户端中止（浏览器关闭或面板“停止按钮”abort fetch）：RequestAborted 触发后，取消异常会从
            // LLM/查数等 await 点冒泡到这里。必须优雅收口而不是让取消异常穿透到 Furion 异常管道——
            // 否则每次中止都会被当成 500 记 Error 日志并连带触发错误邮件等后台旁路。会话保留（消息与
            // 草稿原样）、循环状态记 failed 供前端重试，返回已产生的部分结果，stderr 只留一行观测痕迹。
            session.FailLoop();
            Console.Error.WriteLine(
                $"[AI] chat aborted by client: session={session.Id}, keptMessages={appended.Count}");
            return new PocAiChatResult(
                session.Id,
                appended.Select(ToView).ToArray(),
                expired,
                BuildDraftView(session.Draft));
        }
        catch (Exception)
        {
            // LLM 失败保留会话：用户消息与已产生的中间结果都在，前端提示重试即可续聊；循环状态记 failed。
            session.FailLoop();
            throw;
        }
    }

    /// <summary>
    /// 工具循环进度轮询：返回循环状态与 afterSeq 之后的新增消息，供前端在 POST /chat 返回前实时渲染。
    /// AI 关闭时与 chat 同闸 fail-fast；会话不存在/已过期按 idle 空列表返回（不报错），以最终响应为准。
    /// </summary>
    /// <param name="sessionId">会话编号。</param>
    /// <param name="afterSeq">前端已渲染的最大 Seq；传 0 表示取全部。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>循环状态与增量消息。</returns>
    public async Task<PocAiChatProgressDocument> GetProgressAsync(
        string sessionId,
        long afterSeq,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // 与 chat 同一道闸：AI 关闭/配置不完整时 fail-fast，绝不泄露会话存在性。
        await _settingsService.RequireEnabledAsync();
        var session = _sessionStore.Get(sessionId);
        if (session is null)
        {
            return new PocAiChatProgressDocument(FormatLoopState(PocAiLoopState.Idle), Array.Empty<PocAiChatMessageView>());
        }
        var (state, messages) = session.GetProgressSnapshot(afterSeq);
        return new PocAiChatProgressDocument(FormatLoopState(state), messages.Select(ToView).ToArray());
    }

    /// <summary>把循环状态枚举转换为前端契约的小写字符串；集中映射避免各处硬编码。</summary>
    /// <param name="state">循环状态枚举。</param>
    /// <returns>idle / running / completed / failed 文本。</returns>
    private static string FormatLoopState(PocAiLoopState state) => state switch
    {
        PocAiLoopState.Running => "running",
        PocAiLoopState.Completed => "completed",
        PocAiLoopState.Failed => "failed",
        _ => "idle"
    };

    /// <summary>
    /// 附件解析分发（阶段四）：xlsx 走 ClosedXML 解析、pdf 走 PdfPig 解析、图片走视觉模型单次调用；
    /// 视觉失败降级为带说明的空材料（会话继续），校验/解码失败抛 AI_ATTACHMENT_INVALID（API 层映射 400）。
    /// </summary>
    /// <param name="settings">当前 AI 配置（视觉端点取 VisionModel，空回退主 Model）。</param>
    /// <param name="attachments">本次消息的附件输入。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>解析后的参考材料列表（顺序与附件一致）。</returns>
    private async Task<IReadOnlyList<PocAiReferenceMaterial>> ParseAttachmentsAsync(
        PocAiSettings settings,
        IReadOnlyList<PocAiChatAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var materials = new List<PocAiReferenceMaterial>();
        var excelParser = new PocAiExcelReferenceParser();
        var pdfParser = new PocAiPdfReferenceParser();
        // 第一段：逐条校验（类型/大小/base64 合法性），任何一条失败整体拒绝，避免半截参考材料注入。
        foreach (var attachment in attachments)
        {
            PocAiReferenceMaterial.Validate(attachment);
            byte[] content;
            try
            {
                content = Convert.FromBase64String(attachment.DataBase64);
            }
            catch (FormatException exception)
            {
                throw new PocDatasetConfigurationException(
                    "AI_ATTACHMENT_INVALID",
                    $"附件 '{attachment.FileName}' 的 base64 内容无法解码：{exception.Message}");
            }
            if (content.Length == 0)
            {
                throw new PocDatasetConfigurationException(
                    "AI_ATTACHMENT_INVALID",
                    $"附件 '{attachment.FileName}' 内容为空。");
            }
            if (content.Length > PocAiReferenceMaterial.DocumentMaxBytes)
            {
                throw new PocDatasetConfigurationException(
                    "AI_ATTACHMENT_INVALID",
                    $"附件 '{attachment.FileName}' 超过 10MB 上限，请拆分后再上传。");
            }

            // 第二段：按扩展名分发解析——图片走视觉模型（失败降级），xlsx/pdf 走纯后端解析器。
            var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
            if (PocAiReferenceMaterial.ImageExtensions.Contains(extension))
            {
                materials.Add(await ParseImageAttachmentAsync(settings, content, attachment, cancellationToken));
                continue;
            }
            materials.Add(extension == ".xlsx"
                ? excelParser.Parse(content, attachment.FileName)
                : pdfParser.Parse(content, attachment.FileName));
        }
        return materials;
    }

    /// <summary>图片附件解析：以 VisionModel（空回退主 Model）做单次视觉调用；失败降级为带说明的空材料不中断会话。</summary>
    /// <param name="settings">当前 AI 配置。</param>
    /// <param name="content">图片原始字节。</param>
    /// <param name="attachment">附件输入（文件名/MIME）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>参考材料；视觉失败时仅含降级说明警告。</returns>
    private async Task<PocAiReferenceMaterial> ParseImageAttachmentAsync(
        PocAiSettings settings,
        byte[] content,
        PocAiChatAttachment attachment,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        var mimeType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
        var visionModel = string.IsNullOrWhiteSpace(settings.VisionModel) ? settings.Model : settings.VisionModel;
        var endpoint = new PocLlmEndpoint(settings.BaseUrl, settings.ApiKey, visionModel);
        try
        {
            return await _visionAnalyzer.AnalyzeAsync(
                Convert.ToBase64String(content), mimeType, attachment.FileName, endpoint);
        }
        catch (PocAiVisionException exception)
        {
            // 视觉降级语义（spec §2）：仅文本继续——返回带说明的空材料，让模型知道该附件未能解析。
            return new PocAiReferenceMaterial(
                "image", attachment.FileName, null, [], [exception.Message]);
        }
    }

    /// <summary>附件审计：只记元信息（文件名/类型/大小），绝不记附件内容（spec §4）。</summary>
    /// <param name="sessionId">会话编号。</param>
    /// <param name="attachments">本次消息的附件输入。</param>
    /// <param name="materials">解析后的参考材料（命中列数进审计行数位）。</param>
    /// <returns>Task 完成信号；审计失败由 AuditStore 自行兜底。</returns>
    private async Task WriteAttachmentAuditAsync(
        string sessionId,
        IReadOnlyList<PocAiChatAttachment> attachments,
        IReadOnlyList<PocAiReferenceMaterial> materials)
    {
        var metadata = JsonConvert.SerializeObject(new Dictionary<string, object?>
        {
            ["attachments"] = attachments.Select(item => new Dictionary<string, object?>
            {
                ["fileName"] = item.FileName,
                ["contentType"] = item.ContentType,
                ["sizeBytes"] = item.SizeBytes
            }).ToArray(),
            ["parsedColumns"] = materials.Sum(item => item.Columns.Count),
        }, Formatting.None);
        await TryWriteAuditAsync(sessionId, new PocAiToolExecutionResult(
            "attachment_reference",
            null,
            null,
            materials.Sum(item => item.Columns.Count),
            0,
            true,
            null,
            metadata));
    }

    /// <summary>
    /// 尽力写入一次工具/附件审计：审计是旁路能力，写入失败（主库不可用、连接中断或请求取消）
    /// 不允许打断对话主流程，也不允许异常穿透到 API 层——就地消化并只向 stderr 写一行观测痕迹，
    /// 便于事后核对“审计缺行”的原因。
    /// </summary>
    /// <param name="sessionId">AI 会话编号。</param>
    /// <param name="result">工具执行结果（含 SQL、行数、耗时与错误摘要）。</param>
    /// <returns>Task 完成信号；审计失败已就地消化，绝不抛出。</returns>
    private async Task TryWriteAuditAsync(string sessionId, PocAiToolExecutionResult result)
    {
        try
        {
            await _auditStore.WriteAsync(sessionId, result);
        }
        catch (Exception exception)
        {
            // 审计失败只留痕不阻断：会话消息与响应主体均不受影响，stderr 一行便于排查。
            Console.Error.WriteLine(
                $"[AI] audit write failed: session={sessionId}, tool={result.ToolName}, error={exception.Message}");
        }
    }

    /// <summary>执行“模型↔工具”循环：模型请求工具则执行并回传结构化结果，直到模型给出结论或达到轮数上限。</summary>
    /// <param name="settings">当前 AI 配置。</param>
    /// <param name="session">当前会话。</param>
    /// <param name="appended">本轮新增消息集合（含工具消息），供接口回传前端。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    private async Task RunToolLoopAsync(
        PocAiSettings settings,
        PocAiChatSession session,
        List<PocAiChatMessage> appended,
        CancellationToken cancellationToken)
    {
        var rounds = 0;
        var systemPrompt = await BuildSystemPromptAsync(session, cancellationToken);
        while (true)
        {
            // 第一阶段：每轮从会话快照重建 llmMessages（而不是循环开始时构建一次）——这是插话
            // 纠正能生效的前提：用户在循环运行中经 guidance 接口追加的消息，下一轮自然被带上。
            // 达到轮数上限后不再提供工具，只要求模型给出结论。
            var llmMessages = ComposeLlmMessages(systemPrompt, session);
            var forceConclusion = rounds >= settings.MaxToolRounds;
            // 对话链路不设每调用硬超时（超时理念：超时只该发生在失败时；交互式对话的成功
            // 生成可合法超过任何预设上限），请求只由调用方取消令牌中止——浏览器断开/取消
            // 即中止整个工具循环，不再消耗 LLM token。
            var response = await _llmClient.ChatAsync(
                new PocLlmEndpoint(settings.BaseUrl, settings.ApiKey, settings.Model),
                new PocLlmRequest(
                    forceConclusion
                        ? [.. llmMessages, PocAiChatServicePrompts.ForceConclusion]
                        : llmMessages,
                    forceConclusion ? null : PocAiToolRegistry.ListTools()),
                PocLlmClient.ChatUnboundedTimeoutSeconds,
                cancellationToken);

            // 第二阶段：模型请求工具时执行并回传结果，一轮可能包含多个工具调用。
            if (response.ToolCalls.Count > 0 && !forceConclusion)
            {
                rounds++;
                var assistantMessage = new PocAiChatMessage(
                    PocAiChatRole.Assistant,
                    response.Content,
                    response.ToolCalls);
                session.AppendMessages([assistantMessage]);
                appended.Add(assistantMessage);
                // 模型可能一次请求多个工具；逐个执行、逐条审计、逐条回传，保证审计与会话一致。
                foreach (var toolCall in response.ToolCalls)
                {
                    PocAiToolExecutionResult result;
                    if (PocAiToolRegistry.IsSpecTool(toolCall.Name))
                    {
                        // 产物工具（submit/dry_run）不走只读取数通道：由对话服务编译、校验并维护草稿状态。
                        result = await HandleSpecToolAsync(
                            settings,
                            session,
                            toolCall.Name,
                            toolCall.ArgumentsJson,
                            cancellationToken);
                    }
                    else
                    {
                        result = await _toolExecutor.ExecuteAsync(
                            settings,
                            toolCall.Name,
                            toolCall.ArgumentsJson,
                            session.DataSourceCode,
                            cancellationToken);
                    }
                    await TryWriteAuditAsync(session.Id, result);
                    var toolMessage = new PocAiChatMessage(
                        PocAiToolRegistry.IsRegistered(toolCall.Name) ? PocAiChatRole.Tool : PocAiChatRole.Assistant,
                        result.ContentJson,
                        null,
                        toolCall.Id,
                        toolCall.Name,
                        result.DataSourceCode,
                        result.SqlText ?? BuildToolParamSummary(toolCall.Name, toolCall.ArgumentsJson),
                        result.RowCount,
                        result.Success);
                    session.AppendMessages([toolMessage]);
                    appended.Add(toolMessage);
                }
                continue;
            }

            // 第三阶段：模型直接给出文本结论，写入会话后本轮对话结束。
            var finalMessage = new PocAiChatMessage(PocAiChatRole.Assistant, response.Content ?? string.Empty);
            session.AppendMessages([finalMessage]);
            appended.Add(finalMessage);
            return;
        }
    }

    /// <summary>
    /// 为无 SQL 文本的元数据类工具生成卡片摘要：describe_table 取目标表名，list_tables 标注为全量清单。
    /// 前端工具卡片以该摘要作副标题，避免只显示工具名让人看不出"查的是哪张表"。
    /// </summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="argumentsJson">模型给出的工具调用参数 JSON。</param>
    /// <returns>可读摘要；参数缺失或解析失败时返回 null，卡片回退为仅工具名。</returns>
    private static string? BuildToolParamSummary(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }
        try
        {
            var root = JToken.Parse(argumentsJson);
            switch (toolName)
            {
                case "describe_table":
                    return root.Value<string>("tableName") is { Length: > 0 } table ? table : null;
                case "list_tables":
                    return "全部表与视图清单";
                default:
                    return null;
            }
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    /// <summary>
    /// 预创建空会话（实时体验补强）：AI 关闭 fail-fast 照旧，数据源必填且必须存在并启用，
    /// 通过后登记一个无消息历史的空会话并返回编号。前端在面板打开且选定数据源时调用，
    /// 使每一轮对话（含首轮）从发消息瞬间就能按 Seq 轮询 progress；会话本身 30 分钟空闲过期。
    /// </summary>
    /// <param name="request">预创建请求（数据源编码）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>会话编号与绑定数据源。</returns>
    public async Task<PocAiSessionPreCreateDocument> PreCreateSessionAsync(
        PocAiSessionPreCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // 与 chat 同一道闸：AI 关闭/配置不完整时不建会话、不发任何请求，保持“AI 可关闭”硬约束。
        await _settingsService.RequireEnabledAsync();
        var dataSourceCode = (request.DataSourceCode ?? string.Empty).Trim();
        if (dataSourceCode.Length == 0)
        {
            throw new PocDatasetConfigurationException("AI_DATA_SOURCE_REQUIRED", "预创建会话必须先选择数据源。");
        }

        // 数据源存在性预检：让前端在发消息前就发现选错源，而不是等首轮对话才收到 400。
        var dataSource = await _dataAssetStore.GetDataSourceAsync(dataSourceCode);
        if (dataSource is null || !dataSource.IsEnabled)
        {
            throw new PocDatasetConfigurationException(
                "AI_DATA_SOURCE_INVALID",
                $"数据源「{dataSourceCode}」不存在或已停用。");
        }

        var session = _sessionStore.PreCreate(dataSourceCode);
        return new PocAiSessionPreCreateDocument(session.Id, session.DataSourceCode);
    }

    /// <summary>显式关闭会话：清空内存上下文；会话不存在同样幂等成功。</summary>
    /// <param name="sessionId">会话编号。</param>
    /// <returns>是否关闭了现存会话。</returns>
    public bool CloseSession(string sessionId) => _sessionStore.Delete(sessionId);

    /// <summary>
    /// 插话纠正（中途方向修正）：把一条文本以 User 角色追加进指定会话（正常分配 Seq），
    /// 运行中的工具循环在下一轮组装 llmMessages 时自然读到——模型即收到用户的方向纠正，
    /// 不必等本轮失败后从头重来；循环未运行时同样允许追加，作为下一轮对话的既有上下文。
    /// AI 关闭 fail-fast；会话不存在或已过期抛 AI_SESSION_NOT_FOUND；内容必填；
    /// 插话照常进入 50 条裁剪、progress 轮询与前端展示流，并刷新会话活跃时间。
    /// </summary>
    /// <param name="sessionId">目标会话编号。</param>
    /// <param name="message">插话文本（用户视角的方向纠正）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>追加后的消息展示视图（含 Seq，供前端对齐消息流）。</returns>
    public async Task<PocAiChatMessageView> AppendGuidanceAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // 与 chat 同一道闸：AI 关闭/配置不完整时不改任何会话状态，保持“AI 可关闭”硬约束。
        await _settingsService.RequireEnabledAsync();
        var content = (message ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            throw new PocDatasetConfigurationException("AI_MESSAGE_REQUIRED", "插话内容不能为空。");
        }
        var session = _sessionStore.Get(sessionId);
        if (session is null)
        {
            throw new PocDatasetConfigurationException("AI_SESSION_NOT_FOUND", "会话不存在或已过期，无法插话。");
        }
        _sessionStore.Touch(sessionId);
        var guidance = new PocAiChatMessage(PocAiChatRole.User, content);
        session.AppendMessages([guidance]);
        return ToView(guidance);
    }

    /// <summary>
    /// 产物工具统一入口：submit_report_spec 走“编译 + 全链校验”，dry_run_report_spec 走“编译 + 受限试跑”。
    /// 一切失败都转成带稳定错误码的结构化 JSON 回传模型；失败不中断会话，连续 3 次提交失败后熔断。
    /// </summary>
    /// <param name="settings">当前 AI 配置。</param>
    /// <param name="session">当前会话（草稿与熔断计数挂在其上）。</param>
    /// <param name="toolName">工具名。</param>
    /// <param name="argumentsJson">模型给出的参数 JSON。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>审计与回传复用的工具执行结果。</returns>
    private async Task<PocAiToolExecutionResult> HandleSpecToolAsync(
        PocAiSettings settings,
        PocAiChatSession session,
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        _ = settings;
        var stopwatch = Stopwatch.StartNew();
        var arguments = ParseSpecToolArguments(argumentsJson);
        if (_packageService is null)
        {
            // 报表包服务缺失（如测试装配精简）时不抛异常穿越会话，按结构化工具错误回传模型。
            stopwatch.Stop();
            return SpecToolResult(
                toolName,
                stopwatch,
                success: false,
                rowCount: null,
                errorSummary: "AI_PACKAGE_SERVICE_NOT_AVAILABLE",
                payload: new Dictionary<string, object?>
                {
                    ["error"] = "AI_PACKAGE_SERVICE_NOT_AVAILABLE",
                    ["message"] = "报表包服务不可用，无法编译/校验/试跑 spec。",
                });
        }
        if (toolName == PocAiToolRegistry.SubmitReportSpec)
        {
            return await SubmitSpecAsync(session, arguments, stopwatch, cancellationToken);
        }
        return await DryRunSpecAsync(session, arguments, stopwatch, cancellationToken);
    }

    /// <summary>submit_report_spec：熔断检查 → 编译 → 全链校验（内存态）→ 更新会话草稿与熔断计数。</summary>
    /// <param name="session">当前会话。</param>
    /// <param name="arguments">工具参数（spec 对象）。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>结构化执行结果。</returns>
    private async Task<PocAiToolExecutionResult> SubmitSpecAsync(
        PocAiChatSession session,
        JObject arguments,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        // 第一段：熔断检查。连续 3 次提交失败后不再消耗 LLM/服务资源，引导用户走离线流水线人工介入。
        if (session.FailedSubmitCount >= MaxSubmitFailures)
        {
            stopwatch.Stop();
            return SpecToolResult(
                PocAiToolRegistry.SubmitReportSpec,
                stopwatch,
                success: false,
                rowCount: null,
                errorSummary: "SUBMIT_CIRCUIT_OPEN",
                payload: new Dictionary<string, object?>
                {
                    ["error"] = "SUBMIT_CIRCUIT_OPEN",
                    ["message"] = "本会话已连续 3 次提交失败被熔断。请直接告诉用户改用离线流水线" +
                        "（ZCode + report-design skill + New-ReportPackage.ps1）人工介入修正，不要继续尝试提交。",
                });
        }

        var specJson = ExtractSpecJson(arguments);
        var compile = _specCompiler.Compile(specJson);
        if (!compile.Ok)
        {
            // 第二段：编译失败。登记熔断计数、草稿置 failed，并把带字段路径的错误列表回传模型自修。
            session.RegisterSubmitFailure();
            session.SetDraft(new PocAiChatDraft(specJson, null!, null, PocAiDraftStatus.Failed));
            stopwatch.Stop();
            return SpecToolResult(
                PocAiToolRegistry.SubmitReportSpec,
                stopwatch,
                success: false,
                rowCount: null,
                errorSummary: SummarizeIssues(compile.Issues),
                payload: new Dictionary<string, object?>
                {
                    ["error"] = "SPEC_COMPILE_FAILED",
                    ["message"] = "spec 编译失败，请根据 issues 中的字段路径逐条修正后重新提交。",
                    ["issues"] = compile.Issues.Select(item => new Dictionary<string, object?>
                    {
                        ["path"] = item.Path,
                        ["code"] = item.Code,
                        ["message"] = item.Message
                    }).ToArray(),
                });
        }

        // 第三段：全链校验（内存态，不落库）。校验失败同样计入熔断计数并回传错误清单。
        PocReportPackageValidateResult validation;
        try
        {
            validation = await RequirePackageService().ValidateAsync(compile.Package);
        }
        catch (Exception exception) when (exception is PocDatasetConfigurationException or PocReportPackageException)
        {
            var code = exception switch
            {
                PocReportPackageException named => named.Code,
                PocDatasetConfigurationException configured => configured.Code,
                _ => "PACKAGE_VALIDATION_FAILED",
            };
            session.RegisterSubmitFailure();
            session.SetDraft(new PocAiChatDraft(specJson, compile.Package!, null, PocAiDraftStatus.Failed));
            stopwatch.Stop();
            return SpecToolResult(
                PocAiToolRegistry.SubmitReportSpec,
                stopwatch,
                success: false,
                rowCount: null,
                errorSummary: code,
                payload: new Dictionary<string, object?>
                {
                    ["error"] = code,
                    ["message"] = exception.Message,
                });
        }

        if (!validation.Ok)
        {
            session.RegisterSubmitFailure();
            session.SetDraft(new PocAiChatDraft(
                specJson,
                compile.Package!,
                validation,
                PocAiDraftStatus.Failed));
            stopwatch.Stop();
            return SpecToolResult(
                PocAiToolRegistry.SubmitReportSpec,
                stopwatch,
                success: false,
                rowCount: null,
                errorSummary: SummarizeIssues(validation.Errors.Select(item => new PocAiSpecIssue(
                    "package", item.Code, item.Message)).ToArray()),
                payload: new Dictionary<string, object?>
                {
                    ["error"] = "PACKAGE_VALIDATION_FAILED",
                    ["message"] = "报表包全链校验未通过，请按 errors 逐条修正 spec 后重新提交。",
                    ["errors"] = validation.Errors.Select(item => new Dictionary<string, object?>
                    {
                        ["code"] = item.Code,
                        ["message"] = item.Message
                    }).ToArray(),
                });
        }

        // 第四段：提交通过。草稿置 validated 并清零熔断计数；告知模型草稿卡片已可见。
        session.SetDraft(new PocAiChatDraft(
            specJson,
            compile.Package!,
            validation,
            PocAiDraftStatus.Validated));
        stopwatch.Stop();
        var summary = validation.PackageSummary;
        return SpecToolResult(
            PocAiToolRegistry.SubmitReportSpec,
            stopwatch,
            success: true,
            rowCount: null,
            errorSummary: null,
            payload: new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["message"] = "spec 编译与全链校验通过，草稿卡片已展示给用户（校验/试跑/导入由用户在卡片上确认）。",
                ["summary"] = summary is null
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["reportCode"] = summary.ReportCode,
                        ["reportName"] = summary.ReportName,
                        ["parameterCount"] = summary.ParameterCount,
                        ["fieldCount"] = summary.FieldCount,
                    },
                ["warnings"] = validation.Warnings.Select(item => item.Message).ToArray(),
                ["next"] = "可以调用 dry_run_report_spec 验证数据口径，或直接提示用户在草稿卡片上完成导入。",
            });
    }

    /// <summary>dry_run_report_spec：可选重新编译 → 复用 DryRunAsync 限额（50 行/30 秒）→ 草稿置 dryRunOk。</summary>
    /// <param name="session">当前会话（目标数据源取会话绑定数据源）。</param>
    /// <param name="arguments">工具参数（可选 spec 与 sampleParameters）。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>结构化执行结果。</returns>
    private async Task<PocAiToolExecutionResult> DryRunSpecAsync(
        PocAiChatSession session,
        JObject arguments,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        // 第一段：确定待试跑的 spec——传入了 spec 则重新编译，否则使用会话上最近提交通过的草稿。
        var specJson = ExtractSpecJson(arguments);
        var draft = session.Draft;
        PocReportPackage package;
        PocReportPackageValidateResult? validation;
        if (specJson is not null)
        {
            var compile = _specCompiler.Compile(specJson);
            if (!compile.Ok)
            {
                stopwatch.Stop();
                return SpecToolResult(
                    PocAiToolRegistry.DryRunReportSpec,
                    stopwatch,
                    success: false,
                    rowCount: null,
                    errorSummary: SummarizeIssues(compile.Issues),
                    payload: new Dictionary<string, object?>
                    {
                        ["error"] = "SPEC_COMPILE_FAILED",
                        ["message"] = "试跑前编译失败，请根据 issues 中的字段路径修正 spec。",
                        ["issues"] = compile.Issues.Select(item => new Dictionary<string, object?>
                        {
                            ["path"] = item.Path,
                            ["code"] = item.Code,
                            ["message"] = item.Message
                        }).ToArray(),
                    });
            }
            package = compile.Package!;
            validation = await RequirePackageService().ValidateAsync(package);
            if (!validation.Ok)
            {
                stopwatch.Stop();
                return SpecToolResult(
                    PocAiToolRegistry.DryRunReportSpec,
                    stopwatch,
                    success: false,
                    rowCount: null,
                    errorSummary: SummarizeIssues(validation.Errors.Select(item => new PocAiSpecIssue(
                        "package", item.Code, item.Message)).ToArray()),
                    payload: new Dictionary<string, object?>
                    {
                        ["error"] = "PACKAGE_VALIDATION_FAILED",
                        ["message"] = "试跑前全链校验未通过，请按 errors 修正 spec。",
                        ["errors"] = validation.Errors.Select(item => new Dictionary<string, object?>
                        {
                            ["code"] = item.Code,
                            ["message"] = item.Message
                        }).ToArray(),
                    });
            }
            session.SetDraft(new PocAiChatDraft(specJson, package, validation, PocAiDraftStatus.Validated));
        }
        else
        {
            if (draft?.Package is null ||
                draft.Status is PocAiDraftStatus.Failed or PocAiDraftStatus.Submitted)
            {
                stopwatch.Stop();
                return SpecToolResult(
                    PocAiToolRegistry.DryRunReportSpec,
                    stopwatch,
                    success: false,
                    rowCount: null,
                    errorSummary: "SPEC_DRAFT_REQUIRED",
                    payload: new Dictionary<string, object?>
                    {
                        ["error"] = "SPEC_DRAFT_REQUIRED",
                        ["message"] = "会话上没有可试跑的草稿，请先提交（submit_report_spec）或在参数中携带 spec。",
                    });
            }
            package = draft.Package;
            validation = draft.ValidateResult;
        }

        // 第二段：受限试跑。目标数据源固定为会话数据源；限额（50 行/30 秒）由 DryRunAsync 内部强制。
        var sampleParameters = arguments["sampleParameters"] is JObject values
            ? (IReadOnlyDictionary<string, string?>)values.Properties()
                .ToDictionary(property => property.Name, property => property.Value?.Value<string>(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>();
        PocReportPackageDryRunResult dryRun;
        try
        {
            dryRun = await RequirePackageService().DryRunAsync(
                new PocReportPackageDryRunRequest(package, session.DataSourceCode, sampleParameters),
                cancellationToken);
        }
        // 第三段：试跑失败转结构化错误（类型不匹配/参数缺失/数据源不存在/查询失败等），模型可自行修正后重试。
        catch (Exception exception) when (
            exception is PocReportPackageException
                or PocParameterResolutionException
                or PocDatasetConfigurationException
                or PocDatasetExecutionException
                or PocDataAssetRevisionConflictException)
        {
            stopwatch.Stop();
            var code = exception switch
            {
                PocReportPackageException named => named.Code,
                PocParameterResolutionException parameter => parameter.Code,
                PocDatasetConfigurationException configured => configured.Code,
                PocDatasetExecutionException execution => execution.Code,
                _ => "DATA_SOURCE_STATE_CHANGED",
            };
            return SpecToolResult(
                PocAiToolRegistry.DryRunReportSpec,
                stopwatch,
                success: false,
                rowCount: null,
                errorSummary: code,
                payload: new Dictionary<string, object?>
                {
                    ["error"] = code,
                    ["message"] = exception.Message,
                });
        }

        // 第四段：试跑成功。草稿状态升级为 dryRunOk（保留校验结果），并把样例数据回传模型核对口径。
        session.SetDraft(new PocAiChatDraft(
            draft?.SpecJson ?? specJson ?? string.Empty,
            package,
            validation,
            PocAiDraftStatus.DryRunOk,
            dryRun));
        stopwatch.Stop();
        return SpecToolResult(
            PocAiToolRegistry.DryRunReportSpec,
            stopwatch,
            success: true,
            rowCount: dryRun.Rows.Count,
            errorSummary: null,
            payload: new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["columns"] = dryRun.Columns,
                ["rows"] = dryRun.Rows,
                ["rowCount"] = dryRun.Rows.Count,
                ["rowCountCapped"] = dryRun.RowCountCapped,
                ["elapsedMilliseconds"] = dryRun.ElapsedMilliseconds,
                ["fieldSchemaMatch"] = dryRun.FieldSchemaMatch,
                ["fieldSchemaMismatches"] = dryRun.FieldSchemaMismatches,
                ["message"] = dryRun.FieldSchemaMatch
                    ? "试跑通过，字段契约与实际列一致。"
                    : "试跑已返回数据，但实际列与 spec 字段契约存在差异，请核对 fieldSchemaMismatches。",
            });
    }

    /// <summary>解析产物工具参数；非法 JSON 统一按结构化拒绝处理。</summary>
    /// <param name="argumentsJson">模型给出的参数文本。</param>
    /// <returns>参数对象；解析失败返回空对象（由后续步骤给出更明确的错误）。</returns>
    private static JObject ParseSpecToolArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }
        try
        {
            return JObject.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>从工具参数中提取 spec 原文；spec 节点必须是对象，否则返回 null 走草稿回落。</summary>
    /// <param name="arguments">工具参数。</param>
    /// <returns>spec JSON 文本。</returns>
    private static string? ExtractSpecJson(JObject arguments)
    {
        var spec = arguments["spec"] as JObject;
        return spec?.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>获取报表包服务；依赖缺失时抛出结构化业务错误（由调用方转成工具错误结果）。</summary>
    /// <returns>可用的报表包服务。</returns>
    private PocReportPackageService RequirePackageService() =>
        _packageService
        ?? throw new PocReportPackageException(
            "AI_PACKAGE_SERVICE_NOT_AVAILABLE",
            "报表包服务不可用，无法编译/校验/试跑 spec。");

    /// <summary>把错误清单压缩为审计摘要文本（保留字段路径），控制长度避免审计行膨胀。</summary>
    /// <param name="issues">错误清单。</param>
    /// <returns>单行摘要文本。</returns>
    private static string SummarizeIssues(IReadOnlyList<PocAiSpecIssue> issues)
    {
        if (issues.Count == 0)
        {
            return "SPEC_FAILED";
        }
        var first = issues[0];
        var text = $"{first.Code}@{first.Path}: {first.Message}";
        if (issues.Count > 1)
        {
            text += $"（共 {issues.Count} 项）";
        }
        return text.Length <= 400 ? text : text[..400];
    }

    /// <summary>统一构造产物工具的执行结果：审计元数据 + 回传 JSON。</summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="success">是否成功。</param>
    /// <param name="rowCount">返回行数（submit 为 null）。</param>
    /// <param name="errorSummary">审计错误摘要。</param>
    /// <param name="payload">回传模型的负载。</param>
    /// <returns>工具执行结果。</returns>
    private static PocAiToolExecutionResult SpecToolResult(
        string toolName,
        Stopwatch stopwatch,
        bool success,
        int? rowCount,
        string? errorSummary,
        Dictionary<string, object?> payload)
    {
        return new PocAiToolExecutionResult(
            toolName,
            null,
            null,
            rowCount,
            (int)stopwatch.ElapsedMilliseconds,
            success,
            errorSummary,
            JsonConvert.SerializeObject(payload, Formatting.None));
    }

    /// <summary>把会话草稿转换为下发前端的视图；spec/package 以 JSON 对象形态随消息返回。</summary>
    /// <param name="draft">会话草稿。</param>
    /// <returns>草稿视图；无草稿时为 null。</returns>
    private static PocAiChatDraftView? BuildDraftView(PocAiChatDraft? draft)
    {
        if (draft is null)
        {
            return null;
        }
        // 编译失败时 Package 为空：仍下发 failed 视图（spec 原文 + 状态），前端据此展示错误态草稿卡片。
        return new PocAiChatDraftView(
            draft.Status switch
            {
                PocAiDraftStatus.Validated => "validated",
                PocAiDraftStatus.DryRunOk => "dryRunOk",
                PocAiDraftStatus.Failed => "failed",
                _ => "submitted"
            },
            SafeParse(draft.SpecJson),
            draft.Package is null ? null : SafeParse(PocReportPackageFormat.SerializeCanonical(draft.Package)),
            draft.ValidateResult);
    }

    /// <summary>安全解析 JSON 文本；解析失败返回 null，不影响响应序列化。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>解析后的 JToken。</returns>
    private static JToken? SafeParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JToken.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>组装系统提示词：注入全部数据源摘要、所选源的表名列表与会话参考材料段；表清单读取失败按空清单降级。</summary>
    /// <param name="session">当前会话（携带绑定数据源与参考材料存量）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>完整系统提示词文本。</returns>
    private async Task<string> BuildSystemPromptAsync(PocAiChatSession session, CancellationToken cancellationToken)
    {
        var dataSources = (await _dataAssetStore.ListDataSourcesAsync())
            .Where(item => item.IsEnabled)
            .Select(item => (item.Code, item.Name, item.DbType))
            .ToArray();
        var tablesBySource = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        // 只注入会话所选数据源的表清单摘要；其余数据源只列编码/名称/类型，结构靠按需 list_tables 获取。
        var selected = await _dataAssetStore.GetDataSourceAsync(session.DataSourceCode);
        if (selected is not null && selected.IsEnabled)
        {
            try
            {
                using var database = _clientFactory(
                    selected.DbType,
                    selected.ConnectionString,
                    5,
                    cancellationToken);
                var tables = database.DbMaintenance.GetTableInfoList(false);
                tablesBySource[selected.Code] = tables.Select(item => item.Name).ToArray();
            }
            catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception)
                || PocDatabaseProvider.IsTimeout(exception))
            {
                // 表清单注入失败只影响上下文丰富度；工具仍可按需 list_tables，因此按空清单降级继续。
                tablesBySource[selected.Code] = [];
            }
        }

        var summary = PocAiSystemPrompt.BuildDataSourceSummary(dataSources, tablesBySource, session.DataSourceCode);
        var referenceSection = PocAiSystemPrompt.BuildReferenceSection(session.GetReferenceMaterials());
        return PocAiSystemPrompt.Build(summary, referenceSection);
    }

    /// <summary>
    /// 把会话历史转换为 LLM 线格式消息：system 在前，其余按会话顺序；tool 消息补充工具名。
    /// 发送前对历史做上下文压缩（超时治理）：仅压缩较老轮次的 tool 结果——保留最近
    /// FullToolResultRounds 轮全文，更早轮次的超长结果截为前缀并追加截断标记；
    /// user/assistant 消息（承载排查思路主线与中间结论）一律全文保留。
    /// 压缩只作用于本次请求，会话内存储、前端展示与 Seq 语义均不变，
    /// 目的是压低长会话上下文体积、让每轮更快返回，避免后期轮次系统性逼近请求超时。
    /// </summary>
    /// <param name="systemPrompt">系统提示词。</param>
    /// <param name="session">当前会话。</param>
    /// <returns>LLM 请求消息列表。</returns>
    private static IReadOnlyList<PocLlmChatMessage> ComposeLlmMessages(
        string systemPrompt,
        PocAiChatSession session)
    {
        List<PocAiChatMessage> snapshot;
        lock (session.Messages)
        {
            // 锁内只做快照复制，压缩与映射放到锁外执行，避免长会话处理拉长并发追加/轮询的锁等待。
            snapshot = [.. session.Messages];
        }
        List<PocLlmChatMessage> messages = [new PocLlmChatMessage("system", systemPrompt)];
        foreach (var item in CompressHistoryForLlm(snapshot))
        {
            var role = item.Role switch
            {
                PocAiChatRole.User => "user",
                PocAiChatRole.Assistant => "assistant",
                PocAiChatRole.Tool => "tool",
                _ => "system"
            };
            messages.Add(new PocLlmChatMessage(
                role,
                item.Content,
                item.ToolCalls,
                item.ToolCallId,
                item.ToolName));
        }
        return messages;
    }

    /// <summary>
    /// 会话历史的发送前压缩（纯函数）：以「assistant 携带 tool_calls」为界把 tool 结果划入
    /// 工具调用轮次，最近 FullToolResultRounds 轮保持全文，更早轮次中超长的 tool 消息内容
    /// 截为前 CompressedToolContentLength 字符并追加 CompressedToolContentSuffix；
    /// 长度未超限的早期结果保持原样，不追加误导性的“已截断”标记。
    /// 返回新列表（原消息对象或其截断副本），绝不改动会话状态；轮次不足或无历史时原样返回。
    /// </summary>
    /// <param name="history">会话消息快照（不含系统提示词）。</param>
    /// <returns>压缩后的消息列表，顺序与输入一致。</returns>
    internal static IReadOnlyList<PocAiChatMessage> CompressHistoryForLlm(IReadOnlyList<PocAiChatMessage> history)
    {
        // 第一遍：按最近的 assistant tool_calls 给每条 tool 消息标注所属轮次（从 1 起计数），
        // 没有前置 tool_calls 的孤立 tool 消息归入轮次 0，视作最早的压缩候选。
        var annotated = new List<(PocAiChatMessage Message, int ToolRound)>(history.Count);
        var currentRound = 0;
        foreach (var item in history)
        {
            if (item.Role == PocAiChatRole.Assistant && item.ToolCalls is { Count: > 0 })
            {
                currentRound++;
            }
            annotated.Add((item, item.Role == PocAiChatRole.Tool ? currentRound : 0));
        }
        // 轮次号不大于该值的 tool 结果属“更早轮次”；总轮数不足时不压缩，直接原样返回。
        var compressUpToRound = currentRound - FullToolResultRounds;
        if (compressUpToRound <= 0)
        {
            return history;
        }

        // 第二遍：只替换命中压缩条件的超长 tool 内容；短结果与最近两轮均保持原对象引用。
        var compressed = new List<PocAiChatMessage>(history.Count);
        foreach (var (item, toolRound) in annotated)
        {
            if (item.Role == PocAiChatRole.Tool
                && toolRound <= compressUpToRound
                && item.Content is { Length: > CompressedToolContentLength } content)
            {
                compressed.Add(item with
                {
                    Content = content[..CompressedToolContentLength] + CompressedToolContentSuffix
                });
                continue;
            }
            compressed.Add(item);
        }
        return compressed;
    }

    /// <summary>把会话消息转换为前端展示视图：tool 消息折叠为“工具名 + SQL 摘要 + 行数”，并携带 Seq 供轮询去重。</summary>
    /// <param name="message">会话消息。</param>
    /// <returns>展示视图。</returns>
    private static PocAiChatMessageView ToView(PocAiChatMessage message) => new(
        message.Role switch
        {
            PocAiChatRole.User => "user",
            PocAiChatRole.Assistant => "assistant",
            PocAiChatRole.Tool => "tool",
            _ => "system"
        },
        message.Role == PocAiChatRole.Tool ? null : message.Content,
        message.ToolName,
        message.ToolCallId,
        message.Role == PocAiChatRole.Tool ? SummarizeSql(message.SqlText) : null,
        message.RowCount,
        message.Success,
        TruncatedFlag(message.Content),
        message.Seq);

    /// <summary>生成 SQL 折叠摘要：压缩空白并截断，前端工具条只显示一行摘要。</summary>
    /// <param name="sqlText">完整 SQL 文本。</param>
    /// <returns>单行摘要文本，可为 null。</returns>
    private static string? SummarizeSql(string? sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            return null;
        }
        var compact = string.Join(' ', sqlText.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        return compact.Length <= 160 ? compact : $"{compact[..160]}…";
    }

    /// <summary>从工具回传 JSON 中解析截断标记；解析失败视为未截断，不影响展示。</summary>
    /// <param name="content">工具回传 JSON。</param>
    /// <returns>是否被行数上限截断。</returns>
    private static bool TruncatedFlag(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }
        try
        {
            return JObject.Parse(content).Value<bool?>("truncated") ?? false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>工具循环的附加指令文案；集中定义便于测试断言。</summary>
internal static class PocAiChatServicePrompts
{
    /// <summary>轮数超限后注入的强制结论指令；随消息追加发送，并在该次请求中不再提供工具。</summary>
    public static readonly PocLlmChatMessage ForceConclusion = new(
        "user",
        "工具调用轮数已达上限，请基于已获取的信息直接给出结论性回复，不要尝试继续调用任何工具。");
}
