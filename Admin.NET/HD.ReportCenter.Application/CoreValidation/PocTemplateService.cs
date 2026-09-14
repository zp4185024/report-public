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

    /// <summary>初始化 PocTemplateBindingMismatchException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    public PocTemplateBindingMismatchException()
        : base(ErrorCode)
    {
    }

    /// <summary>初始化 PocTemplateBindingMismatchException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="innerException">innerException 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>初始化 PocStoredTemplateCorruptedException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="innerException">innerException 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>初始化 PocTemplateService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="store">store 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="configurationService">configurationService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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
                var seedJson = reportCode.Equals(
                    PocReportCatalog.ReportCode,
                    StringComparison.OrdinalIgnoreCase)
                        ? LoadSeedJson()
                        : CreateInitialTemplateJson(definition);
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
            var warning = exception is PocTemplateValidationException validationException
                ? $"当前模板结构无法按报表引擎展开：{validationException.Message} 请调整明细字段到同一行后保存模板。"
                : "数据集字段或报表参数已经变化，当前模板绑定需要重新编辑并保存。";
            return new PocTemplateDocument(
                stored.ReportCode,
                JObject.Parse(stored.SnapshotJson),
                new PocTemplateBinding(1, [], null),
                stored.Revision,
                warning);
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

    /// <summary>构建 PocTemplateService 所需的 Create Document 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="snapshotJson">snapshotJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="storedBindingJson">storedBindingJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="revision">revision 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>将 PocTemplateService 中的 Serialize Binding 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="binding">模板字段与工作簿坐标之间的绑定定义。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string SerializeBinding(PocTemplateBinding binding) =>
        JsonConvert.SerializeObject(binding, Formatting.None);

    /// <summary>查询或读取 PocTemplateService 所需的 Get Definition Async 数据，并按调用约定返回结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private Task<PocReportDefinition> GetDefinitionAsync(string reportCode) =>
        _configurationService is null
            ? Task.FromResult(PocReportCatalog.GetDefinition())
            : _configurationService.GetDefinitionAsync(reportCode);

    /// <summary>校验 PocTemplateService 的 Is Template Data Exception 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="exception">exception 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool IsTemplateDataException(Exception exception) =>
        exception is JsonException or
            PocTemplateValidationException or
            PocTemplateBindingMismatchException;

    /// <summary>查询或读取 PocTemplateService 所需的 Load Seed Json 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>构建 PocTemplateService 所需的 Create Initial Template Json 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string CreateInitialTemplateJson(PocReportDefinition definition)
    {
        const string sheetId = "report-sheet";
        var fieldCount = definition.Fields.Count;
        var parameterCount = definition.Parameters.Count;
        var usedColumnCount = Math.Max(6, Math.Max(fieldCount, parameterCount));
        // 第一阶段：构造 CreateInitialTemplateJson 当前步骤需要的中间数据和执行参数。
        var cellData = new JObject
        {
            ["0"] = new JObject
            {
                ["0"] = new JObject
                {
                    ["v"] = $"{definition.Name}（可编辑模板）",
                    ["s"] = "title"
                }
            }
        };

        if (parameterCount > 0)
        {
            var parameterRow = new JObject();
            for (var index = 0; index < parameterCount; index++)
            {
                var parameter = definition.Parameters[index];
                parameterRow[index.ToString()] = new JObject
                {
                    ["v"] = $"{parameter.DisplayName}：{{{{params.{parameter.Code}}}}}",
                    ["s"] = "meta"
                };
            }
            cellData["1"] = parameterRow;
        }

        if (fieldCount > 0)
        {
            var headerRow = new JObject();
            var detailRow = new JObject();
            for (var index = 0; index < fieldCount; index++)
            {
                var field = definition.Fields[index];
                headerRow[index.ToString()] = new JObject
                {
                    ["v"] = field.DisplayName,
                    ["s"] = "header"
                };
                // 第2阶段：根据 CreateInitialTemplateJson 当前分支转换输入数据，并保存该阶段产生的中间结果。
                detailRow[index.ToString()] = new JObject
                {
                    ["v"] = $"{{{{detail.{field.Code}}}}}",
                    ["s"] = field.DataType.Equals("number", StringComparison.OrdinalIgnoreCase)
                        ? "number"
                        : "center"
                };
            }
            cellData["3"] = headerRow;
            cellData["4"] = detailRow;
        }

        var columnData = new JObject();
        for (var index = 0; index < usedColumnCount; index++)
        {
            columnData[index.ToString()] = new JObject { ["w"] = index == 0 ? 150 : 120 };
        }

        // 第3阶段：构造 CreateInitialTemplateJson 当前步骤需要的中间数据和执行参数。
        var snapshot = new JObject
        {
            ["id"] = $"report-{definition.Code}",
            ["rev"] = 1,
            ["name"] = $"{definition.Name}（可编辑模板）",
            ["appVersion"] = "1.0.0",
            ["locale"] = "zhCN",
            ["styles"] = new JObject
            {
                ["title"] = new JObject
                {
                    ["ff"] = "Microsoft YaHei",
                    ["fs"] = 16,
                    ["bl"] = 1,
                    ["vt"] = 2
                },
                ["meta"] = new JObject
                {
                    ["ff"] = "Microsoft YaHei",
                    ["fs"] = 10,
                    ["cl"] = new JObject { ["rgb"] = "#344054" },
                    ["vt"] = 2
                },
                ["header"] = new JObject
                {
                    ["ff"] = "Microsoft YaHei",
                    ["fs"] = 10,
                    ["bl"] = 1,
                    ["bg"] = new JObject { ["rgb"] = "#e9eef5" },
                    ["cl"] = new JObject { ["rgb"] = "#18212b" },
                    ["ht"] = 2,
                    ["vt"] = 2
                },
                ["center"] = new JObject { ["ht"] = 2, ["vt"] = 2 },
                ["number"] = new JObject
                {
                    ["ht"] = 3,
                    ["vt"] = 2,
                    ["n"] = new JObject { ["pattern"] = "#,##0.00" }
                }
            },
            ["sheetOrder"] = new JArray(sheetId),
            ["sheets"] = new JObject
            {
                [sheetId] = new JObject
                {
                    ["id"] = sheetId,
                    ["name"] = definition.SheetName,
                    ["hidden"] = 0,
                    ["rowCount"] = 50,
                    ["columnCount"] = Math.Max(12, usedColumnCount),
                    ["zoomRatio"] = 1,
                    ["defaultColumnWidth"] = 100,
                    ["defaultRowHeight"] = 26,
                    ["mergeData"] = new JArray(new JObject
                    {
                        ["startRow"] = 0,
                        ["endRow"] = 0,
                        ["startColumn"] = 0,
                        ["endColumn"] = usedColumnCount - 1
                    }),
                    ["cellData"] = cellData,
                    ["rowData"] = new JObject
                    {
                        ["0"] = new JObject { ["h"] = 42 },
                        ["1"] = new JObject { ["h"] = 30 },
                        ["3"] = new JObject { ["h"] = 34 }
                    },
                    ["columnData"] = columnData,
                    ["rowHeader"] = new JObject { ["width"] = 46, ["hidden"] = 0 },
                    ["columnHeader"] = new JObject { ["height"] = 24, ["hidden"] = 0 },
                    ["showGridlines"] = 1,
                    ["rightToLeft"] = 0
                }
            }
        };
        // 第4阶段：整理 CreateInitialTemplateJson 已计算的数据，并按方法契约返回最终结果。
        return snapshot.ToString(Formatting.None);
    }
}
