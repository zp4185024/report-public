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
    /// <summary>初始化 PocDatasetExecutionException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="innerException">innerException 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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
    // 预览页大小通常只有几十行；导出沿用它会让十万行数据产生数千次数据库往返。
    // 导出仍按批流式写入文件，因此增大批次不会把完整结果装入内存。
    private const int ExportPageSize = 4_096;

    private readonly PocReportConfigurationService _configurationService;
    private readonly PocExternalParameterSourceService? _externalParameters;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<PocSqlDatasetExecutor>? _logger;

    /// <summary>初始化 PocSqlDatasetExecutor 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="configurationService">configurationService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="externalParameters">externalParameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="environment">environment 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="logger">logger 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>调用 PocSqlDatasetExecutor 的异步依赖完成 Prepare Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>执行 PocSqlDatasetExecutor 的 Execute Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocExecuteResult> ExecuteAsync(
        string reportCode,
        PocExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareAsync(reportCode, request, cancellationToken);
        return await ExecuteAsync(plan, cancellationToken, _logger);
    }

    /// <summary>执行 PocSqlDatasetExecutor 的 Execute Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <param name="logger">logger 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="pageSizeOverride">pageSizeOverride 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static async Task<PocExecuteResult> ExecuteAsync(
        PocRuntimeExecutionPlan plan,
        CancellationToken cancellationToken = default,
        ILogger<PocSqlDatasetExecutor>? logger = null,
        int? pageSizeOverride = null)
    {
        if (plan.Configuration.UsesFallbackData)
        {
            return PocReportCatalog.Execute(plan.Query);
        }

        var definition = plan.Configuration.Definition;
        var preview = definition.Preview;
        // 第一阶段：构造 ExecuteAsync 当前步骤需要的中间数据和执行参数。
        var full = pageSizeOverride is null && preview.Mode == PocPreviewMode.Full;
        var pageSize = pageSizeOverride ?? preview.PageSize;
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSizeOverride));
        }
        var page = full ? 1 : Math.Max(1, plan.Query.Page);
        var take = full ? plan.Configuration.MaxRows + 1 : checked(pageSize + 1);
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
                full ? PocPreviewMode.Full : PocPreviewMode.Paged);
            foreach (var code in referenced)
            {
                var parameterDefinition = definition.Parameters.Single(
                    item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                parameters.Add(PocDatabaseProvider.CreateParameter(
                    code,
                    parameterDefinition.DataType,
                    plan.Parameters.DatabaseValues[code]));
            }
            // 第2阶段：检查 ExecuteAsync 当前条件，并按匹配分支执行对应处理。
            if (!full)
            {
                parameters.Add(PocDatabaseProvider.CreateParameter(
                    PocDatabaseProvider.PageOffsetParameterCode,
                    "int",
                    checked((page - 1) * pageSize)));
                parameters.Add(PocDatabaseProvider.CreateParameter(
                    PocDatabaseProvider.PageTakeParameterCode,
                    "int",
                    take));
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
            // 第3阶段：调用 ExecuteAsync 依赖的异步能力，等待并校验返回数据。
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

            var hasMore = rows.Count > (full ? plan.Configuration.MaxRows : pageSize);
            if (full && hasMore)
            {
                throw new PocDatasetExecutionException(
                    "PREVIEW_LIMIT_EXCEEDED",
                    $"完整预览超过 {plan.Configuration.MaxRows} 行限制。" );
            }
            if (!full && hasMore)
            {
                // 第4阶段：根据 ExecuteAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
                rows.RemoveAt(rows.Count - 1);
            }

            return new PocExecuteResult(
                page,
                full ? rows.Count : pageSize,
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

    /// <summary>
    /// 按导出批次读取完整结果。导出批次独立于预览页大小，避免几十行一页造成大量数据库往返。
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadExportPagesAsync(
        PocRuntimeExecutionPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!plan.Configuration.UsesFallbackData &&
            plan.Configuration.Definition.Preview.Mode == PocPreviewMode.Full)
        {
            await foreach (var page in ReadFullExportPagesAsync(plan, cancellationToken))
            {
                yield return page;
            }
            yield break;
        }

        for (var page = 1; ; page++)
        {
            var result = await ExecuteAsync(plan with
            {
                Query = plan.Query with { Page = page }
            }, cancellationToken, pageSizeOverride: ExportPageSize);
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

    /// <summary>Full 报表使用一次数据库查询和前向只读游标，避免 OFFSET 分页和重复执行 SQL。</summary>
    private static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadFullExportPagesAsync(
        PocRuntimeExecutionPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var definition = plan.Configuration.Definition;
        using var database = PocDatabaseProvider.CreateClient(
            plan.Configuration.DbType,
            plan.Configuration.ConnectionString,
            plan.Configuration.TimeoutSeconds,
            cancellationToken);
        var parameters = new List<SugarParameter>();
        // 第一阶段：遍历 ReadFullExportPagesAsync 当前数据集合，逐项转换并维护处理结果。
        var referenced = PocReadOnlySqlValidator.Validate(
            plan.Configuration.QueryText,
            definition.Parameters.Select(item => item.Code),
            PocPreviewMode.Full);
        foreach (var code in referenced)
        {
            var parameterDefinition = definition.Parameters.Single(
                item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            parameters.Add(PocDatabaseProvider.CreateParameter(
                code,
                parameterDefinition.DataType,
                plan.Parameters.DatabaseValues[code]));
        }

        DbDataReader reader;
        try
        {
            var dataReader = await database.Ado.GetDataReaderAsync(
                plan.Configuration.QueryText,
                parameters);
            reader = dataReader as DbDataReader
                ?? throw new PocDatasetExecutionException(
                    "DATASET_QUERY_FAILED",
                    "数据库驱动未返回可读取的数据集结果。");
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
        // 第2阶段：根据 ReadFullExportPagesAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            throw new PocDatasetExecutionException(
                PocDatabaseProvider.IsTimeout(exception) ? "DATASET_TIMEOUT" : "DATASET_QUERY_FAILED",
                "数据集查询失败。",
                exception);
        }

        await using (reader)
        {
            var fields = definition.Fields.ToArray();
            var ordinals = ResolveOrdinals(reader, fields);
            var rows = new List<PocDataRow>(ExportPageSize);
            while (true)
            {
                bool hasRow;
                try
                {
                    hasRow = await reader.ReadAsync(cancellationToken);
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

                if (!hasRow)
                // 第3阶段：根据 ReadFullExportPagesAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
                {
                    break;
                }

                var row = new PocDataRow();
                foreach (var field in fields)
                {
                    row[field.Code] = PocDatasetValueNormalizer.Normalize(
                        reader.GetValue(ordinals[field.Code]),
                        field);
                }
                rows.Add(row);
                if (rows.Count == ExportPageSize)
                {
                    yield return rows.ToArray();
                    rows.Clear();
                }
            }

            if (rows.Count > 0)
            {
                yield return rows.ToArray();
            }
        }
    }

    /// <summary>解析并计算 PocSqlDatasetExecutor 的 Resolve Ordinals 结果，处理必要的边界条件。</summary>
    /// <param name="reader">reader 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="fields">参与映射、显示或数据写入的字段集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>构建 PocSqlDatasetExecutor 所需的 Create 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="parameterizedSql">parameterizedSql 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>将 PocSqlDatasetExecutor 中的 Format Parameters For Log 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>根据 PocSqlDebugFormatter 当前输入计算 Replace Parameter 结果，并按方法声明的类型返回。</summary>
    /// <param name="sql">sql 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="parameter">parameter 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ReplaceParameter(string sql, SugarParameter parameter)
    {
        var pattern = $@"(?<![\w@]){Regex.Escape(parameter.ParameterName)}\b";
        return Regex.Replace(
            sql,
            pattern,
            _ => FormatValue(parameter.Value),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>将 PocSqlDatasetExecutor 中的 Format Value 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
