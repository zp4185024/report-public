using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation;

internal sealed record JnpfSourceInterface(
    string Id,
    string Name,
    string DataConfigJson,
    string ParameterJson,
    string FieldJson);

internal sealed record JnpfDatabaseLink(string Id, string Name, string DbType);

internal sealed record JnpfSourceDataset(
    string Id,
    string Name,
    string ParameterJson,
    JnpfSourceInterface? Interface,
    JnpfDatabaseLink? DatabaseLink,
    string QueryText);

internal sealed record JnpfSourcePackage(
    string ReportId,
    string ReportName,
    string SourceReportCode,
    string VersionId,
    int VersionNumber,
    int ActiveVersionCount,
    string SnapshotJson,
    string QueryListJson,
    string ConvertConfigJson,
    IReadOnlyList<JnpfSourceDataset> Datasets);

internal sealed record JnpfField(
    string Source,
    string Code,
    string DisplayName,
    string DataType);

internal sealed record JnpfParameter(
    string Code,
    string DisplayName,
    string DataType,
    string ControlType,
    string SourceType,
    string? SourceCode,
    string? SourceFieldCode,
    string? UrlKey,
    string? DefaultExpression,
    string? FixedValue,
    string? OptionsJson,
    bool IsVisible,
    bool IsReadOnly,
    bool IsRequired,
    bool AllowUrlPrefill,
    int DisplayOrder);

internal sealed record JnpfMigration(
    string SourceReportId,
    string SourceVersionId,
    string ReportCode,
    string ReportName,
    string DatasetCode,
    string DatasetName,
    string DataSourceCode,
    string DataSourceDbType,
    string QueryText,
    string FieldSchemaJson,
    IReadOnlyList<JnpfParameter> Parameters,
    string SheetName,
    string SnapshotJson,
    string BindingJson,
    int TemplateRevision,
    string PreviewSettingsJson,
    string ExportSettingsJson,
    string FieldPresentationsJson,
    int FieldCount,
    int ParameterCount,
    int DetailFieldCount);

internal sealed record JnpfMigrationCandidate(
    JnpfSourcePackage Package,
    bool Migratable,
    string Status,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Markers,
    JnpfMigration? Migration);

internal sealed record JnpfSqlConversion(
    string QueryText,
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<string> Markers);

internal sealed record JnpfTemplateConversion(
    string SnapshotJson,
    string BindingJson,
    string SheetName,
    int DetailFieldCount,
    int FormulaRangeFixCount,
    IReadOnlyList<string> Markers);

internal static class PocJnpfMigrationSourceReader
{
    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Read Async 数据，并按调用约定返回结果。</summary>
    /// <param name="endpoint">endpoint 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static async Task<IReadOnlyList<JnpfSourcePackage>> ReadAsync(
        PocDataSourceEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = endpoint.Server,
            Port = checked((uint)endpoint.Port),
            Database = endpoint.Database,
            UserID = endpoint.UserName,
            Password = endpoint.Password,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 60,
            SslMode = MySqlSslMode.Preferred
        };

        // 第一阶段：调用 ReadAsync 依赖的异步能力，等待并校验返回数据。
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var reportRows = await QueryAsync(connection, """
            SELECT
                t.f_id AS report_id,
                t.f_full_name AS report_name,
                t.f_en_code AS source_report_code,
                v.f_id AS version_id,
                v.f_version AS version_number,
                v.f_snapshot AS snapshot_json,
                v.f_query_list AS query_list_json,
                v.f_convert_config AS convert_config_json
            FROM report_template t
            INNER JOIN report_version v ON v.f_template_id = t.f_id
            WHERE COALESCE(t.f_delete_mark, 0) = 0
              AND COALESCE(v.f_delete_mark, 0) = 0
              AND v.f_state = 1
            ORDER BY t.f_full_name, v.f_version DESC;
            """, null, cancellationToken);

        var versionCounts = reportRows
            .GroupBy(row => GetString(row, "report_id"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var interfaceCache = new Dictionary<string, JnpfSourceInterface?>(StringComparer.Ordinal);
        var databaseLinkCache = new Dictionary<string, JnpfDatabaseLink?>(StringComparer.Ordinal);
        var packages = new List<JnpfSourcePackage>(reportRows.Count);

        foreach (var report in reportRows)
        {
            // 第2阶段：构造 ReadAsync 当前步骤需要的中间数据和执行参数。
            var versionId = GetString(report, "version_id");
            var datasetRows = await QueryAsync(connection, """
                SELECT
                    ds.f_id AS dataset_id,
                    ds.f_full_name AS dataset_name,
                    ds.f_interface_id AS interface_id,
                    ds.f_parameter_json AS parameter_json,
                    ds.f_object_id AS object_id
                FROM base_data_set ds
                LEFT JOIN report_version linked_version ON linked_version.f_id = ds.f_object_id
                WHERE COALESCE(ds.f_delete_mark, 0) = 0
                  AND (
                        ds.f_object_id = @versionId
                        OR ds.f_object_id = @reportId
                        OR linked_version.f_template_id = (
                            SELECT current_version.f_template_id
                            FROM report_version current_version
                            WHERE current_version.f_id = @versionId
                        )
                      )
                ORDER BY ds.f_id DESC;
                """, new Dictionary<string, object?>
                {
                    ["versionId"] = versionId,
                    ["reportId"] = GetString(report, "report_id")
                }, cancellationToken);

            var datasets = new List<JnpfSourceDataset>(datasetRows.Count);
            var preferredDatasetRows = datasetRows
                .GroupBy(row => GetString(row, "dataset_name"), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(row => GetString(row, "object_id").Equals(versionId, StringComparison.Ordinal))
                    .ThenByDescending(row => GetString(row, "dataset_id"), StringComparer.Ordinal)
                    .First())
                .ToArray();
            foreach (var dataset in preferredDatasetRows)
            {
                var interfaceId = GetString(dataset, "interface_id");
                if (!interfaceCache.TryGetValue(interfaceId, out var sourceInterface))
                {
                    var rows = string.IsNullOrWhiteSpace(interfaceId)
                        ? []
                        : await QueryAsync(connection, """
                            SELECT
                                f_id,
                                f_full_name,
                                f_enabled_mark,
                                f_data_config_json,
                                f_parameter_json,
                                f_field_json
                            FROM base_data_interface
                            WHERE f_id = @interfaceId
                              AND COALESCE(f_delete_mark, 0) = 0
                            LIMIT 2;
                            """, new Dictionary<string, object?> { ["interfaceId"] = interfaceId }, cancellationToken);
                    // 第3阶段：构造 ReadAsync 当前步骤需要的中间数据和执行参数。
                    var row = Single(rows, $"JNPF 数据接口 '{interfaceId}'");
                    sourceInterface = row is null ? null : new JnpfSourceInterface(
                        GetString(row, "f_id"),
                        GetString(row, "f_full_name"),
                        GetString(row, "f_data_config_json"),
                        GetString(row, "f_parameter_json"),
                        GetString(row, "f_field_json"));
                    interfaceCache[interfaceId] = sourceInterface;
                }

                var dataConfig = PocJnpfMigrationConverter.ParseObject(sourceInterface?.DataConfigJson);
                var sqlData = PocJnpfMigrationConverter.GetObject(dataConfig, "sqlData") ?? new JObject();
                var databaseLinkId = PocJnpfMigrationConverter.GetString(sqlData, "dbLinkId");
                if (!databaseLinkCache.TryGetValue(databaseLinkId, out var databaseLink))
                {
                    var rows = string.IsNullOrWhiteSpace(databaseLinkId)
                        ? []
                        : await QueryAsync(connection, """
                            SELECT *
                            FROM base_db_link
                            WHERE f_id = @databaseLinkId
                              AND COALESCE(f_delete_mark, 0) = 0
                            LIMIT 2;
                            """, new Dictionary<string, object?> { ["databaseLinkId"] = databaseLinkId }, cancellationToken);
                    var row = Single(rows, $"JNPF 数据库连接 '{databaseLinkId}'");
                    databaseLink = row is null ? null : new JnpfDatabaseLink(
                        GetString(row, "f_id"),
                        GetString(row, "f_full_name"),
                        GetString(row, "f_db_type"));
                    databaseLinkCache[databaseLinkId] = databaseLink;
                }

                // 第4阶段：根据 ReadAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
                datasets.Add(new JnpfSourceDataset(
                    GetString(dataset, "dataset_id"),
                    GetString(dataset, "dataset_name"),
                    GetString(dataset, "parameter_json"),
                    sourceInterface,
                    databaseLink,
                    PocJnpfMigrationConverter.GetString(sqlData, "sql")));
            }

            var reportId = GetString(report, "report_id");
            packages.Add(new JnpfSourcePackage(
                reportId,
                GetString(report, "report_name"),
                GetString(report, "source_report_code"),
                versionId,
                GetInt32(report, "version_number"),
                versionCounts[reportId],
                GetString(report, "snapshot_json"),
                GetString(report, "query_list_json"),
                GetString(report, "convert_config_json"),
                datasets));
        }

        return packages;
    }

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Query Async 数据，并按调用约定返回结果。</summary>
    /// <param name="connection">connection 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="commandText">需要读取、校验或转换的业务值。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static async Task<List<Dictionary<string, object?>>> QueryAsync(
        MySqlConnection connection,
        string commandText,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 60;
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue($"@{name}", value ?? DBNull.Value);
            }
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken)
                    ? null
                    : reader.GetValue(index);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>负责 PocJnpfMigrationSourceReader 的 Single 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="rows">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="description">description 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static Dictionary<string, object?>? Single(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string description) => rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException($"{description} 返回了 {rows.Count} 条，无法确定唯一记录。")
        };

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Get String 数据，并按调用约定返回结果。</summary>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string GetString(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Get Int32 数据，并按调用约定返回结果。</summary>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;
}

