using System.Text.RegularExpressions;
using System.Data.Common;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocDatasetPreviewRequest(
    IReadOnlyDictionary<string, string?> Parameters,
    IReadOnlyDictionary<string, string?>? Context = null);

public sealed record PocDatasetPreviewResult(
    IReadOnlyList<PocDatasetFieldDefinition> Fields,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool HasMore,
    int Limit,
    long DurationMilliseconds);

public sealed record PocDatasetSchemaDocument(
    IReadOnlyList<PocDatasetVariableDefinition> Variables,
    IReadOnlyList<PocDatasetFieldDefinition> Fields);

public sealed record PocDatasetReferenceDocument(
    string ReportCode,
    string ReportName,
    string SheetName,
    bool Enabled);

internal sealed record PocLegacyDatasetColumn(
    string Code,
    string Header,
    string DataType,
    int Width,
    string? FormulaTemplate);

/// <summary>全局数据源和数据集的应用服务，公开契约不返回连接字符串或密码。</summary>
public sealed class PocDataAssetService : ITransient
{
    private const int PreviewRowLimit = 50;
    private const int PreviewTimeoutLimitSeconds = 30;
    private static readonly Regex CodeRegex = new(
        "^[A-Za-z][A-Za-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SqlParameterRegex = new(
        "(?<!@)@(?<code>[A-Za-z][A-Za-z0-9_]{0,63})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> VariableDataTypes = new(
        ["string", "int", "long", "decimal", "date", "datetime", "bool"],
        StringComparer.OrdinalIgnoreCase);
    // Fixed 是 JNPF 迁移链路的合法产出（legacy sourceType=2 的固定值参数），PocParameterResolver
    // 已支持其取值语义；数据集变量允许 Form/External/Fixed 三种来源。
    private static readonly HashSet<string> VariableSourceTypes = new(
        ["Form", "External", "Fixed"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FieldDataTypes = new(
        ["string", "number", "date", "datetime", "bool"],
        StringComparer.OrdinalIgnoreCase);

    private readonly PocDataAssetStore _store;
    private readonly PocExternalParameterSourceService? _externalParameters;
    /// <summary>初始化 PocDataAssetService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="store">store 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="externalParameters">externalParameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocDataAssetService(
        PocDataAssetStore store,
        PocExternalParameterSourceService? externalParameters = null)
    {
        _store = store;
        _externalParameters = externalParameters;
    }

    /// <summary>调用 PocDataAssetService 的异步依赖完成 List Data Sources Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocDataSourceDocument>> ListDataSourcesAsync()
    {
        var dataSources = await _store.ListDataSourcesAsync();
        return dataSources.Select(ToDataSourceDocument).ToArray();
    }

    /// <summary>调用 PocDataAssetService 的异步依赖完成 List Datasets Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocDatasetDocument>> ListDatasetsAsync()
    {
        var datasets = await _store.ListDatasetsAsync();
        return datasets.Select(ToDatasetDocument).ToArray();
    }

    /// <summary>按关键字分页读取数据集摘要和字段契约，避免管理页加载全部资源。</summary>
    /// <param name="page">从一开始的页码。</param>
    /// <param name="pageSize">每页返回的最大记录数，服务端限制为 100。</param>
    /// <param name="keyword">可选的数据集名称、编码或数据源搜索关键字。</param>
    /// <returns>返回当前页、总数和总页数，供数据集管理列表翻页。</returns>
    public async Task<PocPagedDocument<PocDatasetDocument>> ListDatasetsPageAsync(
        int page,
        int pageSize,
        string? keyword)
    {
        var normalizedPage = Math.Max(page, 1);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        var storedPage = await _store.ListDatasetsPageAsync(
            normalizedPage,
            normalizedPageSize,
            keyword);
        var items = storedPage.Items.Select(ToDatasetDocument).ToArray();
        return new PocPagedDocument<PocDatasetDocument>(
            items,
            normalizedPage,
            normalizedPageSize,
            storedPage.Total,
            (int)Math.Ceiling(storedPage.Total / (double)normalizedPageSize));
    }

    /// <summary>调用 PocDataAssetService 的异步依赖完成 List Dataset References Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocDatasetReferenceDocument>> ListDatasetReferencesAsync(
        string code)
    {
        ValidateCode(code, "DATASET_REFERENCE_INVALID");
        var normalizedCode = code.Trim();
        _ = await _store.GetDatasetAsync(normalizedCode)
            ?? throw new PocDatasetConfigurationException(
                "DATASET_NOT_FOUND",
                $"不存在数据集 '{normalizedCode}'。");
        var reports = await _store.ListDatasetReferencesAsync(normalizedCode);
        return reports.Select(report => new PocDatasetReferenceDocument(
            report.Code,
            report.Name,
            report.SheetName,
            report.IsEnabled)).ToArray();
    }

