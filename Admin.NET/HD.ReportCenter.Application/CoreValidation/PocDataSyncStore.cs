namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>同步任务保存输入（存储层契约）；ExpectedRevision 为乐观并发期望修订号，0 表示首次落库。</summary>
public sealed record PocSyncTaskMutation(
    string Code,
    string Name,
    string SourceDatasetCode,
    string TargetDataSourceCode,
    string TargetTable,
    string DefinitionJson,
    string ConflictPolicy,
    string ScheduleType,
    string? ScheduleTime,
    int IntervalMinutes,
    string? ScheduleCron,
    bool IsEnabled,
    int ExpectedRevision);

/// <summary>同步任务修订号并发冲突：期望修订号与库内当前值不一致，保存被拒绝。</summary>
public sealed class PocDataSyncRevisionConflictException : Exception
{
    /// <summary>初始化 PocDataSyncRevisionConflictException 异常实例，说明冲突的任务与期望修订号。</summary>
    /// <param name="code">发生冲突的同步任务编码。</param>
    /// <param name="expectedRevision">保存请求携带的期望修订号。</param>
    public PocDataSyncRevisionConflictException(string code, int expectedRevision)
        : base($"Sync task '{code}' is no longer at revision {expectedRevision}.")
    {
    }
}

/// <summary>数据同步任务与运行日志的独立存储边界；保存走修订号乐观锁，与报表配置一致。</summary>
public sealed class PocDataSyncStore : ITransient
{
    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocDataSyncStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="database">报表中心主库的 SqlSugar 客户端，由宿主注入。</param>
    public PocDataSyncStore(ISqlSugarClient database)
    {
        _database = database;
    }

    /// <summary>列出全部同步任务，启用中的任务排前，再按名称与编码稳定排序供任务管理页使用。</summary>
    /// <returns>同步任务实体列表。</returns>
    public async Task<IReadOnlyList<PocSyncTask>> ListTasksAsync() =>
        await _database.Queryable<PocSyncTask>()
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    /// <summary>按编码读取单个同步任务；不存在返回 null，由服务层转换为业务错误。</summary>
    /// <param name="code">同步任务编码。</param>
    /// <returns>命中的任务实体或 null。</returns>
    public async Task<PocSyncTask?> GetTaskAsync(string code) =>
        await _database.Queryable<PocSyncTask>()
            .Where(item => item.Code == code)
            .FirstAsync();

    /// <summary>按编码物理删除同步任务；运行日志按设计保留，用于历史执行审计。</summary>
    /// <param name="code">待删除的同步任务编码。</param>
    /// <returns>是否恰好删除了一行；false 表示编码在删除瞬间已不存在。</returns>
    public async Task<bool> DeleteTaskAsync(string code) =>
        await _database.Deleteable<PocSyncTask>()
            .Where(item => item.Code == code)
            .ExecuteCommandAsync() == 1;

