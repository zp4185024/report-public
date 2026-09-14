using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocDataSourceDocument(
    string Code,
    string Name,
    string DbType,
    string Server,
    int Port,
    string Database,
    string UserName,
    bool PasswordConfigured,
    int TimeoutSeconds,
    int MaxRows,
    bool Enabled,
    int Revision);

public sealed record PocDataSourceInput(
    string Code,
    string Name,
    string DbType,
    string Server,
    int Port,
    string Database,
    string UserName,
    string? Password,
    int TimeoutSeconds,
    int MaxRows,
    bool Enabled,
    int Revision);

public sealed record PocDatasetDocument(
    string Code,
    string Name,
    string DataSourceCode,
    string QueryText,
    IReadOnlyList<PocDatasetVariableDefinition> Variables,
    IReadOnlyList<PocDatasetFieldDefinition> Fields,
    int TimeoutSeconds,
    int MaxRows,
    bool Enabled,
    int Revision);

public sealed record PocDatasetOption(
    string Code,
    string Name,
    string DataSourceCode,
    IReadOnlyList<PocDatasetVariableDefinition> Variables,
    IReadOnlyList<PocDatasetFieldDefinition> Fields,
    bool Enabled);

public sealed record PocExternalParameterSourceDocument(
    string Code,
    string Name,
    string BaseUrl,
    string Endpoint,
    string HttpMethod,
    string RequestBodyTemplate,
    string HeadersTemplate,
    IReadOnlyList<PocExternalRequestParameterDefinition> RequestParameters,
    int TimeoutSeconds,
    bool Enabled,
    int Revision,
    IReadOnlyList<PocExternalParameterFieldDefinition> Fields);

public sealed record PocExternalParameterSourceInput(
    string Code,
    string Name,
    string BaseUrl,
    string Endpoint,
    string HttpMethod,
    string RequestBodyTemplate,
    string HeadersTemplate,
    IReadOnlyList<PocExternalRequestParameterDefinition> RequestParameters,
    int TimeoutSeconds,
    bool Enabled,
    int Revision,
    IReadOnlyList<PocExternalParameterFieldDefinition> Fields);

public sealed record PocReportConfigurationDocument(
    string ReportCode,
    string Name,
    string SheetName,
    PocPreviewSettings Preview,
    PocExportSettings Export,
    string DatasetCode,
    IReadOnlyList<PocDatasetOption> Datasets,
    IReadOnlyList<PocParameterDefinition> Parameters,
    IReadOnlyList<PocDatasetFieldDefinition> Fields,
    int Revision,
    IReadOnlyList<PocFieldPresentationDefinition>? FieldPresentations = null,
    PocShareProtectionSettings? ShareProtection = null,
    IReadOnlyList<PocExternalParameterSourceDefinition>? ExternalSources = null);

public sealed record PocReportSummaryDocument(
    string ReportCode,
    string Name,
    string SheetName,
    string DatasetCode,
    bool Enabled,
    int ConfigurationRevision,
    int TemplateRevision,
    DateTime? UpdatedAt);

public sealed record PocPagedDocument<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total,
    int TotalPages);

public sealed record PocCreateReportRequest(
    string ReportCode,
    string Name,
    string SheetName,
    string DatasetCode);

public sealed record PocReportCreateOptionsDocument(
    IReadOnlyList<PocDatasetOption> Datasets);

public sealed record PocSaveReportConfigurationRequest(
    string Name,
    string SheetName,
    PocPreviewSettings Preview,
    PocExportSettings Export,
    string DatasetCode,
    IReadOnlyList<PocParameterDefinition> Parameters,
    int Revision,
    IReadOnlyList<PocFieldPresentationDefinition>? FieldPresentations = null,
    PocSaveShareProtectionRequest? ShareProtection = null);

public sealed record PocSaveShareProtectionRequest(
    bool Enabled,
    string? NewPassword);

public sealed record PocShareProtectionUnlockRequest(string Password);

public sealed record PocShareProtectionUnlockResult(bool Valid);

public sealed record PocDataSourceTestResult(
    bool Success,
    long DurationMilliseconds,
    string DatabaseVersion);

public sealed record PocRuntimeReportConfiguration(
    PocReportDefinition Definition,
    string DataSourceCode,
    string DbType,
    string ConnectionString,
    string QueryText,
    int TimeoutSeconds,
    int MaxRows,
    bool UsesFallbackData);

public sealed class PocReportAlreadyExistsException : Exception
{
    /// <summary>初始化 PocReportAlreadyExistsException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    public PocReportAlreadyExistsException(string reportCode)
        : base($"报表编码 '{reportCode}' 已存在。")
    {
    }
}

