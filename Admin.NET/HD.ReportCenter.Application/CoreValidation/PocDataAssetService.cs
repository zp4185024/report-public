using System.Text.RegularExpressions;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>全局数据源和数据集的应用服务，公开契约不返回连接字符串或密码。</summary>
public sealed class PocDataAssetService : ITransient
{
    private static readonly Regex CodeRegex = new(
        "^[A-Za-z][A-Za-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SqlParameterRegex = new(
        "(?<!@)@(?<code>[A-Za-z][A-Za-z0-9_]{0,63})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ColumnDataTypes = new(
        ["string", "text", "number", "int", "long", "decimal", "date", "datetime", "bool", "boolean", "formula"],
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

        _ = ExtractParameterCodes(input.QueryText);
        await _store.SaveDatasetAsync(new PocDatasetMutation(
            input.Code.Trim(),
            input.Name.Trim(),
            input.DataSourceCode.Trim(),
            input.QueryText.Trim(),
            JsonConvert.SerializeObject(input.Columns, Formatting.None),
            input.TimeoutSeconds,
            input.MaxRows,
            input.Enabled,
            input.Revision));
        return ToDatasetDocument((await _store.GetDatasetAsync(input.Code.Trim()))!);
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

    private static PocDatasetDocument ToDatasetDocument(PocReportDataset dataset) =>
        new(
            dataset.Code,
            dataset.Name,
            dataset.DataSourceCode,
            dataset.QueryText,
            ExtractParameterCodes(dataset.QueryText),
            DeserializeColumns(dataset.FieldSchemaJson),
            dataset.TimeoutSeconds,
            dataset.MaxRows,
            dataset.IsEnabled,
            dataset.Revision);

    private static IReadOnlyList<PocColumnDefinition> DeserializeColumns(string json) =>
        JsonConvert.DeserializeObject<PocColumnDefinition[]>(json)
        ?? throw new InvalidDataException("Stored dataset field schema is null.");

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
            input.Columns.Count is 0 or > 256 ||
            input.TimeoutSeconds is < 1 or > 600 ||
            input.MaxRows is < 1 or > 1_000_000 ||
            input.Revision < 0)
        {
            throw new PocDatasetConfigurationException(
                "DATASET_DEFINITION_INVALID",
                "数据集配置无效。");
        }

        var columnCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in input.Columns)
        {
            ValidateCode(column.Code, "DATASET_FIELD_INVALID");
            if (!columnCodes.Add(column.Code))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_FIELD_INVALID",
                    $"字段编码 '{column.Code}' 重复。");
            }
            var isFormula = column.DataType.Equals("formula", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(column.Header) ||
                column.Header.Length > 128 ||
                !ColumnDataTypes.Contains(column.DataType) ||
                column.Width is < 40 or > 500 ||
                (isFormula &&
                 (string.IsNullOrWhiteSpace(column.FormulaTemplate) ||
                  column.FormulaTemplate.Length > 1_024 ||
                  !column.FormulaTemplate.TrimStart().StartsWith('='))) ||
                (!isFormula && !string.IsNullOrWhiteSpace(column.FormulaTemplate)))
            {
                throw new PocDatasetConfigurationException(
                    "DATASET_FIELD_INVALID",
                    $"字段 '{column.Code}' 的定义无效。");
            }
        }
        if (input.Columns.All(column =>
                column.DataType.Equals("formula", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PocDatasetConfigurationException(
                "DATASET_FIELD_INVALID",
                "至少需要一个来自查询结果的普通字段。");
        }
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
}
