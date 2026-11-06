using System.Data.Common;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>单次同步执行需要的全部运行输入：源计划 + 目标描述 + 编译好的字段委托。</summary>
/// <param name="SourcePlan">源数据集执行计划（复用 PocSqlDatasetExecutor 的取数管线）。</param>
/// <param name="TargetDbType">目标库类型标识。</param>
/// <param name="TargetConnectionString">目标库连接串。</param>
/// <param name="TargetTimeoutSeconds">目标库命令超时秒数。</param>
/// <param name="TargetTable">目标表名。</param>
/// <param name="ConflictPolicy">冲突策略：TruncateFirst 清空后写入，其余直接追加。</param>
/// <param name="Fields">编译后的映射字段（含转换委托与目标类型）。</param>
/// <param name="RowLimit">可选行数上限；仅受限验证场景使用，正式跑保持 null 不限行数。</param>
public sealed record PocDataSyncExecutionInput(
    PocRuntimeExecutionPlan SourcePlan,
    string TargetDbType,
    string TargetConnectionString,
    int TargetTimeoutSeconds,
    string TargetTable,
    string ConflictPolicy,
    IReadOnlyList<PocDataSyncCompiledField> Fields,
    int? RowLimit = null);

/// <summary>一次真实同步的统计结果；失败明细封顶 200 条，完整明细写入运行日志。</summary>
/// <param name="RowsRead">源端读取的总行数。</param>
/// <param name="RowsWritten">成功写入目标表的总行数。</param>
/// <param name="RowsFailed">转换失败被跳过的行数。</param>
/// <param name="Failures">失败行明细（封顶 200 条）。</param>
public sealed record PocDataSyncRunStats(
    long RowsRead,
    long RowsWritten,
    long RowsFailed,
    IReadOnlyList<PocDataSyncFailureDetail> Failures);

/// <summary>试跑核心结果：源/目标列、转换前后样例与失败统计，不写目标库。</summary>
/// <param name="SourceColumns">源查询返回的列名。</param>
/// <param name="TargetColumns">按映射推导的目标列名。</param>
/// <param name="Samples">转换前后字段对照样例。</param>
/// <param name="RowCountCapped">真实结果超过行数上限被截断。</param>
/// <param name="FailedRows">转换失败的行数。</param>
/// <param name="Failures">失败行明细。</param>
/// <param name="Errors">映射与真实数据的兼容性结论错误清单。</param>
public sealed record PocDataSyncTryRunCoreResult(
    IReadOnlyList<string> SourceColumns,
    IReadOnlyList<string> TargetColumns,
    IReadOnlyList<PocDataSyncSampleRow> Samples,
    bool RowCountCapped,
    long FailedRows,
    IReadOnlyList<PocDataSyncFailureDetail> Failures,
    IReadOnlyList<string> Errors);

/// <summary>
/// 数据同步执行器：薄编排层。取数完全复用 PocSqlDatasetExecutor 的静态管线
/// （试跑走 ExecutePreviewAsync，正式跑走 ReadExportPagesAsync 的 Full 流式游标），
/// 转换调用编译好的字段委托，写入交给 PocDataSyncBulkWriter；自身不解释 SQL、不触碰连接串格式。
/// 行级转换失败按“跳过并记明细”策略处理；写入失败中止整个任务。
/// </summary>
public sealed class PocDataSyncExecutor : ITransient
{
    /// <summary>失败明细封顶条数，与运行日志 ErrorSummaryJson 的约定一致。</summary>
    public const int FailureDetailLimit = 200;

    private readonly CreateDatasetClient _clientFactory;
    private readonly PocDataSyncBulkWriter _writer = new();

    /// <summary>获取本执行器使用的连接工厂，供旁路预览（映射画布）复用同一连接策略。</summary>
    internal CreateDatasetClient ClientFactory => _clientFactory;

    /// <summary>初始化 PocDataSyncExecutor 实例；缺省连接工厂走 PocDatabaseProvider。</summary>
    /// <param name="clientFactory">可选连接工厂，测试环境可注入绑定既有连接的实现。</param>
    public PocDataSyncExecutor(CreateDatasetClient? clientFactory = null)
    {
        _clientFactory = clientFactory ?? PocDatabaseProvider.CreateClient;
    }

