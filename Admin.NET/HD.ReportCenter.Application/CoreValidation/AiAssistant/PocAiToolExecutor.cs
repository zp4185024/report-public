using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>AI 工具执行的完整结果：既包含回传 LLM 的结构化内容，也包含审计所需的元数据。</summary>
public sealed record PocAiToolExecutionResult(
    string ToolName,
    string? DataSourceCode,
    string? SqlText,
    int? RowCount,
    int DurationMs,
    bool Success,
    string? ErrorSummary,
    string ContentJson);

/// <summary>工具被安全规则或配置拒绝时抛出；由执行器统一转换为结构化错误回传 LLM。</summary>
internal sealed class PocAiToolRejectException(string code, string message) : Exception(message)
{
    /// <summary>稳定拒绝码，如 SQL_PROBE_DISABLED、TABLE_NAME_INVALID。</summary>
    public string Code { get; } = code;
}

/// <summary>
/// AI 只读查数工具执行器：复用 RcDataSource 配置、PocDatabaseProvider 与 PocReadOnlySqlValidator 等现有机制，
/// 绝不为 AI 另建平行取数通道；连接串只在本类内部使用，任何输出（含错误）不得携带。
/// </summary>
public sealed class PocAiToolExecutor : ITransient
{
    /// <summary>单工具超时硬上限（秒）；实际超时取数据源 DefaultTimeoutSeconds 与本上限的较小值。</summary>
    public const int MaxToolTimeoutSeconds = 30;

