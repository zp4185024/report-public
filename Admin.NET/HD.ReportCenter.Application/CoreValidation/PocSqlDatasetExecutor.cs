using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed class PocDatasetExecutionException : Exception
{
    public PocDatasetExecutionException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record PocRuntimeExecutionPlan(
    PocRuntimeReportConfiguration Configuration,
    PocExecuteRequest Query,
    PocResolvedParameterSet Parameters,
    bool IncludeSqlDebug = false);

/// <summary>多数据库只读数据集执行器。配置读取与 SQL 执行分离，便于导出任务冻结运行输入。</summary>
public sealed class PocSqlDatasetExecutor : ITransient
{
    private readonly PocReportConfigurationService _configurationService;
    private readonly PocExternalParameterSourceService? _externalParameters;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<PocSqlDatasetExecutor>? _logger;

    public PocSqlDatasetExecutor(
        PocReportConfigurationService configurationService,
        PocExternalParameterSourceService? externalParameters = null,
        IHostEnvironment? environment = null,
        ILogger<PocSqlDatasetExecutor>? logger = null)
    {
        _configurationService = configurationService;
        _externalParameters = externalParameters;
        _environment = environment;
        _logger = logger;
    }

    public async Task<PocRuntimeExecutionPlan> PrepareAsync(
        string reportCode,
        PocExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = await _configurationService.GetRuntimeAsync(reportCode);
        var externalValues = _externalParameters is null
            ? new Dictionary<string, string?>()
            : await _externalParameters.ResolveAsync(
                configuration.Definition.Parameters,
                request.Context,
                cancellationToken);
        var parameters = PocParameterResolver.Resolve(
            configuration.Definition.Parameters,
            request.Parameters,
            externalValues);
        if (!configuration.UsesFallbackData)
        {
            _ = PocReadOnlySqlValidator.Validate(
                configuration.QueryText,
                configuration.Definition.Parameters.Select(item => item.Code),
                configuration.Definition.Preview.Mode);
        }
        return new PocRuntimeExecutionPlan(
            configuration,
            request,
            parameters,
            request.SqlDebug && _environment?.IsDevelopment() == true);
    }

    public async Task<PocExecuteResult> ExecuteAsync(
        string reportCode,
        PocExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareAsync(reportCode, request, cancellationToken);
        return await ExecuteAsync(plan, cancellationToken, _logger);
    }

    public static async Task<PocExecuteResult> ExecuteAsync(
        PocRuntimeExecutionPlan plan,
        CancellationToken cancellationToken = default,
        ILogger<PocSqlDatasetExecutor>? logger = null)
    {
        if (plan.Configuration.UsesFallbackData)
        {
            return PocReportCatalog.Execute(plan.Query);
        }

        var definition = plan.Configuration.Definition;
        var preview = definition.Preview;
        var full = preview.Mode == PocPreviewMode.Full;
        var page = full ? 1 : Math.Max(1, plan.Query.Page);
        var take = full ? plan.Configuration.MaxRows + 1 : preview.PageSize + 1;
        var queryText = full
            ? plan.Configuration.QueryText
            : PocDatabaseProvider.BuildPagedQuery(
                plan.Configuration.DbType,
                plan.Configuration.QueryText);

        try
        {
            using var database = PocDatabaseProvider.CreateClient(
                plan.Configuration.DbType,
                plan.Configuration.ConnectionString,
                plan.Configuration.TimeoutSeconds,
                cancellationToken);
            var parameters = new List<SugarParameter>();

            var referenced = PocReadOnlySqlValidator.Validate(
                plan.Configuration.QueryText,
                definition.Parameters.Select(item => item.Code),
                preview.Mode);
            foreach (var code in referenced)
            {
                var parameterDefinition = definition.Parameters.Single(
                    item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                parameters.Add(PocDatabaseProvider.CreateParameter(
                    code,
                    parameterDefinition.DataType,
                    plan.Parameters.DatabaseValues[code]));
            }
            if (!full)
            {
                parameters.Add(PocDatabaseProvider.CreateParameter(
                    "__offset",
                    "int",
                    checked((page - 1) * preview.PageSize)));
                parameters.Add(PocDatabaseProvider.CreateParameter("__take", "int", take));
            }

            // SqlSugar 执行时会改写参数对象；调试快照必须在提交数据库前生成。
            var sqlDebug = plan.IncludeSqlDebug
                ? PocSqlDebugFormatter.Create(queryText, parameters)
                : null;
            if (sqlDebug is not null)
            {
                logger?.LogInformation(
                    """
                    ==================== 报表 SQL 开始 ====================
                    报表编码: {ReportCode}
                    实际绑定参数:
                    {BoundParameters}
                    参数化 SQL:
                    {ParameterizedSql}
                    参数替换后的展示 SQL（仅用于调试）:
                    {DisplaySql}
                    ==================== 报表 SQL 结束 ====================
                    """,
                    definition.Code,
                    PocSqlDebugFormatter.FormatParametersForLog(sqlDebug.Parameters),
                    sqlDebug.ParameterizedSql,
                    sqlDebug.DisplaySql);
            }
            var dataReader = await database.Ado.GetDataReaderAsync(queryText, parameters);
            await using var reader = dataReader as DbDataReader
                ?? throw new PocDatasetExecutionException(
                    "DATASET_QUERY_FAILED",
                    "数据库驱动未返回可读取的数据集结果。");
            var fields = definition.Fields.ToArray();
            var ordinals = ResolveOrdinals(reader, fields);
            var rows = new List<PocDataRow>(Math.Min(take, 4096));
            while (rows.Count < take && await reader.ReadAsync(cancellationToken))
            {
                var row = new PocDataRow();
                foreach (var field in fields)
                {
                    row[field.Code] = PocDatasetValueNormalizer.Normalize(
                        reader.GetValue(ordinals[field.Code]),
                        field);
                }
                rows.Add(row);
            }

            var hasMore = rows.Count > (full ? plan.Configuration.MaxRows : preview.PageSize);
            if (full && hasMore)
            {
                throw new PocDatasetExecutionException(
                    "PREVIEW_LIMIT_EXCEEDED",
                    $"完整预览超过 {plan.Configuration.MaxRows} 行限制。" );
            }
            if (!full && hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            return new PocExecuteResult(
                page,
                full ? rows.Count : preview.PageSize,
                !full && hasMore,
                plan.Parameters.DisplayValues,
                rows,
                sqlDebug);
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
                "数据集查询失败。",
                exception);
        }
    }

    public static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadExportPagesAsync(
        PocRuntimeExecutionPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var preview = plan.Configuration.Definition.Preview;
        if (preview.Mode == PocPreviewMode.Full)
        {
            var result = await ExecuteAsync(plan with
            {
                Query = plan.Query with { Page = 1 }
            }, cancellationToken);
            yield return result.Rows;
            yield break;
        }

        for (var page = 1; ; page++)
        {
            var result = await ExecuteAsync(plan with
            {
                Query = plan.Query with { Page = page }
            }, cancellationToken);
            if (result.Rows.Count > 0)
            {
                yield return result.Rows;
            }
            if (!result.HasMore)
            {
                yield break;
            }
        }
    }

    private static IReadOnlyDictionary<string, int> ResolveOrdinals(
        DbDataReader reader,
        IReadOnlyList<PocDatasetFieldDefinition> fields)
    {
        var actual = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!actual.TryGetValue(field.Code, out var ordinal))
            {
                throw new PocDatasetExecutionException(
                    "DATASET_FIELD_MISSING",
                    $"查询结果缺少字段 '{field.Code}'。" );
            }
            ordinals[field.Code] = ordinal;
        }
        return ordinals;
    }

}