    /// <summary>
    /// 执行一次完整同步：准备目标表与冲突策略 → 流式读源 → 逐批转换写入 → 汇总统计。
    /// 任何目标端数据库失败都会中止任务并包装为稳定错误码，由上层记入运行日志。
    /// </summary>
    /// <param name="input">同步执行输入（计划、目标、编译字段）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>读/写/失败行数与失败明细。</returns>
    public async Task<PocDataSyncRunStats> ExecuteAsync(
        PocDataSyncExecutionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateExecutionInput(input);

        // 第一阶段：建立目标连接并完成建表探测与冲突清空，全部失败即中止。
        using var targetClient = _clientFactory(
            input.TargetDbType,
            input.TargetConnectionString,
            input.TargetTimeoutSeconds,
            cancellationToken);
        await using var connection = (DbConnection)targetClient.Ado.Connection;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
        await PrepareTargetAsync(connection, input, cancellationToken);

        var failures = new List<PocDataSyncFailureDetail>();
        long rowsRead = 0;
        long rowsWritten = 0;
        long rowsFailed = 0;

        // 第二阶段：流式读取源数据并逐批转换写入；RowLimit 仅用于受限验证场景。
        if (input.RowLimit is > 0)
        {
            var preview = await PocSqlDatasetExecutor.ExecutePreviewAsync(
                input.SourcePlan,
                input.RowLimit.Value,
                cancellationToken,
                _clientFactory);
            var (previewRead, previewWritten, previewFailed, previewFailures) =
                await TransformAndWriteBatchAsync(
                    preview.Rows,
                    connection,
                    input,
                    rowsRead,
                    failures,
                    cancellationToken);
            rowsRead += previewRead;
            rowsWritten += previewWritten;
            rowsFailed += previewFailed;
            failures.AddRange(previewFailures);
        }
        else
        {
            await foreach (var page in PocSqlDatasetExecutor.ReadExportPagesAsync(
                input.SourcePlan,
                cancellationToken,
                _clientFactory))
            {
                var (pageRead, pageWritten, pageFailed, pageFailures) =
                    await TransformAndWriteBatchAsync(
                        page,
                        connection,
                        input,
                        rowsRead,
                        failures,
                        cancellationToken);
                rowsRead += pageRead;
                rowsWritten += pageWritten;
                rowsFailed += pageFailed;
                failures.AddRange(pageFailures);
            }
        }

        return new PocDataSyncRunStats(
            rowsRead,
            rowsWritten,
            rowsFailed,
            failures.Take(FailureDetailLimit).ToArray());
    }

    /// <summary>
    /// 对计划做受限预览并逐行转换，不写目标库。供任务试跑与同步包试跑复用：
    /// 返回转换前后样例、失败明细与兼容性错误结论。
    /// </summary>
    /// <param name="plan">源数据集执行计划。</param>
    /// <param name="fields">编译后的映射字段。</param>
    /// <param name="rowLimit">行数上限（服务端封顶 50）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>试跑核心结果。</returns>
    public async Task<PocDataSyncTryRunCoreResult> PreviewAsync(
        PocRuntimeExecutionPlan plan,
        IReadOnlyList<PocDataSyncCompiledField> fields,
        int rowLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateFields(fields);
        if (rowLimit < 1 || rowLimit > 50)
        {
            throw new PocDataSyncException(
                "SYNC_TRY_RUN_LIMIT_INVALID",
                "试跑行数上限必须在 1 到 50 之间。");
        }

        var preview = await PocSqlDatasetExecutor.ExecutePreviewAsync(
            plan,
            rowLimit,
            cancellationToken,
            _clientFactory);
        var samples = new List<PocDataSyncSampleRow>();
        var failures = new List<PocDataSyncFailureDetail>();
        long failedRows = 0;

        // 第一阶段：逐行转换并收集样例；失败行跳过计数，不中断试跑。
        foreach (var row in preview.Rows)
        {
            var (target, failure) = TransformRow(fields, row, samples.Count + failedRows + 1);
            if (failure is not null)
            {
                failedRows++;
                failures.Add(failure);
                continue;
            }
            samples.Add(new PocDataSyncSampleRow(
                new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase),
                target!));
        }