    /// <summary>表/模式标识符的最严格白名单；不满足即拒绝，防止 AI 通过表名注入任意 SQL。</summary>
    private static readonly Regex IdentifierRegex = new(
        "^[A-Za-z_][A-Za-z0-9_#$]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly PocDataAssetStore _dataAssetStore;
    private readonly CreateDatasetClient _clientFactory;

    /// <summary>初始化 PocAiToolExecutor 实例；连接工厂可注入，测试用 SQLite 内存库替代真实业务库。</summary>
    /// <param name="dataAssetStore">全局数据源存储，用于按编码解析数据源配置。</param>
    /// <param name="clientFactory">目标数据源连接工厂；缺省使用统一的 PocDatabaseProvider.CreateClient。</param>
    public PocAiToolExecutor(
        PocDataAssetStore dataAssetStore,
        CreateDatasetClient? clientFactory = null)
    {
        _dataAssetStore = dataAssetStore;
        _clientFactory = clientFactory ?? PocDatabaseProvider.CreateClient;
    }

    /// <summary>执行一次 AI 工具调用：解析参数、执行对应只读查询并组织回传内容；一切失败都转为结构化错误结果而非异常外抛。</summary>
    /// <param name="settings">当前 AI 配置，提供采样/探测上限与 SqlProbeEnabled 开关。</param>
    /// <param name="toolName">LLM 请求的工具名；未注册的工具直接拒绝。</param>
    /// <param name="argumentsJson">LLM 给出的 JSON 字符串参数。</param>
    /// <param name="defaultDataSourceCode">会话绑定的默认数据源；工具参数未显式指定时使用。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>包含审计元数据与 LLM 回传内容的执行结果。</returns>
    public async Task<PocAiToolExecutionResult> ExecuteAsync(
        PocAiSettings settings,
        string toolName,
        string argumentsJson,
        string? defaultDataSourceCode,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? dataSourceCode = null;
        string? sqlText = null;
        int? rowCount = null;
        try
        {
            if (PocAiToolRegistry.IsSpecTool(toolName))
            {
                // 产物工具由对话服务处理（需要会话草稿与编译器）；只读执行器接到时给出明确防御性拒绝。
                throw new PocAiToolRejectException(
                    "AI_SPEC_TOOL_MISROUTED",
                    $"工具 '{toolName}' 属于产物交付工具，应由对话服务处理。");
            }
            if (!PocAiToolRegistry.IsRegistered(toolName))
            {
                throw new PocAiToolRejectException(
                    "AI_TOOL_NOT_REGISTERED",
                    $"工具 '{toolName}' 未注册，本阶段只支持只读查数与产物交付工具。");
            }

            var arguments = ParseArguments(argumentsJson);
            dataSourceCode = ResolveDataSourceCode(arguments, defaultDataSourceCode);
            PocAiToolExecutionResult result = toolName switch
            {
                PocAiToolRegistry.ListDataSources => await ListDataSourcesAsync(stopwatch),
                PocAiToolRegistry.ListTables => await ListTablesAsync(settings, arguments, dataSourceCode, stopwatch, cancellationToken),
                PocAiToolRegistry.DescribeTable => await DescribeTableAsync(arguments, dataSourceCode, stopwatch, cancellationToken),
                PocAiToolRegistry.SampleRows => await SampleRowsAsync(settings, arguments, dataSourceCode, stopwatch, cancellationToken),
                PocAiToolRegistry.RunReadonlySql => await RunReadonlySqlAsync(settings, arguments, dataSourceCode, stopwatch, cancellationToken),
                _ => throw new PocAiToolRejectException("AI_TOOL_NOT_REGISTERED", $"工具 '{toolName}' 未注册。")
            };
            return result;
        }
        // 拒绝类失败：安全规则或配置不足，错误码原样回传 LLM 让它改写调用。
        catch (PocAiToolRejectException exception)
        {
            stopwatch.Stop();
            return StructuredError(toolName, dataSourceCode, sqlText, rowCount, stopwatch, exception.Code, exception.Message);
        }
        // 只读校验器/配置类业务拒绝（DML、@参数、方言不支持等）：同样转结构化错误，不让异常打断会话。
        catch (PocDatasetConfigurationException exception)
        {
            stopwatch.Stop();
            return StructuredError(toolName, dataSourceCode, sqlText, rowCount, stopwatch, exception.Code, exception.Message);
        }
        // 数据库侧失败：复用连接错误净化逻辑，错误文本不携带连接串或密码。
        catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception)
            || PocDatabaseProvider.IsTimeout(exception))
        {
            stopwatch.Stop();
            return StructuredError(
                toolName,
                dataSourceCode,
                sqlText,
                rowCount,
                stopwatch,
                "QUERY_FAILED",
                PocDatabaseProvider.GetSafeConnectionError(exception));
        }
    }

    /// <summary>组织统一的失败结果：审计摘要携带错误码与说明，回传内容是结构化 JSON 供模型自行重试。</summary>
    /// <param name="toolName">工具名。</param>
    /// <param name="dataSourceCode">目标数据源编码。</param>
    /// <param name="sqlText">已构造的 SQL（可能为空）。</param>
    /// <param name="rowCount">已读取的行数（失败时常为空）。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="code">稳定错误码。</param>
    /// <param name="message">中文错误说明。</param>
    /// <returns>失败态执行结果。</returns>
    private static PocAiToolExecutionResult StructuredError(
        string toolName,
        string? dataSourceCode,
        string? sqlText,
        int? rowCount,
        Stopwatch stopwatch,
        string code,
        string message)
    {
        return new PocAiToolExecutionResult(
            toolName,
            dataSourceCode,
            sqlText,
            rowCount,
            (int)stopwatch.ElapsedMilliseconds,
            false,
            $"{code}: {message}",
            JsonConvert.SerializeObject(new Dictionary<string, object?>
            {
                ["error"] = code,
                ["message"] = message
            }, Formatting.None));
    }

    /// <summary>列出已启用数据源的编码/名称/类型；输出固定不含连接串等任何连接信息。</summary>
    /// <param name="stopwatch">调用耗时计时器，由入口方法创建并共享。</param>
    /// <returns>成功执行结果。</returns>
    private async Task<PocAiToolExecutionResult> ListDataSourcesAsync(Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var dataSources = await _dataAssetStore.ListDataSourcesAsync();
        var payload = dataSources
            .Where(item => item.IsEnabled)
            .Select(item => new Dictionary<string, object?>
            {
                ["code"] = item.Code,
                ["name"] = item.Name,
                ["dbType"] = item.DbType
            })
            .ToArray();
        return new PocAiToolExecutionResult(
            PocAiToolRegistry.ListDataSources,
            null,
            null,
            payload.Length,
            (int)stopwatch.ElapsedMilliseconds,
            true,
            null,
            JsonConvert.SerializeObject(new Dictionary<string, object?>
            {
                ["dataSources"] = payload,
                ["count"] = payload.Length
            }, Formatting.None));
    }

    /// <summary>列出目标数据源下的表清单（表名+注释）；结构信息按需用 describe_table 获取，避免上下文爆炸。</summary>
    /// <param name="settings">当前 AI 配置（保留参数以统一各工具签名）。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="dataSourceCode">目标数据源编码。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>成功执行结果。</returns>
    private async Task<PocAiToolExecutionResult> ListTablesAsync(
        PocAiSettings settings,
        JObject arguments,
        string? dataSourceCode,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        _ = settings;
        var dataSource = await RequireUsableDataSourceAsync(dataSourceCode);
        using var database = CreateToolClient(dataSource, cancellationToken);
        var tables = database.DbMaintenance.GetTableInfoList(false);
        stopwatch.Stop();
        var payload = tables
            .Select(item => new Dictionary<string, object?>
            {
                ["name"] = item.Name,
                ["comment"] = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description
            })
            .ToArray();
        return new PocAiToolExecutionResult(
            PocAiToolRegistry.ListTables,
            dataSource.Code,
            null,
            payload.Length,
            (int)stopwatch.ElapsedMilliseconds,
            true,
            null,
            JsonConvert.SerializeObject(new Dictionary<string, object?>
            {
                ["dataSource"] = dataSource.Code,
                ["tables"] = payload,
                ["count"] = payload.Length
            }, Formatting.None));
    }

    /// <summary>读取目标表的列结构；表名先做标识符校验，输出列名/数据库类型/可空/注释。</summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="dataSourceCode">目标数据源编码。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>成功执行结果。</returns>
    private async Task<PocAiToolExecutionResult> DescribeTableAsync(
        JObject arguments,
        string? dataSourceCode,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var tableName = RequireTableIdentifier(arguments, out var schema, out var table);
        var dataSource = await RequireUsableDataSourceAsync(dataSourceCode);
        using var database = CreateToolClient(dataSource, cancellationToken);
        var physicalTable = schema is null ? table : $"{schema}.{table}";
        var columns = database.DbMaintenance.GetColumnInfosByTableName(physicalTable, false);
        if (columns.Count == 0)
        {
            // 失败消息必须带自纠正线索：模型常按 SQL Server 习惯传 schema 前缀表名，
            // 提示其用 list_tables 返回的真实表名重试，避免误把 TABLE_NOT_FOUND 当作权限墙而放弃。
            throw new PocAiToolRejectException(
                "TABLE_NOT_FOUND",
                $"数据源 '{dataSource.Code}' 下不存在表 '{tableName}'。" +
                "这不是权限限制；请先调用 list_tables 获取该数据源的真实表名（SQL Server 表名通常不含 dbo. 架构前缀），再用返回的表名重试。");
        }

        stopwatch.Stop();
        var payload = columns
            .Select(item => new Dictionary<string, object?>
            {
                ["name"] = item.DbColumnName,
                ["dbType"] = item.DataType,
                ["length"] = item.Length > 0 ? item.Length : null,
                ["nullable"] = item.IsNullable,
                ["comment"] = string.IsNullOrWhiteSpace(item.ColumnDescription) ? null : item.ColumnDescription
            })
            .ToArray();
        return new PocAiToolExecutionResult(
            PocAiToolRegistry.DescribeTable,
            dataSource.Code,
            null,
            payload.Length,
            (int)stopwatch.ElapsedMilliseconds,
            true,
            null,
            JsonConvert.SerializeObject(new Dictionary<string, object?>
            {
                ["dataSource"] = dataSource.Code,
                ["table"] = tableName,
                ["columns"] = payload,
                ["count"] = payload.Length
            }, Formatting.None));
    }

    /// <summary>采样目标表前 N 行：采样 SQL 由本方法按方言生成 TOP/LIMIT 包装，AI 不参与 SQL 编写。</summary>
    /// <param name="settings">当前 AI 配置，决定采样上限；上限为 0 时等效禁用采样。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="dataSourceCode">目标数据源编码。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>成功执行结果。</returns>
    private async Task<PocAiToolExecutionResult> SampleRowsAsync(
        PocAiSettings settings,
        JObject arguments,
        string? dataSourceCode,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (settings.SampleRowLimit <= 0)
        {
            throw new PocAiToolRejectException(
                "SAMPLE_DISABLED",
                "采样功能已被管理员禁用（采样上限配置为 0）。");
        }

        var tableName = RequireTableIdentifier(arguments, out var schema, out var table);
        var dataSource = await RequireUsableDataSourceAsync(dataSourceCode);
        var limit = ClampRequestedLimit(
            arguments.Value<int?>("limit"),
            settings.SampleRowLimit,
            fallback: settings.SampleRowLimit);
        // 包装 SQL 多取一行用于探测“是否还有更多数据”；读取仍按 limit 截断，探测行立即丢弃不回传。
        var sql = BuildSampleQuery(dataSource.DbType, schema, table, limit + 1);

        using var database = CreateToolClient(dataSource, cancellationToken);
        var (columns, rows, capped) = await ReadRowsAsync(database, sql, limit, cancellationToken);
        stopwatch.Stop();
        return new PocAiToolExecutionResult(
            PocAiToolRegistry.SampleRows,
            dataSource.Code,
            sql,
            rows.Count,
            (int)stopwatch.ElapsedMilliseconds,
            true,
            null,
            SerializeRows(dataSource.Code, tableName, columns, rows, capped, limit));
    }

    /// <summary>执行 AI 给出的验证性只读 SQL：先过 PocReadOnlySqlValidator 全量校验，再按读取侧截断返回。</summary>
    /// <param name="settings">当前 AI 配置；SqlProbeEnabled=false 时整体拒绝。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="dataSourceCode">目标数据源编码。</param>
    /// <param name="stopwatch">调用耗时计时器。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>成功执行结果。</returns>
    private async Task<PocAiToolExecutionResult> RunReadonlySqlAsync(
        PocAiSettings settings,
        JObject arguments,
        string? dataSourceCode,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!settings.SqlProbeEnabled)
        {
            throw new PocAiToolRejectException(
                "SQL_PROBE_DISABLED",
                "只读 SQL 探测工具已被管理员禁用。");
        }

        var sql = (arguments.Value<string>("sql") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new PocAiToolRejectException("SQL_REQUIRED", "run_readonly_sql 必须提供 sql 参数。");
        }

        var dataSource = await RequireUsableDataSourceAsync(dataSourceCode);
        // 校验复用现有只读校验器；参数编码集合传空，等价于禁止 AI 使用 @参数，探测 SQL 只能写字面量。
        _ = PocReadOnlySqlValidator.Validate(sql, Array.Empty<string>(), PocPreviewMode.Full);
        var limit = ClampRequestedLimit(
            arguments.Value<int?>("limit"),
            settings.SqlProbeRowLimit,
            fallback: settings.SqlProbeRowLimit);

        using var database = CreateToolClient(dataSource, cancellationToken);
        // 读取侧截断：SQL 原样执行、不包装改写，读到上限即停止读取，避免方言包装破坏 CTE/ORDER BY 语义。
        var (columns, rows, capped) = await ReadRowsAsync(database, sql, limit, cancellationToken);
        stopwatch.Stop();
        return new PocAiToolExecutionResult(
            PocAiToolRegistry.RunReadonlySql,
            dataSource.Code,
            sql,
            rows.Count,
            (int)stopwatch.ElapsedMilliseconds,
            true,
            null,
            SerializeRows(dataSource.Code, null, columns, rows, capped, limit));
    }

    /// <summary>解析目标数据源配置；数据源不存在或已停用时统一按不存在拒绝，不给 AI 探测停用资源的机会。</summary>
    /// <param name="dataSourceCode">数据源编码。</param>
    /// <returns>已启用的数据源实体。</returns>
    private async Task<PocDataSource> RequireUsableDataSourceAsync(string? dataSourceCode)
    {
        if (string.IsNullOrWhiteSpace(dataSourceCode))
        {
            throw new PocAiToolRejectException(
                "DATA_SOURCE_REQUIRED",
                "必须指定数据源编码；可先调用 list_data_sources 获取可用数据源。");
        }

        var dataSource = await _dataAssetStore.GetDataSourceAsync(dataSourceCode.Trim());
        if (dataSource is null || !dataSource.IsEnabled)
        {
            throw new PocAiToolRejectException(
                "DATA_SOURCE_NOT_FOUND",
                $"不存在已启用的数据源 '{dataSourceCode}'。");
        }
        return dataSource;
    }

    /// <summary>校验并解析工具参数中的表名；只允许“schema.table”或“table”，且每段必须匹配标识符白名单。</summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="schema">解析出的 schema 段；无前缀时为 null。</param>
    /// <param name="table">解析出的表名段。</param>
    /// <returns>原始表名文本，供结果展示。</returns>
    private static string RequireTableIdentifier(JObject arguments, out string? schema, out string table)
    {
        schema = null;
        var raw = (arguments.Value<string>("tableName") ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new PocAiToolRejectException("TABLE_NAME_INVALID", "必须提供合法的表名。");
        }

        var parts = raw.Split('.');
        if (parts.Length > 2 ||
            parts.Any(part => !IdentifierRegex.IsMatch(part)))
        {
            throw new PocAiToolRejectException(
                "TABLE_NAME_INVALID",
                $"表名 '{raw}' 不符合标识符规范，只允许字母开头且由字母/数字/下划线组成，最多一段 schema 前缀。");
        }

        if (parts.Length == 2)
        {
            schema = parts[0];
            table = parts[1];
        }
        else
        {
            table = parts[0];
        }
        return raw;
    }

    /// <summary>解析工具调用参数 JSON；非法 JSON 按结构化拒绝处理，让模型自行重试。</summary>
    /// <param name="argumentsJson">LLM 给出的参数文本。</param>
    /// <returns>解析后的参数对象。</returns>
    private static JObject ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            return JObject.Parse(argumentsJson);
        }
        catch (JsonException exception)
        {
            throw new PocAiToolRejectException(
                "AI_TOOL_ARGUMENTS_INVALID",
                $"工具参数不是有效的 JSON：{exception.Message}");
        }
    }

    /// <summary>解析默认数据源：工具参数显式给出时以参数为准，否则回落到会话绑定的数据源。</summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="defaultDataSourceCode">会话绑定的默认数据源编码。</param>
    /// <returns>本次调用使用的数据源编码；可为空（由各工具再校验）。</returns>
    private static string? ResolveDataSourceCode(JObject arguments, string? defaultDataSourceCode)
    {
        var explicitCode = arguments.Value<string>("dataSourceCode");
        return string.IsNullOrWhiteSpace(explicitCode) ? defaultDataSourceCode : explicitCode.Trim();
    }

    /// <summary>把 AI 请求的行数钳制到管理员配置的上限内；未提供时使用默认值。</summary>
    /// <param name="requested">AI 请求的行数，可为空。</param>
    /// <param name="maxLimit">管理员配置的上限。</param>
    /// <param name="fallback">未提供时的默认行数。</param>
    /// <returns>钳制后的行数，至少为 1。</returns>
    private static int ClampRequestedLimit(int? requested, int maxLimit, int fallback)
    {
        var value = requested is > 0 ? requested.Value : fallback;
        return Math.Clamp(value, 1, Math.Max(1, maxLimit));
    }

    /// <summary>创建工具专用目标库客户端；超时取数据源配置与硬上限的较小值，并挂接取消令牌。</summary>
    /// <param name="dataSource">目标数据源配置。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>配置好超时的 SqlSugar 客户端。</returns>
    private SqlSugarClient CreateToolClient(PocDataSource dataSource, CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(dataSource.DefaultTimeoutSeconds, 1, MaxToolTimeoutSeconds);
        return _clientFactory(dataSource.DbType, dataSource.ConnectionString, timeoutSeconds, cancellationToken);
    }

    /// <summary>按方言生成采样 SQL；limit 由代码计算后内联，绝不接受 AI 提供的 SQL 片段。</summary>
    /// <param name="dbType">数据源数据库类型。</param>
    /// <param name="schema">可选 schema 段。</param>
    /// <param name="table">表名段。</param>
    /// <param name="limit">采样行数。</param>
    /// <returns>可直接执行的采样 SQL。</returns>
    public static string BuildSampleQuery(string dbType, string? schema, string table, int limit)
    {
        var normalized = PocDatabaseProvider.NormalizeType(dbType);
        var quoted = QuoteTable(normalized, schema, table);
        return normalized switch
        {
            PocDatabaseProvider.SqlServer => $"SELECT TOP ({limit.ToString(CultureInfo.InvariantCulture)}) * FROM {quoted}",
            PocDatabaseProvider.Oracle => $"SELECT * FROM {quoted} FETCH FIRST {limit.ToString(CultureInfo.InvariantCulture)} ROWS ONLY",
            _ => $"SELECT * FROM {quoted} LIMIT {limit.ToString(CultureInfo.InvariantCulture)}"
        };
    }

    /// <summary>按方言为表名加引号；段内容已通过标识符白名单校验，此处仅做方言包装。</summary>
    /// <param name="dbType">已规范化的数据库类型。</param>
    /// <param name="schema">可选 schema 段。</param>
    /// <param name="table">表名段。</param>
    /// <returns>加引号后的表引用。</returns>
    private static string QuoteTable(string dbType, string? schema, string table)
    {
        var quoted = dbType switch
        {
            PocDatabaseProvider.SqlServer => $"[{table}]",
            PocDatabaseProvider.MySql => $"`{table}`",
            _ => $"\"{table}\""
        };
        if (schema is null)
        {
            return quoted;
        }

        var quotedSchema = dbType switch
        {
            PocDatabaseProvider.SqlServer => $"[{schema}]",
            PocDatabaseProvider.MySql => $"`{schema}`",
            _ => $"\"{schema}\""
        };
        return $"{quotedSchema}.{quoted}";
    }

    /// <summary>执行只读查询并按上限读取行；多读一行仅用于判断截断，超出部分立即丢弃不回传。</summary>
    /// <param name="database">目标库客户端。</param>
    /// <param name="sql">已校验的只读 SQL。</param>
    /// <param name="limit">读取行数上限。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>列名、行数据和是否发生截断。</returns>
    private static async Task<(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Capped)>
        ReadRowsAsync(SqlSugarClient database, string sql, int limit, CancellationToken cancellationToken)
    {
        var dataReader = await database.Ado.GetDataReaderAsync(sql, new List<SugarParameter>());
        await using var reader = dataReader as DbDataReader
            ?? throw new PocAiToolRejectException("QUERY_FAILED", "数据库驱动未返回可读取的数据集结果。");

        var columns = new List<string>(reader.FieldCount);
        var fields = new List<PocDatasetFieldDefinition>(reader.FieldCount);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var code = reader.GetName(index);
            columns.Add(code);
            fields.Add(new PocDatasetFieldDefinition(code, code, InferFieldDataType(reader.GetFieldType(index))));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(limit);
        // 多读一行用于探测截断；该行立即丢弃，绝不进入回传内容。
        while (rows.Count <= limit && await reader.ReadAsync(cancellationToken))
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

        var capped = rows.Count > limit;
        if (capped)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return (columns, rows, capped);
    }

    /// <summary>把驱动返回的 CLR 列类型映射为契约数据类型，让数值/布尔/时间在 JSON 里保持原生形态。</summary>
    /// <param name="type">数据读取器报告的列 CLR 类型。</param>
    /// <returns>string/number/bool/datetime 类型名。</returns>
    private static string InferFieldDataType(Type type) =>
        type == typeof(bool) ? "bool" :
        type == typeof(DateTime) || type == typeof(DateTimeOffset) ? "datetime" :
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal)
            ? "number"
            : "string";

    /// <summary>组织查询类工具的回传 JSON：数据源、可选表名、列名、行数据与截断标记。</summary>
    /// <param name="dataSourceCode">目标数据源编码。</param>
    /// <param name="tableName">目标表名；SQL 探测类工具无表名传 null。</param>
    /// <param name="columns">返回列名。</param>
    /// <param name="rows">返回行数据。</param>
    /// <param name="capped">是否因行数上限被截断。</param>
    /// <param name="limit">本次生效的行数上限。</param>
    /// <returns>序列化后的 JSON 文本。</returns>
    private static string SerializeRows(
        string dataSourceCode,
        string? tableName,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        bool capped,
        int limit)
    {
        var payload = new Dictionary<string, object?>
        {
            ["dataSource"] = dataSourceCode,
            ["columns"] = columns,
            ["rows"] = rows,
            ["rowCount"] = rows.Count,
            ["limit"] = limit,
            ["truncated"] = capped
        };
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            payload["table"] = tableName;
        }
        if (capped)
        {
            payload["notice"] = $"结果已达 {limit} 行上限被截断，如需更多数据请收紧筛选条件。";
        }
        return JsonConvert.SerializeObject(payload, Formatting.None);
    }
}
