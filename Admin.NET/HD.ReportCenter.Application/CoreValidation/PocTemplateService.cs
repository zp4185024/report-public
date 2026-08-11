using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>返回给编辑器的模板文档：工作簿快照、从快照推导出的绑定以及当前修订号。</summary>
public sealed record PocTemplateDocument(
    string ReportCode,
    JObject Snapshot,
    PocTemplateBinding Binding,
    int Revision,
    string? ValidationWarning = null);

/// <summary>保存模板请求。Revision 是浏览器加载模板时拿到的旧修订号。</summary>
public sealed record PocSaveTemplateRequest(
    JObject Snapshot,
    PocTemplateBinding Binding,
    int Revision);

/// <summary>
/// 请求或数据库中的 Binding 与后台从 Snapshot 重新扫描的结果不一致。
/// 该检查防止旧前端、损坏数据或手工请求让绑定与实际工作簿脱节。
/// </summary>
public sealed class PocTemplateBindingMismatchException : Exception
{
    public const string ErrorCode = "TEMPLATE_BINDING_MISMATCH";

    public PocTemplateBindingMismatchException()
        : base(ErrorCode)
    {
    }

    public PocTemplateBindingMismatchException(Exception innerException)
        : base(ErrorCode, innerException)
    {
    }

    public string Code => ErrorCode;
}

/// <summary>
/// 数据库模板或内嵌种子模板无法解析、校验或复算绑定。
/// 与用户编辑错误区分开，API 会将其作为服务端数据损坏返回。
/// </summary>
public sealed class PocStoredTemplateCorruptedException : Exception
{
    public const string ErrorCode = "TEMPLATE_STORED_DATA_INVALID";

    public PocStoredTemplateCorruptedException(string reportCode, Exception innerException)
        : base($"Stored template '{reportCode}' is invalid.", innerException)
    {
    }

    public string Code => ErrorCode;
}

/// <summary>
/// 模板应用服务，负责种子回退、绑定可信边界、数据规范化和存储层异常语义。
/// </summary>
public sealed class PocTemplateService : ITransient
{
    private const string SeedResourceName =
        "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";

    private readonly PocTemplateStore _store;
    private readonly PocReportConfigurationService? _configurationService;

    public PocTemplateService(
        PocTemplateStore store,
        PocReportConfigurationService? configurationService = null)
    {
        _store = store;
        _configurationService = configurationService;
    }

    /// <summary>
    /// 读取模板。数据库没有记录时返回 revision 0 的内嵌种子；已有记录时校验快照和绑定后再返回。
    /// </summary>
    public async Task<PocTemplateDocument> GetAsync(string reportCode)
    {
        var definition = await GetDefinitionAsync(reportCode);
        var stored = await _store.GetAsync(reportCode);
        if (stored is null)
        {
            // 种子只负责“第一次打开即可编辑”，不会自动写库；用户明确保存后才产生 revision 1。
            try
            {
                var seedJson = LoadSeedJson();
                return CreateDocument(reportCode, seedJson, null, 0, definition);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException || IsTemplateDataException(exception))
            {
                throw new PocStoredTemplateCorruptedException(reportCode, exception);
            }
        }

        try
        {
            // 已存模板每次读取都重新扫描，尽早发现数据库中的快照/绑定不一致，而不是到预览时才失败。
            return CreateDocument(
                stored.ReportCode,
                stored.SnapshotJson,
                stored.BindingJson,
                stored.Revision,
                definition);
        }
        catch (Exception exception) when (IsTemplateDataException(exception))
        {
            throw new PocStoredTemplateCorruptedException(reportCode, exception);
        }
    }

