using System.Text.RegularExpressions;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Data;
using System.Globalization;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocDatasetPreviewRequest(
    IReadOnlyDictionary<string, string?> Parameters);

public sealed record PocDatasetPreviewResult(
    IReadOnlyList<PocDatasetFieldDefinition> Fields,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool HasMore,
    int Limit,
    long DurationMilliseconds);

public sealed record PocDatasetSchemaDocument(
    IReadOnlyList<PocDatasetVariableDefinition> Variables,
    IReadOnlyList<PocDatasetFieldDefinition> Fields);

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
    private static readonly HashSet<string> FieldDataTypes = new(
        ["string", "number", "date", "datetime", "bool"],
        StringComparer.OrdinalIgnoreCase);

    private readonly PocDataAssetStore _store;
    private readonly ISqlSugarClient _database;

    public PocDataAssetService(PocDataAssetStore store, ISqlSugarClient database)
    {
        _store = store;
        _database = database;
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
            "SqlServer",
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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        started.Stop();
        return new PocDataSourceTestResult(
            true,
            started.ElapsedMilliseconds,
            connection.ServerVersion);
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
        var resolved = PocParameterResolver.Resolve(
            schema.Variables.Select(variable => new PocParameterDefinition(
                variable.Code,
                variable.DisplayName,
                "text",
                "Form",
                null,
                true,
                variable.DataType,
                Required: variable.Required)).ToArray(),
            input.Parameters);

        var parameterCodes = ExtractParameterCodes(dataset.QueryText);
        foreach (var parameterCode in parameterCodes)
        {
            if (!input.Parameters.Keys.Any(key =>
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
            await using var connection = new SqlConnection(dataSource.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = dataset.QueryText.Trim();
            command.CommandTimeout = Math.Min(dataset.TimeoutSeconds, PreviewTimeoutLimitSeconds);
            foreach (var parameterCode in parameterCodes)
            {
                var variable = schema.Variables.Single(item =>
                    item.Code.Equals(parameterCode, StringComparison.OrdinalIgnoreCase));
                command.Parameters.Add(PocSqlDatasetExecutor.CreateParameter(
                    parameterCode,
                    variable.DataType,
                    resolved.DatabaseValues[variable.Code]));
            }

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken);
            var fields = ReadPreviewFields(reader);
            var rows = new List<IReadOnlyList<object?>>(PreviewRowLimit + 1);
            while (rows.Count <= PreviewRowLimit && await reader.ReadAsync(cancellationToken))
            {
                var row = new object?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[index] = NormalizePreviewValue(reader.GetValue(index));
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
        catch (SqlException exception)
        {
            throw new PocDatasetExecutionException(
                exception.Number == -2 ? "DATASET_TIMEOUT" : "DATASET_QUERY_FAILED",
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
            schema.Variables ?? [],
            schema.Fields ?? []);
    }

    private static PocDataSourceDocument ToDataSourceDocument(PocDataSource dataSource)
    {
        var builder = ParseConnectionString(dataSource.ConnectionString);
        var (server, port) = ParseDataSource(builder.DataSource);
        return new PocDataSourceDocument(
            dataSource.Code,
            dataSource.Name,
            "SqlServer",
            server,
            port,
            builder.InitialCatalog,
            builder.UserID,
            !string.IsNullOrWhiteSpace(builder.Password),
            dataSource.DefaultTimeoutSeconds,
            dataSource.DefaultMaxRows,
            dataSource.IsEnabled,
            dataSource.Revision);
    }

    private string BuildConnectionString(PocDataSourceInput input, PocDataSource? stored)
    {
        var baseConnectionString = stored is not null
            ? stored.ConnectionString
            : _database.CurrentConnectionConfig.ConnectionString;
        var existing = ParseConnectionString(baseConnectionString);
        var password = NullIfWhiteSpace(input.Password);
        if (password is null)
        {
            var existingEndpoint = ParseDataSource(existing.DataSource);
            var canReusePassword = stored is not null &&
                existingEndpoint.Server.Equals(input.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
                existingEndpoint.Port == input.Port &&
                existing.InitialCatalog.Equals(input.Database.Trim(), StringComparison.OrdinalIgnoreCase) &&
                existing.UserID.Equals(input.UserName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(existing.Password);
            if (!canReusePassword)
            {
                throw new PocDatasetConfigurationException(
                    "DATA_SOURCE_PASSWORD_REQUIRED",
                    "新数据源或连接信息变化时必须提供密码。");
            }
            password = existing.Password;
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = $"{input.Server.Trim()},{input.Port}",
            InitialCatalog = input.Database.Trim(),
            UserID = input.UserName.Trim(),
            Password = password,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = Math.Clamp(input.TimeoutSeconds, 1, 120),
            ApplicationName = "hd-report-center-runtime"
        }.ConnectionString;
    }

    private static void ValidateDataSource(PocDataSourceInput input)
    {
        ValidateCode(input.Code, "DATA_SOURCE_INVALID");
        if (!input.DbType.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(input.Name) ||
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
                "SQL Server 数据源配置无效。");
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
                !VariableDataTypes.Contains(variable.DataType))
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

    private static IReadOnlyList<PocDatasetFieldDefinition> ReadPreviewFields(SqlDataReader reader)
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

    private static SqlConnectionStringBuilder ParseConnectionString(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new PocDatasetConfigurationException("DATA_SOURCE_INVALID", exception.Message);
        }
    }

    private static (string Server, int Port) ParseDataSource(string dataSource)
    {
        var parts = dataSource.Split(',', 2, StringSplitOptions.TrimEntries);
        return (
            parts[0],
            parts.Length == 2 && int.TryParse(parts[1], out var port) ? port : 1433);
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

    private static object? NormalizePreviewValue(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid guid => guid.ToString("D"),
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        DateTime dateTime => dateTime,
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}