internal static class PocJnpfMigrationConverter
{
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Include
    });
    private static readonly Regex ReadOnlySql = new(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LeadingSqlComments = new(@"\A\s*(?:(?:--[^\r\n]*(?:\r\n|\r|\n)|/\*.*?\*/)\s*)*", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SqlObjectReview = new(@"\bV_YB_FEE_DETAIL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrderBy = new(@"\bORDER\s+BY\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FormulaSum = new(@"^=SUM\(([A-Za-z]+[0-9]+)\)$", RegexOptions.Compiled);
    private static readonly Regex LegacyDataParameterToken = new(
        @"#\{\s*DataParameter\|[^}.]+\.(?<code>[A-Za-z][A-Za-z0-9_]*)\s*\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LegacySystemParameterToken = new(
        @"#\{\s*@(?<code>[A-Za-z][A-Za-z0-9_]*)\s*\}",
        RegexOptions.Compiled);
    private static readonly Regex LegacyDetailToken = new(
        @"^#\{\s*DataSet[^.{}]*\.(?<field>[^}]+)\s*\}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, (string DisplayName, string SourceFieldCode)> LegacySystemParameters =
        new Dictionary<string, (string DisplayName, string SourceFieldCode)>(StringComparer.OrdinalIgnoreCase)
        {
            ["hisOrganizeName"] = ("医院名称", "hisOrganizeName"),
            ["hisYbOrgId"] = ("定点医疗机构编码", "hisOrganizeCode")
        };
    private static readonly (Regex Pattern, string Syntax)[] ParameterPatterns =
    [
        (new Regex(@"\A\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled), "double-brace"),
        (new Regex(@"\A#\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}#?", RegexOptions.Compiled), "hash-brace"),
        (new Regex(@"\A\$\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled), "dollar-brace"),
        (new Regex(@"\A\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled), "brace"),
        (new Regex(@"\A#([A-Za-z][A-Za-z0-9_]*)#", RegexOptions.Compiled), "hash"),
        (new Regex(@"\A\$([A-Za-z][A-Za-z0-9_]*)\$", RegexOptions.Compiled), "dollar")
    ];

    /// <summary>构建 PocJnpfMigrationService 所需的 Create Candidate 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="package">package 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="reportDataSourceCode">需要读取、校验或转换的业务值。</param>
    /// <param name="reportDataSourceType">reportDataSourceType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static JnpfMigrationCandidate CreateCandidate(
        JnpfSourcePackage package,
        string reportDataSourceCode,
        string reportDataSourceType)
    {
        var blockers = new List<string>();
        var markers = new List<string>();
        JnpfMigration? migration = null;
        if (package.ActiveVersionCount != 1) blockers.Add($"ACTIVE_VERSION_COUNT:{package.ActiveVersionCount}");

        var dataset = SelectPrimaryDataset(package.Datasets, package.SnapshotJson);
        var databaseLink = dataset?.DatabaseLink;
        // 第一阶段：检查 CreateCandidate 当前条件，并按匹配分支执行对应处理。
        if (package.Datasets.Count > 1) markers.Add($"MULTI_DATASET_PRIMARY_SELECTED:{package.Datasets.Count}");
        if (dataset is null) markers.Add("DATASET_MISSING_PLACEHOLDER_CREATED");
        if (dataset?.Interface is null) markers.Add("DATA_INTERFACE_MISSING_FIELDS_INFERRED");
        if (databaseLink is null) markers.Add("DATABASE_LINK_MISSING_USING_TARGET");
        if (databaseLink is not null && !PocDatabaseProvider.SupportedTypes.Contains(databaseLink.DbType, StringComparer.OrdinalIgnoreCase))
        {
            markers.Add($"UNSUPPORTED_SOURCE_DB_TYPE_USING_TARGET:{databaseLink.DbType}");
        }

        if (blockers.Count == 0)
        {
            try
            {
                var datasetName = dataset?.Name ?? InferDatasetName(package.SnapshotJson) ?? "detail";
                var fields = dataset?.Interface is null
                    ? InferFieldsFromSnapshot(package.SnapshotJson, datasetName)
                    : ConvertFields(dataset.Interface.FieldJson);
                fields = MergeSnapshotFields(fields, package.SnapshotJson, datasetName);
                if (fields.Count == 0)
                {
                    fields = [new JnpfField("legacy_value", "legacy_value", "迁移占位字段", "string")];
                    markers.Add("EMPTY_FIELD_SCHEMA_PLACEHOLDER_CREATED");
                }
                var queryText = string.IsNullOrWhiteSpace(dataset?.QueryText)
                    ? BuildPlaceholderQuery(fields, reportDataSourceType)
                    : dataset.QueryText;
                var sql = ConvertSql(queryText, fields, reportDataSourceType);
                markers.AddRange(sql.Markers);
                if (databaseLink is not null && !databaseLink.DbType.Equals(reportDataSourceType, StringComparison.OrdinalIgnoreCase))
                {
                    markers.Add($"SQL_DIALECT_REVIEW:{databaseLink.DbType}->{reportDataSourceType}");
                }
                var sourceReportName = package.ReportName.Trim();
                var parameters = ConvertParameters(
                    sql.ParameterNames,
                    sql.QueryText,
                    package.SnapshotJson,
                    package.QueryListJson,
                    dataset?.ParameterJson,
                    dataset?.Interface?.ParameterJson);
                markers.AddRange(parameters.Markers);
                var template = ConvertSnapshot(
                    package.SnapshotJson,
                    datasetName,
                    fields,
                    parameters.Parameters);
                markers.AddRange(template.Markers);
                markers.Add("SOURCE_SQL_NOT_EXECUTED");
                // 第4阶段：整理迁移预览策略，统计字段参数并准备最终配置对象。

                var sourceCode = string.IsNullOrWhiteSpace(package.SourceReportCode)
                    ? $"report_{package.ReportId}"
                    : package.SourceReportCode;
                // 第2阶段：构造 CreateCandidate 当前步骤需要的中间数据和执行参数。
                var reportCode = ConvertTargetCode($"jnpf_{sourceCode}", $"jnpf_report_{package.ReportId}");
                var fieldSchema = new JObject
                {
                    ["variables"] = new JArray(parameters.Parameters
                        .Where(parameter => sql.ParameterNames.Contains(
                            parameter.Code,
                            StringComparer.OrdinalIgnoreCase))
                        .Select(parameter => new JObject
                    {
                        ["code"] = parameter.Code,
                        ["displayName"] = parameter.DisplayName,
                        ["dataType"] = parameter.DataType,
                        ["required"] = parameter.IsRequired,
                        ["sourceType"] = parameter.SourceType,
                        ["sourceCode"] = parameter.SourceCode,
                        ["sourceFieldCode"] = parameter.SourceFieldCode
                    })),
                    ["fields"] = new JArray(fields.Select(field => new JObject
                    {
                        ["code"] = field.Code,
                        ["displayName"] = field.DisplayName,
                        ["dataType"] = field.DataType
                    }))
                };
                var reportName = sourceReportName.Contains("JNPF迁移", StringComparison.Ordinal)
                    ? sourceReportName
                    : $"{sourceReportName}（JNPF迁移）";
                // 第3阶段：更新 CreateCandidate 当前分支对应的业务状态，并保持关联数据一致。
                migration = new JnpfMigration(
                    package.ReportId,
                    package.VersionId,
                    reportCode,
                    reportName,
                    reportCode,
                    reportName,
                    reportDataSourceCode,
                    reportDataSourceType,
                    sql.QueryText,
                    fieldSchema.ToString(Formatting.None),
                    parameters.Parameters,
                    string.IsNullOrWhiteSpace(template.SheetName) ? package.ReportName : template.SheetName,
                    template.SnapshotJson,
                    template.BindingJson,
                    Math.Max(1, package.VersionNumber),
                    "{\"mode\":\"Full\",\"pageSize\":20,\"showExactTotal\":false}",
                    "{\"defaultMode\":\"Normal\",\"normalMaxRows\":20000,\"normalMaxCells\":500000,\"normalTimeoutSeconds\":60,\"largeExportEnabled\":true,\"rowsPerSheet\":1000000,\"fileRetentionHours\":24}",
                    ConvertFieldPresentations(package.ConvertConfigJson, fields),
                    fields.Count,
                    parameters.Parameters.Count,
                    template.DetailFieldCount);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
            {
                blockers.Add($"CONVERSION_FAILED:{exception.Message}");
            }
        }

        var status = blockers.Count == 0 ? "Ready" : "Blocked";
        return new JnpfMigrationCandidate(
            package,
            status == "Ready",
            status,
            blockers,
            markers.Distinct(StringComparer.Ordinal).ToArray(),
            migration);
    }

    /// <summary>遍历 PocJnpfMigrationConverter 当前数据并执行 To Inventory 转换，生成调用方需要的结果集合。</summary>
    /// <param name="candidate">candidate 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static PocJnpfMigrationInventoryItem ToInventory(JnpfMigrationCandidate candidate)
    {
        var links = candidate.Package.Datasets
            .Select(dataset => dataset.DatabaseLink)
            .OfType<JnpfDatabaseLink>()
            .ToArray();
        return new PocJnpfMigrationInventoryItem(
            candidate.Package.ReportId,
            candidate.Package.VersionId,
            candidate.Package.ReportName,
            candidate.Package.SourceReportCode,
            candidate.Migration?.ReportCode ?? string.Empty,
            string.Join(',', links.Select(link => link.Name).Distinct(StringComparer.Ordinal)),
            string.Join(',', links.Select(link => link.DbType).Distinct(StringComparer.Ordinal)),
            candidate.Migratable,
            candidate.Status,
            candidate.Migration?.FieldCount ?? 0,
            candidate.Migration?.ParameterCount ?? 0,
            candidate.Migration?.DetailFieldCount ?? 0,
            string.Join(" | ", candidate.Blockers),
            string.Join(" | ", candidate.Markers));
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Fields 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="fieldJson">fieldJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static IReadOnlyList<JnpfField> ConvertFields(string fieldJson)
    {
        var sourceFields = Enumerate(ParseJson(fieldJson, new JArray())).ToArray();
        if (sourceFields.Length == 0) throw new InvalidOperationException("JNPF 数据接口没有返回字段定义。");
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<JnpfField>(sourceFields.Length);
        for (var index = 0; index < sourceFields.Length; index++)
        {
            var sourceField = sourceFields[index];
            var sourceName = GetString(sourceField, "field", "columnName", "column", "name");
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new InvalidOperationException($"JNPF 第 {index + 1} 个返回字段缺少字段名。");
            }
            var explicitCode = GetString(sourceField, "fieldCode", "enCode", "code");
            var codeCandidate = Regex.IsMatch(explicitCode, @"^[A-Za-z][A-Za-z0-9_]*$")
                ? explicitCode
                : Regex.IsMatch(sourceName, @"^[A-Za-z][A-Za-z0-9_]*$")
                    ? sourceName
                    : $"field_{index + 1:00}";
            var code = codeCandidate;
            for (var sequence = 2; !usedCodes.Add(code); sequence++) code = $"{codeCandidate}_{sequence}";
            var displayName = GetString(sourceField, "fieldName", "fullName", "label", "title");
            fields.Add(new JnpfField(
                sourceName,
                code,
                string.IsNullOrWhiteSpace(displayName) ? sourceName : displayName,
                ConvertDataType(GetString(sourceField, "dataType", "type", "fieldType"))));
        }
        return fields;
    }

    /// <summary>从旧模板快照中选择被引用最多的数据集，解决同一报表携带辅助数据集时的主绑定歧义。</summary>
    /// <param name="datasets">源端报表声明的数据集集合。</param>
    /// <param name="snapshotJson">包含工作表和占位符的模板快照。</param>
    /// <returns>返回主数据集；源端没有数据集时返回空值。</returns>
    private static JnpfSourceDataset? SelectPrimaryDataset(
        IReadOnlyList<JnpfSourceDataset> datasets,
        string snapshotJson)
    {
        if (datasets.Count == 0) return null;
        return datasets
            .OrderByDescending(dataset => CountDatasetReferences(snapshotJson, dataset.Name))
            .ThenByDescending(dataset => dataset.Interface is not null)
            .ThenBy(dataset => dataset.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>统计模板中某个旧数据集名称的占位符引用次数，用于确定主数据集。</summary>
    /// <param name="snapshotJson">包含工作表和占位符的模板快照。</param>
    /// <param name="datasetName">待统计的数据集名称。</param>
    /// <returns>返回不区分大小写的引用次数。</returns>
    private static int CountDatasetReferences(string snapshotJson, string datasetName) =>
        Regex.Matches(snapshotJson, $@"(?:\$\{{\s*|#\{{\s*DataSet)[^}}]*\b{Regex.Escape(datasetName)}\.", RegexOptions.IgnoreCase)
            .Count;

    /// <summary>从模板占位符推断旧数据集名称，供没有数据集记录的历史模板生成兼容配置。</summary>
    /// <param name="snapshotJson">包含工作表和占位符的模板快照。</param>
    /// <returns>返回首个可识别的数据集名称；不存在时返回空值。</returns>
    private static string? InferDatasetName(string snapshotJson)
    {
        var match = Regex.Match(snapshotJson, @"\$\{\s*([^\.{}]+)\.", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value.Trim();
        match = Regex.Match(snapshotJson, @"#\{\s*DataSet([^\.{}]*)\.", RegexOptions.IgnoreCase);
        return match.Success ? $"DataSet{match.Groups[1].Value}" : null;
    }

    /// <summary>从旧模板快照提取字段占位符，供接口缺失或字段定义为空时建立最小字段契约。</summary>
    /// <param name="snapshotJson">包含工作表和占位符的模板快照。</param>
    /// <param name="datasetName">当前主数据集名称。</param>
    /// <returns>返回去重后的推断字段集合。</returns>
    private static IReadOnlyList<JnpfField> InferFieldsFromSnapshot(string snapshotJson, string datasetName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new[]
        {
            new Regex($@"\$\{{\s*{Regex.Escape(datasetName)}\.([^}}]+)\s*\}}", RegexOptions.IgnoreCase),
            new Regex($@"#\{{\s*DataSet[^.{{}}]*\.([^}}]+)\s*\}}", RegexOptions.IgnoreCase)
        };
        foreach (var pattern in patterns)
        {
            foreach (Match match in pattern.Matches(snapshotJson))
            {
                var source = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(source)) names.Add(source);
            }
        }
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return names.Select((source, index) =>
        {
            var candidate = Regex.IsMatch(source, @"^[A-Za-z][A-Za-z0-9_]*$")
                ? source
                : ConvertTargetCode(source, $"field_{index + 1:00}");
            var code = candidate;
            for (var suffix = 2; !usedCodes.Add(code); suffix++) code = $"{candidate}_{suffix}";
            return new JnpfField(source, code, source, "string");
        }).ToArray();
    }

    /// <summary>从模板占位符补齐数据接口遗漏的字段定义，确保旧模板仍能建立稳定绑定。</summary>
    /// <param name="fields">接口已声明的字段集合。</param>
    /// <param name="snapshotJson">包含工作表和占位符的模板快照。</param>
    /// <param name="datasetName">当前主数据集名称。</param>
    /// <returns>返回去重后的有效字段集合。</returns>
    private static IReadOnlyList<JnpfField> MergeSnapshotFields(
        IReadOnlyList<JnpfField> fields,
        string snapshotJson,
        string datasetName)
    {
        var result = fields.ToList();
        var bySource = result.ToDictionary(field => field.Source, StringComparer.OrdinalIgnoreCase);
        var usedCodes = result.Select(field => field.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pattern = new Regex($@"\$\{{\s*{Regex.Escape(datasetName)}\.([^}}]+)\s*\}}", RegexOptions.IgnoreCase);
        foreach (Match match in pattern.Matches(snapshotJson))
        {
            var source = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(source) || bySource.ContainsKey(source)) continue;
            var codeCandidate = Regex.IsMatch(source, @"^[A-Za-z][A-Za-z0-9_]*$")
                ? source
                : ConvertTargetCode(source, "field");
            var code = codeCandidate;
            for (var suffix = 2; !usedCodes.Add(code); suffix++) code = $"{codeCandidate}_{suffix}";
            var field = new JnpfField(source, code, source, "string");
            result.Add(field);
            bySource[source] = field;
        }
        var legacyPattern = new Regex(@"#\{\s*DataSet[^.{}]*\.([^}]+)\s*\}", RegexOptions.IgnoreCase);
        foreach (Match match in legacyPattern.Matches(snapshotJson))
        {
            var source = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(source) || bySource.ContainsKey(source)) continue;
            var codeCandidate = Regex.IsMatch(source, @"^[A-Za-z][A-Za-z0-9_]*$")
                ? source
                : ConvertTargetCode(source, "field");
            var code = codeCandidate;
            for (var suffix = 2; !usedCodes.Add(code); suffix++) code = $"{codeCandidate}_{suffix}";
            var field = new JnpfField(source, code, source, "string");
            result.Add(field);
            bySource[source] = field;
        }
        return result;
    }

    /// <summary>为完全缺失 SQL 的旧模板构造只读占位查询，保证模板可先迁移再由管理员补齐数据源。</summary>
    /// <param name="fields">模板需要的字段集合。</param>
    /// <param name="dbType">目标报表数据源类型。</param>
    /// <returns>返回符合目标 SQL 方言的单行只读查询。</returns>
    private static string BuildPlaceholderQuery(IReadOnlyList<JnpfField> fields, string dbType)
    {
        var projections = fields.Count == 0
            ? "NULL AS legacy_value"
            : string.Join(", ", fields.Select(field => $"NULL AS {QuoteIdentifier(field.Code, dbType)}"));
        return dbType.ToLowerInvariant() switch
        {
            "oracle" => $"SELECT {projections} FROM dual WHERE 1 = 0 ORDER BY 1",
            "sqlserver" => $"SELECT TOP 0 {projections} ORDER BY (SELECT 1)",
            "mysql" or "postgresql" => $"SELECT {projections} ORDER BY 1 LIMIT 0",
            _ => $"SELECT {projections} ORDER BY 1"
        };
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Sql 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <param name="fields">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="dbType">dbType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static JnpfSqlConversion ConvertSql(
        string queryText,
        IReadOnlyList<JnpfField> fields,
        string dbType)
    {
        if (string.IsNullOrWhiteSpace(queryText)) throw new InvalidOperationException("JNPF 数据接口没有 SQL。");
        var converted = queryText.Trim().TrimEnd(';').TrimEnd();
        var parameterConversion = ConvertParameterMarkers(converted);
        converted = parameterConversion.QueryText;
        converted = LeadingSqlComments.Replace(converted, string.Empty);
        if (parameterConversion.UnsupportedMarkers.Count > 0)
        {
            throw new InvalidOperationException($"SQL 中存在未识别的 JNPF 参数标记，需要人工转换：{string.Join(", ", parameterConversion.UnsupportedMarkers)}");
        }
        if (!ReadOnlySql.IsMatch(converted)) throw new InvalidOperationException("数据接口不是 SELECT/WITH 只读查询。");

        var markers = parameterConversion.Markers.ToList();
        if (SqlObjectReview.IsMatch(converted)) markers.Add("DEV_SQL_OBJECT_REVIEW:V_YB_FEE_DETAIL");
        if (fields.Any(field => !field.Source.Equals(field.Code, StringComparison.Ordinal)))
        {
            var selectList = string.Join(",\r\n", fields.Select(field =>
                $"    jnpf_source.{QuoteIdentifier(field.Source, dbType)} AS {field.Code}"));
            converted = $"SELECT\r\n{selectList}\r\nFROM (\r\n{converted}\r\n) jnpf_source";
            markers.Add("SQL_OUTER_FIELD_ALIAS_APPLIED");
            if (OrderBy.IsMatch(queryText)) markers.Add("SQL_WRAPPED_ORDER_BY_REVIEW");
        }
        return new JnpfSqlConversion(converted, parameterConversion.ParameterNames, markers);
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Snapshot 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="snapshotJson">snapshotJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="datasetName">需要读取、校验或转换的业务值。</param>
    /// <param name="fields">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static JnpfTemplateConversion ConvertSnapshot(
        string snapshotJson,
        string datasetName,
        IReadOnlyList<JnpfField> fields,
        IReadOnlyList<JnpfParameter>? parameters = null)
    {
        var snapshot = ParseObject(snapshotJson);
        if (snapshot["sheets"] is not JObject sheets || snapshot["sheetOrder"] is null)
        {
            throw new InvalidOperationException("JNPF 模板快照缺少 sheets 或 sheetOrder。");
        }
        var markers = new List<string>();
        if (snapshot.Remove("resources")) markers.Add("LEGACY_WORKBOOK_RESOURCES_REMOVED");
        var fieldBySource = fields.ToDictionary(field => field.Source, StringComparer.OrdinalIgnoreCase);
        // 第一阶段：构造 ConvertSnapshot 当前步骤需要的中间数据和执行参数。
        var parameterByCode = (parameters ?? [])
            .ToDictionary(parameter => parameter.Code, StringComparer.OrdinalIgnoreCase);
        var coordinates = new List<(string SheetId, int Row, int Column, string Code)>();
        var parameterCoordinates = new List<(string SheetId, int Row, int Column, string Code)>();
        var legacyDetailCells = new List<(string SheetId, int Row, int Column, JObject Cell, string SourceName)>();
        var tokenPattern = new Regex(@"^\$\{\s*" + Regex.Escape(datasetName) + @"\.([^}]+)\s*\}$", RegexOptions.Compiled);
        var formulaRangeFixCount = 0;
        var mergeCoordinateFixCount = 0;
        var unconvertedTokenCount = 0;
        var sheetIds = Enumerate(snapshot["sheetOrder"]).Select(item => item.ToString()).ToArray();
        foreach (var sheetId in sheetIds)
        {
            if (sheets[sheetId] is not JObject sheet) throw new InvalidOperationException($"模板 sheetOrder 引用了不存在的工作表 '{sheetId}'。");
            foreach (var legacyKey in new[] { "floatEcharts", "cellEcharts", "floatImages" })
            {
                if (sheet.TryGetValue(legacyKey, out var legacyValue))
                {
                    if (Enumerate(legacyValue).Any()) markers.Add($"UNSUPPORTED_TEMPLATE_ASSET_REMOVED:{legacyKey}");
                    sheet.Remove(legacyKey);
                }
            }
            foreach (var merge in Enumerate(sheet["mergeData"]).OfType<JObject>())
            {
                foreach (var coordinateName in new[] { "startRow", "endRow", "startColumn", "endColumn", "rangeType" })
                {
                    if (merge[coordinateName]?.Type != JTokenType.String ||
                        !int.TryParse(merge.Value<string>(coordinateName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var coordinate))
                    {
                        continue;
                    }

                    merge[coordinateName] = coordinate;
                    mergeCoordinateFixCount++;
                }
            }
            if (sheet["cellData"] is not JObject cellData) continue;
            // 第2阶段：遍历 ConvertSnapshot 当前数据集合，逐项转换并维护处理结果。
            foreach (var rowProperty in cellData.Properties())
            {
                if (rowProperty.Value is not JObject row || !int.TryParse(rowProperty.Name, out var rowIndex)) continue;
                foreach (var columnProperty in row.Properties())
                {
                    if (columnProperty.Value is not JObject cell || !int.TryParse(columnProperty.Name, out var columnIndex)) continue;
                    cell.Remove("custom");
                    if (cell["v"]?.Type == JTokenType.String)
                    {
                        var value = cell.Value<string>("v") ?? string.Empty;
                        var match = tokenPattern.Match(value);
                        if (match.Success)
                        {
                            var sourceName = match.Groups[1].Value.Trim();
                            if (!fieldBySource.TryGetValue(sourceName, out var field))
                            {
                                field = new JnpfField(sourceName, ConvertTargetCode(sourceName, "field"), sourceName, "string");
                                fieldBySource[sourceName] = field;
                                markers.Add($"TEMPLATE_FIELD_INFERRED:{sourceName}");
                            }
                            cell["v"] = $"{{{{detail.{field.Code}}}}}";
                            coordinates.Add((sheetId, rowIndex, columnIndex, field.Code));
                        }
                        else
                        {
                            var legacyDetail = LegacyDetailToken.Match(value);
                            if (legacyDetail.Success)
                            {
                                legacyDetailCells.Add((
                                    sheetId,
                                    rowIndex,
                                    columnIndex,
                                    cell,
                                    legacyDetail.Groups["field"].Value.Trim()));
                            }
                            else if (Regex.IsMatch(value, @"\$\{[^}]+\}"))
                            {
                                // 第3阶段：根据 ConvertSnapshot 当前分支转换输入数据，并保存该阶段产生的中间结果。
                                unconvertedTokenCount++;
                            }
                        }

                        var parameterCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var convertedValue = value;
                        foreach (var pattern in new[] { LegacyDataParameterToken, LegacySystemParameterToken })
                        {
                            convertedValue = pattern.Replace(convertedValue, legacyMatch =>
                            {
                                var code = legacyMatch.Groups["code"].Value;
                                if (!parameterByCode.TryGetValue(code, out var parameter))
                                {
                                    throw new InvalidOperationException($"模板使用了未声明的参数 '{code}'。");
                                }
                                parameterCodes.Add(parameter.Code);
                                return $"{{{{params.{parameter.Code}}}}}";
                            });
                        }
                        if (!convertedValue.Equals(value, StringComparison.Ordinal))
                        {
                            cell["v"] = convertedValue;
                            parameterCoordinates.AddRange(parameterCodes.Select(code =>
                                (sheetId, rowIndex, columnIndex, code)));
                        }
                    }
                    if (cell["f"]?.Type == JTokenType.String)
                    {
                        var formula = cell.Value<string>("f") ?? string.Empty;
                        var match = FormulaSum.Match(formula);
                        if (match.Success)
                        {
                            cell["f"] = $"=SUM({match.Groups[1].Value}:{match.Groups[1].Value})";
                            // 第4阶段：根据 ConvertSnapshot 当前分支转换输入数据，并保存该阶段产生的中间结果。
                            formulaRangeFixCount++;
                        }
                    }
                }
            }
        }

        (string SheetId, int Row)? canonicalDetailLocation = coordinates.Count > 0
            ? (coordinates[0].SheetId, coordinates[0].Row)
            : null;
        if (canonicalDetailLocation is null && legacyDetailCells.Count > 0)
        {
            var groups = legacyDetailCells
                .GroupBy(item => (item.SheetId, item.Row))
                .OrderByDescending(group => group.Count())
                .ToArray();
            if (groups.Length > 1 && groups[0].Count() == groups[1].Count())
            {
                throw new InvalidOperationException("模板中存在多行旧明细占位符，无法确定明细模板行。");
            }
            canonicalDetailLocation = groups[0].Key;
        }

        var staleDetailTokenCount = 0;
        foreach (var legacyDetail in legacyDetailCells)
        {
            if (canonicalDetailLocation is not null &&
                canonicalDetailLocation.Value.SheetId.Equals(legacyDetail.SheetId, StringComparison.Ordinal) &&
                canonicalDetailLocation.Value.Row == legacyDetail.Row)
            {
                if (!fieldBySource.TryGetValue(legacyDetail.SourceName, out var field))
                {
                    field = new JnpfField(legacyDetail.SourceName, ConvertTargetCode(legacyDetail.SourceName, "field"), legacyDetail.SourceName, "string");
                    fieldBySource[legacyDetail.SourceName] = field;
                    markers.Add($"TEMPLATE_FIELD_INFERRED:{legacyDetail.SourceName}");
                }
                legacyDetail.Cell["v"] = $"{{{{detail.{field.Code}}}}}";
                coordinates.Add((legacyDetail.SheetId, legacyDetail.Row, legacyDetail.Column, field.Code));
                continue;
            }

            // 第5阶段：根据 ConvertSnapshot 当前分支转换输入数据，并保存该阶段产生的中间结果。
            legacyDetail.Cell.Remove("v");
            staleDetailTokenCount++;
        }
        if (staleDetailTokenCount > 0)
        {
            markers.Add($"STALE_DETAIL_TOKEN_REMOVED:{staleDetailTokenCount}");
        }
        if (coordinates.Count == 0)
        {
            markers.Add("STATIC_TEMPLATE_NO_DETAIL_BINDING");
        }
        var coordinateGroups = coordinates
            .GroupBy(item => (item.SheetId, item.Row))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.SheetId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Row)
            .ToArray();
        if (coordinateGroups.Length > 1)
        {
            markers.Add($"MULTI_DETAIL_PRIMARY_SELECTED:{coordinateGroups.Length}");
            var primary = coordinateGroups[0].Key;
            var removedSecondaryTokens = 0;
            foreach (var sheetId in sheetIds)
            {
                if (sheets[sheetId] is not JObject sheet || sheet["cellData"] is not JObject cellData) continue;
                foreach (var rowProperty in cellData.Properties())
                {
                    if (!int.TryParse(rowProperty.Name, out var rowIndex) || rowProperty.Value is not JObject row) continue;
                    foreach (var cellProperty in row.Properties())
                    {
                        if (!int.TryParse(cellProperty.Name, out _) || cellProperty.Value is not JObject cell) continue;
                        if (cell["v"]?.Type == JTokenType.String &&
                            cell.Value<string>("v")?.StartsWith("{{detail.", StringComparison.Ordinal) == true &&
                            (!sheetId.Equals(primary.SheetId, StringComparison.Ordinal) || rowIndex != primary.Row))
                        {
                            cell.Remove("v");
                            removedSecondaryTokens++;
                        }
                    }
                }
            }
            if (removedSecondaryTokens > 0) markers.Add($"SECONDARY_DETAIL_TOKEN_REMOVED:{removedSecondaryTokens}");
            coordinates = coordinateGroups[0].ToList();
        }
        if (unconvertedTokenCount > 0) markers.Add($"UNCONVERTED_TEMPLATE_TOKEN:{unconvertedTokenCount}");
        if (mergeCoordinateFixCount > 0) markers.Add($"LEGACY_MERGE_COORDINATE_NORMALIZED:{mergeCoordinateFixCount}");
        if (sheetIds.Length > 1) markers.Add($"MULTI_SHEET_REVIEW:{sheetIds.Length}");

        var detail = coordinates.OrderBy(item => item.Column).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray();
        var detailSheet = detail.Length == 0 ? null : (JObject)sheets[detail[0].SheetId]!;
        // 第7阶段：完成明细位置校验并组装模板绑定，保证静态模板也能保存。
        foreach (var merge in Enumerate(detailSheet?["mergeData"]).OfType<JObject>())
        {
            var startRow = GetInt32(merge, -1, "startRow");
            var endRow = GetInt32(merge, -1, "endRow");
            if (startRow <= detail[0].Row && detail[0].Row <= endRow)
            {
                markers.Add("DETAIL_ROW_MERGED_REVIEW_REQUIRED");
            }
        }
        // 第6阶段：遍历 ConvertSnapshot 当前数据集合，逐项转换并维护处理结果。
        var binding = new JObject
        {
            ["version"] = 1,
            ["parameterBindings"] = new JArray(parameterCoordinates
                .Distinct()
                .OrderBy(item => item.SheetId, StringComparer.Ordinal)
                .ThenBy(item => item.Row)
                .ThenBy(item => item.Column)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .Select(item => new JObject
                {
                    ["code"] = item.Code,
                    ["sheetId"] = item.SheetId,
                    ["row"] = item.Row,
                    ["column"] = item.Column
                })),
            ["detailBinding"] = detail.Length == 0
                ? null
                : new JObject
                {
                    ["datasetCode"] = "detail",
                    ["sheetId"] = detail[0].SheetId,
                    ["templateRow"] = detail[0].Row,
                    ["fields"] = new JArray(detail.Select(item => new JObject
                    {
                        ["code"] = item.Code,
                        ["column"] = item.Column
                    }))
                }
        };
        return new JnpfTemplateConversion(
            snapshot.ToString(Formatting.None),
            binding.ToString(Formatting.None),
            detailSheet is null ? string.Empty : GetString(detailSheet, "name"),
            detail.Length,
            formulaRangeFixCount,
            markers);
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Target Code 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="fallback">fallback 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="maximumLength">maximumLength 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static string ConvertTargetCode(string? value, string fallback, int maximumLength = 64)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        candidate = Regex.Replace(candidate, @"[^A-Za-z0-9_-]+", "_").Trim('_', '-');
        if (string.IsNullOrWhiteSpace(candidate)) candidate = fallback;
        if (!Regex.IsMatch(candidate, "^[A-Za-z]")) candidate = $"jnpf_{candidate}";
        if (candidate.Length <= maximumLength) return candidate.ToLowerInvariant();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)))[..8].ToLowerInvariant();
        return $"{candidate[..(maximumLength - suffix.Length - 1)].TrimEnd('_', '-')}_{suffix}".ToLowerInvariant();
    }

    /// <summary>将 PocJnpfMigrationService 中的 Parse Object 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="json">json 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal static JObject ParseObject(string? json) => ParseJson(json, new JObject()) as JObject
        ?? throw new InvalidOperationException("JNPF 元数据 JSON 应为对象。");

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Get Object 数据，并按调用约定返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="names">names 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal static JObject? GetObject(JToken? token, params string[] names) => GetToken(token, names) as JObject;

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Get String 数据，并按调用约定返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="names">names 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal static string GetString(JToken? token, params string[] names)
    {
        var value = GetToken(token, names);
        return value is null || value.Type is JTokenType.Null or JTokenType.Undefined
            ? string.Empty
            : value.Type == JTokenType.String
                ? value.Value<string>() ?? string.Empty
                : Convert.ToString(((JValue)value).Value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Parameters 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="parameterNames">parameterNames 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <param name="snapshotJson">snapshotJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="jsonDocuments">jsonDocuments 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static (IReadOnlyList<JnpfParameter> Parameters, IReadOnlyList<string> Markers) ConvertParameters(
        IReadOnlyList<string> parameterNames,
        string queryText,
        string snapshotJson,
        params string?[] jsonDocuments)
    {
        var documents = jsonDocuments.Select(json => ParseJson(json, new JObject())).ToArray();
        var parameters = new List<JnpfParameter>(parameterNames.Count);
        var markers = new List<string>();
        for (var index = 0; index < parameterNames.Count; index++)
        {
            var code = parameterNames[index];
            var metadata = FindParameterMetadata(code, documents);
            var config = GetObject(metadata, "config") ?? new JObject();
            // 第一阶段：构造 ConvertParameters 当前步骤需要的中间数据和执行参数。
            var displayName = GetString(metadata, "label", "fullName", "fieldName", "title", "name");
            var rawType = GetString(metadata, "controlType", "jnpfKey", "type", "dataType");
            if (string.IsNullOrWhiteSpace(rawType)) rawType = GetString(config, "controlType", "jnpfKey", "type", "dataType");
            var format = GetString(metadata, "format");
            if (string.IsNullOrWhiteSpace(format)) format = GetString(config, "format");
            var requiredToken = GetToken(metadata, "required", "isRequired") ?? GetToken(config, "required", "isRequired");
            var legacySourceType = GetString(metadata, "sourceType");
            var relationField = GetString(metadata, "relationField");
            var isFixedParameter = legacySourceType.Equals("2", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(relationField);
            if (metadata is null) markers.Add($"PARAMETER_METADATA_DEFAULTED:{code}");
            if (isFixedParameter) markers.Add($"PARAMETER_FIXED_VALUE_MIGRATED:{code}");
            var controlType = InferCalendarControlType(queryText, code)
                ?? ConvertControlType(rawType, format, code);
            var parameter = new JnpfParameter(
                code,
                string.IsNullOrWhiteSpace(displayName) ? code : displayName,
                controlType == "number" ? "number" : "string",
                controlType,
                isFixedParameter ? "Fixed" : "Form",
                null,
                null,
                isFixedParameter ? null : code,
                null,
                isFixedParameter ? relationField : null,
                null,
                !isFixedParameter,
                isFixedParameter,
                ToBoolean(requiredToken),
                !isFixedParameter,
                index);
            parameters.Add(BindLegacySystemParameter(parameter));
        }

        foreach (Match match in LegacySystemParameterToken.Matches(snapshotJson))
        {
            var code = match.Groups["code"].Value;
            // 第2阶段：检查 ConvertParameters 当前条件，并按匹配分支执行对应处理。
            if (!LegacySystemParameters.TryGetValue(code, out var mapping))
            {
                markers.Add($"UNSUPPORTED_SYSTEM_TEMPLATE_PARAMETER:{code}");
                continue;
            }
            if (parameters.Any(parameter => parameter.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            parameters.Add(new JnpfParameter(
                code,
                mapping.DisplayName,
                "string",
                "text",
                "External",
                "his",
                mapping.SourceFieldCode,
                null,
                null,
                null,
                null,
                false,
                true,
                false,
                false,
                parameters.Count));
        }
        return (parameters, markers);
    }

    /// <summary>根据 PocJnpfMigrationConverter 当前输入计算 Bind Legacy System Parameter 结果，并按方法声明的类型返回。</summary>
    /// <param name="parameter">parameter 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JnpfParameter BindLegacySystemParameter(JnpfParameter parameter)
    {
        if (!LegacySystemParameters.TryGetValue(parameter.Code, out var mapping)) return parameter;
        return parameter with
        {
            DisplayName = mapping.DisplayName,
            DataType = "string",
            ControlType = "text",
            SourceType = "External",
            SourceCode = "his",
            SourceFieldCode = mapping.SourceFieldCode,
            UrlKey = null,
            DefaultExpression = null,
            FixedValue = null,
            OptionsJson = null,
            IsVisible = false,
            IsReadOnly = true,
            AllowUrlPrefill = false
        };
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Field Presentations 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="convertConfigJson">convertConfigJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="fields">参与映射、显示或数据写入的字段集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ConvertFieldPresentations(string? convertConfigJson, IReadOnlyList<JnpfField> fields)
    {
        if (string.IsNullOrWhiteSpace(convertConfigJson)) return "[]";
        var fieldBySource = fields.ToDictionary(field => field.Source, StringComparer.OrdinalIgnoreCase);
        var result = new JArray();
        foreach (var conversion in Enumerate(ParseJson(convertConfigJson, new JArray())))
        {
            if (!GetString(conversion, "type").Equals("select", StringComparison.Ordinal)) continue;
            var sourceName = GetString(conversion, "field").Split('.').LastOrDefault() ?? string.Empty;
            if (!fieldBySource.TryGetValue(sourceName, out var field)) continue;
            var options = new JArray();
            foreach (var option in Enumerate(GetToken(GetObject(conversion, "config"), "options")))
            {
                var value = GetString(option, "id", "value", "code");
                if (string.IsNullOrWhiteSpace(value)) continue;
                options.Add(new JObject
                {
                    ["value"] = value,
                    ["label"] = GetString(option, "fullName", "label", "name")
                });
            }
            if (options.Count > 0)
            {
                result.Add(new JObject { ["code"] = field.Code, ["valueMappings"] = options });
            }
        }
        return result.ToString(Formatting.None);
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Parameter Markers 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static (string QueryText, IReadOnlyList<string> ParameterNames, IReadOnlyList<string> Markers, IReadOnlyList<string> UnsupportedMarkers) ConvertParameterMarkers(string queryText)
    {
        var builder = new StringBuilder(queryText.Length);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var markers = new List<string>();
        var unsupported = new List<string>();
        for (var index = 0; index < queryText.Length;)
        // 第一阶段：根据 ConvertParameterMarkers 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            var current = queryText[index];
            if (current == '\'')
            {
                CopyQuoted(queryText, builder, ref index, '\'', '\'');
                continue;
            }
            if (current is '"' or '[')
            {
                CopyQuoted(queryText, builder, ref index, current, current == '[' ? ']' : '"');
                continue;
            }
            if (current == '-' && index + 1 < queryText.Length && queryText[index + 1] == '-')
            {
                while (index < queryText.Length)
                {
                    var character = queryText[index++];
                    builder.Append(character);
                    if (character == '\n') break;
                }
                continue;
            }
            if (current == '/' && index + 1 < queryText.Length && queryText[index + 1] == '*')
            {
                builder.Append("/*");
                index += 2;
                while (index < queryText.Length)
                // 第2阶段：根据 ConvertParameterMarkers 当前分支转换输入数据，并保存该阶段产生的中间结果。
                {
                    var character = queryText[index++];
                    builder.Append(character);
                    if (character == '*' && index < queryText.Length && queryText[index] == '/')
                    {
                        builder.Append('/');
                        index++;
                        break;
                    }
                }
                continue;
            }

            var remaining = queryText[index..];
            Match? matched = null;
            string? syntax = null;
            foreach (var pattern in ParameterPatterns)
            {
                var match = pattern.Pattern.Match(remaining);
                if (!match.Success) continue;
                matched = match;
                syntax = pattern.Syntax;
                break;
            }
            if (matched is null && current == ':' && IsParameterBoundary(queryText, index, allowDollar: false))
            {
                var match = Regex.Match(remaining, @"\A:([A-Za-z][A-Za-z0-9_]*)");
                // 第3阶段：检查 ConvertParameterMarkers 当前条件，并按匹配分支执行对应处理。
                if (match.Success) { matched = match; syntax = "colon"; }
            }
            if (matched is null && current == '@' && IsParameterBoundary(queryText, index, allowDollar: true))
            {
                var match = Regex.Match(remaining, @"\A@([A-Za-z][A-Za-z0-9_]*)");
                if (match.Success) { matched = match; syntax = "at"; }
            }
            if (matched is not null)
            {
                var name = matched.Groups[1].Value;
                builder.Append('@').Append(name);
                if (seen.Add(name)) names.Add(name);
                markers.Add($"JNPF_PARAMETER_MARKER:{syntax}->{name}");
                index += matched.Length;
                continue;
            }
            if (current is '{' or '}') unsupported.Add($"UNSUPPORTED_SQL_PARAMETER_MARKER:{current}@{index}");
            builder.Append(current);
            index++;
        }
        return (builder.ToString(), names, markers, unsupported);
    }

    /// <summary>按照 PocJnpfMigrationService 的业务约束完成 Copy Quoted 数据合并或复制。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="output">output 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="index">控制数据范围或位置的数值。</param>
    /// <param name="opening">opening 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="closing">closing 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void CopyQuoted(string input, StringBuilder output, ref int index, char opening, char closing)
    {
        output.Append(input[index++]);
        while (index < input.Length)
        {
            var character = input[index++];
            output.Append(character);
            if (character != closing) continue;
            if (index < input.Length && input[index] == closing)
            {
                output.Append(input[index++]);
                continue;
            }
            break;
        }
    }

    /// <summary>校验 PocJnpfMigrationService 的 Is Parameter Boundary 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <param name="index">控制数据范围或位置的数值。</param>
    /// <param name="allowDollar">allowDollar 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool IsParameterBoundary(string queryText, int index, bool allowDollar)
    {
        if (index == 0) return true;
        var previous = queryText[index - 1];
        return !(char.IsAsciiLetterOrDigit(previous) || previous == '_' || previous == ':' || (!allowDollar && previous == '$'));
    }

    /// <summary>将 PocJnpfMigrationService 中的 Quote Identifier 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="dbType">dbType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string QuoteIdentifier(string value, string dbType) => dbType.ToLowerInvariant() switch
    {
        "sqlserver" => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]",
        "mysql" => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`",
        _ => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
    };

    /// <summary>将 PocJnpfMigrationService 中的 Parse Json 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="json">json 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="fallback">fallback 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JToken ParseJson(string? json, JToken fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback.DeepClone();
        try { return JToken.Parse(json); }
        catch (JsonReaderException exception) { throw new InvalidOperationException($"JNPF 元数据 JSON 无法解析：{exception.Message}", exception); }
    }

    /// <summary>负责 PocJnpfMigrationConverter 的 Enumerate 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IEnumerable<JToken> Enumerate(JToken? token) => token switch
    {
        null => [],
        JArray array => array.Children(),
        _ => [token]
    };

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Get Token 数据，并按调用约定返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="names">names 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JToken? GetToken(JToken? token, params string[] names)
    {
        if (token is not JObject value) return null;
        foreach (var name in names)
        {
            var property = value.Properties().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property is not null) return property.Value;
        }
        return null;
    }

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Find Parameter Metadata 数据，并按调用约定返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="documents">documents 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject? FindParameterMetadata(string code, IEnumerable<JToken> documents)
    {
        foreach (var document in documents)
        {
            foreach (var map in AllObjects(document))
            {
                var candidate = GetString(map, "field", "code", "variable", "parameter", "vModel", "id", "name");
                if (candidate.Contains('.', StringComparison.Ordinal)) candidate = candidate.Split('.').Last();
                if (candidate.Equals(code, StringComparison.OrdinalIgnoreCase)) return map;
            }
        }
        return null;
    }

    /// <summary>遍历 PocJnpfMigrationConverter 当前数据并执行 All Objects 转换，生成调用方需要的结果集合。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IEnumerable<JObject> AllObjects(JToken? token)
    {
        if (token is JObject value)
        {
            yield return value;
            foreach (var child in value.Properties().SelectMany(property => AllObjects(property.Value))) yield return child;
        }
        else if (token is JArray array)
        {
            foreach (var child in array.SelectMany(AllObjects)) yield return child;
        }
    }

    /// <summary>查询或读取 PocJnpfMigrationService 所需的 Get Int32 数据，并按调用约定返回结果。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="fallback">fallback 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="names">names 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int GetInt32(JToken? token, int fallback, params string[] names)
    {
        var value = GetToken(token, names);
        return value is null || value.Type is JTokenType.Null or JTokenType.Undefined
            ? fallback
            : Convert.ToInt32(((JValue)value).Value, CultureInfo.InvariantCulture);
    }

    /// <summary>根据 PocJnpfMigrationConverter 当前输入计算 To Boolean 结果，并按方法声明的类型返回。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool ToBoolean(JToken? token)
    {
        if (token?.Type == JTokenType.Boolean) return token.Value<bool>();
        return Regex.IsMatch(token?.ToString() ?? string.Empty, "^(1|true|yes)$", RegexOptions.IgnoreCase);
    }

    /// <summary>将 PocJnpfMigrationService 中的 Convert Data Type 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ConvertDataType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "int" or "integer" or "long" or "short" or "decimal" or "double" or "float" or "number" or "numeric" or "money" => "number",
        "date" or "datetime" or "timestamp" or "time" => "datetime",
        "bool" or "boolean" or "bit" => "boolean",
        _ => "string"
    };

    /// <summary>将 PocJnpfMigrationService 中的 Convert Control Type 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="format">format 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ConvertControlType(string value, string format, string code)
    {
        var normalized = value.ToLowerInvariant();
        var normalizedFormat = Regex.Replace(format.ToLowerInvariant(), @"[^a-z]", string.Empty);
        if (normalizedFormat == "yyyy") return "year";
        if (normalizedFormat == "yyyymm") return "month";
        if (normalizedFormat.Contains("hh", StringComparison.Ordinal) ||
            normalizedFormat.Contains("ss", StringComparison.Ordinal)) return "datetime";
        if (normalizedFormat.Contains("yyyymmdd", StringComparison.Ordinal)) return "date";
        if (normalized.Contains("multi", StringComparison.Ordinal)) return "multiSelect";
        if (normalized.Contains("select", StringComparison.Ordinal) || normalized.Contains("radio", StringComparison.Ordinal)) return "select";
        if (normalized.Contains("number", StringComparison.Ordinal) || normalized.Contains("inputnumber", StringComparison.Ordinal)) return "number";
        if (normalized.Contains("check", StringComparison.Ordinal) || normalized.Contains("switch", StringComparison.Ordinal) || normalized.Contains("bool", StringComparison.Ordinal)) return "checkbox";
        if (normalized.Contains("month", StringComparison.Ordinal)) return "month";
        if (normalized.Contains("year", StringComparison.Ordinal)) return "year";
        if (normalized.Contains("datetime", StringComparison.Ordinal) || normalized.Contains("time", StringComparison.Ordinal)) return "datetime";
        if (normalized.Contains("date", StringComparison.Ordinal)) return "date";
        if (!string.IsNullOrWhiteSpace(normalized)) return "text";
        if (Regex.IsMatch(code, "(date|sdate|edate|bdate|time)$", RegexOptions.IgnoreCase)) return "date";
        return "text";
    }

    /// <summary>根据 PocJnpfMigrationConverter 当前输入计算 Infer Calendar Control Type 结果，并按方法声明的类型返回。</summary>
    /// <param name="queryText">需要读取、校验或转换的业务值。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? InferCalendarControlType(string queryText, string code)
    {
        var parameter = Regex.Escape($"@{code}");
        if (Regex.IsMatch(
                queryText,
                $@"{parameter}\s*(?:\|\||\+)\s*'-01-01'|CONCAT\s*\(\s*{parameter}\s*,\s*'-01-01'\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "year";
        }
        if (Regex.IsMatch(
                queryText,
                $@"{parameter}\s*(?:\|\||\+)\s*'-01'|CONCAT\s*\(\s*{parameter}\s*,\s*'-01'\s*\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "month";
        }
        return null;
    }
}
