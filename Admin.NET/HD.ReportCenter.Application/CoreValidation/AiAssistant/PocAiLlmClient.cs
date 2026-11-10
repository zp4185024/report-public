using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>一次 OpenAI 兼容调用的目标端点信息；BaseUrl/ApiKey/Model 随设置即时生效，不缓存旧值。</summary>
public sealed record PocLlmEndpoint(string BaseUrl, string? ApiKey, string Model);

/// <summary>LLM 返回的单个工具调用请求：工具名与 JSON 字符串形式的参数（按 OpenAI 协议原样保留）。</summary>
public sealed record PocLlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>LLM 一次补全的解析结果；Content 与 ToolCalls 互斥出现，两者都空视为模型返回无效。</summary>
public sealed record PocLlmResponse(string? Content, IReadOnlyList<PocLlmToolCall> ToolCalls, string? FinishReason);

/// <summary>发送给 LLM 的消息；统一承载 system/user/assistant/tool 四种角色，按角色序列化为 OpenAI 线格式。
/// 多模态消息（如图片附件的 image_url content part）通过 ContentParts 提供纯追加的 content 数组形态，原构造签名不变。</summary>
/// <param name="Role">角色：system/user/assistant/tool。</param>
/// <param name="Content">纯文本内容；多模态消息可传空串并以 ContentParts 承载。</param>
/// <param name="ToolCalls">assistant 消息携带的工具调用请求。</param>
/// <param name="ToolCallId">tool 消息对应的调用编号。</param>
/// <param name="ToolName">tool 消息对应的工具名。</param>
public sealed record PocLlmChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<PocLlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null)
{
    /// <summary>多模态 content parts（OpenAI content 数组形态，如 image_url + text）；非空时序列化优先于 Content 字符串。</summary>
    public IReadOnlyList<JObject>? ContentParts { get; init; }
}

/// <summary>一次对话补全请求：消息历史 + 可选的工具 schema 列表。</summary>
public sealed record PocLlmRequest(
    IReadOnlyList<PocLlmChatMessage> Messages,
    IReadOnlyList<JObject>? Tools = null);

/// <summary>LLM 调用的业务异常；Code 供 API 层映射稳定错误码，消息不包含密钥等敏感信息。</summary>
public sealed class PocAiLlmException : Exception
{
    /// <summary>初始化 PocAiLlmException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">稳定错误码，如 LLM_REQUEST_FAILED、LLM_RESPONSE_INVALID。</param>
    /// <param name="message">面向管理员的中文说明。</param>
    public PocAiLlmException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>供前端识别失败类型的稳定业务错误码。</summary>
    public string Code { get; }
}

/// <summary>
/// OpenAI 兼容 LLM 客户端：不引入任何 LLM SDK，使用 HttpClient 手写 /chat/completions 调用，
/// 覆盖 tools 声明、tool_calls 请求解析与 tool 结果回传。
/// </summary>
public sealed class PocLlmClient
{
    /// <summary>命名 HttpClient 的注册名；由 ReportCenterStartup 统一注册超时与请求头。</summary>
    public const string HttpClientName = "ReportCenterAiLlm";

    /// <summary>
    /// 对话链路使用的超时约定值：0 表示不设每调用硬超时——超时只该发生在真正失败时，
    /// 交互式对话的成功生成可以合法超过任何预设上限；请求只由调用方取消令牌中止
    /// （浏览器断开/取消即中止循环），连通性测试等短调用仍传各自的有限超时。
    /// </summary>
    public const int ChatUnboundedTimeoutSeconds = 0;

    /// <summary>连通性测试的超时秒数；测试只发一条短消息，快速失败给管理员明确提示。</summary>
    public const int TestTimeoutSeconds = 30;

    private readonly HttpClient _httpClient;

    /// <summary>初始化 PocLlmClient 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="httpClientFactory">HttpClient 工厂，用于按注册名创建带默认配置的客户端。</param>
    public PocLlmClient(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
    }