/// <summary>配置应用服务：负责种子回退、公开脱敏、输入校验和连接串合成。</summary>
public sealed class PocReportConfigurationService : ITransient
{
    private const string DefaultDataSourceCode = "report_center";
    private const string DefaultDatasetCode = "detail";
    private static readonly HashSet<string> ParameterDataTypes = new(
        ["string", "text", "int", "long", "decimal", "date", "datetime", "bool", "boolean"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ParameterSourceTypes = new(
        ["Form", "Url", "System", "Fixed", "External"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ParameterControlTypes = new(
        ["text", "number", "date", "datetime", "month", "year", "checkbox", "select", "multiSelect", "hidden"],
        StringComparer.OrdinalIgnoreCase);
    private readonly PocReportConfigurationStore _store;
    private readonly PocDataAssetStore _dataAssets;
    private readonly ISqlSugarClient _database;
    private readonly PocTemplateStore? _templates;
    private readonly PocExternalParameterSourceService? _externalParameters;

    /// <summary>初始化 PocReportConfigurationService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="store">store 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="dataAssets">dataAssets 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="database">database 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="templates">templates 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="externalParameters">externalParameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocReportConfigurationService(
        PocReportConfigurationStore store,
        PocDataAssetStore dataAssets,
        ISqlSugarClient database,
        PocTemplateStore? templates = null,
        PocExternalParameterSourceService? externalParameters = null)
    {
        _store = store;
        _dataAssets = dataAssets;
        _database = database;
        _templates = templates;
        _externalParameters = externalParameters;
    }

    /// <summary>调用 PocReportConfigurationService 的异步依赖完成 List Reports Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocReportSummaryDocument>> ListReportsAsync()
    {
        var reports = await _store.ListReportsAsync();
        IReadOnlyList<PocStoredTemplateSummary> templates = _templates is null
            ? []
            : await _templates.ListAsync();
        // 第一阶段：构造 ListReportsAsync 当前步骤需要的中间数据和执行参数。
        var templateByReport = templates.ToDictionary(
            item => item.ReportCode,
            StringComparer.OrdinalIgnoreCase);

        if (reports.Count == 0)
        {
            templateByReport.TryGetValue(PocReportCatalog.ReportCode, out var fallbackTemplate);
            var fallback = PocReportCatalog.GetDefinition();
            return [new PocReportSummaryDocument(
                fallback.Code,
                fallback.Name,
                fallback.SheetName,
                DefaultDatasetCode,
                true,
                0,
                fallbackTemplate?.Revision ?? 0,
                fallbackTemplate?.UpdatedAt)];
        }

        return reports.Select(report =>
        {
            // 第2阶段：根据 ListReportsAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
            templateByReport.TryGetValue(report.Code, out var template);
            var reportUpdatedAt = report.UpdateTime ?? report.CreateTime;
            var updatedAt = template?.UpdatedAt is DateTime templateUpdatedAt &&
                templateUpdatedAt > reportUpdatedAt
                    ? templateUpdatedAt
                    : reportUpdatedAt;
            return new PocReportSummaryDocument(
                report.Code,
                report.Name,
                report.SheetName,
                report.DatasetCode,
                report.IsEnabled,
                report.Revision,
                template?.Revision ?? 0,
                updatedAt);
        }).ToArray();
    }

    /// <summary>按关键字分页读取报表摘要，模板版本只查询当前页对应的记录。</summary>
    /// <param name="page">从一开始的页码。</param>
    /// <param name="pageSize">每页返回的最大记录数，服务端限制为 100。</param>
    /// <param name="keyword">可选的名称、编码、工作表或数据集搜索关键字。</param>
    /// <returns>返回当前页、总数和总页数，供管理列表稳定翻页。</returns>
    public async Task<PocPagedDocument<PocReportSummaryDocument>> ListReportsPageAsync(
        int page,
        int pageSize,
        string? keyword)
    {
        var normalizedPage = Math.Max(page, 1);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        var storedPage = await _store.ListReportsPageAsync(
            normalizedPage,
            normalizedPageSize,
            keyword);
        IReadOnlyList<PocStoredTemplateSummary> templates = _templates is null
            ? []
            : await _templates.ListByReportCodesAsync(
                storedPage.Items.Select(item => item.Code).ToArray());
        var templateByReport = templates.ToDictionary(
            item => item.ReportCode,
            StringComparer.OrdinalIgnoreCase);
        var items = storedPage.Items.Select(report =>
        {
            templateByReport.TryGetValue(report.Code, out var template);
            var reportUpdatedAt = report.UpdateTime ?? report.CreateTime;
            var updatedAt = template?.UpdatedAt is DateTime templateUpdatedAt &&
                templateUpdatedAt > reportUpdatedAt
                    ? templateUpdatedAt
                    : reportUpdatedAt;
            return new PocReportSummaryDocument(
                report.Code,
                report.Name,
                report.SheetName,
                report.DatasetCode,
                report.IsEnabled,
                report.Revision,
                template?.Revision ?? 0,
                updatedAt);
        }).ToArray();
        var total = storedPage.Total;
        if (total == 0 && string.IsNullOrWhiteSpace(keyword))
        {
            var fallback = PocReportCatalog.GetDefinition();
            total = 1;
            if (normalizedPage == 1)
            {
                items = [new PocReportSummaryDocument(
                    fallback.Code,
                    fallback.Name,
                    fallback.SheetName,
                    DefaultDatasetCode,
                    true,
                    0,
                    0,
                    null)];
            }
        }

        return new PocPagedDocument<PocReportSummaryDocument>(
            items,
            normalizedPage,
            normalizedPageSize,
            total,
            (int)Math.Ceiling(total / (double)normalizedPageSize));
    }

    /// <summary>查询或读取 PocReportConfigurationService 所需的 Get Create Options Async 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocReportCreateOptionsDocument> GetCreateOptionsAsync()
    {
        var datasets = await _dataAssets.ListDatasetsAsync();
        return new PocReportCreateOptionsDocument(
            datasets
                .Where(item => item.IsEnabled)
                .Select(ToDatasetOption)
                .ToArray());
    }

    /// <summary>构建 PocReportConfigurationService 所需的 Create Async 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocReportConfigurationDocument> CreateAsync(
        PocCreateReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reportCode = NormalizeReportCode(request.ReportCode).ToLowerInvariant();
        var datasetCode = request.DatasetCode?.Trim() ?? string.Empty;
        ValidateCode(datasetCode, "DATASET_DEFINITION_INVALID");
        if (await _store.ReportExistsAsync(reportCode))
        // 第一阶段：根据 CreateAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            throw new PocReportAlreadyExistsException(reportCode);
        }

        var dataset = await _dataAssets.GetDatasetAsync(datasetCode)
            ?? throw new PocDatasetConfigurationException(
                "DATASET_NOT_FOUND",
                $"不存在数据集 '{datasetCode}'。");
        var schema = PocDataAssetService.ReadSchema(dataset);
        var parameters = schema.Variables.Select(variable => new PocParameterDefinition(
            variable.Code,
            variable.DisplayName,
            ControlTypeFor(variable.DataType),
            variable.SourceType,
            null,
            !variable.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase),
            variable.DataType,
            ReadOnly: variable.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase),
            Required: variable.Required,
            AllowUrlPrefill: variable.SourceType.Equals("Form", StringComparison.OrdinalIgnoreCase),
            UrlKey: variable.SourceType.Equals("Form", StringComparison.OrdinalIgnoreCase)
                ? variable.Code
                : null,
            SourceCode: variable.SourceCode,
            SourceFieldCode: variable.SourceFieldCode)).ToArray();