    /// <summary>
    /// 删除未被任何报表引用的全局数据集。存在引用时抛出 PocResourceInUseException（DATASET_IN_USE）
    /// 并附引用报表清单，由调用方提示管理员先处理报表，绝不静默级联删除。
    /// </summary>
    /// <param name="code">待删除数据集的编码。</param>
    /// <returns>回传被删除编码的确认文档。</returns>
    public async Task<PocDeletedDocument> DeleteDatasetAsync(string code)
    {
        ValidateCode(code, "DATASET_DEFINITION_INVALID");
        var normalizedCode = code.Trim();
        var dataset = await _store.GetDatasetAsync(normalizedCode)
            ?? throw new PocDatasetConfigurationException(
                "DATASET_NOT_FOUND",
                $"不存在数据集 '{normalizedCode}'。");
        var references = await _store.ListDatasetReferencesAsync(normalizedCode);
        if (references.Count > 0)
        {
            throw new PocResourceInUseException(
                "DATASET_IN_USE",
                $"数据集 '{dataset.Name}'（{normalizedCode}）正被 {references.Count} 个报表引用：" +
                $"{PocDeleteGuard.FormatReferenceSummary(references.Select(item => item.Name).ToArray())}。" +
                "请先删除这些报表，或把报表改绑到其他数据集。");
        }

        await _store.DeleteDatasetAsync(normalizedCode);
        return new PocDeletedDocument(normalizedCode);
    }

    /// <summary>
    /// 删除未被任何数据集引用的全局数据源。存在引用时抛出 PocResourceInUseException（DATA_SOURCE_IN_USE）
    /// 并附引用数据集清单，防止删除后留下连接信息缺失的数据集。
    /// </summary>
    /// <param name="code">待删除数据源的编码。</param>
    /// <returns>回传被删除编码的确认文档。</returns>
    public async Task<PocDeletedDocument> DeleteDataSourceAsync(string code)
    {
        ValidateCode(code, "DATA_SOURCE_INVALID");
        var normalizedCode = code.Trim();
        var dataSource = await _store.GetDataSourceAsync(normalizedCode)
            ?? throw new PocDatasetConfigurationException(
                "DATA_SOURCE_NOT_FOUND",
                $"不存在数据源 '{normalizedCode}'。");
        var referencingDatasets = await _store.ListDatasetsByDataSourceAsync(normalizedCode);
        if (referencingDatasets.Count > 0)
        {
            throw new PocResourceInUseException(
                "DATA_SOURCE_IN_USE",
                $"数据源 '{dataSource.Name}'（{normalizedCode}）正被 {referencingDatasets.Count} 个数据集引用：" +
                $"{PocDeleteGuard.FormatReferenceSummary(referencingDatasets.Select(item => item.Name).ToArray())}。" +
                "请先删除这些数据集，或把它们改绑到其他数据源。");
        }

        await _store.DeleteDataSourceAsync(normalizedCode);
        return new PocDeletedDocument(normalizedCode);
    }

