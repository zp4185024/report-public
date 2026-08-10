using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.SqlClient;
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
    bool Enabled);

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
    bool Enabled);

public sealed record PocDatasetDocument(
    string Code,
    string Name,
    string DataSourceCode,
    string QueryText,
    int TimeoutSeconds,
    int MaxRows,
    bool Enabled);

public sealed record PocReportConfigurationDocument(
    string ReportCode,
    string Name,
    string SheetName,
    PocPreviewSettings Preview,
    PocExportSettings Export,
    PocDataSourceDocument DataSource,
    PocDatasetDocument Dataset,
    IReadOnlyList<PocParameterDefinition> Parameters,
    IReadOnlyList<PocColumnDefinition> Columns,
    int Revision);

public sealed record PocSaveReportConfigurationRequest(
    string Name,
    string SheetName,
    PocPreviewSettings Preview,
    PocExportSettings Export,
    PocDataSourceInput DataSource,
    PocDatasetDocument Dataset,
    IReadOnlyList<PocParameterDefinition> Parameters,
    IReadOnlyList<PocColumnDefinition> Columns,
    int Revision);

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
        ["text", "number", "date", "checkbox", "hidden"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ColumnDataTypes = new(
        ["string", "text", "number", "int", "long", "decimal", "date", "datetime", "bool", "boolean", "formula"],
        StringComparer.OrdinalIgnoreCase);

    private readonly PocReportConfigurationStore _store;
    private readonly ISqlSugarClient _database;

    public PocReportConfigurationService(
        PocReportConfigurationStore store,
        ISqlSugarClient database)
    {
        _store = store;
        _database = database;
    }

    public async Task<PocReportConfigurationDocument> GetDocumentAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        EnsureReportCode(reportCode);
        var stored = await _store.GetAsync(PocReportCatalog.ReportCode);
        return stored is null
            ? CreateFallbackDocument(now)
            : CreateDocument(stored, now);
    }

    public async Task<PocReportDefinition> GetDefinitionAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        var document = await GetDocumentAsync(reportCode, now);
        return ToDefinition(document);
    }

    public async Task<PocRuntimeReportConfiguration> GetRuntimeAsync(
        string reportCode,
        DateTimeOffset? now = null)
    {
        EnsureReportCode(reportCode);
        var stored = await _store.GetAsync(PocReportCatalog.ReportCode);
        if (stored is null)
        {
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

        var document = CreateDocument(stored, now);
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
        EnsureReportCode(reportCode);
        ValidateRequest(request);

        var stored = await _store.GetAsync(PocReportCatalog.ReportCode);
        var connectionString = BuildConnectionString(request.DataSource, stored?.DataSource);
        _ = PocReadOnlySqlValidator.Validate(
            request.Dataset.QueryText,
            request.Parameters.Select(item => item.Code),
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
                IsVisible = parameter.Visible,
                IsReadOnly = parameter.ReadOnly,
                IsRequired = parameter.Required,
                AllowUrlPrefill = parameter.AllowUrlPrefill,
                DisplayOrder = index
            }).ToArray();

        await _store.SaveAsync(new PocReportConfigurationMutation(
            PocReportCatalog.ReportCode,
            request.Name.Trim(),
            request.SheetName.Trim(),
            JsonConvert.SerializeObject(request.Preview, Formatting.None),
            JsonConvert.SerializeObject(request.Export, Formatting.None),
            request.DataSource.Code.Trim(),
            request.DataSource.Name.Trim(),
            "SqlServer",
            connectionString,
            request.DataSource.TimeoutSeconds,
            request.DataSource.MaxRows,
            request.Dataset.Code.Trim(),
            request.Dataset.Name.Trim(),
            request.Dataset.QueryText.Trim(),
            JsonConvert.SerializeObject(request.Columns, Formatting.None),
            request.Dataset.TimeoutSeconds,
            request.Dataset.MaxRows,
            parameterEntities,
            request.Revision));

        return await GetDocumentAsync(reportCode);
    }

    public async Task<PocDataSourceTestResult> TestDataSourceAsync(
        string reportCode,
        PocDataSourceInput input,
        CancellationToken cancellationToken = default)
    {
        EnsureReportCode(reportCode);
        ValidateDataSource(input);
        var stored = await _store.GetAsync(PocReportCatalog.ReportCode);
        var connectionString = BuildConnectionString(input, stored?.DataSource);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        started.Stop();
        return new PocDataSourceTestResult(
            true,
            started.ElapsedMilliseconds,
            connection.ServerVersion);
    }

    private PocReportConfigurationDocument CreateFallbackDocument(DateTimeOffset? now)
    {
        var definition = PocReportCatalog.GetDefinition(now);
        var builder = ParseConnectionString(_database.CurrentConnectionConfig.ConnectionString);
        return new PocReportConfigurationDocument(
            definition.Code,
            definition.Name,
            definition.SheetName,
            definition.Preview,
            definition.Export,
            ToDataSourceDocument(
                DefaultDataSourceCode,
                "报表中心 SQL Server",
                builder,
                30,
                20_000,
                true),
            new PocDatasetDocument(
                DefaultDatasetCode,
                "科室工作量明细",
                DefaultDataSourceCode,
                FallbackQueryText,
                30,
                20_000,
                true),
            definition.Parameters,
            definition.Columns,
            0);
    }

    private static PocReportConfigurationDocument CreateDocument(
        PocStoredReportConfiguration stored,
        DateTimeOffset? now)
    {
        var preview = Deserialize<PocPreviewSettings>(stored.Report.PreviewSettingsJson);
        var export = Deserialize<PocExportSettings>(stored.Report.ExportSettingsJson);
        var columns = Deserialize<PocColumnDefinition[]>(stored.Dataset.FieldSchemaJson);
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
                parameter.UrlKey);
        }).ToArray();
        var builder = ParseConnectionString(stored.DataSource.ConnectionString);

        return new PocReportConfigurationDocument(
            stored.Report.Code,
            stored.Report.Name,
            stored.Report.SheetName,
            preview,
            export,
            ToDataSourceDocument(
                stored.DataSource.Code,
                stored.DataSource.Name,
                builder,
                stored.DataSource.DefaultTimeoutSeconds,
                stored.DataSource.DefaultMaxRows,
                stored.DataSource.IsEnabled),
            new PocDatasetDocument(
                stored.Dataset.Code,
                stored.Dataset.Name,
                stored.Dataset.DataSourceCode,
                stored.Dataset.QueryText,
                stored.Dataset.TimeoutSeconds,
                stored.Dataset.MaxRows,
                stored.Dataset.IsEnabled),
            parameters,
            columns,
            stored.Report.Revision);
    }

    private static PocReportDefinition ToDefinition(PocReportConfigurationDocument document) =>
        new(
            document.ReportCode,
            document.Name,
            document.SheetName,
            document.Preview,
            document.Export,
            document.Parameters,
            document.Columns);

    private string BuildConnectionString(PocDataSourceInput input, PocDataSource? stored)
    {
        var baseConnectionString = stored is not null &&
            stored.Code.Equals(input.Code, StringComparison.OrdinalIgnoreCase)
                ? stored.ConnectionString
                : _database.CurrentConnectionConfig.ConnectionString;
        var existing = ParseConnectionString(baseConnectionString);
        var password = NullIfWhiteSpace(input.Password);
        if (password is null)
        {
            var existingEndpoint = ParseDataSource(existing.DataSource);
            var canReusePassword =
                existingEndpoint.Server.Equals(input.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
                existingEndpoint.Port == input.Port &&
                existing.InitialCatalog.Equals(input.Database, StringComparison.OrdinalIgnoreCase) &&
                existing.UserID.Equals(input.UserName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(existing.Password);
            if (!canReusePassword)
            {
                throw new PocDatasetConfigurationException(
                    "DATA_SOURCE_PASSWORD_REQUIRED",
                    "新数据源或连接信息变化时必须提供密码。");
            }
            password = existing.Password;
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = FormatDataSource(input.Server.Trim(), input.Port),
            InitialCatalog = input.Database.Trim(),
            UserID = input.UserName.Trim(),
            Password = password,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = Math.Clamp(input.TimeoutSeconds, 1, 120),
            ApplicationName = "hd-report-center-runtime"
        };
        return builder.ConnectionString;
    }

    private static PocDataSourceDocument ToDataSourceDocument(
        string code,
        string name,
        SqlConnectionStringBuilder builder,
        int timeoutSeconds,
        int maxRows,
        bool enabled)
    {
        var (server, port) = ParseDataSource(builder.DataSource);
        return new PocDataSourceDocument(
            code,
            name,
            "SqlServer",
            server,
            port,
            builder.InitialCatalog,
            builder.UserID,
            !string.IsNullOrWhiteSpace(builder.Password),
            timeoutSeconds,
            maxRows,
            enabled);
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
            request.Parameters.Count > 100 ||
            request.Columns.Count is 0 or > 256)
        {
            throw new PocDatasetConfigurationException("REPORT_DEFINITION_INVALID", "预览页大小或字段定义无效。");
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
        ValidateDataSource(request.DataSource);
        ValidateCode(request.Dataset.Code, "DATASET_DEFINITION_INVALID");
        if (!request.Dataset.DataSourceCode.Equals(request.DataSource.Code, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request.Dataset.Name) ||
            request.Dataset.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Dataset.QueryText) ||
            request.Dataset.QueryText.Length > 200_000 ||
            request.Dataset.TimeoutSeconds is < 1 or > 600 ||
            request.Dataset.MaxRows is < 1 or > 1_000_000)
        {
            throw new PocDatasetConfigurationException("DATASET_DEFINITION_INVALID", "主数据集配置无效。");
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
            _ = PocDefaultExpressionEvaluator.Evaluate(parameter.DefaultExpression, parameter.DataType);
        }

        var columnCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in request.Columns)
        {
            ValidateCode(column.Code, "DATASET_FIELD_INVALID");
            if (!columnCodes.Add(column.Code))
            {
                throw new PocDatasetConfigurationException("DATASET_FIELD_INVALID", $"字段编码 '{column.Code}' 重复。");
            }
            var isFormula = column.DataType.Equals("formula", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(column.Header) ||
                column.Header.Length > 128 ||
                !ColumnDataTypes.Contains(column.DataType) ||
                column.Width is < 40 or > 500 ||
                (isFormula &&
                 (string.IsNullOrWhiteSpace(column.FormulaTemplate) ||
                  column.FormulaTemplate.Length > 1_024 ||
                  !column.FormulaTemplate.TrimStart().StartsWith('=') )) ||
                (!isFormula && !string.IsNullOrWhiteSpace(column.FormulaTemplate)))
            {
                throw new PocDatasetConfigurationException("DATASET_FIELD_INVALID", $"字段 '{column.Code}' 的定义无效。");
            }
        }
        if (request.Columns.All(column =>
                column.DataType.Equals("formula", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PocDatasetConfigurationException("DATASET_FIELD_INVALID", "至少需要一个来自数据集的普通字段。");
        }
    }

    private static void ValidateDataSource(PocDataSourceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
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
            input.MaxRows is < 1 or > 1_000_000)
        {
            throw new PocDatasetConfigurationException("DATA_SOURCE_INVALID", "SQL Server 数据源配置无效。");
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

    private static string FormatDataSource(string server, int port) => $"{server.Trim()},{port}";

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureReportCode(string reportCode)
    {
        if (!string.Equals(reportCode, PocReportCatalog.ReportCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new KeyNotFoundException($"不存在报表 '{reportCode}'。");
        }
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
