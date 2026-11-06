using System.Text.RegularExpressions;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>同步 AI 匹配的源字段描述：名称 + 数据类型代码 + 可选注释（来自数据集字段契约）。</summary>
public sealed record PocAiSyncMatchSourceField(string Name, string DataType, string? Description);

/// <summary>
/// 同步 AI 智能匹配请求。两种形态二选一（直接载荷优先）：
/// 直接携带源字段清单 + 目标列元数据 + 当前映射 + 源/目标库型（映射设计器草稿在浏览器内存中齐备）；
/// 或只传 taskCode，由服务端从任务配置与既有元数据装配（不新增取数口径之外的读取）。
/// </summary>
public sealed record PocAiSyncMatchRequest(
    string? TaskCode,
    string? SourceDbType,
    string? TargetDbType,
    IReadOnlyList<PocAiSyncMatchSourceField>? SourceFields,
    IReadOnlyList<PocSyncColumnMetadataDocument>? TargetColumns,
    IReadOnlyList<PocSyncColumnMapping>? Mappings);

/// <summary>
/// 单条映射建议：结构与 PocSyncColumnMapping 完全同构（可直接回填草稿），
/// 另附 confidence(high/mid/low) 与 reason 仅供 UI 标注，写回 mappings 时丢弃。
/// NeedsManual=true 表示目标列在源表未找到对应字段（“需人工处理”），绝不写入 mappings。
/// </summary>
public sealed record PocAiSyncMatchSuggestion(
    string SourceField,
    string TargetField,
    string TargetType,
    int? Length,
    bool Nullable,
    IReadOnlyList<PocSyncConverterStep> Converters,
    string Confidence,
    string Reason,
    bool NeedsManual);

/// <summary>
/// 同步智能匹配结果：建议列表 + 降级标记。服务端绝不落库不保存——回填草稿、
/// 人工确认、保存与试跑完全走同步设计器现有链路。
/// </summary>
public sealed record PocAiSyncMatchResult(
    IReadOnlyList<PocAiSyncMatchSuggestion> Suggestions,
    bool Degraded,
    string? Message);

/// <summary>
/// 同步 AI 智能字段匹配（spec §8）：三层策略、确定性优先——
/// ①同名规则基线（与前端 buildInitialMappings 同语义：忽略大小写、只填空、不覆盖人工）；
/// ②LLM 语义匹配只处理歧义项（两侧字段清单 + 已填映射交给模型，模型只返回映射建议 JSON）；
/// ③类型兼容与转换链建议走确定性矩阵，转换器只允许 PocDataSyncConverter.SupportedConverters
/// 白名单，白名单外一律拒绝。LLM 不可用或返回非法 JSON 时降级为同名规则结果并在响应中提示。
/// </summary>
public sealed partial class PocAiSyncMatchService : ITransient
{
    /// <summary>匹配请求的审计工具名；区别于查数工具，便于按任务回溯匹配行为。</summary>
    public const string AuditToolName = "sync_match";

    /// <summary>LLM 语义匹配的超时秒数；清单通常几十行，短超时快速降级。</summary>
    public const int MatchTimeoutSeconds = 60;

    private readonly PocAiSettingsService _settingsService;
    private readonly PocLlmClient _llmClient;
    private readonly PocAiAuditStore _auditStore;
    private readonly PocDataSyncStore _syncStore;
    private readonly PocDataAssetStore _dataAssets;
    private readonly PocDataSyncService? _syncService;

    /// <summary>初始化 PocAiSyncMatchService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="settingsService">AI 设置服务（AI_DISABLED 守卫）。</param>
    /// <param name="llmClient">OpenAI 兼容 LLM 客户端。</param>
    /// <param name="auditStore">AI 审计存储。</param>
    /// <param name="syncStore">同步任务存储（taskCode 模式装配输入用）。</param>
    /// <param name="dataAssets">全局数据资产存储（源数据集字段契约读取）。</param>
    /// <param name="syncService">同步服务，taskCode 模式读取目标表列元数据；缺失时该模式返回结构化错误。</param>
    public PocAiSyncMatchService(
        PocAiSettingsService settingsService,
        PocLlmClient llmClient,
        PocAiAuditStore auditStore,
        PocDataSyncStore syncStore,
        PocDataAssetStore dataAssets,
        PocDataSyncService? syncService = null)
    {
        _settingsService = settingsService;
        _llmClient = llmClient;
        _auditStore = auditStore;
        _syncStore = syncStore;
        _dataAssets = dataAssets;
        _syncService = syncService;
    }