        // 第2阶段：整理 CreateAsync 已计算的数据，并按方法契约返回最终结果。
        return await SaveAsync(
            reportCode,
            new PocSaveReportConfigurationRequest(
                request.Name,
                request.SheetName,
                new PocPreviewSettings(PocPreviewMode.Full, 20, false),
                new PocExportSettings(
                    PocDataExportMode.Normal,
                    NormalMaxRows: 20_000,
                    NormalMaxCells: 500_000,
                    NormalTimeoutSeconds: 60,
                    LargeExportEnabled: true,
                    RowsPerSheet: 1_000_000,
                    FileRetentionHours: 24),
                dataset.Code,
                parameters,
                0));
    }

    /// <summary>查询或读取 PocReportConfigurationService 所需的 Get Document Async 数据，并按调用约定返回结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="now">now 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocReportConfigurationDocument> GetDocumentAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        var normalizedCode = NormalizeReportCode(reportCode);
        var stored = await _store.GetAsync(normalizedCode);
        var datasets = await _dataAssets.ListDatasetsAsync();
        var externalSources = await ExternalSourceDefinitionsAsync();
        if (stored is not null)
        {
            return CreateDocument(stored, datasets, externalSources, now);
        }
        if (normalizedCode.Equals(PocReportCatalog.ReportCode, StringComparison.OrdinalIgnoreCase))
        {
            return CreateFallbackDocument(now, datasets, externalSources);
        }
        throw new KeyNotFoundException($"不存在报表 '{normalizedCode}'。");
    }

    /// <summary>查询或读取 PocReportConfigurationService 所需的 Get Definition Async 数据，并按调用约定返回结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="now">now 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocReportDefinition> GetDefinitionAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        var document = await GetDocumentAsync(reportCode, now);
        return ToDefinition(document);
    }

    /// <summary>校验 PocReportConfigurationService 的 Verify Share Protection Password Async 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="password">password 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<bool> VerifyShareProtectionPasswordAsync(
        string reportCode,
        string? password)
    {
        var normalizedCode = NormalizeReportCode(reportCode);
        var stored = await _store.GetAsync(normalizedCode);
        if (stored is null || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var protection = PocShareProtectionPassword.Read(stored.Report.PreviewSettingsJson);
        return protection is { Enabled: true } &&
            PocShareProtectionPassword.Verify(protection, password);
    }

    /// <summary>查询或读取 PocReportConfigurationService 所需的 Get Runtime Async 数据，并按调用约定返回结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="now">now 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocRuntimeReportConfiguration> GetRuntimeAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        var normalizedCode = NormalizeReportCode(reportCode);
        var stored = await _store.GetAsync(normalizedCode);
        if (stored is null)
        {
            // 第一阶段：检查 GetRuntimeAsync 当前条件，并按匹配分支执行对应处理。
            if (!normalizedCode.Equals(PocReportCatalog.ReportCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new KeyNotFoundException($"不存在报表 '{normalizedCode}'。");
            }
            return new PocRuntimeReportConfiguration(
                PocReportCatalog.GetDefinition(now),
                DefaultDataSourceCode,
                "SqlServer",
                _database.CurrentConnectionConfig.ConnectionString,
                FallbackQueryText,
                30,
                20_000,
                true);
        }

        if (!stored.Dataset.IsEnabled || !stored.DataSource.IsEnabled)
        {
            throw new PocDatasetConfigurationException(
                "DATA_ASSET_DISABLED",
                "报表引用的数据集或数据源已停用。");
        }
        // 第2阶段：调用 GetRuntimeAsync 依赖的异步能力，等待并校验返回数据。
        var document = CreateDocument(
            stored,
            [stored.Dataset],
            await ExternalSourceDefinitionsAsync(),
            now);
        return new PocRuntimeReportConfiguration(
            ToDefinition(document),
            stored.DataSource.Code,
            stored.DataSource.DbType,
            stored.DataSource.ConnectionString,
            stored.Dataset.QueryText,
            stored.Dataset.TimeoutSeconds,
            Math.Min(stored.Dataset.MaxRows, stored.DataSource.DefaultMaxRows),
            false);
    }

    /// <summary>更新 PocReportConfigurationService 的 Save Async 状态，并同步相关业务数据。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocReportConfigurationDocument> SaveAsync(
        string reportCode,
        PocSaveReportConfigurationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedCode = NormalizeReportCode(reportCode);
        ValidateRequest(request);

        var existing = await _store.GetAsync(normalizedCode);
        var storedProtection = PocShareProtectionPassword.Read(
            existing?.Report.PreviewSettingsJson);
        // 第一阶段：构造 SaveAsync 当前步骤需要的中间数据和执行参数。
        var nextProtection = ResolveShareProtection(
            request.ShareProtection,
            storedProtection);

        var dataset = await _dataAssets.GetDatasetAsync(request.DatasetCode.Trim())
            ?? throw new PocDatasetConfigurationException(
                "DATASET_NOT_FOUND",
                $"不存在数据集 '{request.DatasetCode}'。");
        var dataSource = await _dataAssets.GetDataSourceAsync(dataset.DataSourceCode)
            ?? throw new PocDatasetConfigurationException(
                "DATA_SOURCE_NOT_FOUND",
                $"数据集引用的数据源 '{dataset.DataSourceCode}' 不存在。");
        if (!dataset.IsEnabled || !dataSource.IsEnabled)
        {
            throw new PocDatasetConfigurationException(
                "DATA_ASSET_DISABLED",
                "只能选择已启用且数据源可用的数据集。");
        }
        var datasetSchema = PocDataAssetService.ReadSchema(dataset);
        ValidateDatasetVariableBindings(datasetSchema.Variables, request.Parameters);
        ValidateExternalParameterBindings(
            request.Parameters,
            await ExternalSourceDocumentsAsync());
        ValidateFieldPresentations(request.FieldPresentations ?? [], datasetSchema.Fields);
        _ = PocReadOnlySqlValidator.Validate(
            dataset.QueryText,
            datasetSchema.Variables.Select(item => item.Code),
            request.Preview.Mode);

        // 第2阶段：遍历 SaveAsync 当前数据集合，逐项转换并维护处理结果。
        var parameterEntities = request.Parameters.Select((parameter, index) =>
            new PocReportParameter
            {
                Code = parameter.Code.Trim(),
                DisplayName = parameter.DisplayName.Trim(),
                DataType = parameter.DataType.Trim(),
                ControlType = parameter.ControlType.Trim(),
                SourceType = parameter.SourceType.Trim(),
                SourceCode = NullIfWhiteSpace(parameter.SourceCode),
                SourceFieldCode = NullIfWhiteSpace(parameter.SourceFieldCode),
                UrlKey = NullIfWhiteSpace(parameter.UrlKey),
                DefaultExpression = NullIfWhiteSpace(parameter.DefaultExpression),
                FixedValue = NullIfWhiteSpace(parameter.FixedValue),
                OptionsJson = parameter.Options is null
                    ? null
                    : JsonConvert.SerializeObject(parameter.Options, Formatting.None),
                IsVisible = parameter.Visible,
                IsReadOnly = parameter.ReadOnly,
                IsRequired = parameter.Required,
                AllowUrlPrefill = parameter.AllowUrlPrefill,
                DisplayOrder = index
            }).ToArray();

        await _store.SaveAsync(new PocReportConfigurationMutation(
            normalizedCode,
            request.Name.Trim(),
            request.SheetName.Trim(),
            PocShareProtectionPassword.Write(request.Preview, nextProtection),
            JsonConvert.SerializeObject(request.Export, Formatting.None),
            dataset.Code,
            parameterEntities,
            request.Revision,
            JsonConvert.SerializeObject(request.FieldPresentations ?? [], Formatting.None)));

        return await GetDocumentAsync(reportCode);
    }

    /// <summary>构建 PocReportConfigurationService 所需的 Create Fallback Document 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="now">now 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="storedDatasets">storedDatasets 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="externalSources">externalSources 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private PocReportConfigurationDocument CreateFallbackDocument(
        DateTimeOffset? now,
        IReadOnlyList<PocReportDataset> storedDatasets,
        IReadOnlyList<PocExternalParameterSourceDefinition> externalSources)
    {
        var definition = PocReportCatalog.GetDefinition(now);
        var options = storedDatasets.Count == 0
            ? [new PocDatasetOption(
                DefaultDatasetCode,
                "科室工作量明细",
                DefaultDataSourceCode,
                [new PocDatasetVariableDefinition("deptId", "科室", "string", false)],
                definition.Fields,
                true)]
            : storedDatasets.Select(ToDatasetOption).ToArray();
        var selected = options.FirstOrDefault(item =>
                item.Code.Equals(DefaultDatasetCode, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(item => item.Enabled)
            ?? options[0];
        return new PocReportConfigurationDocument(
            definition.Code,
            definition.Name,
            definition.SheetName,
            definition.Preview,
            definition.Export,
            selected.Code,
            options,
            definition.Parameters,
            selected.Fields,
            0,
            definition.FieldPresentations ?? [],
            definition.ShareProtection ?? new PocShareProtectionSettings(false, false),
            externalSources);
    }

    /// <summary>构建 PocReportConfigurationService 所需的 Create Document 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="stored">stored 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="datasets">datasets 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="externalSources">externalSources 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="now">now 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private PocReportConfigurationDocument CreateDocument(
        PocStoredReportConfiguration stored,
        IReadOnlyList<PocReportDataset> datasets,
        IReadOnlyList<PocExternalParameterSourceDefinition> externalSources,
        DateTimeOffset? now)
    {
        var preview = Deserialize<PocPreviewSettings>(stored.Report.PreviewSettingsJson);
        var storedProtection = PocShareProtectionPassword.Read(stored.Report.PreviewSettingsJson);
        var export = Deserialize<PocExportSettings>(stored.Report.ExportSettingsJson);
        var datasetSchema = PocDataAssetService.ReadSchema(stored.Dataset);
        var storedParameters = stored.Parameters.Select(parameter =>
        // 第一阶段：根据 CreateDocument 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            var defaultValue = PocDefaultExpressionEvaluator.Evaluate(
                parameter.DefaultExpression,
                DefaultExpressionDataType(parameter.DataType, parameter.ControlType),
                now);
            return new PocParameterDefinition(
                parameter.Code,
                parameter.DisplayName,
                parameter.ControlType,
                parameter.SourceType,
                defaultValue,
                parameter.IsVisible,
                parameter.DataType,
                parameter.DefaultExpression,
                parameter.FixedValue,
                parameter.IsReadOnly,
                parameter.IsRequired,
                parameter.AllowUrlPrefill,
                parameter.UrlKey,
                DeserializeList<PocValueOption>(parameter.OptionsJson),
                parameter.SourceCode,
                parameter.SourceFieldCode);
        }).ToArray();
        // 第2阶段：构造 CreateDocument 当前步骤需要的中间数据和执行参数。
        var parameters = AlignExternalParameters(
            storedParameters,
            datasetSchema.Variables,
            externalSources);
        return new PocReportConfigurationDocument(
            stored.Report.Code,
            stored.Report.Name,
            stored.Report.SheetName,
            preview,
            export,
            stored.Report.DatasetCode,
            datasets.Select(ToDatasetOption).ToArray(),
            parameters,
            datasetSchema.Fields,
            stored.Report.Revision,
            DeserializeList<PocFieldPresentationDefinition>(stored.Report.FieldPresentationsJson),
            new PocShareProtectionSettings(
                storedProtection?.Enabled ?? false,
                storedProtection is not null),
            externalSources);
    }

    /// <summary>将 PocReportConfigurationService 的 To Definition 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="document">document 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocReportDefinition ToDefinition(PocReportConfigurationDocument document) =>
        new(
            document.ReportCode,
            document.Name,
            document.SheetName,
            document.Preview,
            document.Export,
            document.Parameters,
            document.Fields,
            document.FieldPresentations ?? [],
            document.ShareProtection ?? new PocShareProtectionSettings(false, false),
            document.ExternalSources ?? []);

    /// <summary>负责 PocReportConfigurationService 的 External Source Definitions Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<IReadOnlyList<PocExternalParameterSourceDefinition>> ExternalSourceDefinitionsAsync() =>
        _externalParameters is null ? [] : await _externalParameters.ListDefinitionsAsync();

    /// <summary>负责 PocReportConfigurationService 的 External Source Documents Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<IReadOnlyList<PocExternalParameterSourceDocument>> ExternalSourceDocumentsAsync() =>
        _externalParameters is null ? [] : await _externalParameters.ListAsync();

    /// <summary>解析并计算 PocReportConfigurationService 的 Resolve Share Protection 结果，处理必要的边界条件。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="existing">existing 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocStoredShareProtection? ResolveShareProtection(
        PocSaveShareProtectionRequest? request,
        PocStoredShareProtection? existing)
    {
        if (request is null)
        {
            return existing;
        }

        var next = string.IsNullOrEmpty(request.NewPassword)
            ? existing
            : PocShareProtectionPassword.Create(request.NewPassword, request.Enabled);
        if (request.Enabled && next is null)
        {
            throw new PocDatasetConfigurationException(
                "SHARE_PROTECTION_PASSWORD_REQUIRED",
                "开启共享数据保护前必须设置解除保护密码。");
        }

        return next is null ? null : next with { Enabled = request.Enabled };
    }

    /// <summary>根据 PocReportConfigurationService 当前输入计算 To Dataset Option 结果，并按方法声明的类型返回。</summary>
    /// <param name="dataset">dataset 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDatasetOption ToDatasetOption(PocReportDataset dataset)
    {
        var schema = PocDataAssetService.ReadSchema(dataset);
        return new PocDatasetOption(
            dataset.Code,
            dataset.Name,
            dataset.DataSourceCode,
            schema.Variables,
            schema.Fields,
            dataset.IsEnabled);
    }

    /// <summary>校验 PocReportConfigurationService 的 Validate Dataset Variable Bindings 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="variables">variables 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void ValidateDatasetVariableBindings(
        IReadOnlyList<PocDatasetVariableDefinition> variables,
        IReadOnlyList<PocParameterDefinition> parameters)
    {
        foreach (var variable in variables)
        {
            var parameter = parameters.FirstOrDefault(item =>
                item.Code.Equals(variable.Code, StringComparison.OrdinalIgnoreCase))
                ?? throw new PocDatasetConfigurationException(
                    "DATASET_PARAMETER_UNMAPPED",
                    $"数据集变量 '@{variable.Code}' 尚未配置报表取值。");
            var external = variable.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase);
            if (!parameter.DataType.Equals(variable.DataType, StringComparison.OrdinalIgnoreCase) ||
                (external && !parameter.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase)) ||
                (external && !string.Equals(
                    parameter.SourceCode,
                    variable.SourceCode,
                    StringComparison.OrdinalIgnoreCase)) ||
                (external && !string.Equals(
                    parameter.SourceFieldCode,
                    variable.SourceFieldCode,
                    StringComparison.OrdinalIgnoreCase)) ||
                (variable.Required && !parameter.Required))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_PARAMETER_TYPE_MISMATCH",
                    $"报表参数 '{parameter.Code}' 与数据集变量定义不一致。");
            }
        }
    }

    /// <summary>遍历 PocReportConfigurationService 当前数据并执行 Align External Parameters 转换，生成调用方需要的结果集合。</summary>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="variables">variables 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sources">sources 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal static IReadOnlyList<PocParameterDefinition> AlignExternalParameters(
        IReadOnlyList<PocParameterDefinition> parameters,
        IReadOnlyList<PocDatasetVariableDefinition> variables,
        IReadOnlyList<PocExternalParameterSourceDefinition> sources)
    {
        var result = parameters.Select(parameter => parameter with { }).ToList();
        var sourceByCode = sources.ToDictionary(source => source.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables.Where(variable =>
            variable.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase)))
        // 第一阶段：根据 AlignExternalParameters 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            if (string.IsNullOrWhiteSpace(variable.SourceCode) ||
                string.IsNullOrWhiteSpace(variable.SourceFieldCode))
            {
                continue;
            }
            UpsertExternalParameter(
                result,
                variable.Code,
                variable.DisplayName,
                variable.DataType,
                variable.Required,
                variable.SourceCode,
                variable.SourceFieldCode);

            if (!sourceByCode.TryGetValue(variable.SourceCode, out var source))
            {
                continue;
            }

            foreach (var sourceField in source.Fields)
            {
                // 第2阶段：检查 AlignExternalParameters 当前条件，并按匹配分支执行对应处理。
                if (result.Any(parameter =>
                    parameter.Code.Equals(sourceField.Code, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                UpsertExternalParameter(
                    result,
                    sourceField.Code,
                    sourceField.DisplayName,
                    sourceField.DataType,
                    false,
                    source.Code,
                    sourceField.Code);
            }
        }
        return result;
    }

    /// <summary>负责 PocReportConfigurationService 的 Upsert External Parameter 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="displayName">需要读取、校验或转换的业务值。</param>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="required">required 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sourceCode">需要读取、校验或转换的业务值。</param>
    /// <param name="sourceFieldCode">需要读取、校验或转换的业务值。</param>
    private static void UpsertExternalParameter(
        IList<PocParameterDefinition> parameters,
        string code,
        string displayName,
        string dataType,
        bool required,
        string sourceCode,
        string sourceFieldCode)
    {
        var index = parameters.ToList().FindIndex(parameter =>
            parameter.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        var current = index >= 0 ? parameters[index] : null;
        var external = new PocParameterDefinition(
            code,
            string.IsNullOrWhiteSpace(current?.DisplayName) ? displayName : current.DisplayName,
            current?.ControlType ?? "text",
            "External",
            null,
            false,
            dataType,
            ReadOnly: true,
            Required: required || current?.Required == true,
            AllowUrlPrefill: false,
            Options: current?.Options,
            SourceCode: sourceCode,
            SourceFieldCode: sourceFieldCode);
        if (index >= 0)
        {
            parameters[index] = external;
        }
        else
        {
            parameters.Add(external);
        }
    }

    /// <summary>校验 PocReportConfigurationService 的 Validate External Parameter Bindings 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sources">sources 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void ValidateExternalParameterBindings(
        IReadOnlyList<PocParameterDefinition> parameters,
        IReadOnlyList<PocExternalParameterSourceDocument> sources)
    {
        foreach (var parameter in parameters.Where(item =>
            item.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase)))
        {
            var source = sources.FirstOrDefault(item =>
                    item.Code.Equals(parameter.SourceCode, StringComparison.OrdinalIgnoreCase))
                ?? throw new PocDatasetConfigurationException(
                    "EXTERNAL_PARAMETER_SOURCE_NOT_FOUND",
                    $"参数 '{parameter.Code}' 引用的外部参数源不存在。");
            var field = source.Fields.FirstOrDefault(item =>
                    item.Code.Equals(parameter.SourceFieldCode, StringComparison.OrdinalIgnoreCase))
                ?? throw new PocDatasetConfigurationException(
                    "EXTERNAL_PARAMETER_FIELD_NOT_FOUND",
                    $"参数 '{parameter.Code}' 引用的外部字段不存在。");
            if (!source.Enabled ||
                !field.DataType.Equals(parameter.DataType, StringComparison.OrdinalIgnoreCase))
            {
                throw new PocDatasetConfigurationException(
                    "EXTERNAL_PARAMETER_BINDING_INVALID",
                    $"参数 '{parameter.Code}' 的外部字段绑定无效。");
            }
        }
    }

    /// <summary>校验 PocReportConfigurationService 的 Validate Request 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    private static void ValidateRequest(PocSaveReportConfigurationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            request.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(request.SheetName) ||
            request.SheetName.Length > 31 ||
            request.SheetName.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0)
        {
            // 第一阶段：根据 ValidateRequest 当前分支转换输入数据，并保存该阶段产生的中间结果。
            throw new PocDatasetConfigurationException("REPORT_DEFINITION_INVALID", "报表名称或工作表名称无效。");
        }
        if (!Enum.IsDefined(request.Preview.Mode) ||
            request.Preview.PageSize is < 1 or > 10_000 ||
            request.Parameters.Count > 100)
        {
            throw new PocDatasetConfigurationException("REPORT_DEFINITION_INVALID", "预览页大小无效。");
        }
        if (!Enum.IsDefined(request.Export.DefaultMode) ||
            request.Export.NormalMaxRows is < 1 or > 1_000_000 ||
            request.Export.NormalMaxCells is < 1 or > 20_000_000 ||
            request.Export.NormalTimeoutSeconds is < 1 or > 3_600 ||
            request.Export.RowsPerSheet is < 1 or > 1_048_576 ||
            request.Export.FileRetentionHours is < 1 or > 720 ||
            (request.Export.DefaultMode == PocDataExportMode.Large && !request.Export.LargeExportEnabled))
        {
            throw new PocDatasetConfigurationException("REPORT_DEFINITION_INVALID", "导出设置无效。");
        }
        ValidateCode(request.DatasetCode, "DATASET_DEFINITION_INVALID");

        if (request.ShareProtection?.NewPassword is string password &&
            (string.IsNullOrWhiteSpace(password) || password.Length is < 8 or > 128))
        {
            throw new PocDatasetConfigurationException(
                "SHARE_PROTECTION_PASSWORD_INVALID",
                "解除保护密码长度必须为 8 到 128 个字符。");
        }

        var parameterCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 第2阶段：遍历 ValidateRequest 当前数据集合，逐项转换并维护处理结果。
        foreach (var parameter in request.Parameters)
        {
            ValidateCode(parameter.Code, "PARAM_DEFINITION_INVALID");
            if (!parameterCodes.Add(parameter.Code))
            {
                throw new PocDatasetConfigurationException("PARAM_DEFINITION_INVALID", $"参数编码 '{parameter.Code}' 重复。");
            }
            if (string.IsNullOrWhiteSpace(parameter.DisplayName) ||
                parameter.DisplayName.Length > 128 ||
                !ParameterDataTypes.Contains(parameter.DataType) ||
                !ParameterSourceTypes.Contains(parameter.SourceType) ||
                !ParameterControlTypes.Contains(parameter.ControlType) ||
                (parameter.UrlKey?.Length ?? 0) > 64 ||
                (parameter.DefaultExpression?.Length ?? 0) > 256 ||
                (parameter.FixedValue?.Length ?? 0) > 1_024 ||
                (parameter.SourceType.Equals("Fixed", StringComparison.OrdinalIgnoreCase) &&
                 string.IsNullOrWhiteSpace(parameter.FixedValue)) ||
                (parameter.SourceType.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                 string.IsNullOrWhiteSpace(parameter.DefaultExpression)) ||
                (parameter.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase) &&
                 (string.IsNullOrWhiteSpace(parameter.SourceCode) ||
                  string.IsNullOrWhiteSpace(parameter.SourceFieldCode))) ||
                (!parameter.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase) &&
                 (!string.IsNullOrWhiteSpace(parameter.SourceCode) ||
                  !string.IsNullOrWhiteSpace(parameter.SourceFieldCode))) ||
                (!string.IsNullOrWhiteSpace(parameter.DefaultExpression) &&
                 !IsDateParameter(parameter.DataType, parameter.ControlType)))
            {
                // 第3阶段：根据 ValidateRequest 当前分支转换输入数据，并保存该阶段产生的中间结果。
                throw new PocDatasetConfigurationException("PARAM_DEFINITION_INVALID", $"参数 '{parameter.Code}' 的定义无效。");
            }
            var choiceControl =
                parameter.ControlType.Equals("select", StringComparison.OrdinalIgnoreCase) ||
                parameter.ControlType.Equals("multiSelect", StringComparison.OrdinalIgnoreCase);
            var parameterOptions = parameter.Options ?? [];
            if ((choiceControl && parameterOptions.Count == 0) ||
                (!choiceControl && parameterOptions.Count > 0) ||
                (parameter.ControlType.Equals("multiSelect", StringComparison.OrdinalIgnoreCase) &&
                 !parameter.DataType.Equals("string", StringComparison.OrdinalIgnoreCase) &&
                 !parameter.DataType.Equals("text", StringComparison.OrdinalIgnoreCase)))
            {
                throw new PocDatasetConfigurationException(
                    "PARAM_DEFINITION_INVALID",
                    $"参数 '{parameter.Code}' 的选项配置无效。");
            }
            ValidateValueOptions(parameterOptions, "PARAM_DEFINITION_INVALID", parameter.Code);
            _ = PocDefaultExpressionEvaluator.Evaluate(
                parameter.DefaultExpression,
                DefaultExpressionDataType(parameter.DataType, parameter.ControlType));
        }

    }

    /// <summary>校验 PocReportConfigurationService 的 Validate Field Presentations 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="presentations">presentations 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="fields">参与映射、显示或数据写入的字段集合。</param>
    private static void ValidateFieldPresentations(
        IReadOnlyList<PocFieldPresentationDefinition> presentations,
        IReadOnlyList<PocDatasetFieldDefinition> fields)
    {
        if (presentations.Count > fields.Count)
        {
            throw new PocDatasetConfigurationException(
                "FIELD_PRESENTATION_INVALID",
                "返回字段显示映射数量无效。");
        }
        var fieldCodes = fields.Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presentationCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var presentation in presentations)
        {
            if (!fieldCodes.Contains(presentation.Code) ||
                !presentationCodes.Add(presentation.Code) ||
                presentation.ValueMappings.Count == 0)
            {
                throw new PocDatasetConfigurationException(
                    "FIELD_PRESENTATION_INVALID",
                    $"返回字段 '{presentation.Code}' 的显示映射无效。");
            }
            ValidateValueOptions(
                presentation.ValueMappings,
                "FIELD_PRESENTATION_INVALID",
                presentation.Code);
        }
    }

    /// <summary>校验 PocReportConfigurationService 的 Validate Value Options 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="options">控制本次处理行为的配置选项。</param>
    /// <param name="errorCode">需要读取、校验或转换的业务值。</param>
    /// <param name="ownerCode">需要读取、校验或转换的业务值。</param>
    private static void ValidateValueOptions(
        IReadOnlyList<PocValueOption> options,
        string errorCode,
        string ownerCode)
    {
        if (options.Count > 200)
        {
            throw new PocDatasetConfigurationException(errorCode, $"'{ownerCode}' 的选项不能超过 200 个。");
        }
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Value) ||
                option.Value.Length > 256 ||
                string.IsNullOrWhiteSpace(option.Label) ||
                option.Label.Length > 256 ||
                !values.Add(option.Value.Trim()))
            {
                throw new PocDatasetConfigurationException(errorCode, $"'{ownerCode}' 包含无效或重复的选项。");
            }
        }
    }

    /// <summary>校验 PocReportConfigurationService 的 Validate Code 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="errorCode">需要读取、校验或转换的业务值。</param>
    private static void ValidateCode(string code, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                code,
                "^[A-Za-z][A-Za-z0-9_]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new PocDatasetConfigurationException(errorCode, $"编码 '{code}' 无效。");
        }
    }

    /// <summary>将 PocReportConfigurationService 中的 Deserialize 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="json">json 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static T Deserialize<T>(string json) =>
        JsonConvert.DeserializeObject<T>(json)
        ?? throw new InvalidDataException($"Stored configuration JSON '{typeof(T).Name}' is null.");

    /// <summary>将 PocReportConfigurationService 中的 Deserialize List 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="json">json 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IReadOnlyList<T> DeserializeList<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonConvert.DeserializeObject<IReadOnlyList<T>>(json) ?? [];

    /// <summary>负责 PocReportConfigurationService 的 Null If White Space 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>负责 PocReportConfigurationService 的 Control Type For 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ControlTypeFor(string dataType) =>
        dataType.ToLowerInvariant() switch
        {
            "int" or "long" or "decimal" => "number",
            "date" => "date",
            "datetime" => "datetime",
            "bool" or "boolean" => "checkbox",
            _ => "text"
        };

    /// <summary>校验 PocReportConfigurationService 的 Is Date Parameter 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="controlType">controlType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool IsDateParameter(string dataType, string controlType) =>
        dataType.Equals("date", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("datetime", StringComparison.OrdinalIgnoreCase) ||
        controlType.Equals("date", StringComparison.OrdinalIgnoreCase) ||
        controlType.Equals("datetime", StringComparison.OrdinalIgnoreCase) ||
        controlType.Equals("month", StringComparison.OrdinalIgnoreCase) ||
        controlType.Equals("year", StringComparison.OrdinalIgnoreCase);

    /// <summary>创建并返回 PocReportConfigurationService 的 Default Expression Data Type 初始数据，避免不同调用方共享可变对象。</summary>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="controlType">controlType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string DefaultExpressionDataType(string dataType, string controlType) =>
        controlType.Trim().ToLowerInvariant() switch
        {
            "month" => "month",
            "year" => "year",
            "datetime" => "datetime",
            _ => dataType.Equals("datetime", StringComparison.OrdinalIgnoreCase)
                ? "datetime"
                : "date"
        };

    /// <summary>将 PocReportConfigurationService 中的 Normalize Report Code 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string NormalizeReportCode(string reportCode)
    {
        if (string.IsNullOrWhiteSpace(reportCode) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                reportCode,
                "^[A-Za-z][A-Za-z0-9_-]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new PocDatasetConfigurationException(
                "REPORT_DEFINITION_INVALID",
                $"报表编码 '{reportCode}' 无效。");
        }
        return reportCode.Trim();
    }

    private const string FallbackQueryText = """
        WITH numbers AS
        (
            SELECT TOP (137) ROW_NUMBER() OVER (ORDER BY object_id) AS sequence
            FROM sys.all_objects
        )
        SELECT
            CAST(sequence AS int) AS sequence,
            CONCAT(
                COALESCE(NULLIF(@deptId, N''), N'科室'),
                N'-',
                RIGHT(N'00' + CAST(((sequence - 1) % 12) + 1 AS nvarchar(2)), 2)) AS departmentName,
            CAST(80 + sequence * 17 % 121 AS int) AS outpatientCount,
            CAST(20 + sequence * 11 % 61 AS int) AS dischargeCount
        FROM numbers
        """;
}