        // 第二阶段：汇总兼容性结论错误——缺源字段与高频失败原因都要在试跑报告里露面。
        var errors = BuildTryRunErrors(fields, failures, failedRows);
        return new PocDataSyncTryRunCoreResult(
            preview.Columns,
            fields.Select(field => field.TargetField).ToArray(),
            samples,
            preview.RowCountCapped,
            failedRows,
            failures.Take(FailureDetailLimit).ToArray(),
            errors);
    }

    /// <summary>执行入参校验：目标描述与映射字段必须齐全，冲突策略在白名单内。</summary>
    /// <param name="input">同步执行输入。</param>
    private static void ValidateExecutionInput(PocDataSyncExecutionInput input)
    {
        ValidateFields(input.Fields);
        if (string.IsNullOrWhiteSpace(input.TargetTable) ||
            string.IsNullOrWhiteSpace(input.TargetDbType) ||
            string.IsNullOrWhiteSpace(input.TargetConnectionString))
        {
            throw new PocDataSyncException(
                "SYNC_TARGET_CONFIG_INVALID",
                "同步目标配置不完整：目标表、目标数据源类型与连接信息都必须提供。");
        }
        var policy = (input.ConflictPolicy ?? string.Empty).Trim();
        if (!policy.Equals("TruncateFirst", StringComparison.OrdinalIgnoreCase) &&
            !policy.Equals("Append", StringComparison.OrdinalIgnoreCase))
        {
            throw new PocDataSyncException(
                "SYNC_CONFLICT_POLICY_INVALID",
                $"冲突策略 '{input.ConflictPolicy}' 无效，仅支持 TruncateFirst 或 Append。");
        }
    }

    /// <summary>校验字段集合：空集合无法建表写入，直接拒绝。</summary>
    /// <param name="fields">编译后的映射字段。</param>
    private static void ValidateFields(IReadOnlyList<PocDataSyncCompiledField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INCOMPLETE",
                "同步任务没有配置任何字段映射，无法执行。");
        }
    }

    /// <summary>准备目标端：探测建表，TruncateFirst 策略下再清空目标表。</summary>
    /// <param name="connection">已打开的目标连接。</param>
    /// <param name="input">同步执行输入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PrepareTargetAsync(
        DbConnection connection,
        PocDataSyncExecutionInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            // 第一阶段：目标表存在性探测与自动建表，映射即 DDL 契约。
            await _writer.EnsureTableAsync(
                connection,
                input.TargetDbType,
                input.TargetTable,
                input.Fields,
                cancellationToken);
            // 第二阶段：按冲突策略清空；Append 策略跳过清空直接追加。
            if (input.ConflictPolicy.Trim().Equals("TruncateFirst", StringComparison.OrdinalIgnoreCase))
            {
                await _writer.TruncateAsync(connection, input.TargetDbType, input.TargetTable, cancellationToken);
            }
        }
        catch (PocDataSyncException)
        {
            throw;
        }
        catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception))
        {
            throw new PocDataSyncException(
                "SYNC_TARGET_PREPARE_FAILED",
                $"目标表准备失败：{exception.Message}",
                exception);
        }
    }

    /// <summary>转换一批行并写入目标表；返回本批统计与新增失败明细。</summary>
    /// <param name="rows">源数据页（键为源字段名）。</param>
    /// <param name="connection">已打开的目标连接。</param>
    /// <param name="input">同步执行输入。</param>
    /// <param name="rowsReadSoFar">此前累计读取行数（用于失败明细定位行号）。</param>
    /// <param name="failures">跨批共享的失败明细集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本批读取/写入/失败行数与新增失败明细。</returns>
    private async Task<(long Read, long Written, long Failed, IReadOnlyList<PocDataSyncFailureDetail> BatchFailures)>
        TransformAndWriteBatchAsync(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            DbConnection connection,
            PocDataSyncExecutionInput input,
            long rowsReadSoFar,
            List<PocDataSyncFailureDetail> failures,
            CancellationToken cancellationToken)
    {
        var batch = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        var batchFailures = new List<PocDataSyncFailureDetail>();
        long failed = 0;

        // 第一阶段：逐行转换，失败行按策略跳过并记明细（封顶后在统计层截断）。
        foreach (var row in rows)
        {
            var (target, failure) = TransformRow(input.Fields, row, rowsReadSoFar + batch.Count + failed + 1);
            if (failure is not null)
            {
                failed++;
                batchFailures.Add(failure);
                continue;
            }
            batch.Add(target!);
        }

        // 第二阶段：整批写入目标表；写入失败属于基础设施错误，直接中止任务。
        try
        {
            await _writer.WriteBatchAsync(
                connection,
                input.TargetDbType,
                input.TargetTable,
                input.Fields,
                batch,
                cancellationToken);
        }
        catch (PocDataSyncException)
        {
            throw;
        }
        catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception))
        {
            throw new PocDataSyncException(
                "SYNC_TARGET_WRITE_FAILED",
                $"写入目标表失败：{exception.Message}",
                exception);
        }

        // 失败明细由调用方统一汇总（返回值带回本批明细），此处不再重复写入共享集合。
        return (rows.Count, batch.Count, failed, batchFailures);
    }

    /// <summary>转换单行：按字段委托串行执行；缺源字段或转换失败返回失败明细。</summary>
    /// <param name="fields">编译后的映射字段。</param>
    /// <param name="row">源数据行。</param>
    /// <param name="rowIndex">行序（从 1 开始），用于失败明细定位。</param>
    /// <returns>目标行字典与失败明细（成功时明细为 null）。</returns>
    private static (Dictionary<string, object?>? Target, PocDataSyncFailureDetail? Failure) TransformRow(
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyDictionary<string, object?> row,
        long rowIndex)
    {
        var target = new Dictionary<string, object?>(fields.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!row.TryGetValue(field.SourceField, out var sourceValue))
            {
                return (null, new PocDataSyncFailureDetail(
                    rowIndex,
                    field.SourceField,
                    $"源数据缺少字段 '{field.SourceField}'。",
                    null));
            }
            try
            {
                target[field.TargetField] = field.Transform(sourceValue);
            }
            catch (PocDataSyncTransformException exception)
            {
                return (null, new PocDataSyncFailureDetail(
                    rowIndex,
                    exception.Field,
                    exception.Reason,
                    exception.OriginalValue));
            }
            catch (Exception exception)
            {
                return (null, new PocDataSyncFailureDetail(
                    rowIndex,
                    field.SourceField,
                    $"转换出现未预期错误：{exception.Message}",
                    null));
            }
        }
        return (target, null);
    }

    /// <summary>汇总试跑兼容性错误：缺源字段与各类失败原因去重后形成结论清单。</summary>
    /// <param name="fields">编译后的映射字段。</param>
    /// <param name="failures">试跑失败明细。</param>
    /// <param name="failedRows">失败行总数。</param>
    /// <returns>面向前端的错误描述清单。</returns>
    private static IReadOnlyList<string> BuildTryRunErrors(
        IReadOnlyList<PocDataSyncCompiledField> fields,
        IReadOnlyList<PocDataSyncFailureDetail> failures,
        long failedRows)
    {
        var errors = new List<string>();
        var missingFields = failures
            .Where(item => item.Reason.StartsWith("源数据缺少字段", StringComparison.Ordinal))
            .Select(item => item.Field)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var field in fields.Where(item => missingFields.Contains(item.SourceField, StringComparer.OrdinalIgnoreCase)))
        {
            errors.Add($"源查询缺少映射要求的字段 '{field.SourceField}'。");
        }
        foreach (var reason in failures
            .Where(item => !item.Reason.StartsWith("源数据缺少字段", StringComparison.Ordinal))
            .Select(item => item.Reason)
            .Distinct(StringComparer.Ordinal)
            .Take(10))
        {
            errors.Add($"行级转换失败：{reason}");
        }
        if (failedRows > 0)
        {
            errors.Add($"共 {failedRows} 行转换失败，明细见失败列表。");
        }
        return errors;
    }
}