    /// <summary>
    /// 保存同步任务：首次落库要求 ExpectedRevision 为 0，更新阶段用 WHERE 修订号匹配实现
    /// 乐观并发；匹配不到抛 PocDataSyncRevisionConflictException，防止覆盖他人改动。
    /// </summary>
    /// <param name="mutation">任务保存契约（含期望修订号）。</param>
    /// <returns>落库后的新修订号。</returns>
    public async Task<int> SaveTaskAsync(PocSyncTaskMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = DateTime.Now;
        var revision = mutation.ExpectedRevision + 1;
        var existing = await GetTaskAsync(mutation.Code);
        if (existing is null)
        {
            // 第一阶段：新建分支。非零期望修订号说明客户端基于一个已不存在的版本编辑，直接拒绝。
            if (mutation.ExpectedRevision != 0)
            {
                throw new PocDataSyncRevisionConflictException(
                    mutation.Code,
                    mutation.ExpectedRevision);
            }
            await _database.Insertable(new PocSyncTask
            {
                Code = mutation.Code,
                Name = mutation.Name,
                SourceDatasetCode = mutation.SourceDatasetCode,
                TargetDataSourceCode = mutation.TargetDataSourceCode,
                TargetTable = mutation.TargetTable,
                DefinitionJson = mutation.DefinitionJson,
                ConflictPolicy = mutation.ConflictPolicy,
                ScheduleType = mutation.ScheduleType,
                ScheduleTime = mutation.ScheduleTime,
                IntervalMinutes = mutation.IntervalMinutes,
                ScheduleCron = mutation.ScheduleCron,
                Revision = revision,
                IsEnabled = mutation.IsEnabled,
                CreateTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();
            return revision;
        }

        // 第二阶段：更新分支。UPDATE 条件带上旧修订号，受影响行数不为 1 即视为并发冲突。
        // ScheduleCron 必须同步映射：漏改会造成“保存成功但列永远为空”的静默丢数。
        var affected = await _database.Updateable<PocSyncTask>()
            .SetColumns(item => new PocSyncTask
            {
                Name = mutation.Name,
                SourceDatasetCode = mutation.SourceDatasetCode,
                TargetDataSourceCode = mutation.TargetDataSourceCode,
                TargetTable = mutation.TargetTable,
                DefinitionJson = mutation.DefinitionJson,
                ConflictPolicy = mutation.ConflictPolicy,
                ScheduleType = mutation.ScheduleType,
                ScheduleTime = mutation.ScheduleTime,
                IntervalMinutes = mutation.IntervalMinutes,
                ScheduleCron = mutation.ScheduleCron,
                Revision = revision,
                IsEnabled = mutation.IsEnabled,
                UpdateTime = now
            })
            .Where(item => item.Code == mutation.Code && item.Revision == mutation.ExpectedRevision)
            .ExecuteCommandAsync();
        if (affected != 1)
        {
            throw new PocDataSyncRevisionConflictException(
                mutation.Code,
                mutation.ExpectedRevision);
        }
        return revision;
    }

    /// <summary>同步任务执行完成后回写最近运行时间，供间隔调度与任务页展示到期情况。</summary>
    /// <param name="code">同步任务编码。</param>
    /// <param name="lastRunAt">本次运行完成（或失败）的时间。</param>
    public async Task UpdateTaskLastRunAsync(string code, DateTimeOffset lastRunAt)
    {
        // 与运行日志保持同一口径：按本地偏移落库。API 序列化输出的是墙上时间，
        // 存 UTC 会让任务页"上次运行"差一个时区，每日调度的日期比较也会随之偏移。
        await _database.Updateable<PocSyncTask>()
            .SetColumns(item => new PocSyncTask { LastRunAt = lastRunAt })
            .Where(item => item.Code == code)
            .ExecuteCommandAsync();
    }

    /// <summary>判断任务是否存在未结束（Running）的运行记录，作为防重入闸门。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    /// <returns>存在 Running 日志返回 true。</returns>
    public async Task<bool> HasRunningRunLogAsync(string taskCode) =>
        await _database.Queryable<PocSyncRunLog>()
            .AnyAsync(item => item.TaskCode == taskCode && item.Status == "Running");

    /// <summary>插入一条运行日志；Running 状态先落库，执行器随后回写终态与统计。</summary>
    /// <param name="log">待插入的运行日志实体。</param>
    public async Task InsertRunLogAsync(PocSyncRunLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        await _database.Insertable(log).ExecuteCommandAsync();
    }

    /// <summary>按运行编号读取运行日志；不存在返回 null。</summary>
    /// <param name="runNo">运行编号。</param>
    /// <returns>命中的运行日志实体或 null。</returns>
    public async Task<PocSyncRunLog?> GetRunLogAsync(string runNo) =>
        await _database.Queryable<PocSyncRunLog>()
            .Where(item => item.RunNo == runNo)
            .FirstAsync();

    /// <summary>按任务编码倒序列出最近的运行日志，供任务页轮询展示执行历史。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    /// <param name="limit">最多返回的日志条数。</param>
    /// <returns>按开始时间倒序排列的运行日志列表。</returns>
    public async Task<IReadOnlyList<PocSyncRunLog>> ListRunLogsAsync(string taskCode, int limit) =>
        await _database.Queryable<PocSyncRunLog>()
            .Where(item => item.TaskCode == taskCode)
            .OrderByDescending(item => item.StartedAt)
            .OrderByDescending(item => item.Id)
            .Take(limit)
            .ToListAsync();

    /// <summary>按运行编号整体回写日志（终态、统计、失败明细、消息），由执行器在结束时调用。</summary>
    /// <param name="log">携带终态数据的运行日志实体（Id 必须已存在）。</param>
    public async Task UpdateRunLogAsync(PocSyncRunLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        await _database.Updateable(log)
            .WhereColumns(item => new { item.RunNo })
            .ExecuteCommandAsync();
    }
}