internal static class PocSqlDebugFormatter
{
    private const string DisplaySqlWarning =
        "-- 仅用于开发调试展示；数据库实际执行参数化 SQL，不执行下方拼接文本。";

    public static PocSqlDebugInfo Create(
        string parameterizedSql,
        IReadOnlyList<SugarParameter> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterizedSql);
        ArgumentNullException.ThrowIfNull(parameters);

        var debugParameters = parameters
            .Select(parameter => new PocSqlDebugParameter(
                parameter.ParameterName,
                parameter.DbType.ToString(),
                parameter.Value is DBNull ? null : parameter.Value))
            .ToArray();
        var displaySql = parameters
            .OrderByDescending(parameter => parameter.ParameterName.Length)
            .Aggregate(parameterizedSql, ReplaceParameter);

        return new PocSqlDebugInfo(
            parameterizedSql,
            $"{DisplaySqlWarning}{Environment.NewLine}{displaySql}",
            debugParameters);
    }

    public static string FormatParametersForLog(
        IReadOnlyList<PocSqlDebugParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Count == 0
            ? "  （无）"
            : string.Join(
                Environment.NewLine,
                parameters.Select(parameter =>
                    $"  {parameter.Name} [{parameter.DataType}] = {FormatValue(parameter.Value)}"));
    }

    private static string ReplaceParameter(string sql, SugarParameter parameter)
    {
        var pattern = $@"(?<![\w@]){Regex.Escape(parameter.ParameterName)}\b";
        return Regex.Replace(
            sql,
            pattern,
            _ => FormatValue(parameter.Value),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string FormatValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool boolean => boolean ? "1" : "0",
        DateTime dateTime => $"'{dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'",
        DateTimeOffset dateTimeOffset => $"'{dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)}'",
        byte[] bytes => $"'<{bytes.Length} bytes>'",
        string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        char character => $"'{character.ToString().Replace("'", "''", StringComparison.Ordinal)}'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => $"'{Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''", StringComparison.Ordinal)}'"
    };
}
