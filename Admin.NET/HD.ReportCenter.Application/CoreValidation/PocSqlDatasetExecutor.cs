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

/// <summary>
/// 临时数据集受限试跑的结果：列编码按驱动返回顺序排列，行值已经过统一归一化；
/// RowCountCapped 表示真实结果超过行数上限被截断，调用方应提示“仅预览前 N 行”。
/// </summary>
public sealed record PocPreviewExecutionResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool RowCountCapped);

/// <summary>
/// 数据集执行器的连接创建委托：默认走 PocDatabaseProvider.CreateClient；
/// 数据同步等旁路能力可注入自己的工厂以绑定非标准方言（如测试用 SQLite 内存库）。
/// </summary>
/// <param name="dbType">数据源声明的数据库类型。</param>
/// <param name="connectionString">数据源连接串。</param>
/// <param name="timeoutSeconds">命令超时秒数。</param>
/// <param name="cancellationToken">用于取消异步操作的令牌。</param>
/// <returns>可执行 SQL 的 SqlSugar 客户端。</returns>
public delegate SqlSugarClient CreateDatasetClient(
    string dbType,
    string connectionString,
    int timeoutSeconds,
    CancellationToken cancellationToken);

/// <summary>多数据库只读数据集执行器。配置读取与 SQL 执行分离，便于导出任务冻结运行输入。</summary>
public sealed class PocSqlDatasetExecutor : ITransient
{
    /// <summary>默认连接创建策略：全部走统一的 PocDatabaseProvider，保证方言与超时规则一致。</summary>
    private static readonly CreateDatasetClient DefaultClientFactory = PocDatabaseProvider.CreateClient;
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
                        PocDatasetValueNormalizer.ReadColumnValue(reader, ordinals[field.Code]),
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
    /// 按功能阶段组织：<see cref="ExecutePreviewAsync"/> 是为报表包试跑提供的最小重载：
    /// 针对"尚未入库的临时数据集 + 调用方指定的数据源"执行原样只读查询，不做分页改写，
    /// 参数绑定、连接创建与结果归一化全部复用正式执行机制，避免复制执行逻辑产生第二套语义。
    /// </summary>
    /// <param name="plan">调用方用包内容与目标数据源拼装出的临时执行计划。</param>
    /// <param name="rowLimit">硬性行数上限（试跑固定 50），结果超限被截断而不是报错。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <param name="clientFactory">可选连接工厂；缺省使用 PocDatabaseProvider。</param>
    /// <returns>列编码、行数据（以列编码为键）和是否发生截断。</returns>
    public static async Task<PocPreviewExecutionResult> ExecutePreviewAsync(
        PocRuntimeExecutionPlan plan,
        int rowLimit,
        CancellationToken cancellationToken = default,
        CreateDatasetClient? clientFactory = null)
    {
        if (rowLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rowLimit));
        }
        if (plan.Configuration.UsesFallbackData)
        {
            // 试跑必须打到真实目标库；回退演示数据会给出错误的可用性结论。
            throw new PocDatasetExecutionException(
                "PACKAGE_DRY_RUN_TARGET_REQUIRED",
                "试跑必须指定真实的目标数据源。");
        }

        var definition = plan.Configuration.Definition;
        var parameters = new List<SugarParameter>();
        // 第一阶段：复用只读校验器解析 SQL 引用的参数并逐一绑定，确保试跑与正式执行同一条防线。
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

        try
        {
            using var database = (clientFactory ?? DefaultClientFactory)(
                plan.Configuration.DbType,
                plan.Configuration.ConnectionString,
                plan.Configuration.TimeoutSeconds,
                cancellationToken);
            var dataReader = await database.Ado.GetDataReaderAsync(
                plan.Configuration.QueryText.Trim(),
                parameters);
            await using var reader = dataReader as DbDataReader
                ?? throw new PocDatasetExecutionException(
                    "DATASET_QUERY_FAILED",
                    "数据库驱动未返回可读取的数据集结果。");

            // 第二阶段：按驱动返回的列构造字段契约并逐行归一化，多读一行用于判断是否截断。
            var columns = new List<string>(reader.FieldCount);
            var fields = new List<PocDatasetFieldDefinition>(reader.FieldCount);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var code = reader.GetName(index);
                columns.Add(code);
                fields.Add(new PocDatasetFieldDefinition(
                    code,
                    code,
                    InferPreviewFieldDataType(reader.GetFieldType(index))));
            }

            var rows = new List<IReadOnlyDictionary<string, object?>>(rowLimit + 1);
            while (rows.Count <= rowLimit && await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[columns[index]] = PocDatasetValueNormalizer.Normalize(
                        PocDatasetValueNormalizer.ReadColumnValue(reader, index),
                        fields[index]);
                }
                rows.Add(row);
            }

            var capped = rows.Count > rowLimit;
            if (capped)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            return new PocPreviewExecutionResult(columns, rows, capped);
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
                "数据集试跑查询失败。",
                exception);
        }
    }

    /// <summary>
    /// 把驱动返回的 CLR 列类型映射到数据集字段契约的数据类型，让试跑行内的数值、
    /// 布尔和日期在 JSON 里保持原生标量形态而不是全部字符串化。
    /// </summary>
    /// <param name="type">数据读取器报告的列 CLR 类型。</param>
    /// <returns>字段契约使用的 string/number/bool/datetime 类型名。</returns>
    private static string InferPreviewFieldDataType(Type type) =>
        type == typeof(bool) ? "bool" :
        type == typeof(DateTime) || type == typeof(DateTimeOffset) ? "datetime" :
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal)
            ? "number"
            : "string";

    /// <summary>
    /// 按导出批次读取完整结果。导出批次独立于预览页大小，避免几十行一页造成大量数据库往返。
    /// </summary>
    /// <param name="plan">报表执行计划。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <param name="clientFactory">可选连接工厂；缺省使用 PocDatabaseProvider。</param>
    public static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadExportPagesAsync(
        PocRuntimeExecutionPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        CreateDatasetClient? clientFactory = null)
    {
        if (!plan.Configuration.UsesFallbackData &&
            plan.Configuration.Definition.Preview.Mode == PocPreviewMode.Full)
        {
            await foreach (var page in ReadFullExportPagesAsync(plan, cancellationToken, clientFactory))
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
    /// <param name="plan">报表执行计划。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <param name="clientFactory">可选连接工厂；缺省使用 PocDatabaseProvider。</param>
    private static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadFullExportPagesAsync(
        PocRuntimeExecutionPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        CreateDatasetClient? clientFactory = null)
    {
        var definition = plan.Configuration.Definition;
        using var database = (clientFactory ?? DefaultClientFactory)(
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
                        PocDatasetValueNormalizer.ReadColumnValue(reader, ordinals[field.Code]),
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
