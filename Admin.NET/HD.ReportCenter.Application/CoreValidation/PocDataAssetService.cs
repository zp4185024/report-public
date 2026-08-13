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
    private static readonly HashSet<string> VariableSourceTypes = new(
        ["Form", "External"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FieldDataTypes = new(
        ["string", "number", "date", "datetime", "bool"],
        StringComparer.OrdinalIgnoreCase);

    private readonly PocDataAssetStore _store;
    private readonly PocExternalParameterSourceService? _externalParameters;
    public PocDataAssetService(
        PocDataAssetStore store,
        PocExternalParameterSourceService? externalParameters = null)
    {
        _store = store;
        _externalParameters = externalParameters;
    }

    public async Task<IReadOnlyList<PocDataSourceDocument>> ListDataSourcesAsync()
    {
        var dataSources = await _store.ListDataSourcesAsync();
        return dataSources.Select(ToDataSourceDocument).ToArray();
    }

    public async Task<IReadOnlyList<PocDatasetDocument>> ListDatasetsAsync()
    {
        var datasets = await _store.ListDatasetsAsync();
        return datasets.Select(ToDatasetDocument).ToArray();
    }

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
                null,
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

        var parameterCodes = ExtractParameterCodes(dataset.QueryText);
        foreach (var parameterCode in parameterCodes)
        {
            var definition = parameterDefinitions.Single(item =>
                item.Code.Equals(parameterCode, StringComparison.OrdinalIgnoreCase));
            if (!definition.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase) &&
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
                    row[index] = PocDatasetValueNormalizer.Normalize(reader.GetValue(index), fields[index]);
                }
                rows.Add(row);
            }

            var hasMore = rows.Count > PreviewRowLimit;
            if (hasMore)
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

    private static void ValidateDataset(PocDatasetDocument input)
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
                  !string.IsNullOrWhiteSpace(variable.SourceFieldCode))))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_VARIABLE_INVALID",
                    $"变量 '@{variable.Code}' 的定义无效。");
            }
        }
        if (referencedCodes.Count != variableCodes.Count ||
            referencedCodes.Any(code => !variableCodes.Contains(code)))
        {
            throw new PocDatasetConfigurationException(
                "DATASET_VARIABLE_MISMATCH",
                "SQL 中的变量必须与数据集变量定义完全一致。");
        }

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

    private static void EnsureMatchingCode(string routeCode, string bodyCode, string errorCode)
    {
        if (!routeCode.Equals(bodyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new PocDatasetConfigurationException(errorCode, "路径编码与请求内容不一致。");
        }
    }

    private static void ValidateCode(string code, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(code) || !CodeRegex.IsMatch(code))
        {
            throw new PocDatasetConfigurationException(errorCode, $"编码 '{code}' 无效。");
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
