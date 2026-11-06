using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>AI 能力被关闭或配置不完整时抛出；所有调用型 /ai/* 接口据此 fail-fast，绝不发起 LLM 请求或查数工具。</summary>
public sealed class PocAiDisabledException : Exception
{
    /// <summary>AI 关闭降级的稳定错误码；前端据此隐藏入口，调用方据此提示管理员启用 AI。</summary>
    public const string ErrorCode = "AI_DISABLED";

    /// <summary>初始化 PocAiDisabledException 异常实例，说明当前是配置关闭还是配置不完整。</summary>
    /// <param name="reason">给管理员的中文原因说明。</param>
    public PocAiDisabledException(string reason) : base($"{ErrorCode}: {reason}")
    {
        Reason = reason;
    }

    /// <summary>关闭原因（未启用 / 缺少服务地址 / 缺少模型名）。</summary>
    public string Reason { get; }
}

/// <summary>AI 设置的公开契约；ApiKey 永不回传原文，只返回打码值与是否已配置。模型名非密钥，VisionModel 正常回显。</summary>
public sealed record PocAiSettingsDocument(
    bool IsEnabled,
    string BaseUrl,
    string ApiKeyMasked,
    bool ApiKeyConfigured,
    string Model,
    int SampleRowLimit,
    bool SqlProbeEnabled,
    int SqlProbeRowLimit,
    int MaxToolRounds,
    DateTime? UpdateTime = null,
    string VisionModel = "");

/// <summary>AI 设置保存输入；ApiKey 传空表示保留库中原值，前端打码回显时不会误清空密钥。VisionModel 空 = 回退主模型。</summary>
public sealed record PocAiSettingsInput(
    bool IsEnabled,
    string BaseUrl,
    string? ApiKey,
    string Model,
    int SampleRowLimit,
    bool SqlProbeEnabled,
    int SqlProbeRowLimit,
    int MaxToolRounds,
    string VisionModel = "");

/// <summary>连通性测试结果；message 面向管理员展示，失败时携带可操作的原因。</summary>
public sealed record PocAiConnectionTestResult(bool Success, string Message, int LatencyMs);

/// <summary>
/// AI 设置应用服务：读写配置、ApiKey 打码与“空值保留原值”语义、限额校验，
/// 以及所有调用型接口共用的 AI 启用守卫（IsEnabled=false 或缺 BaseUrl/Model 时 fail-fast）。
/// </summary>
public sealed class PocAiSettingsService : ITransient
{
    /// <summary>采样行数配置范围；0 表示禁用采样，硬上限 100。</summary>
    public const int SampleRowLimitMax = 100;

    /// <summary>SQL 探测行数配置范围；上限刻意收紧，防止把全表数据外发 LLM。</summary>
    public const int SqlProbeRowLimitMax = 1000;

    /// <summary>工具轮数配置范围；过大只会增加 token 消耗，不会提升能力。</summary>
    public const int MaxToolRoundsMax = 20;

    private readonly PocAiSettingsStore _store;
    private readonly PocLlmClient _llmClient;

    /// <summary>初始化 PocAiSettingsService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="store">单行配置存储。</param>
    /// <param name="llmClient">LLM 客户端，用于连通性测试。</param>
    public PocAiSettingsService(PocAiSettingsStore store, PocLlmClient llmClient)
    {
        _store = store;
        _llmClient = llmClient;
    }

    /// <summary>读取设置并打码 ApiKey；未配置时返回默认关闭配置，前端据此控制 AI 入口渲染。</summary>
    /// <returns>打码后的设置文档。</returns>
    public async Task<PocAiSettingsDocument> GetDocumentAsync()
    {
        var settings = await _store.GetAsync();
        return ToDocument(settings);
    }

    /// <summary>保存设置：校验限额与启用条件；ApiKey 传空表示保留原值，传新值则整体替换。</summary>
    /// <param name="input">保存输入。</param>
    /// <returns>保存后的打码设置文档。</returns>
    public async Task<PocAiSettingsDocument> SaveAsync(PocAiSettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        var existing = await _store.GetAsync();
        var baseUrl = input.BaseUrl.Trim();
        var model = input.Model.Trim();
        // 启用时必须有可解析的服务地址与模型名，保证“启用即可用”，避免带着残缺配置打开 AI。
        if (input.IsEnabled)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new PocDatasetConfigurationException(
                    "AI_SETTINGS_INVALID",
                    "启用 AI 时必须提供合法的 http(s) 服务地址。");
            }
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new PocDatasetConfigurationException(
                    "AI_SETTINGS_INVALID",
                    "启用 AI 时必须提供模型名称。");
            }
        }

        var apiKey = string.IsNullOrWhiteSpace(input.ApiKey)
            ? existing.ApiKey
            : input.ApiKey!.Trim();
        // 第二段：组装实体并保存——ApiKey 空值语义在此落定（空 = 沿用库中原值），VisionModel 空回退主模型。
        var saved = await _store.SaveAsync(new PocAiSettings
        {
            IsEnabled = input.IsEnabled,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            VisionModel = (input.VisionModel ?? string.Empty).Trim(),
            SampleRowLimit = input.SampleRowLimit,
            SqlProbeEnabled = input.SqlProbeEnabled,
            SqlProbeRowLimit = input.SqlProbeRowLimit,
            MaxToolRounds = input.MaxToolRounds
        });
        return ToDocument(saved);
    }

    /// <summary>对已保存配置做一次最小 chat 连通性测试；AI 未启用或配置不完整时按 AI_DISABLED fail-fast。</summary>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>测试结果（成功/失败原因/耗时）。</returns>
    public async Task<PocAiConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var settings = await RequireEnabledAsync();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await _llmClient.ChatAsync(
                new PocLlmEndpoint(settings.BaseUrl, settings.ApiKey, settings.Model),
                new PocLlmRequest([new PocLlmChatMessage("user", "连接测试，请回复“正常”。")]),
                PocLlmClient.TestTimeoutSeconds,
                cancellationToken);
            stopwatch.Stop();
            return new PocAiConnectionTestResult(
                true,
                $"连接成功，模型 {settings.Model} 已响应（{(int)stopwatch.Elapsed.TotalMilliseconds} ms）。",
                (int)stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (PocAiLlmException exception)
        {
            stopwatch.Stop();
            return new PocAiConnectionTestResult(false, exception.Message, (int)stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>AI 启用守卫：读取配置并在未启用/配置不完整时抛出 AI_DISABLED；这是所有调用型接口的第一道闸。</summary>
    /// <returns>可用的配置实体。</returns>
    public async Task<PocAiSettings> RequireEnabledAsync()
    {
        var settings = await _store.GetAsync();
        if (!settings.IsEnabled)
        {
            throw new PocAiDisabledException("AI 能力未启用，请在“AI 设置”页开启后再使用。");
        }
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new PocAiDisabledException("AI 配置缺少服务地址（BaseUrl），请先补全配置。");
        }
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new PocAiDisabledException("AI 配置缺少模型名称（Model），请先补全配置。");
        }
        return settings;
    }

    /// <summary>把配置实体转换为打码文档；打码规则固定 sk-***last4，密钥原文与空值都不外泄。VisionModel 非密钥，原样回显。</summary>
    /// <param name="settings">配置实体。</param>
    /// <returns>打码后的展示文本。</returns>
    private static PocAiSettingsDocument ToDocument(PocAiSettings settings)
    {
        var configured = !string.IsNullOrWhiteSpace(settings.ApiKey);
        return new PocAiSettingsDocument(
            settings.IsEnabled,
            settings.BaseUrl,
            configured ? MaskApiKey(settings.ApiKey!) : string.Empty,
            configured,
            settings.Model,
            settings.SampleRowLimit,
            settings.SqlProbeEnabled,
            settings.SqlProbeRowLimit,
            settings.MaxToolRounds,
            settings.UpdateTime,
            settings.VisionModel);
    }

    /// <summary>按仓库惯例打码密钥：sk-*** + 末 4 位；过短密钥只显示前缀，避免长度泄漏。</summary>
    /// <param name="apiKey">密钥原文。</param>
    /// <returns>打码后的展示文本。</returns>
    public static string MaskApiKey(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.Length <= 4
            ? "sk-***"
            : $"sk-***{trimmed[^4..]}";
    }

    /// <summary>校验保存输入的限额字段与必填文本，违规直接拒绝，防止把极端配置写进单行表。</summary>
    /// <param name="input">保存输入。</param>
    private static void ValidateInput(PocAiSettingsInput input)
    {
        var problems = new List<string>();
        if (input.SampleRowLimit is < 0 or > SampleRowLimitMax)
        {
            problems.Add($"采样行数上限必须在 0-{SampleRowLimitMax} 之间（0 表示禁用采样）。");
        }
        if (input.SqlProbeRowLimit is < 1 or > SqlProbeRowLimitMax)
        {
            problems.Add($"SQL 探测行数上限必须在 1-{SqlProbeRowLimitMax} 之间。");
        }
        if (input.MaxToolRounds is < 1 or > MaxToolRoundsMax)
        {
            problems.Add($"最大工具轮数必须在 1-{MaxToolRoundsMax} 之间。");
        }
        if (input.BaseUrl is { Length: > 512 })
        {
            problems.Add("服务地址过长（上限 512 字符）。");
        }
        if (input.Model is { Length: > 128 })
        {
            problems.Add("模型名称过长（上限 128 字符）。");
        }
        if (input.VisionModel is { Length: > 128 })
        {
            problems.Add("视觉模型名称过长（上限 128 字符）。");
        }
        if (input.ApiKey is { Length: > 512 })
        {
            problems.Add("ApiKey 过长（上限 512 字符）。");
        }
        if (problems.Count > 0)
        {
            throw new PocDatasetConfigurationException(
                "AI_SETTINGS_INVALID",
                string.Join("；", problems));
        }
    }
}