    /// <summary>执行同步智能匹配：守卫 AI 开关 → 装配输入 → 同名基线 → LLM 歧义补齐 → 转换链建议 → 审计。</summary>
    /// <param name="request">匹配请求。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>建议列表（含需人工处理项）与降级标记；服务端不落库。</returns>
    public async Task<PocAiSyncMatchResult> MatchAsync(
        PocAiSyncMatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // AI 启用守卫放在最前：未启用/配置不完整时 fail-fast，不发起任何 LLM 请求。
        _ = await _settingsService.RequireEnabledAsync();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 第一段：装配输入（直接载荷优先，taskCode 回落服务端装配），并规范化目标列推断类型。
        var (sourceFields, targetColumns, mappings, taskCode) = await ResolveInputsAsync(request, cancellationToken);
        if (sourceFields.Count == 0 || targetColumns.Count == 0)
        {
            throw new PocDataSyncException(
                "SYNC_MATCH_INPUT_EMPTY",
                "源字段或目标列清单为空，无法匹配；请先完成源取数与目标表选择。");
        }

        // 第二段：同名规则基线（确定性第一层）。已有人工映射的源/目标一律跳过，绝不覆盖。
        var suggestions = new List<PocAiSyncMatchSuggestion>();
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingTargets = new List<PocSyncColumnMetadataDocument>();
        foreach (var mapping in mappings)
        {
            usedSources.Add(mapping.SourceField.Trim());
        }
        var manuallyMappedTargets = new HashSet<string>(
            mappings.Select(item => item.TargetField.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var sourceByName = new Dictionary<string, PocAiSyncMatchSourceField>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceFields)
        {
            sourceByName[source.Name.Trim()] = source;
        }
        foreach (var column in targetColumns)
        {
            var targetName = column.Name.Trim();
            if (manuallyMappedTargets.Contains(targetName))
            {
                // 人工已填：以人工为准，不生成建议、目标也不再进入歧义清单。
                continue;
            }
            if (sourceByName.TryGetValue(targetName, out var sameName))
            {
                var targetType = InferTargetType(column.DataType);
                suggestions.Add(new PocAiSyncMatchSuggestion(
                    sameName.Name.Trim(),
                    targetName,
                    targetType,
                    column.Length,
                    column.Nullable,
                    SuggestConverters(sameName.DataType, targetType),
                    "high",
                    "同名匹配：源字段与目标列名一致（忽略大小写）。",
                    false));
                usedSources.Add(sameName.Name.Trim());
                continue;
            }
            pendingTargets.Add(column);
        }

        // 第三段：LLM 语义匹配只处理歧义项；清单为空时不发任何 LLM 请求（确定性层先行的钉子）。
        var degraded = false;
        string? message = null;
        if (pendingTargets.Count > 0)
        {
            try
            {
                var matched = await MatchAmbiguousWithLlmAsync(
                    sourceFields,
                    targetColumns,
                    mappings,
                    suggestions,
                    pendingTargets,
                    sourceByName,
                    usedSources,
                    cancellationToken);
                suggestions.AddRange(matched);
            }
            catch (Exception exception) when (exception is PocAiLlmException or JsonException)
            {
                // 降级：LLM 不可用或返回非法 JSON → 保留同名基线并在响应中明示。
                degraded = true;
                message = exception is PocAiLlmException llm
                    ? $"LLM 匹配不可用，已降级为同名规则结果：{llm.Message}"
                    : "LLM 返回内容不是合法的匹配 JSON，已降级为同名规则结果。";
            }
        }

        // 第四段：仍无出处的歧义目标列显式标记“需人工处理”，绝不写入 mappings。
        var matchedTargets = new HashSet<string>(
            suggestions.Select(item => item.TargetField.Trim()),
            StringComparer.OrdinalIgnoreCase);
        foreach (var column in pendingTargets)
        {
            var targetName = column.Name.Trim();
            if (matchedTargets.Contains(targetName))
            {
                continue;
            }
            suggestions.Add(new PocAiSyncMatchSuggestion(
                string.Empty,
                targetName,
                InferTargetType(column.DataType),
                column.Length,
                column.Nullable,
                [],
                "low",
                degraded
                    ? "需人工处理：LLM 不可用已降级，源表未发现同名字段。"
                    : "需人工处理：源表与 LLM 均未找到对应字段。",
                true));
        }

        // 第五段：审计一行（TaskCode、字段数、命中数、耗时），审计失败不影响匹配结果。
        stopwatch.Stop();
        var hitCount = suggestions.Count(item => !item.NeedsManual);
        try
        {
            await _auditStore.WriteAsync(taskCode ?? "adhoc", new PocAiToolExecutionResult(
                AuditToolName,
                null,
                null,
                hitCount,
                (int)stopwatch.ElapsedMilliseconds,
                !degraded,
                degraded ? message : null,
                JsonConvert.SerializeObject(new Dictionary<string, object?>
                {
                    ["sourceFields"] = sourceFields.Count,
                    ["targetColumns"] = targetColumns.Count,
                    ["manualMappings"] = mappings.Count,
                    ["suggestions"] = suggestions.Count,
                    ["hits"] = hitCount,
                    ["degraded"] = degraded,
                }, Formatting.None)));
        }
        catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception))
        {
            // 审计写失败只降级为无审计，不改变匹配结果；错误摘要留空由调用方日志兜底。
        }