    /// <summary>测试专用构造：允许注入自定义 HttpClient（伪造 HttpMessageHandler），单测不触达真实 LLM 端点。</summary>
    /// <param name="httpClient">测试用 HttpClient 实例。</param>
    public PocLlmClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>发送一次 OpenAI 兼容对话补全请求，解析 content 或 tool_calls；网络、协议和响应异常统一转业务错误。</summary>
    /// <param name="endpoint">目标端点（baseUrl/apiKey/model）。</param>
    /// <param name="request">消息历史与工具 schema。</param>
    /// <param name="timeoutSeconds">本次调用超时秒数；传 0 或负数表示不设每调用硬超时，仅由取消令牌中止。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>解析后的补全结果。</returns>
    public Task<PocLlmResponse> ChatAsync(
        PocLlmEndpoint endpoint,
        PocLlmRequest request,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        return ChatAsyncCore(endpoint, request, timeoutSeconds, cancellationToken);
    }

    /// <summary>执行一次对话补全的实际网络与解析流程；由诊断包装层调用。</summary>
    /// <param name="endpoint">目标端点。</param>
    /// <param name="request">消息历史与工具 schema。</param>
    /// <param name="timeoutSeconds">本次调用超时秒数；0 或负数表示不限时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析后的补全结果。</returns>
    private async Task<PocLlmResponse> ChatAsyncCore(
        PocLlmEndpoint endpoint,
        PocLlmRequest request,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var url = BuildChatCompletionsUrl(endpoint.BaseUrl);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 仅当调用方给定正值时才设每调用限时；对话链路传 0，长生成不设硬超时，只由取消令牌中止。
        if (timeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)));
        }

        // 第一阶段：组装请求体与鉴权头；密钥只进入 Authorization 头，绝不写入 URL 或日志。
        // 请求体按 ASCII 转义序列化，规避链路上任何按本机码页解码请求体的中间件。
        var payload = SerializePayloadAscii(BuildRequestPayload(endpoint.Model, request));
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }

        // 第二阶段：发送请求并区分超时与网络错误，转成稳定业务错误码。
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PocAiLlmException("LLM_REQUEST_TIMEOUT", "LLM 服务响应超时，请稍后重试或检查服务地址。");
        }
        catch (HttpRequestException exception)
        {
            throw new PocAiLlmException("LLM_REQUEST_FAILED", $"无法连接 LLM 服务：{exception.Message}");
        }
        // 第三阶段：读取并解析响应正文；非 2xx 只保留摘要，避免泄漏密钥或完整请求。
        // 响应统一按 UTF-8 解码：不信任响应头 charset——部分中间件/网关会声明本机码页（如 GBK），
        // 按其解码 UTF-8 中文响应会直接抛解码异常；OpenAI 兼容线格式约定为 UTF-8。
        using (response)
        {
            var responseBytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
            var body = Encoding.UTF8.GetString(responseBytes);
            if (!response.IsSuccessStatusCode)
            {
                // 错误响应只保留首段摘要，绝不回传 Authorization 头或完整请求体，避免泄漏密钥。
                var summary = FirstSegment(body);
                throw new PocAiLlmException(
                    "LLM_REQUEST_FAILED",
                    $"LLM 服务返回 HTTP {(int)response.StatusCode}：{summary}");
            }

            return ParseResponse(body);
        }
    }

    /// <summary>把 baseUrl 规范化为 /chat/completions 完整地址；非法地址直接拒绝，避免把密钥发到意外主机。</summary>
    /// <param name="baseUrl">配置的 OpenAI 兼容服务基础地址。</param>
    /// <returns>可发送的完整 URL 文本。</returns>
    private static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new PocAiLlmException("LLM_BASE_URL_INVALID", "LLM 服务地址必须是合法的 http(s) 绝对地址。");
        }

        return $"{trimmed.TrimEnd('/')}/chat/completions";
    }

    /// <summary>组装 OpenAI 请求体：模型名、消息历史与可选 tools；tool 结果按 role=tool 携带 tool_call_id。</summary>
    /// <param name="model">目标模型名。</param>
    /// <param name="request">对话请求。</param>
    /// <returns>序列化前的请求 JObject。</returns>
    private static JObject BuildRequestPayload(string model, PocLlmRequest request)
    {
        // 第一阶段：按角色序列化消息历史；tool 消息携带 tool_call_id，assistant 携带 tool_calls。
        // 多模态消息（ContentParts 非空）按 OpenAI content 数组形态序列化，纯文本消息维持字符串形态。
        var messages = new JArray();
        foreach (var message in request.Messages)
        {
            var item = new JObject { ["role"] = message.Role };
            if (message.ContentParts is { Count: > 0 })
            {
                item["content"] = JArray.FromObject(message.ContentParts);
            }
            else if (!string.IsNullOrEmpty(message.Content))
            {
                item["content"] = message.Content;
            }
            else
            {
                item["content"] = null;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                var toolCalls = new JArray();
                foreach (var toolCall in message.ToolCalls)
                {
                    toolCalls.Add(new JObject
                    {
                        ["id"] = toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = toolCall.ArgumentsJson
                        }
                    });
                }
                item["tool_calls"] = toolCalls;
            }

            if (!string.IsNullOrEmpty(message.ToolCallId))
            {
                item["tool_call_id"] = message.ToolCallId;
            }
            messages.Add(item);
        }

        // 第二阶段：组装顶层字段；tools 为空时不发送该字段，兼容不支持工具的端点。
        var payload = new JObject
        {
            ["model"] = model,
            ["messages"] = messages
        };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = JArray.FromObject(request.Tools);
        }
        return payload;
    }

    /// <summary>
    /// 以 ASCII 安全形态序列化请求负载：所有非 ASCII 字符转义为 \uXXXX，
    /// 保证线格式不含任何多字节字符——医院内网常见的 GBK 解码代理/中间件因此不可能解码失败，
    /// 目标端点（OpenAI 兼容）按 JSON 规范还原后语义完全等价。
    /// </summary>
    /// <param name="payload">待序列化的请求负载。</param>
    /// <returns>纯 ASCII 的 JSON 文本。</returns>
    private static string SerializePayloadAscii(JObject payload)
    {
        return JsonConvert.SerializeObject(payload, Formatting.None, new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.EscapeNonAscii
        });
    }

    /// <summary>解析 OpenAI 兼容响应：读取首个 choice 的 content 与 tool_calls；结构不符时抛稳定业务错误。</summary>
    /// <param name="body">HTTP 响应正文。</param>
    /// <returns>解析后的补全结果。</returns>
    private static PocLlmResponse ParseResponse(string body)
    {
        JObject root;
        try
        {
            root = JObject.Parse(body);
        }
        catch (JsonException)
        {
            throw new PocAiLlmException("LLM_RESPONSE_INVALID", "LLM 服务返回内容不是有效的 JSON。");
        }

        var choice = root["choices"]?.FirstOrDefault();
        var message = choice?["message"];
        if (message is null)
        {
            throw new PocAiLlmException("LLM_RESPONSE_INVALID", "LLM 服务响应缺少 choices.message 结构。");
        }

        var content = message["content"]?.Type == JTokenType.String
            ? message["content"]!.Value<string>()
            : null;
        var toolCalls = new List<PocLlmToolCall>();
        foreach (var call in message["tool_calls"] as JArray ?? [])
        {
            var id = call.Value<string>("id") ?? string.Empty;
            var name = call["function"]?.Value<string>("name") ?? string.Empty;
            var arguments = call["function"]?.Value<string>("arguments") ?? "{}";
            toolCalls.Add(new PocLlmToolCall(id, name, arguments));
        }

        if (content is null && toolCalls.Count == 0)
        {
            throw new PocAiLlmException("LLM_RESPONSE_INVALID", "LLM 服务响应既无文本也无工具调用。");
        }

        return new PocLlmResponse(
            content,
            toolCalls,
            choice?.Value<string>("finish_reason"));
    }

    /// <summary>截取响应错误正文的第一个可读片段，控制长度并去掉换行，供错误消息直接展示。</summary>
    /// <param name="body">HTTP 响应正文。</param>
    /// <returns>首段摘要文本，最长 512 字符。</returns>
    private static string FirstSegment(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "（无响应正文）";
        }

        var first = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? body;
        return first.Length <= 512 ? first : first[..512];
    }
}