    /// <summary>更新 PocDataAssetService 的 Save Data Source Async 状态，并同步相关业务数据。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocDataSourceDocument> SaveDataSourceAsync(
        string code,
        PocDataSourceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureMatchingCode(code, input.Code, "DATA_SOURCE_INVALID");
        ValidateDataSource(input);
        var existing = await _store.GetDataSourceAsync(input.Code.Trim());
        var connectionString = BuildConnectionString(input, existing);
        await _store.SaveDataSourceAsync(new PocDataSourceMutation(
            input.Code.Trim(),
            input.Name.Trim(),
            PocDatabaseProvider.NormalizeType(input.DbType),
            connectionString,
            input.TimeoutSeconds,
            input.MaxRows,
            input.Enabled,
            input.Revision));
        return ToDataSourceDocument((await _store.GetDataSourceAsync(input.Code.Trim()))!);
    }

    /// <summary>调用 PocDataAssetService 的异步依赖完成 Test Data Source Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocDataSourceTestResult> TestDataSourceAsync(
        PocDataSourceInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDataSource(input);
        var existing = await _store.GetDataSourceAsync(input.Code.Trim());
        var connectionString = BuildConnectionString(input, existing);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var database = PocDatabaseProvider.CreateClient(
                input.DbType,
                connectionString,
                input.TimeoutSeconds,
                cancellationToken);
            await database.Ado.OpenAsync();
            started.Stop();
            return new PocDataSourceTestResult(
                true,
                started.ElapsedMilliseconds,
                ((DbConnection)database.Ado.Connection).ServerVersion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception))
        {
            throw new PocDatasetConfigurationException(
                "DATA_SOURCE_CONNECTION_FAILED",
                $"{PocDatabaseProvider.NormalizeType(input.DbType)} 数据源连接失败：" +
                PocDatabaseProvider.GetSafeConnectionError(exception));
        }
    }

    /// <summary>更新 PocDataAssetService 的 Save Dataset Async 状态，并同步相关业务数据。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocDatasetDocument> SaveDatasetAsync(
        string code,
        PocDatasetDocument input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureMatchingCode(code, input.Code, "DATASET_DEFINITION_INVALID");
        ValidateDataset(input);
        var dataSource = await _store.GetDataSourceAsync(input.DataSourceCode.Trim())
            ?? throw new PocDatasetConfigurationException(
                "DATA_SOURCE_NOT_FOUND",
                $"不存在数据源 '{input.DataSourceCode}'。");
        if (!dataSource.IsEnabled && input.Enabled)
        {
            throw new PocDatasetConfigurationException(
                "DATA_SOURCE_DISABLED",
                "启用数据集前必须先启用其数据源。");
        }
        await ValidateExternalVariableBindingsAsync(input.Variables);

        await _store.SaveDatasetAsync(new PocDatasetMutation(
            input.Code.Trim(),
            input.Name.Trim(),
            input.DataSourceCode.Trim(),
            input.QueryText.Trim(),
            JsonConvert.SerializeObject(
                new PocDatasetSchemaDocument(input.Variables, input.Fields),
                Formatting.None),
            input.TimeoutSeconds,
            input.MaxRows,
            input.Enabled,
            input.Revision));
        return ToDatasetDocument((await _store.GetDatasetAsync(input.Code.Trim()))!);
    }

    /// <summary>调用 PocDataAssetService 的异步依赖完成 Preview Dataset Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocDatasetPreviewResult> PreviewDatasetAsync(
        string code,
        PocDatasetPreviewRequest input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCode(code, "DATASET_PREVIEW_INVALID");
        if (input.Parameters is null)
        {
            throw new PocDatasetConfigurationException(
                "DATASET_PREVIEW_INVALID",
                "数据集预览配置无效。");
        }

        // 第一阶段：调用 PreviewDatasetAsync 依赖的异步能力，等待并校验返回数据。
        var dataset = await _store.GetDatasetAsync(code.Trim())
            ?? throw new PocDatasetConfigurationException(
                "DATASET_NOT_FOUND",
                $"不存在数据集 '{code}'。");
        if (!dataset.IsEnabled)
        {
            throw new PocDatasetConfigurationException(
                "DATASET_DISABLED",
                "预览数据前必须先启用数据集。");
        }
        var schema = ReadSchema(dataset);
        ValidateDatasetSchema(dataset.QueryText, schema.Variables, schema.Fields);
        var parameterDefinitions = schema.Variables.Select(variable => new PocParameterDefinition(
                variable.Code,
                variable.DisplayName,
                "text",
                variable.SourceType,
                ResolveVariableFallback(variable),
                true,
                variable.DataType,
                Required: variable.Required,
                SourceCode: variable.SourceCode,
                SourceFieldCode: variable.SourceFieldCode)).ToArray();
        var externalValues = _externalParameters is null
            ? new Dictionary<string, string?>()
            : await _externalParameters.ResolveAsync(
                parameterDefinitions,
                input.Context,
                cancellationToken);
        var resolved = PocParameterResolver.Resolve(
            parameterDefinitions,
            input.Parameters,
            externalValues);

        // 第2阶段：构造 PreviewDatasetAsync 当前步骤需要的中间数据和执行参数。
        var parameterCodes = ExtractParameterCodes(dataset.QueryText);
        foreach (var parameterCode in parameterCodes)
        {
            var definition = parameterDefinitions.Single(item =>
                item.Code.Equals(parameterCode, StringComparison.OrdinalIgnoreCase));
            // 变量带默认值（表达式现算结果或固定默认值）时允许免填预览，取值回落交给解析器。
            if (!definition.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(definition.DefaultValue) &&
                !input.Parameters.Keys.Any(key =>
                    key.Equals(parameterCode, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_PREVIEW_PARAMETER_REQUIRED",
                    $"请填写预览参数 '@{parameterCode}'。");
            }
        }

        var dataSource = await _store.GetDataSourceAsync(dataset.DataSourceCode)
            ?? throw new PocDatasetConfigurationException(
                "DATA_SOURCE_NOT_FOUND",
                $"不存在数据源 '{dataset.DataSourceCode}'。");
        if (!dataSource.IsEnabled)
        {
            throw new PocDatasetConfigurationException(
                "DATA_SOURCE_DISABLED",
                "预览数据前必须先启用其数据源。");
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var database = PocDatabaseProvider.CreateClient(
                dataSource.DbType,
                dataSource.ConnectionString,
                Math.Min(dataset.TimeoutSeconds, PreviewTimeoutLimitSeconds),
                cancellationToken);
            // 第3阶段：构造 PreviewDatasetAsync 当前步骤需要的中间数据和执行参数。
            var parameters = new List<SugarParameter>(parameterCodes.Count);
            foreach (var parameterCode in parameterCodes)
            {
                var variable = schema.Variables.Single(item =>
                    item.Code.Equals(parameterCode, StringComparison.OrdinalIgnoreCase));
                parameters.Add(PocDatabaseProvider.CreateParameter(
                    parameterCode,
                    variable.DataType,
                    resolved.DatabaseValues[variable.Code]));
            }

            var dataReader = await database.Ado.GetDataReaderAsync(
                dataset.QueryText.Trim(),
                parameters);
            await using var reader = dataReader as DbDataReader
                ?? throw new PocDatasetExecutionException(
                    "DATASET_QUERY_FAILED",
                    "数据库驱动未返回可读取的数据集结果。");
            var fields = ReadPreviewFields(reader);
            var rows = new List<IReadOnlyList<object?>>(PreviewRowLimit + 1);
            while (rows.Count <= PreviewRowLimit && await reader.ReadAsync(cancellationToken))
            {
                var row = new object?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[index] = PocDatasetValueNormalizer.Normalize(
                        PocDatasetValueNormalizer.ReadColumnValue(reader, index),
                        fields[index]);
                }
                rows.Add(row);
            }

            var hasMore = rows.Count > PreviewRowLimit;
            if (hasMore)
            // 第4阶段：根据 PreviewDatasetAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
            {
                rows.RemoveAt(rows.Count - 1);
            }
            started.Stop();
            return new PocDatasetPreviewResult(
                fields,
                rows,
                hasMore,
                PreviewRowLimit,
                started.ElapsedMilliseconds);
        }
        catch (PocDatasetExecutionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception))
        {
            throw new PocDatasetExecutionException(
                PocDatabaseProvider.IsTimeout(exception) ? "DATASET_TIMEOUT" : "DATASET_QUERY_FAILED",
                "数据集预览失败。",
                exception);
        }
    }

    /// <summary>遍历 PocDataAssetService 当前数据并执行 Extract Parameter Codes 转换，生成调用方需要的结果集合。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static IReadOnlyList<string> ExtractParameterCodes(string queryText)
    {
        var sql = queryText ?? string.Empty;
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SqlParameterRegex.Matches(sql))
        {
            var code = match.Groups["code"].Value;
            if (seen.Add(code))
            {
                candidates.Add(code);
            }
        }
        return PocReadOnlySqlValidator.Validate(
            sql,
            candidates,
            PocPreviewMode.Full);
    }

    /// <summary>根据 PocDataAssetService 当前输入计算 To Dataset Document 结果，并按方法声明的类型返回。</summary>
    /// <param name="dataset">dataset 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDatasetDocument ToDatasetDocument(PocReportDataset dataset)
    {
        var schema = ReadSchema(dataset);
        return new PocDatasetDocument(
            dataset.Code,
            dataset.Name,
            dataset.DataSourceCode,
            dataset.QueryText,
            schema.Variables,
            schema.Fields,
            dataset.TimeoutSeconds,
            dataset.MaxRows,
            dataset.IsEnabled,
            dataset.Revision);
    }

    /// <summary>查询或读取 PocDataAssetService 所需的 Read Schema 数据，并按调用约定返回结果。</summary>
    /// <param name="dataset">dataset 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static PocDatasetSchemaDocument ReadSchema(PocReportDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var token = JToken.Parse(dataset.FieldSchemaJson);
        if (token.Type == JTokenType.Array)
        {
            var legacyColumns = token.ToObject<PocLegacyDatasetColumn[]>() ?? [];
            return new PocDatasetSchemaDocument(
                ExtractParameterCodes(dataset.QueryText)
                    .Select(code => new PocDatasetVariableDefinition(code, code, "string", false))
                    .ToArray(),
                legacyColumns
                    .Where(column => !column.DataType.Equals("formula", StringComparison.OrdinalIgnoreCase))
                    .Select(column => new PocDatasetFieldDefinition(
                        column.Code,
                        column.Header,
                        NormalizeFieldDataType(column.DataType)))
                    .ToArray());
        }

        var schema = token.ToObject<PocDatasetSchemaDocument>()
            ?? throw new InvalidDataException("Stored dataset schema is null.");
        return new PocDatasetSchemaDocument(
            (schema.Variables ?? []).Select(variable =>
            {
                var sourceType = string.IsNullOrWhiteSpace(variable.SourceType)
                    ? "Form"
                    : variable.SourceType;
                return variable with
                {
                    SourceType = sourceType,
                    SourceCode = sourceType.Equals("External", StringComparison.OrdinalIgnoreCase)
                        ? variable.SourceCode
                        : null,
                    SourceFieldCode = sourceType.Equals("External", StringComparison.OrdinalIgnoreCase)
                        ? variable.SourceFieldCode
                        : null
                };
            }).ToArray(),
            schema.Fields ?? []);
    }

    /// <summary>根据 PocDataAssetService 当前输入计算 To Data Source Document 结果，并按方法声明的类型返回。</summary>
    /// <param name="dataSource">本次转换或写入使用的数据对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDataSourceDocument ToDataSourceDocument(PocDataSource dataSource)
    {
        var endpoint = PocDatabaseProvider.ParseEndpoint(
            dataSource.DbType,
            dataSource.ConnectionString);
        return new PocDataSourceDocument(
            dataSource.Code,
            dataSource.Name,
            PocDatabaseProvider.NormalizeType(dataSource.DbType),
            endpoint.Server,
            endpoint.Port,
            endpoint.Database,
            endpoint.UserName,
            !string.IsNullOrEmpty(endpoint.Password),
            dataSource.DefaultTimeoutSeconds,
            dataSource.DefaultMaxRows,
            dataSource.IsEnabled,
            dataSource.Revision);
    }

    /// <summary>构建 PocDataAssetService 所需的 Build Connection String 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="stored">stored 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string BuildConnectionString(PocDataSourceInput input, PocDataSource? stored)
    {
        var normalizedType = PocDatabaseProvider.NormalizeType(input.DbType);
        var existing = stored is not null &&
            normalizedType.Equals(stored.DbType, StringComparison.OrdinalIgnoreCase)
                ? PocDatabaseProvider.ParseEndpoint(stored.DbType, stored.ConnectionString)
                : null;
        var password = string.IsNullOrEmpty(input.Password) ? null : input.Password;
        if (password is null)
        {
            var canReusePassword = existing is not null &&
                existing.Server.Equals(input.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
                existing.Port == input.Port &&
                existing.Database.Equals(input.Database.Trim(), StringComparison.OrdinalIgnoreCase) &&
                existing.UserName.Equals(input.UserName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(existing.Password);
            if (!canReusePassword)
            {
                throw new PocDatasetConfigurationException(
                    "DATA_SOURCE_PASSWORD_REQUIRED",
                    "新数据源或连接信息变化时必须提供密码。");
            }
            password = existing!.Password;
        }

        return PocDatabaseProvider.BuildConnectionString(
            normalizedType,
            input.Server.Trim(),
            input.Port,
            input.Database.Trim(),
            input.UserName.Trim(),
            password,
            input.TimeoutSeconds);
    }

    /// <summary>校验 PocDataAssetService 的 Validate Data Source 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    private static void ValidateDataSource(PocDataSourceInput input)
    {
        ValidateCode(input.Code, "DATA_SOURCE_INVALID");
        _ = PocDatabaseProvider.NormalizeType(input.DbType);
        if (string.IsNullOrWhiteSpace(input.Name) ||
            input.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(input.Server) ||
            input.Server.Length > 256 ||
            string.IsNullOrWhiteSpace(input.Database) ||
            input.Database.Length > 128 ||
            string.IsNullOrWhiteSpace(input.UserName) ||
            input.UserName.Length > 128 ||
            (input.Password?.Length ?? 0) > 1_024 ||
            input.Port is < 1 or > 65_535 ||
            input.TimeoutSeconds is < 1 or > 600 ||
            input.MaxRows is < 1 or > 1_000_000 ||
            input.Revision < 0)
        {
            throw new PocDatasetConfigurationException(
                "DATA_SOURCE_INVALID",
                "数据源配置无效。");
        }
    }

    /// <summary>
    /// 校验数据集编码、名称、SQL 长度、变量/字段数量、超时与行数上限，并联动校验
    /// SQL 变量与字段契约。报表包校验（PocReportPackageService）复用同一方法，
    /// 确保包内数据集与线上保存链路使用同一份规则。
    /// </summary>
    /// <param name="input">待校验的数据集文档。</param>
    internal static void ValidateDataset(PocDatasetDocument input)
    {
        ValidateCode(input.Code, "DATASET_DEFINITION_INVALID");
        ValidateCode(input.DataSourceCode, "DATASET_DEFINITION_INVALID");
        if (string.IsNullOrWhiteSpace(input.Name) ||
            input.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(input.QueryText) ||
            input.QueryText.Length > 200_000 ||
            input.Variables is null ||
            input.Fields is null ||
            input.Variables.Count > 100 ||
            input.Fields.Count > 256 ||
            input.TimeoutSeconds is < 1 or > 600 ||
            input.MaxRows is < 1 or > 1_000_000 ||
            input.Revision < 0)
        {
            throw new PocDatasetConfigurationException(
                "DATASET_DEFINITION_INVALID",
                "数据集配置无效。");
        }

        ValidateDatasetSchema(input.QueryText, input.Variables, input.Fields);
    }

    /// <summary>校验 PocDataAssetService 的 Validate Dataset Schema 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <param name="variables">variables 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="fields">参与映射、显示或数据写入的字段集合。</param>
    private static void ValidateDatasetSchema(
        string queryText,
        IReadOnlyList<PocDatasetVariableDefinition> variables,
        IReadOnlyList<PocDatasetFieldDefinition> fields)
    {
        var referencedCodes = ExtractParameterCodes(queryText);
        var variableCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            ValidateCode(variable.Code, "DATASET_VARIABLE_INVALID");
            // 第一阶段：检查 ValidateDatasetSchema 当前条件，并按匹配分支执行对应处理。
            if (!variableCodes.Add(variable.Code) ||
                string.IsNullOrWhiteSpace(variable.DisplayName) ||
                variable.DisplayName.Length > 128 ||
                !VariableDataTypes.Contains(variable.DataType) ||
                !VariableSourceTypes.Contains(variable.SourceType) ||
                (variable.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase) &&
                 (string.IsNullOrWhiteSpace(variable.SourceCode) ||
                  string.IsNullOrWhiteSpace(variable.SourceFieldCode))) ||
                (!variable.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase) &&
                 (!string.IsNullOrWhiteSpace(variable.SourceCode) ||
                  !string.IsNullOrWhiteSpace(variable.SourceFieldCode))) ||
                variable.DefaultValue is { Length: > 1024 } ||
                variable.DefaultExpression is { Length: > 256 })
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_VARIABLE_INVALID",
                    $"变量 '@{variable.Code}' 的定义无效。");
            }

            ValidateVariableDefault(variable);
        }
        if (referencedCodes.Count != variableCodes.Count ||
            referencedCodes.Any(code => !variableCodes.Contains(code)))
        {
            throw new PocDatasetConfigurationException(
                "DATASET_VARIABLE_MISMATCH",
                "SQL 中的变量必须与数据集变量定义完全一致。");
        }

        // 第2阶段：构造 ValidateDatasetSchema 当前步骤需要的中间数据和执行参数。
        var fieldCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            ValidateCode(field.Code, "DATASET_FIELD_INVALID");
            if (!fieldCodes.Add(field.Code))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_FIELD_INVALID",
                    $"返回字段编码 '{field.Code}' 重复。");
            }
            if (string.IsNullOrWhiteSpace(field.DisplayName) ||
                field.DisplayName.Length > 128 ||
                !FieldDataTypes.Contains(field.DataType))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_FIELD_INVALID",
                    $"返回字段 '{field.Code}' 的定义无效。");
            }
        }
    }

    /// <summary>
    /// 校验数据集变量的默认表达式声明：仅日期类变量（date/datetime）允许配置，
    /// 且保存即按当前时间试算一遍——坏表达式在落库前就被拒绝，而不是等到预览或同步运行时才失败。
    /// 非法表达式沿用求值器的 DEFAULT_EXPRESSION_INVALID 错误码与消息（含表达式原文），便于定位。
    /// </summary>
    /// <param name="variable">待校验的变量定义。</param>
    private static void ValidateVariableDefault(PocDatasetVariableDefinition variable)
    {
        if (string.IsNullOrWhiteSpace(variable.DefaultExpression))
        {
            return;
        }

        if (!variable.DataType.Equals("date", StringComparison.OrdinalIgnoreCase) &&
            !variable.DataType.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            throw new PocDatasetConfigurationException(
                "DATASET_VARIABLE_INVALID",
                $"变量 '@{variable.Code}' 仅日期类变量可配置默认表达式。");
        }

        _ = PocDefaultExpressionEvaluator.Evaluate(
            variable.DefaultExpression,
            variable.DataType,
            DateTimeOffset.Now);
    }

    /// <summary>
    /// 把数据集变量收敛为运行时兜底值：默认表达式按当前时间现算的结果优先（随时间滚动），
    /// 其次是固定默认值；两者都为空时返回 null，交由参数解析器的必填校验兜底。
    /// 表达式求值失败直接抛出（保存时已试算，运行期失败通常意味着表达式与数据类型组合异常）。
    /// 静态公开供数据同步构造执行计划时复用，保证预览、正式跑与报表参数同一套兜底语义。
    /// </summary>
    /// <param name="variable">数据集变量定义。</param>
    /// <returns>可作参数兜底值的文本；无兜底时为 null。</returns>
    public static string? ResolveVariableFallback(PocDatasetVariableDefinition variable)
    {
        var evaluated = PocDefaultExpressionEvaluator.Evaluate(
            variable.DefaultExpression,
            variable.DataType,
            DateTimeOffset.Now);
        if (!string.IsNullOrWhiteSpace(evaluated))
        {
            return evaluated;
        }

        return string.IsNullOrWhiteSpace(variable.DefaultValue) ? null : variable.DefaultValue;
    }

    /// <summary>校验 PocDataAssetService 的 Validate External Variable Bindings Async 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="variables">variables 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task ValidateExternalVariableBindingsAsync(
        IReadOnlyList<PocDatasetVariableDefinition> variables)
    {
        var externalVariables = variables.Where(variable =>
            variable.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (externalVariables.Length == 0)
        {
            return;
        }
        if (_externalParameters is null)
        {
            throw new PocDatasetConfigurationException(
                "EXTERNAL_PARAMETER_SERVICE_UNAVAILABLE",
                "外部参数服务不可用。");
        }

        var sources = await _externalParameters.ListAsync();
        foreach (var variable in externalVariables)
        {
            var source = sources.FirstOrDefault(item =>
                    item.Code.Equals(variable.SourceCode, StringComparison.OrdinalIgnoreCase))
                ?? throw new PocDatasetConfigurationException(
                    "EXTERNAL_PARAMETER_SOURCE_NOT_FOUND",
                    $"变量 '@{variable.Code}' 引用的外部参数源不存在。");
            var field = source.Fields.FirstOrDefault(item =>
                    item.Code.Equals(variable.SourceFieldCode, StringComparison.OrdinalIgnoreCase))
                ?? throw new PocDatasetConfigurationException(
                    "EXTERNAL_PARAMETER_FIELD_NOT_FOUND",
                    $"变量 '@{variable.Code}' 引用的外部字段不存在。");
            if (!source.Enabled ||
                !field.DataType.Equals(variable.DataType, StringComparison.OrdinalIgnoreCase))
            {
                throw new PocDatasetConfigurationException(
                    "EXTERNAL_PARAMETER_BINDING_INVALID",
                    $"变量 '@{variable.Code}' 的外部字段绑定无效。");
            }
        }
    }

    /// <summary>查询或读取 PocDataAssetService 所需的 Read Preview Fields 数据，并按调用约定返回结果。</summary>
    /// <param name="reader">reader 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IReadOnlyList<PocDatasetFieldDefinition> ReadPreviewFields(DbDataReader reader)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<PocDatasetFieldDefinition>(reader.FieldCount);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var code = reader.GetName(index);
            ValidateCode(code, "DATASET_FIELD_INVALID");
            if (!codes.Add(code))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_FIELD_INVALID",
                    $"查询返回了重复字段 '{code}'。");
            }
            fields.Add(new PocDatasetFieldDefinition(
                code,
                code,
                ToFieldDataType(reader.GetFieldType(index))));
        }
        return fields;
    }

    /// <summary>校验 PocDataAssetService 的 Ensure Matching Code 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="routeCode">需要读取、校验或转换的业务值。</param>
    /// <param name="bodyCode">需要读取、校验或转换的业务值。</param>
    /// <param name="errorCode">需要读取、校验或转换的业务值。</param>
    private static void EnsureMatchingCode(string routeCode, string bodyCode, string errorCode)
    {
        if (!routeCode.Equals(bodyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new PocDatasetConfigurationException(errorCode, "路径编码与请求内容不一致。");
        }
    }

    /// <summary>校验 PocDataAssetService 的 Validate Code 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="errorCode">需要读取、校验或转换的业务值。</param>
    private static void ValidateCode(string code, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(code) || !CodeRegex.IsMatch(code))
        {
            throw new PocDatasetConfigurationException(errorCode, $"编码 '{code}' 无效。");
        }
    }

    /// <summary>负责 PocDataAssetService 的 Null If White Space 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>将 PocDataAssetService 的 To Field Data Type 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="type">type 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ToFieldDataType(Type type) =>
        type == typeof(bool) ? "bool" :
        type == typeof(DateTimeOffset) ? "datetime" :
        type == typeof(DateTime) ? "datetime" :
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal)
            ? "number"
            : "string";

    /// <summary>将 PocDataAssetService 中的 Normalize Field Data Type 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="dataType">dataType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string NormalizeFieldDataType(string dataType) =>
        dataType.Trim().ToLowerInvariant() switch
        {
            "bool" or "boolean" => "bool",
            "date" => "date",
            "datetime" => "datetime",
            "number" or "int" or "long" or "decimal" => "number",
            _ => "string"
        };

}