    /// <summary>
    /// 为模板设计器读取草稿。字段或参数定义变化导致绑定失效时仍返回原始快照，
    /// 让设计人员可以删除或替换旧 token；执行和导出继续使用严格的 GetAsync。
    /// </summary>
    public async Task<PocTemplateDocument> GetEditableAsync(string reportCode)
    {
        var definition = await GetDefinitionAsync(reportCode);
        var stored = await _store.GetAsync(reportCode);
        if (stored is null)
        {
            return await GetAsync(reportCode);
        }

        try
        {
            return CreateDocument(
                stored.ReportCode,
                stored.SnapshotJson,
                stored.BindingJson,
                stored.Revision,
                definition);
        }
        catch (Exception exception) when (
            exception is PocTemplateValidationException or PocTemplateBindingMismatchException)
        {
            return new PocTemplateDocument(
                stored.ReportCode,
                JObject.Parse(stored.SnapshotJson),
                new PocTemplateBinding(1, [], null),
                stored.Revision,
                "数据集字段或报表参数已经变化，当前模板绑定需要重新编辑并保存。");
        }
        catch (JsonException exception)
        {
            throw new PocStoredTemplateCorruptedException(reportCode, exception);
        }
    }

    /// <summary>
    /// 保存浏览器草稿。后台始终从 Snapshot 重新扫描绑定，前端提交的 Binding 仅用于一致性检查。
    /// </summary>
    public async Task<PocTemplateDocument> SaveAsync(
        string reportCode,
        PocSaveTemplateRequest request)
    {
        var definition = await GetDefinitionAsync(reportCode);
        var snapshotJson = request.Snapshot.ToString(Formatting.None);
        // 浏览器不是信任边界：真正入库的绑定必须由后台校验器根据工作簿快照重新生成。
        var canonicalBinding = PocTemplateValidator.Scan(snapshotJson, definition);
        var canonicalBindingJson = SerializeBinding(canonicalBinding);
        if (!string.Equals(
                SerializeBinding(request.Binding),
                canonicalBindingJson,
                StringComparison.Ordinal))
        {
            throw new PocTemplateBindingMismatchException();
        }

        var stored = await _store.SaveAsync(
            reportCode,
            snapshotJson,
            canonicalBindingJson,
            request.Revision);

        return new PocTemplateDocument(
            stored.ReportCode,
            JObject.Parse(stored.SnapshotJson),
            canonicalBinding,
            stored.Revision);
    }

    private static PocTemplateDocument CreateDocument(
        string reportCode,
        string snapshotJson,
        string? storedBindingJson,
        int revision,
        PocReportDefinition definition)
    {
        var snapshot = JObject.Parse(snapshotJson);
        var canonicalBinding = PocTemplateValidator.Scan(
            snapshot.ToString(Formatting.None),
            definition);
        if (storedBindingJson is not null)
        {
            // 先反序列化再重新序列化，消除 JSON 空白和属性格式差异，只比较绑定的规范形态。
            string normalizedStoredBinding;
            try
            {
                var storedBinding = JsonConvert.DeserializeObject<PocTemplateBinding>(storedBindingJson);
                if (storedBinding is null)
                {
                    throw new JsonSerializationException("Stored template binding is null.");
                }

                normalizedStoredBinding = SerializeBinding(storedBinding);
            }
            catch (JsonException exception)
            {
                throw new PocTemplateBindingMismatchException(exception);
            }

            if (!string.Equals(
                    normalizedStoredBinding,
                    SerializeBinding(canonicalBinding),
                    StringComparison.Ordinal))
            {
                // 快照是事实来源。只要持久化绑定与重新扫描结果不同，就拒绝返回可能错误的数据映射。
                throw new PocTemplateBindingMismatchException();
            }
        }

        return new PocTemplateDocument(reportCode, snapshot, canonicalBinding, revision);
    }

    private static string SerializeBinding(PocTemplateBinding binding) =>
        JsonConvert.SerializeObject(binding, Formatting.None);

    private Task<PocReportDefinition> GetDefinitionAsync(string reportCode) =>
        _configurationService is null
            ? Task.FromResult(PocReportCatalog.GetDefinition())
            : _configurationService.GetDefinitionAsync(reportCode);

    private static bool IsTemplateDataException(Exception exception) =>
        exception is JsonException or
            PocTemplateValidationException or
            PocTemplateBindingMismatchException;

    private static string LoadSeedJson()
    {
        // 作为嵌入资源发布可避免部署时漏拷贝种子文件，也让测试和运行时使用同一份初始模板。
        var assembly = typeof(PocTemplateService).Assembly;
        using var stream = assembly.GetManifestResourceStream(SeedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded report template seed '{SeedResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
