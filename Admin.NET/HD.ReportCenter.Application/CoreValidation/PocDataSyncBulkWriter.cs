using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 同步目标端写入器：按目标库方言分派原生批量通道，并把建表、清空、批量写入收敛到一处。
/// SqlServer 走 SqlBulkCopy、Oracle 走 OracleBulkCopy、MySql 走 MySqlBulkCopy、PostgreSQL 走二进制 COPY，
/// 其余数据库（如测试用 SQLite）退回事务内参数化 INSERT，保证链路在任意连接上可验证。
/// 写入器只消费调用方传入的打开连接，不负责连接生命周期。
/// </summary>
public sealed class PocDataSyncBulkWriter
{
    /// <summary>每个批量事务/批量通道处理的行数；千万级数据下 10000 行一批兼顾吞吐与事务日志压力。</summary>
    public const int DefaultBatchSize = 10_000;

    /// <summary>字符串目标列未显式配置长度时的默认长度，覆盖多数编码/名称类字段。</summary>
    private const int DefaultStringLength = 256;

    /// <summary>字符串列长度上限；Oracle VARCHAR2 与 SQL Server NVARCHAR 在常规模式下都是 4000 边界。</summary>
    private const int MaxStringLength = 4_000;

    /// <summary>
    /// 探测目标表是否存在并在缺失时按映射生成方言 DDL 建表。
    /// 探测通过一次 COUNT 查询尝试实现，任何数据库错误都视为“表缺失”转入建表分支。
    /// </summary>
    /// <param name="connection">已打开的目标库连接。</param>
    /// <param name="targetDbType">目标库类型标识（四种受支持库或其他方言）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">编译后的映射字段，用于生成列 DDL。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    public async Task EnsureTableAsync(
        DbConnection connection,
        string targetDbType,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        CancellationToken cancellationToken = default)
    {
        ValidateFields(fields);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteTable(targetDbType, table)}";
        command.CommandTimeout = 15;
        try
        {
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return;
        }
        catch (DbException)
        {
            // 探测失败按表缺失处理：真实连接故障会在随后的建表/写入阶段再次暴露并按失败收场。
        }

        var createSql = BuildCreateTableSql(targetDbType, table, fields);
        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = createSql;
        createCommand.CommandTimeout = 60;
        await createCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 按冲突策略清空目标表：四种受支持库使用 TRUNCATE TABLE（快速释放并重置表），
    /// 其他方言（SQLite 等）退回 DELETE FROM。是否清空由调用方按冲突策略决定。
    /// </summary>
    /// <param name="connection">已打开的目标库连接。</param>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    public async Task TruncateAsync(
        DbConnection connection,
        string targetDbType,
        string table,
        CancellationToken cancellationToken = default)
    {
        var quotedTable = QuoteTable(targetDbType, table);
        var statement = IsSupportedBulkTarget(targetDbType)
            ? $"TRUNCATE TABLE {quotedTable}"
            : $"DELETE FROM {quotedTable}";
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 把一批已转换行写入目标表。行字典以目标列名为键，值已完成目标类型收敛；
    /// 内部按方言分派批量通道，返回成功写入的行数。
    /// </summary>
    /// <param name="connection">已打开的目标库连接。</param>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">编译后的映射字段（决定列顺序与 DataTable 列类型）。</param>
    /// <param name="rows">本批已转换行，键为目标列名。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>成功写入的行数。</returns>
    public async Task<long> WriteBatchAsync(
        DbConnection connection,
        string targetDbType,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateFields(fields);
        if (rows.Count == 0)
        {
            return 0;
        }

        // 第一阶段：按方言分派写入通道；SQLite 等兜底方言走事务内参数化 INSERT。
        var normalizedType = targetDbType.Trim();
        switch (normalizedType)
        {
            case PocDatabaseProvider.SqlServer:
                await WriteWithSqlBulkCopyAsync(connection, table, fields, rows, cancellationToken);
                return rows.Count;
            case PocDatabaseProvider.Oracle:
                await WriteWithOracleBulkCopyAsync(connection, table, fields, rows, cancellationToken);
                return rows.Count;
            case PocDatabaseProvider.MySql:
                await WriteWithMySqlBulkCopyAsync(connection, table, fields, rows, cancellationToken);
                return rows.Count;
            case PocDatabaseProvider.PostgreSql:
                await WriteWithNpgsqlCopyAsync(connection, table, fields, rows, cancellationToken);
                return rows.Count;
            default:
                return await WriteWithInsertFallbackAsync(connection, table, fields, rows, cancellationToken);
        }
    }

    /// <summary>生成建表 DDL：列类型按方言映射，非空约束按映射 Nullable 推导。</summary>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">编译后的映射字段。</param>
    /// <returns>可执行的 CREATE TABLE 文本。</returns>
    public string BuildCreateTableSql(
        string targetDbType,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields)
    {
        ValidateFields(fields);
        var columns = fields.Select(field =>
        {
            var nullability = field.Nullable ? "NULL" : "NOT NULL";
            return $"{QuoteIdentifier(targetDbType, field.TargetField)} {ToSqlType(targetDbType, field)} {nullability}";
        });
        return $"CREATE TABLE {QuoteTable(targetDbType, table)} ({string.Join(", ", columns)})";
    }

    /// <summary>把目标类型映射为具体方言的列类型文本；方言差异全部收敛在此处。</summary>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="field">编译后的映射字段。</param>
    /// <returns>该方言的列类型文本。</returns>
    private static string ToSqlType(string targetDbType, PocDataSyncCompiledField field)
    {
        // 第一阶段：解析长度默认值与上限，字符串类型在四种库上都有 4000 边界需要钳制。
        var length = Math.Clamp(field.Length ?? DefaultStringLength, 1, MaxStringLength);
        // 第二阶段：按目标类型分支映射方言类型，未列举的方言（SQLite 等）退回通用 NUMERIC/TEXT。
        switch (field.TargetType)
        {
            case "number":
                return targetDbType switch
                {
                    PocDatabaseProvider.SqlServer => "BIGINT",
                    PocDatabaseProvider.Oracle => "NUMBER",
                    PocDatabaseProvider.MySql => "BIGINT",
                    PocDatabaseProvider.PostgreSql => "BIGINT",
                    _ => "NUMERIC"
                };
            case "decimal":
                return targetDbType switch
                {
                    PocDatabaseProvider.SqlServer => "DECIMAL(28, 8)",
                    PocDatabaseProvider.Oracle => "NUMBER(28, 8)",
                    PocDatabaseProvider.MySql => "DECIMAL(28, 8)",
                    PocDatabaseProvider.PostgreSql => "NUMERIC(28, 8)",
                    _ => "NUMERIC"
                };
            case "datetime":
                return targetDbType switch
                {
                    PocDatabaseProvider.SqlServer => "DATETIME2",
                    PocDatabaseProvider.Oracle => "TIMESTAMP",
                    PocDatabaseProvider.MySql => "DATETIME",
                    PocDatabaseProvider.PostgreSql => "TIMESTAMPTZ",
                    _ => "TEXT"
                };
            case "bool":
                return targetDbType switch
                {
                    PocDatabaseProvider.SqlServer => "BIT",
                    PocDatabaseProvider.Oracle => "NUMBER(1)",
                    PocDatabaseProvider.MySql => "TINYINT(1)",
                    PocDatabaseProvider.PostgreSql => "BOOLEAN",
                    _ => "NUMERIC"
                };
            default:
                return targetDbType switch
                {
                    PocDatabaseProvider.SqlServer => $"NVARCHAR({length})",
                    PocDatabaseProvider.Oracle => $"VARCHAR2({length} CHAR)",
                    PocDatabaseProvider.MySql => $"VARCHAR({length})",
                    PocDatabaseProvider.PostgreSql => $"VARCHAR({length})",
                    _ => "TEXT"
                };
        }
    }

    /// <summary>判断是否为四种走原生批量通道的受支持目标库。</summary>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <returns>受支持返回 true。</returns>
    private static bool IsSupportedBulkTarget(string targetDbType) =>
        targetDbType.Equals(PocDatabaseProvider.SqlServer, StringComparison.OrdinalIgnoreCase) ||
        targetDbType.Equals(PocDatabaseProvider.Oracle, StringComparison.OrdinalIgnoreCase) ||
        targetDbType.Equals(PocDatabaseProvider.MySql, StringComparison.OrdinalIgnoreCase) ||
        targetDbType.Equals(PocDatabaseProvider.PostgreSql, StringComparison.OrdinalIgnoreCase);

    /// <summary>按方言给标识符（表名/列名）加引用符并转义内嵌引用字符，杜绝特殊字符破坏 SQL。</summary>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="identifier">原始标识符。</param>
    /// <returns>带方言引用符的标识符。</returns>
    private static string QuoteIdentifier(string targetDbType, string identifier)
    {
        var safe = identifier.Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("`", "``", StringComparison.Ordinal)
            .Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "]]", StringComparison.Ordinal);
        return targetDbType switch
        {
            PocDatabaseProvider.SqlServer => $"[{safe}]",
            PocDatabaseProvider.MySql => $"`{safe}`",
            _ => $"\"{safe}\""
        };
    }

    /// <summary>按方言给表名加引用符，统一走标识符引用规则。</summary>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="table">原始表名。</param>
    /// <returns>带方言引用符的表名。</returns>
    private static string QuoteTable(string targetDbType, string table) =>
        QuoteIdentifier(targetDbType, table);

    /// <summary>校验映射字段：目标列名与列顺序是写入契约，空集合与重复列直接拒绝。</summary>
    /// <param name="fields">编译后的映射字段。</param>
    private static void ValidateFields(IReadOnlyList<PocDataSyncCompiledField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INCOMPLETE",
                "同步任务没有配置任何字段映射，不能写入目标表。");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.TargetField) || !seen.Add(field.TargetField))
            {
                throw new PocDataSyncException(
                    "SYNC_MAPPING_INCOMPLETE",
                    $"字段映射存在空目标列或重复目标列 '{field.TargetField}'。");
            }
        }
    }

    /// <summary>把一批行构造成类型化 DataTable，供各原生批量通道消费。</summary>
    /// <param name="fields">编译后的映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <returns>列名与目标列一致、列类型与目标类型对应的 DataTable。</returns>
    private static DataTable BuildBatchTable(
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var dataTable = new DataTable();
        foreach (var field in fields)
        {
            dataTable.Columns.Add(field.TargetField, ResolveClrType(field.TargetType));
        }
        foreach (var row in rows)
        {
            var dataRow = dataTable.NewRow();
            foreach (var field in fields)
            {
                dataRow[field.TargetField] = row.TryGetValue(field.TargetField, out var value) && value is not null
                    ? value
                    : DBNull.Value;
            }
            dataTable.Rows.Add(dataRow);
        }
        return dataTable;
    }

    /// <summary>目标类型到 DataTable 列 CLR 类型的映射，与转换器终值收敛保持一致。</summary>
    /// <param name="targetType">目标类型名。</param>
    /// <returns>对应 CLR 类型。</returns>
    private static Type ResolveClrType(string targetType) => targetType switch
    {
        "number" => typeof(long),
        "decimal" => typeof(decimal),
        "datetime" => typeof(DateTime),
        "bool" => typeof(bool),
        _ => typeof(string)
    };

    /// <summary>SQL Server 批量通道：SqlBulkCopy 按列名映射写入。</summary>
    /// <param name="connection">目标连接（须为 SqlConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task WriteWithSqlBulkCopyAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        if (connection is not SqlConnection sqlConnection)
        {
            throw new PocDataSyncException(
                "SYNC_TARGET_CONNECTION_INVALID",
                "目标数据源类型为 SqlServer 时连接必须是 SqlConnection。");
        }
        using var bulkCopy = new SqlBulkCopy(sqlConnection)
        {
            DestinationTableName = QuoteTable(PocDatabaseProvider.SqlServer, table),
            BulkCopyTimeout = 0
        };
        foreach (var field in fields)
        {
            bulkCopy.ColumnMappings.Add(field.TargetField, field.TargetField);
        }
        using var dataTable = BuildBatchTable(fields, rows);
        await bulkCopy.WriteToServerAsync(dataTable, cancellationToken);
    }

    /// <summary>Oracle 批量通道：ODP.NET 的 OracleBulkCopy 仅有同步 API，包装进线程池执行统一异步边界。</summary>
    /// <param name="connection">目标连接（须为 OracleConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task WriteWithOracleBulkCopyAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        if (connection is not OracleConnection oracleConnection)
        {
            throw new PocDataSyncException(
                "SYNC_TARGET_CONNECTION_INVALID",
                "目标数据源类型为 Oracle 时连接必须是 OracleConnection。");
        }

        // DataTable 构建与 OracleBulkCopy 都属于同步阻塞调用；让出到线程池避免占用调用方线程。
        await Task.Run(
            () =>
            {
                using var dataTable = BuildBatchTable(fields, rows);
                using var bulkCopy = new OracleBulkCopy(oracleConnection)
                {
                    DestinationTableName = QuoteTable(PocDatabaseProvider.Oracle, table)
                };
                foreach (var field in fields)
                {
                    bulkCopy.ColumnMappings.Add(field.TargetField, field.TargetField);
                }
                bulkCopy.WriteToServer(dataTable);
            },
            cancellationToken);
    }

    /// <summary>MySQL 批量通道：MySqlBulkCopy 按源列序号→目标列名映射写入。</summary>
    /// <param name="connection">目标连接（须为 MySqlConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task WriteWithMySqlBulkCopyAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        if (connection is not MySqlConnection mySqlConnection)
        {
            throw new PocDataSyncException(
                "SYNC_TARGET_CONNECTION_INVALID",
                "目标数据源类型为 MySql 时连接必须是 MySqlConnection。");
        }
        var bulkCopy = new MySqlBulkCopy(mySqlConnection, null)
        {
            DestinationTableName = QuoteTable(PocDatabaseProvider.MySql, table)
        };
        for (var index = 0; index < fields.Count; index++)
        {
            // DataTable 列名与目标列一致，按“源列名→目标列名”映射最直观且不受列序影响。
            bulkCopy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(
                index,
                fields[index].TargetField,
                fields[index].TargetField));
        }
        using var dataTable = BuildBatchTable(fields, rows);
        await bulkCopy.WriteToServerAsync(dataTable, cancellationToken);
    }

    /// <summary>PostgreSQL 批量通道：Npgsql 二进制 COPY FROM STDIN，流式逐行写入。</summary>
    /// <param name="connection">目标连接（须为 NpgsqlConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task WriteWithNpgsqlCopyAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
        {
            throw new PocDataSyncException(
                "SYNC_TARGET_CONNECTION_INVALID",
                "目标数据源类型为 PostgreSQL 时连接必须是 NpgsqlConnection。");
        }
        var columns = string.Join(
            ", ",
            fields.Select(field => QuoteIdentifier(PocDatabaseProvider.PostgreSql, field.TargetField)));
        var copyStatement =
            $"COPY {QuoteTable(PocDatabaseProvider.PostgreSql, table)} ({columns}) FROM STDIN (FORMAT BINARY)";
        await using var importer = npgsqlConnection.BeginBinaryImport(copyStatement);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await importer.StartRowAsync(cancellationToken);
            foreach (var field in fields)
            {
                var value = row.TryGetValue(field.TargetField, out var raw) && raw is not null
                    ? raw
                    : DBNull.Value;
                await importer.WriteAsync(value, ResolveNpgsqlDbType(field.TargetType), cancellationToken);
            }
        }
        await importer.CompleteAsync(cancellationToken);
    }

    /// <summary>目标类型到 NpgsqlDbType 的映射，保证二进制 COPY 的线格式与目标列一致。</summary>
    /// <param name="targetType">目标类型名。</param>
    /// <returns>对应的 NpgsqlDbType。</returns>
    private static NpgsqlTypes.NpgsqlDbType ResolveNpgsqlDbType(string targetType) => targetType switch
    {
        "number" => NpgsqlTypes.NpgsqlDbType.Bigint,
        "decimal" => NpgsqlTypes.NpgsqlDbType.Numeric,
        "datetime" => NpgsqlTypes.NpgsqlDbType.TimestampTz,
        "bool" => NpgsqlTypes.NpgsqlDbType.Boolean,
        _ => NpgsqlTypes.NpgsqlDbType.Text
    };

    /// <summary>
    /// 未知方言兜底通道：单事务内逐行参数化 INSERT。SQLite 参数上限约 999，
    /// 逐行插值天然规避参数个数限制；事务保证整批原子性。
    /// </summary>
    /// <param name="connection">已打开的目标连接。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入行数。</returns>
    private static async Task<long> WriteWithInsertFallbackAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteTable("Fallback", table);
        var columns = string.Join(
            ", ",
            fields.Select(field => QuoteIdentifier("Fallback", field.TargetField)));
        var parameterNames = string.Join(
            ", ",
            fields.Select((field, index) => $"@p{index}"));
        var insertSql = $"INSERT INTO {quotedTable} ({columns}) VALUES ({parameterNames})";

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // 第一阶段：准备共享命令对象，语句只编译一次，逐行复用并替换参数集合。
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = insertSql;

            // 第二阶段：逐行绑定参数并执行；全部成功才提交事务，任一行失败整批回滚交由上层定性。
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters.Clear();
                for (var index = 0; index < fields.Count; index++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@p{index}";
                    var value = row.TryGetValue(fields[index].TargetField, out var raw) && raw is not null
                        ? raw
                        : DBNull.Value;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch
        {
            // 第三阶段：失败回滚，保证目标表不会留下半批数据。
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
