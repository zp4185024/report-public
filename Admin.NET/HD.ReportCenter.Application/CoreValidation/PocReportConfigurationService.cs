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
    PocShareProtectionSettings? ShareProtection = null);

public sealed record PocReportSummaryDocument(
    string ReportCode,
    string Name,
    string SheetName,
    string DatasetCode,
    bool Enabled,
    int ConfigurationRevision,
    int TemplateRevision,
    DateTime? UpdatedAt);

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
        ["Form", "System", "Fixed"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ParameterControlTypes = new(
        ["text", "number", "date", "checkbox", "select", "multiSelect", "hidden"],
        StringComparer.OrdinalIgnoreCase);
    private readonly PocReportConfigurationStore _store;
    private readonly PocDataAssetStore _dataAssets;
    private readonly ISqlSugarClient _database;
    private readonly PocTemplateStore? _templates;

    public PocReportConfigurationService(
        PocReportConfigurationStore store,
        PocDataAssetStore dataAssets,
        ISqlSugarClient database,
        PocTemplateStore? templates = null)
    {
        _store = store;
        _dataAssets = dataAssets;
        _database = database;
        _templates = templates;
    }

    public async Task<IReadOnlyList<PocReportSummaryDocument>> ListReportsAsync()
    {
        var reports = await _store.ListReportsAsync();
        IReadOnlyList<PocStoredTemplateSummary> templates = _templates is null
            ? []
            : await _templates.ListAsync();
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

    public async Task<PocReportCreateOptionsDocument> GetCreateOptionsAsync()
    {
        var datasets = await _dataAssets.ListDatasetsAsync();
        return new PocReportCreateOptionsDocument(
            datasets
                .Where(item => item.IsEnabled)
                .Select(ToDatasetOption)
                .ToArray());
    }

    public async Task<PocReportConfigurationDocument> CreateAsync(
        PocCreateReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reportCode = NormalizeReportCode(request.ReportCode).ToLowerInvariant();
        var datasetCode = request.DatasetCode?.Trim() ?? string.Empty;
        ValidateCode(datasetCode, "DATASET_DEFINITION_INVALID");
        if (await _store.ReportExistsAsync(reportCode))
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
            "Form",
            null,
            true,
            variable.DataType,
            Required: variable.Required,
            AllowUrlPrefill: true,
            UrlKey: variable.Code)).ToArray();

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

    public async Task<PocReportConfigurationDocument> GetDocumentAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        var normalizedCode = NormalizeReportCode(reportCode);
        var stored = await _store.GetAsync(normalizedCode);
        var datasets = await _dataAssets.ListDatasetsAsync();
        if (stored is not null)
        {
            return CreateDocument(stored, datasets, now);
        }
        if (normalizedCode.Equals(PocReportCatalog.ReportCode, StringComparison.OrdinalIgnoreCase))
        {
            return CreateFallbackDocument(now, datasets);
        }
        throw new KeyNotFoundException($"不存在报表 '{normalizedCode}'。");
    }

    public async Task<PocReportDefinition> GetDefinitionAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        var document = await GetDocumentAsync(reportCode, now);
        return ToDefinition(document);
    }

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

    public async Task<PocRuntimeReportConfiguration> GetRuntimeAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        var normalizedCode = NormalizeReportCode(reportCode);
        var stored = await _store.GetAsync(normalizedCode);
        if (stored is null)
        {
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
        var document = CreateDocument(stored, [stored.Dataset], now);
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
        ValidateFieldPresentations(request.FieldPresentations ?? [], datasetSchema.Fields);
        _ = PocReadOnlySqlValidator.Validate(
            dataset.QueryText,
            datasetSchema.Variables.Select(item => item.Code),
            request.Preview.Mode);

        var parameterEntities = request.Parameters.Select((parameter, index) =>
            new PocReportParameter
            {
                Code = parameter.Code.Trim(),
                DisplayName = parameter.DisplayName.Trim(),
                DataType = parameter.DataType.Trim(),
                ControlType = parameter.ControlType.Trim(),
                SourceType = parameter.SourceType.Trim(),
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

    private PocReportConfigurationDocument CreateFallbackDocument(
        DateTimeOffset? now,
        IReadOnlyList<PocReportDataset> storedDatasets)
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
            definition.ShareProtection ?? new PocShareProtectionSettings(false, false));
    }

    private static PocReportConfigurationDocument CreateDocument(
        PocStoredReportConfiguration stored,
        IReadOnlyList<PocReportDataset> datasets,
        DateTimeOffset? now)
    {
        var preview = Deserialize<PocPreviewSettings>(stored.Report.PreviewSettingsJson);
        var storedProtection = PocShareProtectionPassword.Read(stored.Report.PreviewSettingsJson);
        var export = Deserialize<PocExportSettings>(stored.Report.ExportSettingsJson);
        var datasetSchema = PocDataAssetService.ReadSchema(stored.Dataset);
        var parameters = stored.Parameters.Select(parameter =>
        {
            var defaultValue = PocDefaultExpressionEvaluator.Evaluate(
                parameter.DefaultExpression,
                parameter.DataType,
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
                DeserializeList<PocValueOption>(parameter.OptionsJson));
        }).ToArray();
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
                storedProtection is not null));
    }

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
            document.ShareProtection ?? new PocShareProtectionSettings(false, false));

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
            if (!parameter.DataType.Equals(variable.DataType, StringComparison.OrdinalIgnoreCase) ||
                (variable.Required && !parameter.Required))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_PARAMETER_TYPE_MISMATCH",
                    $"报表参数 '{parameter.Code}' 与数据集变量定义不一致。");
            }
        }
    }

    private static void ValidateRequest(PocSaveReportConfigurationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            request.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(request.SheetName) ||
            request.SheetName.Length > 31 ||
            request.SheetName.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0)
        {
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
                (!string.IsNullOrWhiteSpace(parameter.DefaultExpression) &&
                 !parameter.DataType.Equals("date", StringComparison.OrdinalIgnoreCase) &&
                 !parameter.DataType.Equals("datetime", StringComparison.OrdinalIgnoreCase)))
            {
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
            _ = PocDefaultExpressionEvaluator.Evaluate(parameter.DefaultExpression, parameter.DataType);
        }

    }

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

    private static T Deserialize<T>(string json) =>
        JsonConvert.DeserializeObject<T>(json)
        ?? throw new InvalidDataException($"Stored configuration JSON '{typeof(T).Name}' is null.");

    private static IReadOnlyList<T> DeserializeList<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonConvert.DeserializeObject<IReadOnlyList<T>>(json) ?? [];

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ControlTypeFor(string dataType) =>
        dataType.ToLowerInvariant() switch
        {
            "int" or "long" or "decimal" => "number",
            "date" or "datetime" => "date",
            "bool" or "boolean" => "checkbox",
            _ => "text"
        };

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
