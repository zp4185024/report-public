namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 数据同步任务定义表。一条任务把一个源数据集的查询结果按映射规则定时写入目标库表；
/// DefinitionJson 保存映射与调度定义（供导出包复用），调度字段另存列便于作业用 SQL 直接扫描。
/// </summary>
[SugarTable("RcSyncTask", "数据同步任务表")]
[SugarIndex("uk_RcSyncTask_Code", nameof(Code), OrderByType.Asc, IsUnique = true)]
[SysTable]
public sealed class PocSyncTask : EntityBase
{
    [SugarColumn(ColumnDescription = "同步任务编码", Length = 64)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "同步任务名称", Length = 128)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "源数据集编码", Length = 64)]
    public string SourceDatasetCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "目标数据源编码", Length = 64)]
    public string TargetDataSourceCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "目标表名", Length = 128)]
    public string TargetTable { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "字段映射和调度定义", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string DefinitionJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "写入前冲突策略", Length = 32)]
    public string ConflictPolicy { get; set; } = "Append";

    [SugarColumn(ColumnDescription = "调度类型", Length = 32)]
    public string ScheduleType { get; set; } = "Manual";

    [SugarColumn(ColumnDescription = "每日调度时间 HH:mm", Length = 16, IsNullable = true)]
    public string? ScheduleTime { get; set; }

    [SugarColumn(ColumnDescription = "间隔调度分钟数")]
    public int IntervalMinutes { get; set; }

    /// <summary>Cron 调度表达式（标准 5 段：分 时 日 月 周）；仅调度类型为 Cron 时有值，其余类型保存时裁剪为空。</summary>
    [SugarColumn(ColumnDescription = "Cron 调度表达式", Length = 64, IsNullable = true)]
    public string? ScheduleCron { get; set; }

    [SugarColumn(ColumnDescription = "最近一次运行时间", IsNullable = true)]
    public DateTimeOffset? LastRunAt { get; set; }

    [SugarColumn(ColumnDescription = "配置修订号")]
    public int Revision { get; set; }

    [SugarColumn(ColumnDescription = "是否启用")]
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 数据同步运行日志表。每次执行（试跑外的真实同步）先落一条 Running 再异步更新终态，
/// 行数统计与失败明细（封顶 200 条）供任务页轮询展示和事后排障。
/// </summary>
[SugarTable("RcSyncRunLog", "数据同步运行日志表")]
[SugarIndex("uk_RcSyncRunLog_RunNo", nameof(RunNo), OrderByType.Asc, IsUnique = true)]
[SysTable]
public sealed class PocSyncRunLog : EntityBase
{
    [SugarColumn(ColumnDescription = "运行编号", Length = 64)]
    public string RunNo { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "同步任务编码", Length = 64)]
    public string TaskCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "运行状态", Length = 32)]
    public string Status { get; set; } = "Running";

    [SugarColumn(ColumnDescription = "开始时间")]
    public DateTimeOffset StartedAt { get; set; }

    [SugarColumn(ColumnDescription = "结束时间", IsNullable = true)]
    public DateTimeOffset? FinishedAt { get; set; }

    [SugarColumn(ColumnDescription = "读取行数")]
    public long RowsRead { get; set; }

    [SugarColumn(ColumnDescription = "写入行数")]
    public long RowsWritten { get; set; }

    [SugarColumn(ColumnDescription = "失败行数")]
    public long RowsFailed { get; set; }

    [SugarColumn(ColumnDescription = "失败行明细（封顶 200 条）", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true)]
    public string? ErrorSummaryJson { get; set; }

    [SugarColumn(ColumnDescription = "结果消息", ColumnDataType = StaticConfig.CodeFirst_BigString, IsNullable = true)]
    public string? Message { get; set; }
}
