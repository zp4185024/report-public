using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>视觉解析的降级信号：视觉模型不可用/超时/返回非法时由分析器抛出，对话服务捕获后按"仅文本继续"降级并提示。</summary>
public sealed class PocAiVisionException : Exception
{
    /// <summary>初始化 PocAiVisionException 异常实例，保存面向用户的中文降级原因。</summary>
    /// <param name="message">中文原因说明（不含密钥等敏感信息）。</param>
    public PocAiVisionException(string message) : base(message)
    {
    }
}

/// <summary>
/// 图片附件视觉分析器（阶段四）：把图片以 OpenAI 兼容 image_url content part（base64 data URL）
/// 一次性发给视觉模型（PocAiSettings.VisionModel，空则回退主 Model），要求其输出统一参考材料 JSON。
/// 不走 tools、不进循环；失败/超时/返回非法统一抛 PocAiVisionException 由调用方降级。
/// </summary>
public sealed class PocAiVisionAnalyzer : ITransient
{
    /// <summary>视觉单次调用超时秒数；单图解析应快速返回，超时即降级。</summary>
    public const int VisionTimeoutSeconds = 90;

    /// <summary>要求视觉模型输出的 JSON 契约说明（与 PocAiReferenceMaterial 对齐）。</summary>
    private const string VisionPrompt =
        "你是报表参考物料分析助手。请仔细阅读这张报表图片，输出一个 JSON 对象，描述它的参考结构：" +
        "{\"title\":\"报表标题，没有则为空\",\"columns\":[{\"name\":\"列名\",\"note\":\"可选，类型或口径提示\"}]," +
        "\"paramsHint\":\"筛选/期间等参数线索，没有则为空\",\"totalsHint\":\"合计/小计线索，没有则为空\",\"warnings\":[\"识别不清的列等说明\"]}。" +
        "要求：1) columns 按图片中列的从左到右顺序排列，列名尽量照抄图片原文；2) 禁止编造图片中不存在的列或数据值；" +
        "3) 只输出 JSON，不要输出任何其他文字。";

    private readonly PocLlmClient _llmClient;

    /// <summary>初始化 PocAiVisionAnalyzer 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="llmClient">OpenAI 兼容 LLM 客户端（复用主链路的 HTTP 基座与错误翻译）。</param>
    public PocAiVisionAnalyzer(PocLlmClient llmClient)
    {
        _llmClient = llmClient;
    }

    /// <summary>分析单张图片：单次视觉调用，输出统一参考材料契约；任何失败以 PocAiVisionException 抛出供调用方降级。</summary>
    /// <param name="dataBase64">图片 base64（不含 data: 前缀）。</param>
    /// <param name="mimeType">图片 MIME 类型（png/jpg/jpeg/webp）。</param>
    /// <param name="fileName">原始文件名（进参考材料展示）。</param>
    /// <param name="endpoint">视觉调用端点（VisionModel 回退主 Model 由调用方组装）。</param>
    /// <returns>解析后的参考材料。</returns>
    public async Task<PocAiReferenceMaterial> AnalyzeAsync(
        string dataBase64,
        string mimeType,
        string fileName,
        PocLlmEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataBase64);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        // 第一阶段：组装 image_url + text 两种 content part；base64 随消息内联，不新增上传端点。
        var parts = new List<JObject>
        {
            new()
            {
                ["type"] = "image_url",
                ["image_url"] = new JObject { ["url"] = $"data:{mimeType};base64,{dataBase64}" }
            },
            new() { ["type"] = "text", ["text"] = VisionPrompt }
        };
        var request = new PocLlmRequest(
            [new PocLlmChatMessage("user", string.Empty) { ContentParts = parts }]);

        // 第二阶段：单次调用，无 tools、无循环；超时/网络/HTTP 错误统一转降级信号。
        PocLlmResponse response;
        try
        {
            response = await _llmClient.ChatAsync(endpoint, request, VisionTimeoutSeconds);
        }
        catch (Exception exception) when (
            exception is PocAiLlmException or OperationCanceledException or HttpRequestException)
        {
            throw new PocAiVisionException(
                $"视觉模型解析失败，已按纯文本继续：{exception.Message}");
        }

        // 第三阶段：剥围栏解析 JSON，映射为统一参考材料契约；结构不符按降级处理。
        var content = (response.Content ?? string.Empty).Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = content.IndexOf('{', StringComparison.Ordinal);
            var lastBrace = content.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                content = content[firstBrace..(lastBrace + 1)];
            }
        }

        JObject root;
        try
        {
            root = JObject.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new PocAiVisionException(
                $"视觉模型返回内容不是合法 JSON，已按纯文本继续：{exception.Message}");
        }

        var columns = (root["columns"] as JArray ?? [])
            .OfType<JObject>()
            .Select(item => new PocAiReferenceColumn(
                item.Value<string>("name")?.Trim() ?? string.Empty,
                item.Value<string>("note")?.Trim()))
            .Where(column => column.Name.Length > 0)
            .ToList();
        var warnings = (root["warnings"] as JArray ?? [])
            .Select(item => item.Value<string>()?.Trim())
            .OfType<string>()
            .Where(text => text.Length > 0)
            .ToList();
        return new PocAiReferenceMaterial(
            "image",
            fileName,
            root.Value<string>("title")?.Trim(),
            columns,
            warnings,
            root.Value<string>("paramsHint")?.Trim(),
            root.Value<string>("totalsHint")?.Trim());
    }
}