        return new PocAiSyncMatchResult(suggestions, degraded, message);
    }

    /// <summary>
    /// 装配匹配输入：直接载荷优先；taskCode 模式从任务配置与既有元数据装配
    /// （任务实体 + 源数据集字段契约 + 目标表列元数据），不新增取数口径。
    /// </summary>
    /// <param name="request">匹配请求。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>源字段清单、目标列元数据、当前映射与任务编码（临时匹配为 null）。</returns>
    private async Task<(IReadOnlyList<PocAiSyncMatchSourceField> SourceFields,
        IReadOnlyList<PocSyncColumnMetadataDocument> TargetColumns,
        IReadOnlyList<PocSyncColumnMapping> Mappings,
        string? TaskCode)> ResolveInputsAsync(
        PocAiSyncMatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceFields is { Count: > 0 } &&
            request.TargetColumns is { Count: > 0 } &&
            request.Mappings is not null)
        {
            return (request.SourceFields, request.TargetColumns, request.Mappings, request.TaskCode);
        }

        if (string.IsNullOrWhiteSpace(request.TaskCode))
        {
            throw new PocDataSyncException(
                "SYNC_MATCH_INPUT_MISSING",
                "请携带源字段与目标列元数据，或提供 taskCode 由服务端装配。");
        }

        var task = await _syncStore.GetTaskAsync(request.TaskCode.Trim())
            ?? throw new PocDataSyncException(
                "SYNC_TASK_NOT_FOUND",
                $"不存在同步任务 '{request.TaskCode}'。");
        var dataset = await _dataAssets.GetDatasetAsync(task.SourceDatasetCode);
        if (dataset is null)
        {
            throw new PocDataSyncException(
                "SYNC_TASK_NOT_FOUND",
                $"任务 '{task.Code}' 的源数据集 '{task.SourceDatasetCode}' 不存在，无法装配源字段清单。");
        }
        if (_syncService is null)
        {
            throw new PocDataSyncException(
                "SYNC_MATCH_SERVICE_UNAVAILABLE",
                "同步服务不可用，无法读取目标表列元数据。");
        }
        var columns = await _syncService.GetTableColumnsAsync(
            task.TargetDataSourceCode, task.TargetTable, cancellationToken);
        var definition = JsonConvert.DeserializeObject<PocSyncTaskDefinition>(
            task.DefinitionJson ?? "{}") ?? new PocSyncTaskDefinition([], null);
        var fields = ReadSchemaFields(dataset.FieldSchemaJson);
        return (fields, columns.Columns, definition.Mappings, task.Code);
    }

    /// <summary>解析源数据集 FieldSchemaJson 的字段契约，映射为匹配用源字段清单。</summary>
    /// <param name="fieldSchemaJson">数据集字段契约 JSON。</param>
    /// <returns>源字段清单；解析失败按空清单处理。</returns>
    private static IReadOnlyList<PocAiSyncMatchSourceField> ReadSchemaFields(string fieldSchemaJson)
    {
        try
        {
            var schema = JsonConvert.DeserializeObject<PocDatasetSchemaDocument>(
                string.IsNullOrWhiteSpace(fieldSchemaJson) ? "[]" : fieldSchemaJson);
            return (schema?.Fields ?? [])
                .Select(field => new PocAiSyncMatchSourceField(field.Code, field.DataType, field.Description))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// LLM 语义匹配歧义项：把两侧字段清单（名称+类型+注释）与已填映射交给模型，
    /// 解析并校验其映射建议 JSON——只接受两侧真实存在的字段名、目标限定在歧义清单内，
    /// 源/目标一经占用（含同轮先到的建议）不再复用；转换器按白名单逐个放行，白名单外拒绝并记入依据。
    /// </summary>
    /// <param name="sourceFields">源字段清单。</param>
    /// <param name="targetColumns">目标列元数据。</param>
    /// <param name="mappings">已有人工映射。</param>
    /// <param name="baseline">同名基线建议（用于排除已占用目标）。</param>
    /// <param name="pendingTargets">待判定的歧义目标列。</param>
    /// <param name="usedSources">已被占用的源字段（人工 + 同名）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>通过校验的语义匹配建议。</returns>
    private async Task<IReadOnlyList<PocAiSyncMatchSuggestion>> MatchAmbiguousWithLlmAsync(
        IReadOnlyList<PocAiSyncMatchSourceField> sourceFields,
        IReadOnlyList<PocSyncColumnMetadataDocument> targetColumns,
        IReadOnlyList<PocSyncColumnMapping> mappings,
        IReadOnlyList<PocAiSyncMatchSuggestion> baseline,
        IReadOnlyList<PocSyncColumnMetadataDocument> pendingTargets,
        IReadOnlyDictionary<string, PocAiSyncMatchSourceField> sourceByName,
        IReadOnlyCollection<string> usedSources,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.RequireEnabledAsync();
        var targetByName = new Dictionary<string, PocSyncColumnMetadataDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in targetColumns)
        {
            targetByName[column.Name.Trim()] = column;
        }
        var pendingNames = pendingTargets
            .Select(item => item.Name.Trim())
            .ToArray();
        var response = await _llmClient.ChatAsync(
            new PocLlmEndpoint(settings.BaseUrl, settings.ApiKey, settings.Model),
            new PocLlmRequest([BuildMatchPrompt(sourceFields, mappings, pendingNames)]),
            MatchTimeoutSeconds,
            cancellationToken);
        var content = response.Content ?? string.Empty;
        // 模型偶尔用 ```json 围栏包裹：剥掉围栏再解析，围栏内仍非法则按降级处理。
        content = StripJsonFence(content);
        var root = JObject.Parse(content);
        var matches = root["matches"] as JArray
            ?? throw new JsonException("LLM 响应缺少 matches 数组。");

        var localUsedSources = new HashSet<string>(usedSources, StringComparer.OrdinalIgnoreCase);
        var localUsedTargets = new HashSet<string>(
            baseline.Select(item => item.TargetField.Trim()).Concat(
                mappings.Select(item => item.TargetField.Trim())),
            StringComparer.OrdinalIgnoreCase);
        var results = new List<PocAiSyncMatchSuggestion>();
        foreach (var match in matches.OfType<JObject>())
        {
            var sourceName = match.Value<string>("sourceField")?.Trim();
            var targetName = match.Value<string>("targetField")?.Trim();
            var reason = match.Value<string>("reason")?.Trim();
            if (string.IsNullOrEmpty(sourceName) || string.IsNullOrEmpty(targetName) ||
                !sourceByName.ContainsKey(sourceName) ||
                !pendingNames.Contains(targetName, StringComparer.OrdinalIgnoreCase) ||
                !localUsedSources.Add(sourceName) ||
                !localUsedTargets.Add(targetName))
            {
                // 建议引用了不存在的名字、目标不在歧义清单内，或与人工/基线/同轮建议冲突：一律丢弃。
                continue;
            }

            var source = sourceByName[sourceName];
            var column = targetByName[targetName];
            var targetType = InferTargetType(column.DataType);
            // 第三层补充：模型可附转换链建议，白名单外转换器逐个拒绝并写进依据。
            var (steps, rejected) = ResolveConverterChain(
                match["converters"]?.Select(item => item.Value<string>()).OfType<string>().ToArray());
            var rejectedNote = rejected.Count > 0
                ? $"；已拒绝白名单外转换器：{string.Join("、", rejected)}"
                : string.Empty;
            var chain = steps.Count > 0 ? steps : SuggestConverters(source.DataType, targetType);
            results.Add(new PocAiSyncMatchSuggestion(
                source.Name.Trim(),
                targetName,
                targetType,
                column.Length,
                column.Nullable,
                chain,
                NormalizeConfidence(match.Value<string>("confidence")),
                $"{(string.IsNullOrEmpty(reason) ? "LLM 语义匹配" : reason)}{rejectedNote}",
                false));
        }
        return results;
    }

    /// <summary>构造 LLM 匹配提示词：角色约束 + 输出 JSON 契约 + 已确认映射 + 两侧字段清单。</summary>
    /// <param name="sourceFields">源字段清单。</param>
    /// <param name="mappings">已有人工映射。</param>
    /// <param name="pendingTargets">待判定的目标列名。</param>
    /// <returns>用户消息文本。</returns>
    private static PocLlmChatMessage BuildMatchPrompt(
        IReadOnlyList<PocAiSyncMatchSourceField> sourceFields,
        IReadOnlyList<PocSyncColumnMapping> mappings,
        IReadOnlyList<string> pendingTargets)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(
            "你是数据同步任务的字段映射助手。请判断下列目标列在源表中是否存在业务同义字段" +
            "（如 FEE_TYPE 与 FEE_CATEGORY 同为费用类别、REFUND_FLAG 与 IS_REFUND 同为退费标志），并输出映射建议。");
        builder.AppendLine("只输出 JSON，格式：{\"matches\":[{\"sourceField\":\"源字段名\",\"targetField\":\"目标列名\",\"confidence\":\"high|mid|low\",\"reason\":\"依据\",\"converters\":[可选，白名单转换器名]}]}");
        builder.AppendLine("规则：只建议两侧都存在的名字；每列至多一条建议；没有把握的列不要输出；不要改写已确认映射。");
        builder.AppendLine();
        builder.AppendLine($"已确认映射（不得覆盖）：{string.Join("；", mappings.Select(item => $"{item.SourceField}→{item.TargetField}"))}");
        builder.AppendLine("源字段清单（名称 | 类型 | 注释）：");
        foreach (var source in sourceFields)
        {
            builder.AppendLine($"- {source.Name} | {source.DataType} | {source.Description ?? string.Empty}");
        }
        builder.AppendLine("待判定目标列：");
        builder.AppendLine(string.Join("、", pendingTargets));
        return new PocLlmChatMessage("user", builder.ToString());
    }

    /// <summary>剥掉 LLM 输出中的 Markdown 代码围栏，只保留 JSON 本体。</summary>
    /// <param name="content">模型原文。</param>
    /// <returns>清洗后的 JSON 文本。</returns>
    private static string StripJsonFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }
        var firstBrace = trimmed.IndexOf('{', StringComparison.Ordinal);
        var lastBrace = trimmed.LastIndexOf('}');
        return firstBrace >= 0 && lastBrace > firstBrace
            ? trimmed[firstBrace..(lastBrace + 1)]
            : trimmed;
    }

    /// <summary>把模型给出的置信度收敛到 high/mid/low；缺失或非法值按 mid 处理。</summary>
    /// <param name="confidence">模型给出的置信度文本。</param>
    /// <returns>规范置信度。</returns>
    private static string NormalizeConfidence(string? confidence) => confidence?.Trim().ToLowerInvariant() switch
    {
        "high" => "high",
        "low" => "low",
        _ => "mid"
    };

    /// <summary>
    /// 解析转换链建议：逐个对照 PocDataSyncConverter.SupportedConverters 白名单，
    /// 白名单内按声明顺序生成步骤，白名单外收集进拒绝清单由调用方写进依据。
    /// </summary>
    /// <param name="proposed">提议的转换器名序列。</param>
    /// <returns>放行的转换步骤与被拒绝的名字清单。</returns>
    public static (IReadOnlyList<PocSyncConverterStep> Steps, IReadOnlyList<string> Rejected) ResolveConverterChain(
        IReadOnlyList<string>? proposed)
    {
        if (proposed is null || proposed.Count == 0)
        {
            return ([], []);
        }
        var steps = new List<PocSyncConverterStep>();
        var rejected = new List<string>();
        foreach (var name in proposed)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var trimmed = name.Trim();
            if (PocDataSyncConverter.SupportedConverters.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                steps.Add(new PocSyncConverterStep(
                    PocDataSyncConverter.SupportedConverters.First(item =>
                        item.Equals(trimmed, StringComparison.OrdinalIgnoreCase))));
            }
            else
            {
                rejected.Add(trimmed);
            }
        }
        return (steps, rejected);
    }

    /// <summary>
    /// 按源/目标类型兼容矩阵给出确定性转换链：字符串→数值/时间补解析转换，
    /// 其他类型→字符串补 toString；同类型或同为数值不转换（编译期终值校验兜底）。
    /// 矩阵只产出白名单内转换器，杜绝越权建议。
    /// </summary>
    /// <param name="sourceType">源字段数据类型代码。</param>
    /// <param name="targetType">目标列类型代码（string/number/decimal/datetime/bool）。</param>
    /// <returns>转换步骤链（可为空 = 直通写入）。</returns>
    public static IReadOnlyList<PocSyncConverterStep> SuggestConverters(string sourceType, string targetType)
    {
        var source = sourceType?.Trim().ToLowerInvariant() ?? string.Empty;
        var target = targetType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (source == target)
        {
            return [];
        }
        return (source, target) switch
        {
            ("string", "number") => [new PocSyncConverterStep("toNumber")],
            ("string", "decimal") => [new PocSyncConverterStep("toDecimal")],
            ("string", "datetime") => [new PocSyncConverterStep("toDate")],
            (_, "string") when source is "number" or "decimal" or "datetime" or "bool"
                => [new PocSyncConverterStep("toString")],
            _ => [],
        };
    }

    /// <summary>
    /// 把目标列数据库类型文本推断为类型代码，规则与前端 targetDbTypeToType 逐条一致：
    /// 先时间、再布尔、再整数、再小数，未知类型按 string 处理。
    /// </summary>
    /// <param name="dbType">元数据返回的数据库类型文本。</param>
    /// <returns>目标列类型代码。</returns>
    public static string InferTargetType(string dbType)
    {
        var text = (dbType ?? string.Empty).ToLowerInvariant();
        if (Regex_datetime().IsMatch(text))
        {
            return "datetime";
        }
        if (Regex_bool().IsMatch(text))
        {
            return "bool";
        }
        if (Regex_int().IsMatch(text))
        {
            return "number";
        }
        if (Regex_decimal().IsMatch(text))
        {
            return "decimal";
        }
        return "string";
    }

    /// <summary>时间类数据库类型正则；与前端 targetDbTypeToType 同词表。</summary>
    [GeneratedRegex(@"(datetime|timestamp|\bdate\b|\btime\b|year)")]
    private static partial Regex Regex_datetime();

    /// <summary>布尔类数据库类型正则。</summary>
    [GeneratedRegex(@"(bool|bit|boolean)")]
    private static partial Regex Regex_bool();

    /// <summary>整数类数据库类型正则。</summary>
    [GeneratedRegex(@"(int|byte|short|long|serial)")]
    private static partial Regex Regex_int();

    /// <summary>小数类数据库类型正则。</summary>
    [GeneratedRegex(@"(decimal|numeric|float|double|real|money|number)")]
    private static partial Regex Regex_decimal();
}
