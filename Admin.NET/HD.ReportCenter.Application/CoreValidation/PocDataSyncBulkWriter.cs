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
    /// <param name="upsertKeyField">Upsert 策略的合并键列名；提供时建表 DDL 在该列上落 PRIMARY KEY，null 表示不落主键。</param>
    public async Task EnsureTableAsync(
        DbConnection connection,
        string targetDbType,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        CancellationToken cancellationToken = default,
        string? upsertKeyField = null)
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

        var createSql = BuildCreateTableSql(targetDbType, table, fields, upsertKeyField);
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
    /// upsertKeyField 非空时按方言分派合并通道（存在则覆盖、不存在则插入），
    /// 调用方保证键列值非空（执行器已把空键行按失败行剔除）。
    /// </summary>
    /// <param name="connection">已打开的目标库连接。</param>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">编译后的映射字段（决定列顺序与 DataTable 列类型）。</param>
    /// <param name="rows">本批已转换行，键为目标列名。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <param name="upsertKeyField">合并键目标列名；null 表示按普通批量写入。</param>
    /// <returns>成功写入的行数。</returns>
    public async Task<long> WriteBatchAsync(
        DbConnection connection,
        string targetDbType,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default,
        string? upsertKeyField = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateFields(fields);
        if (rows.Count == 0)
        {
            return 0;
        }

        // 第一阶段：按方言分派写入通道；SQLite 等兜底方言走事务内参数化 INSERT。
        var normalizedType = targetDbType.Trim();
        if (!string.IsNullOrWhiteSpace(upsertKeyField))
        {
            return normalizedType switch
            {
                // SqlServer/Oracle 无“插入或覆盖”的批量简易语法，走事务内逐行 MERGE。
                PocDatabaseProvider.SqlServer => await WriteWithSqlServerMergeAsync(
                    connection, table, fields, rows, upsertKeyField, cancellationToken),
                PocDatabaseProvider.Oracle => await WriteWithOracleMergeAsync(
                    connection, table, fields, rows, upsertKeyField, cancellationToken),
                PocDatabaseProvider.MySql => await WriteWithMySqlUpsertAsync(
                    connection, table, fields, rows, upsertKeyField, cancellationToken),
                PocDatabaseProvider.PostgreSql => await WriteWithNpgsqlUpsertAsync(
                    connection, table, fields, rows, upsertKeyField, cancellationToken),
                _ => await WriteWithUpsertFallbackAsync(
                    connection, table, fields, rows, upsertKeyField, cancellationToken),
            };
        }
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

    /// <summary>生成建表 DDL：列类型按方言映射，非空约束按映射 Nullable 推导；合并键列追加主键约束。</summary>
    /// <param name="targetDbType">目标库类型标识。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">编译后的映射字段。</param>
    /// <param name="upsertKeyField">合并键目标列名；命中映射列时该列 DDL 追加 PRIMARY KEY（四种方言与 SQLite 兜底均支持列内联主键），null 或不命中时不落主键。</param>
    /// <returns>可执行的 CREATE TABLE 文本。</returns>
    public string BuildCreateTableSql(
        string targetDbType,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        string? upsertKeyField = null)
    {
        ValidateFields(fields);
        var columns = fields.Select(field =>
        {
            var nullability = field.Nullable ? "NULL" : "NOT NULL";
            var primaryKey = !string.IsNullOrWhiteSpace(upsertKeyField) &&
                field.TargetField.Equals(upsertKeyField.Trim(), StringComparison.OrdinalIgnoreCase)
                ? " PRIMARY KEY"
                : string.Empty;
            return $"{QuoteIdentifier(targetDbType, field.TargetField)} {ToSqlType(targetDbType, field)} {nullability}{primaryKey}";
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

    /// <summary>读取行字典里某目标列的写入值；缺失或 null 统一收敛为 DBNull，保证参数绑定形状一致。</summary>
    /// <param name="row">已转换行。</param>
    /// <param name="field">映射字段。</param>
    /// <returns>列值或 DBNull.Value。</returns>
    private static object? ValueOf(IReadOnlyDictionary<string, object?> row, PocDataSyncCompiledField field) =>
        row.TryGetValue(field.TargetField, out var value) && value is not null ? value : DBNull.Value;

    /// <summary>按目标列名（忽略大小写）定位映射字段；调用方保证键列一定在映射内。</summary>
    /// <param name="fields">映射字段。</param>
    /// <param name="targetField">目标列名。</param>
    /// <returns>命中的映射字段。</returns>
    private static PocDataSyncCompiledField FieldOf(
        IReadOnlyList<PocDataSyncCompiledField> fields,
        string targetField) =>
        fields.First(field => field.TargetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 未知方言兜底合并通道：单事务内逐行“先 UPDATE 后 INSERT”。
    /// UPDATE 命中 0 行说明键不存在再走 INSERT；UPDATE 未命中不会污染事务，语义安全。
    /// 映射只有键列（无非键列）时退化为存在性探测：键已存在则保留原行不再写入。
    /// </summary>
    /// <param name="connection">已打开的目标连接。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="upsertKeyField">合并键目标列名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入（含覆盖）行数。</returns>
    private static async Task<long> WriteWithUpsertFallbackAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteTable("Fallback", table);
        var keyField = FieldOf(fields, upsertKeyField);
        var updateFields = fields
            .Where(field => !field.TargetField.Equals(upsertKeyField, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var quotedAllColumns = string.Join(
            ", ", fields.Select(field => QuoteIdentifier("Fallback", field.TargetField)));
        var insertSql = $"INSERT INTO {quotedTable} ({quotedAllColumns}) VALUES ({BindPlaceholders(fields.Count)})";
        // 单列表的 UPDATE 无 SET 项，退化为存在性探测；SET 占位在生成 SQL 时按有无非键列分支处理。
        var updateSql = updateFields.Length == 0
            ? $"SELECT COUNT(*) FROM {quotedTable} WHERE {QuoteIdentifier("Fallback", keyField.TargetField)} = @pk"
            : $"UPDATE {quotedTable} SET {string.Join(", ", updateFields.Select((field, index) => $"{QuoteIdentifier("Fallback", field.TargetField)} = @v{index}"))} " +
              $"WHERE {QuoteIdentifier("Fallback", keyField.TargetField)} = @pk";

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = updateSql;

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters.Clear();
                var updateAffected = 0;
                if (updateFields.Length == 0)
                {
                    // 存在性探测分支：键已存在即认为该行“已写入”，保持原值不覆盖。
                    command.Parameters.Add(BindParameter(command, "pk", ValueOf(row, keyField)));
                    updateAffected = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                }
                else
                {
                    command.Parameters.Add(BindParameter(command, "pk", ValueOf(row, keyField)));
                    for (var index = 0; index < updateFields.Length; index++)
                    {
                        command.Parameters.Add(BindParameter(command, $"v{index}", ValueOf(row, updateFields[index])));
                    }
                    updateAffected = await command.ExecuteNonQueryAsync(cancellationToken);
                }
                if (updateAffected > 0)
                {
                    continue;
                }
                command.Parameters.Clear();
                command.CommandText = insertSql;
                for (var index = 0; index < fields.Count; index++)
                {
                    command.Parameters.Add(BindParameter(command, $"p{index}", ValueOf(row, fields[index])));
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
                command.CommandText = updateSql;
            }
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// SqlServer 合并通道：事务内逐行 MERGE（USING 常量行按键匹配，命中覆盖非键列，未命中整行插入）。
    /// 增量窗口场景每批行数有限，逐行 MERGE 换取零临时表依赖与最简实现；映射只有键列时省略 WHEN MATCHED 分支。
    /// </summary>
    /// <param name="connection">目标连接（须为 SqlConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="upsertKeyField">合并键目标列名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入（含覆盖）行数。</returns>
    private static async Task<long> WriteWithSqlServerMergeAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteTable(PocDatabaseProvider.SqlServer, table);
        var keyField = FieldOf(fields, upsertKeyField);
        var updateFields = fields
            .Where(field => !field.TargetField.Equals(upsertKeyField, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var quotedAllColumns = string.Join(
            ", ", fields.Select(field => QuoteIdentifier(PocDatabaseProvider.SqlServer, field.TargetField)));
        var quotedKeyColumn = QuoteIdentifier(PocDatabaseProvider.SqlServer, keyField.TargetField);
        var updateClause = updateFields.Length == 0
            ? string.Empty
            : $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updateFields.Select((field, index) => $"tgt.{QuoteIdentifier(PocDatabaseProvider.SqlServer, field.TargetField)} = @v{index}"))} ";
        var mergeSql =
            $"MERGE INTO {quotedTable} AS tgt " +
            $"USING (SELECT @pk AS {quotedKeyColumn}) AS src " +
            $"ON tgt.{quotedKeyColumn} = src.{quotedKeyColumn} " +
            updateClause +
            $"WHEN NOT MATCHED THEN INSERT ({quotedAllColumns}) VALUES (@pk, {BindPlaceholders(fields.Count - 1, "p")});";

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = mergeSql;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters.Clear();
                // 参数按 SQL 占位符首现顺序绑定：先键值，再逐个非键列值；INSERT 段占位符跳过键列后从 p0 连续重编号。
                command.Parameters.Add(BindParameter(command, "pk", ValueOf(row, keyField)));
                for (var index = 0; index < updateFields.Length; index++)
                {
                    command.Parameters.Add(BindParameter(command, $"v{index}", ValueOf(row, updateFields[index])));
                }
                var insertParamIndex = 0;
                for (var index = 0; index < fields.Count; index++)
                {
                    if (fields[index].TargetField.Equals(upsertKeyField, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    command.Parameters.Add(BindParameter(command, $"p{insertParamIndex}", ValueOf(row, fields[index])));
                    insertParamIndex++;
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Oracle 合并通道：事务内逐行 MERGE（USING SELECT :pk FROM dual 按键匹配），语义与 SqlServer 通道一致。
    /// ODP.NET 命名绑定默认按位置，参数严格按 SQL 占位符首现顺序添加。
    /// </summary>
    /// <param name="connection">目标连接（须为 OracleConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="upsertKeyField">合并键目标列名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入（含覆盖）行数。</returns>
    private static async Task<long> WriteWithOracleMergeAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteTable(PocDatabaseProvider.Oracle, table);
        var keyField = FieldOf(fields, upsertKeyField);
        var updateFields = fields
            .Where(field => !field.TargetField.Equals(upsertKeyField, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var quotedAllColumns = string.Join(
            ", ", fields.Select(field => QuoteIdentifier(PocDatabaseProvider.Oracle, field.TargetField)));
        var quotedKeyColumn = QuoteIdentifier(PocDatabaseProvider.Oracle, keyField.TargetField);
        var updateClause = updateFields.Length == 0
            ? string.Empty
            : $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updateFields.Select((field, index) => $"tgt.{QuoteIdentifier(PocDatabaseProvider.Oracle, field.TargetField)} = :v{index}"))} ";
        var mergeSql =
            $"MERGE INTO {quotedTable} tgt " +
            $"USING (SELECT :pk AS {quotedKeyColumn} FROM dual) src " +
            $"ON (tgt.{quotedKeyColumn} = src.{quotedKeyColumn}) " +
            updateClause +
            $"WHEN NOT MATCHED THEN INSERT ({quotedAllColumns}) VALUES (:pk, {OracleBindPlaceholders(fields.Count - 1)})";

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = mergeSql;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters.Clear();
                command.Parameters.Add(BindParameter(command, "pk", ValueOf(row, keyField)));
                for (var index = 0; index < updateFields.Length; index++)
                {
                    command.Parameters.Add(BindParameter(command, $"v{index}", ValueOf(row, updateFields[index])));
                }
                for (var index = 0; index < fields.Count; index++)
                {
                    if (fields[index].TargetField.Equals(upsertKeyField, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    // Oracle 按位置绑定：INSERT VALUES 段的占位顺序是跳过键列后的映射顺序，参数名仅作集合内标识。
                    command.Parameters.Add(BindParameter(command, $"p{index}", ValueOf(row, fields[index])));
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// MySQL 合并通道：分块多行 INSERT ... ON DUPLICATE KEY UPDATE（每块单语句，整批一个事务）。
    /// 分块大小按“每块参数总数 ≤ 1000”收敛，规避 max_allowed_packet 与参数上限；仅键列表用自引用赋值保持原行。
    /// </summary>
    /// <param name="connection">目标连接（须为 MySqlConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="upsertKeyField">合并键目标列名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入（含覆盖）行数。</returns>
    private static async Task<long> WriteWithMySqlUpsertAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteTable(PocDatabaseProvider.MySql, table);
        var keyField = FieldOf(fields, upsertKeyField);
        var updateFields = fields
            .Where(field => !field.TargetField.Equals(upsertKeyField, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var quotedAllColumns = string.Join(
            ", ", fields.Select(field => QuoteIdentifier(PocDatabaseProvider.MySql, field.TargetField)));
        // VALUES() 形态在 MySQL 8+ 虽标记过时但全版本可用，兼容性优先于新别名语法。
        var updateClause = updateFields.Length == 0
            ? $"{QuoteIdentifier(PocDatabaseProvider.MySql, keyField.TargetField)} = {QuoteIdentifier(PocDatabaseProvider.MySql, keyField.TargetField)}"
            : string.Join(", ", updateFields.Select(field =>
                $"{QuoteIdentifier(PocDatabaseProvider.MySql, field.TargetField)} = VALUES({QuoteIdentifier(PocDatabaseProvider.MySql, field.TargetField)})"));
        var rowsPerStatement = Math.Max(1, 1000 / Math.Max(1, fields.Count));

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var offset = 0; offset < rows.Count; offset += rowsPerStatement)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = rows.Skip(offset).Take(rowsPerStatement).ToArray();
                var valuesClauses = string.Join(", ", chunk.Select((_, rowIndex) =>
                    $"({BindPlaceholders(fields.Count, $"r{rowIndex}_")})"));
                command.Parameters.Clear();
                command.CommandText =
                    $"INSERT INTO {quotedTable} ({quotedAllColumns}) VALUES {valuesClauses} ON DUPLICATE KEY UPDATE {updateClause}";
                for (var rowIndex = 0; rowIndex < chunk.Length; rowIndex++)
                {
                    foreach (var field in fields)
                    {
                        command.Parameters.Add(BindParameter(
                            command,
                            $"r{rowIndex}_{field.TargetField}",
                            ValueOf(chunk[rowIndex], field)));
                    }
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// PostgreSQL 合并通道：分块多行 INSERT ... ON CONFLICT DO UPDATE（整批一个事务）。
    /// 冲突目标显式声明合并键列，要求目标表在键列上存在唯一约束（Upsert 建表 DDL 已落主键，选现有表由库方保证）。
    /// </summary>
    /// <param name="connection">目标连接（须为 NpgsqlConnection）。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="fields">映射字段。</param>
    /// <param name="rows">已转换行。</param>
    /// <param name="upsertKeyField">合并键目标列名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入（含覆盖）行数。</returns>
    private static async Task<long> WriteWithNpgsqlUpsertAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string upsertKeyField,
        CancellationToken cancellationToken)
    {
        var quotedTable = QuoteTable(PocDatabaseProvider.PostgreSql, table);
        var keyField = FieldOf(fields, upsertKeyField);
        var updateFields = fields
            .Where(field => !field.TargetField.Equals(upsertKeyField, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var quotedAllColumns = string.Join(
            ", ", fields.Select(field => QuoteIdentifier(PocDatabaseProvider.PostgreSql, field.TargetField)));
        var quotedKeyColumn = QuoteIdentifier(PocDatabaseProvider.PostgreSql, keyField.TargetField);
        var conflictAction = updateFields.Length == 0
            ? "DO NOTHING"
            : $"DO UPDATE SET {string.Join(", ", updateFields.Select(field =>
                $"{QuoteIdentifier(PocDatabaseProvider.PostgreSql, field.TargetField)} = EXCLUDED.{QuoteIdentifier(PocDatabaseProvider.PostgreSql, field.TargetField)}"))}";
        var rowsPerStatement = Math.Max(1, 8000 / Math.Max(1, fields.Count));

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var offset = 0; offset < rows.Count; offset += rowsPerStatement)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = rows.Skip(offset).Take(rowsPerStatement).ToArray();
                var valuesClauses = string.Join(", ", chunk.Select((_, rowIndex) =>
                    $"({BindPlaceholders(fields.Count, $"r{rowIndex}_")})"));
                command.Parameters.Clear();
                command.CommandText =
                    $"INSERT INTO {quotedTable} ({quotedAllColumns}) VALUES {valuesClauses} ON CONFLICT ({quotedKeyColumn}) {conflictAction}";
                for (var rowIndex = 0; rowIndex < chunk.Length; rowIndex++)
                {
                    foreach (var field in fields)
                    {
                        command.Parameters.Add(BindParameter(
                            command,
                            $"r{rowIndex}_{field.TargetField}",
                            ValueOf(chunk[rowIndex], field)));
                    }
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>生成 @前缀 位置占位符序列：@p0, @p1, ... 供 INSERT VALUES 段使用。</summary>
    /// <param name="count">占位符个数。</param>
    /// <param name="prefix">占位符前缀，默认 p。</param>
    /// <returns>逗号分隔的占位符文本；count ≤ 0 返回空串。</returns>
    private static string BindPlaceholders(int count, string prefix = "p") =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => $"@{prefix}{index}"));

    /// <summary>生成 Oracle :前缀 位置占位符序列：:p0, :p1, ...。</summary>
    /// <param name="count">占位符个数。</param>
    /// <returns>逗号分隔的占位符文本；count ≤ 0 返回空串。</returns>
    private static string OracleBindPlaceholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => $":p{index}"));

    /// <summary>创建并绑定一个方言无关的命令参数：参数名 + 已收敛的列值。</summary>
    /// <param name="command">目标命令。</param>
    /// <param name="name">参数名（不含前缀符号）。</param>
    /// <param name="value">列值（null 已收敛为 DBNull）。</param>
    /// <returns>已加入参数集合前的参数对象。</returns>
    private static DbParameter BindParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }
}
