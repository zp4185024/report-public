using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.Sqlite;
using SqlSugar;
using System.Data;
using System.Data.Common;
using System.Reflection;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 数据同步执行链路测试：SQLite 内存库 → SQLite 内存库全链路（源数据集 → 编译映射 → 转换 →
/// INSERT 兜底写入目标表），并覆盖冲突策略、行级失败策略、方言 DDL 生成与调度到期判定。
/// </summary>
public sealed class PocDataSyncExecutorTests : IDisposable
{
    private readonly PocDataSyncTestEnvironment _environment;

    /// <summary>初始化测试实例：搭建内存库测试环境。</summary>
    public PocDataSyncExecutorTests()
    {
        _environment = new PocDataSyncTestEnvironment();
    }

    /// <summary>释放测试环境持有的连接与临时资源。</summary>
    public void Dispose()
    {
        _environment.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 验证全链路同步：源表数据按映射完成 trim/decimal/date 转换后写入自动创建的目标表，
    /// 行数统计正确，目标表数据与期望一致。
    /// </summary>
    [Fact]
    public async Task ExecuteTaskNow_syncs_transformed_rows_into_auto_created_target()
    {
        await _environment.SeedSourceTableAsync(
            ("A-001", "12.345", "20240101", "CARD"),
            ("A-002", "8.5", "20240102", "DEPT"));
        await _environment.SeedTaskAsync("sync_demo", "TruncateFirst");

        var log = await _environment.SyncService.ExecuteTaskNowAsync("sync_demo");

        Assert.Equal("Completed", log.Status);
        Assert.Equal(2, log.RowsRead);
        Assert.Equal(2, log.RowsWritten);
        Assert.Equal(0, log.RowsFailed);
        var rows = _environment.QueryTargetRows("sync_tgt_demo");
        Assert.Equal(2, rows.Count);
        Assert.Equal("A-001", rows[0]["CARD_NO"]);
        Assert.Equal(
            12.35m,
            Convert.ToDecimal(rows[0]["AMOUNT"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(
            new DateTime(2024, 1, 1),
            Convert.ToDateTime(rows[0]["VISIT_DATE"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("CARD", rows[0]["DEPT_CODE"]);
    }

    /// <summary>验证 TruncateFirst 冲突策略：重复运行先清空目标表，重跑后只保留源表当前数据。</summary>
    [Fact]
    public async Task TruncateFirst_policy_replaces_target_rows_on_rerun()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_demo", "TruncateFirst");
        await _environment.SyncService.ExecuteTaskNowAsync("sync_demo");

        // 源表是累积插入：第二次运行时源数据为 A-001 + B-009，目标表先被清空再写入这两行。
        await _environment.SeedSourceTableAsync(("B-009", "2", "20240202", "WARD"));
        await _environment.SyncService.ExecuteTaskNowAsync("sync_demo");

        var rows = _environment.QueryTargetRows("sync_tgt_demo");
        Assert.Equal(2, rows.Count);
        Assert.Equal("A-001", rows[0]["CARD_NO"]);
        Assert.Equal("B-009", rows[1]["CARD_NO"]);
    }

    /// <summary>验证 Append 冲突策略：重复运行直接追加，目标表累计两次运行的数据。</summary>
    [Fact]
    public async Task Append_policy_accumulates_target_rows_on_rerun()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_demo", "Append");
        await _environment.SyncService.ExecuteTaskNowAsync("sync_demo");
        await _environment.SyncService.ExecuteTaskNowAsync("sync_demo");

        Assert.Equal(2, _environment.QueryTargetRows("sync_tgt_demo").Count);
    }

    /// <summary>验证行级失败策略：必填列为空的行被跳过并计数，其余行照常写入。</summary>
    [Fact]
    public async Task Row_transform_failures_are_skipped_and_counted()
    {
        await _environment.SeedSourceTableAsync(
            ("A-001", "1", "20240101", "CARD"),
            ("BAD-00", "", "20240102", ""));
        await _environment.SeedTaskAsync("sync_demo", "Append", requireDeptCode: true);

        var log = await _environment.SyncService.ExecuteTaskNowAsync("sync_demo");

        Assert.Equal(2, log.RowsRead);
        Assert.Equal(1, log.RowsWritten);
        Assert.Equal(1, log.RowsFailed);
        Assert.Single(log.Failures);
        Assert.Equal(2, log.Failures[0].RowIndex);
        Assert.Equal("deptCode", log.Failures[0].Field);
        var rows = _environment.QueryTargetRows("sync_tgt_demo");
        Assert.Single(rows);
        Assert.Equal("A-001", rows[0]["CARD_NO"]);
    }

    /// <summary>验证试跑端到端转换：返回转换前后样例与目标列结论，且不写目标库（目标表不会被创建）。</summary>
    [Fact]
    public async Task TryRun_returns_samples_without_writing_target()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1.5", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_demo", "Append");

        var result = await _environment.SyncService.TryRunAsync("sync_demo", new PocDataSyncTryRunRequest(50));

        Assert.True(result.Ok);
        Assert.Single(result.Samples);
        Assert.Equal("A-001", result.Samples[0].Source["cardNo"]);
        Assert.Equal("A-001", result.Samples[0].Target["CARD_NO"]);
        Assert.Contains("CARD_NO", result.TargetColumns);
        // 试跑不落目标库：目标表连建表动作都不发生。
        Assert.DoesNotContain("sync_tgt_demo", _environment.ListTablesRaw(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>验证映射引用了源查询不存在的字段时，运行在字段解析阶段即失败并回写 Failed 日志。</summary>
    [Fact]
    public async Task Missing_source_field_fails_run_and_records_failed_log()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_demo", "Append");
        // 直接改写任务定义，把映射源字段换成不存在的列，模拟配置与查询漂移。
        var task = await _environment.SyncStore.GetTaskAsync("sync_demo");
        var definition = PocDataSyncService.DeserializeDefinition(task!.DefinitionJson);
        var broken = definition.Mappings.Select(item =>
            item.SourceField == "cardNo" ? item with { SourceField = "ghostField" } : item).ToArray();
        await _environment.SyncStore.SaveTaskAsync(new PocSyncTaskMutation(
            task.Code,
            task.Name,
            task.SourceDatasetCode,
            task.TargetDataSourceCode,
            task.TargetTable,
            PocDataSyncService.SerializeDefinition(definition with { Mappings = broken }),
            task.ConflictPolicy,
            task.ScheduleType,
            task.ScheduleTime,
            task.IntervalMinutes,
            task.ScheduleCron,
            task.IsEnabled,
            task.Revision));

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(
            () => _environment.SyncService.ExecuteTaskNowAsync("sync_demo"));
        Assert.Equal("SYNC_RUN_FAILED", exception.Code);

        var logs = await _environment.SyncService.ListRunLogsAsync("sync_demo");
        Assert.Equal("Failed", logs[0].Status);
    }

    /// <summary>验证运行中防重入：存在 Running 日志时再次启动被拒绝（SYNC_TASK_ALREADY_RUNNING）。</summary>
    [Fact]
    public async Task Running_log_blocks_reentrant_start()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_demo", "Append");
        await _environment.SyncStore.InsertRunLogAsync(new PocSyncRunLog
        {
            RunNo = "running-1",
            TaskCode = "sync_demo",
            Status = "Running",
            StartedAt = DateTimeOffset.Now
        });

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(
            () => _environment.SyncService.ExecuteTaskNowAsync("sync_demo"));

        Assert.Equal("SYNC_TASK_ALREADY_RUNNING", exception.Code);
    }

    /// <summary>验证任务保存链路：合法 Oracle 目标可以保存，重复目标列在保存时被拒绝且不落库。</summary>
    [Fact]
    public async Task Save_validates_mappings_and_rejects_duplicate_target_columns()
    {
        // 先播种源数据集（含源/目标 SQLite 数据源），SaveAsync 会校验源数据集存在且启用。
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_demo", "Append");
        // Oracle 目标走数据资产服务合法创建（仅配置校验，不发生真实连接），保存链路可以完整执行。
        await _environment.DataAssetService.SaveDataSourceAsync("tgt_oracle", new PocDataSourceInput(
            "tgt_oracle",
            "Oracle 目标",
            "Oracle",
            "188.188.2.5",
            1521,
            "orclpdb",
            "writer",
            "secret",
            30,
            20_000,
            Enabled: true,
            Revision: 0));
        var validInput = new PocSyncTaskInput(
            "sync_valid",
            "合法任务",
            _environment.SourceDatasetCode,
            "tgt_oracle",
            "SYNC_TGT_ORACLE",
            "Append",
            "Daily",
            "02:30",
            0,
            [new PocSyncColumnMapping("cardNo", "CARD_NO", "string", 16, true, [])],
            true,
            0);
        var duplicateInput = validInput with
        {
            Code = "sync_dup",
            Mappings =
            [
                new PocSyncColumnMapping("cardNo", "SAME", "string", 16, true, []),
                new PocSyncColumnMapping("deptCode", "same", "string", 16, true, [])
            ]
        };

        var saved = await _environment.SyncService.SaveAsync("sync_valid", validInput);
        Assert.Equal(1, saved.Revision);
        Assert.Equal("Daily", saved.ScheduleType);
        Assert.Equal("02:30", saved.ScheduleTime);
        Assert.Single(saved.Mappings);

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(
            () => _environment.SyncService.SaveAsync("sync_dup", duplicateInput));
        Assert.Equal("SYNC_MAPPING_INCOMPLETE", exception.Code);
        Assert.Null(await _environment.SyncStore.GetTaskAsync("sync_dup"));
    }

    /// <summary>
    /// 验证保存任务的新契约 source 节：服务端自动把源 SQL 落成 `sync_src_&lt;taskCode&gt;`
    /// 数据集（绑定所选数据源、启用状态），任务实体仍按 SourceDatasetCode 关联。
    /// </summary>
    [Fact]
    public async Task SaveAsync_with_source_node_upserts_source_dataset_and_binds_task()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        // SeedTaskAsync 仅用于播种数据源；目标源用受支持类型（SqlServer）过保存校验，
        // 连接本身仍由测试环境的绑定工厂指向 SQLite 内存库。
        await _environment.SeedTaskAsync("sync_seed_base", "Append", targetDbType: "SqlServer");

        var input = new PocSyncTaskInput(
            "sync_from_source_node",
            "选表同步",
            SourceDatasetCode: null,
            TargetDataSourceCode: "tgt_sqlite",
            TargetTable: "SYNC_TGT_SRC_NODE",
            ConflictPolicy: "Append",
            ScheduleType: "Manual",
            ScheduleTime: null,
            IntervalMinutes: 0,
            Mappings: [new PocSyncColumnMapping("card_no", "CARD_NO", "string", 16, true, [])],
            Enabled: true,
            Revision: 0,
            Source: new PocSyncTaskSourceInput(
                "src_sqlite",
                "SELECT card_no FROM sync_src_demo",
                "选表源数据集"));

        var saved = await _environment.SyncService.SaveAsync("sync_from_source_node", input);

        Assert.Equal(1, saved.Revision);
        Assert.Equal("sync_src_sync_from_source_node", saved.SourceDatasetCode);
        var dataset = await _environment.DataAssets.GetDatasetAsync(saved.SourceDatasetCode);
        Assert.NotNull(dataset);
        Assert.Equal("src_sqlite", dataset!.DataSourceCode);
        Assert.Equal("选表源数据集", dataset.Name);
        Assert.Contains("sync_src_demo", dataset.QueryText, StringComparison.Ordinal);
        Assert.True(dataset.IsEnabled);
    }

    /// <summary>
    /// 验证源节的复用与更新决策：内容一致时重复保存复用数据集（修订号不推高），
    /// 内容变化时按库内修订号执行更新而不是撞修订号冲突。
    /// </summary>
    [Fact]
    public async Task SaveAsync_source_node_reuses_identical_dataset_and_updates_changed_source()
    {
        await _environment.SeedTaskAsync("sync_seed_base2", "Append", targetDbType: "SqlServer");
        var mappings = new List<PocSyncColumnMapping>
        {
            new("card_no", "CARD_NO", "string", 16, true, []),
        };

        // 第一次保存：新建 sync_src_* 数据集，修订号 1。
        var first = new PocSyncTaskInput(
            "sync_source_upsert", "复用更新", null, "tgt_sqlite", "SYNC_TGT_UPSERT",
            "Append", "Manual", null, 0, mappings, true, 0,
            new PocSyncTaskSourceInput("src_sqlite", "SELECT card_no FROM sync_src_demo"));
        await _environment.SyncService.SaveAsync("sync_source_upsert", first);
        var afterCreate = await _environment.DataAssets.GetDatasetAsync("sync_src_sync_source_upsert");
        Assert.NotNull(afterCreate);
        Assert.Equal(1, afterCreate!.Revision);

        // 第二次保存（内容一致）：数据集复用不动，修订号保持 1；任务自身按乐观并发推到修订号 2。
        var second = first with { Revision = 1 };
        var savedAgain = await _environment.SyncService.SaveAsync("sync_source_upsert", second);
        Assert.Equal(2, savedAgain.Revision);
        var afterReuse = await _environment.DataAssets.GetDatasetAsync("sync_src_sync_source_upsert");
        Assert.NotNull(afterReuse);
        Assert.Equal(1, afterReuse!.Revision);

        // 第三次保存（源 SQL 变化）：按库内修订号 1 提交更新，数据集修订号推到 2。
        var third = second with
        {
            Revision = 2,
            Source = new PocSyncTaskSourceInput("src_sqlite", "SELECT card_no, dept_code FROM sync_src_demo")
        };
        await _environment.SyncService.SaveAsync("sync_source_upsert", third);
        var afterUpdate = await _environment.DataAssets.GetDatasetAsync("sync_src_sync_source_upsert");
        Assert.NotNull(afterUpdate);
        Assert.Equal(2, afterUpdate!.Revision);
        Assert.Contains("dept_code", afterUpdate.QueryText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证源取数预览（preview-source）：真实执行只读 SQL 返回动态列与样例行；
    /// 非 SELECT/WITH 语句被只读校验以 SYNC_SOURCE_SQL_INVALID 拒绝。
    /// </summary>
    [Fact]
    public async Task PreviewSourceAsync_returns_dynamic_columns_and_rejects_non_readonly_sql()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_seed_base3", "Append");

        var result = await _environment.SyncService.PreviewSourceAsync(
            new PocSyncSourcePreviewRequest("src_sqlite", "SELECT card_no FROM sync_src_demo"));

        Assert.Contains(result.Fields, field => field.Code == "card_no");
        Assert.Single(result.Rows);
        Assert.Equal("A-001", result.Rows[0][0]);

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(
            () => _environment.SyncService.PreviewSourceAsync(
                new PocSyncSourcePreviewRequest("src_sqlite", "DELETE FROM sync_src_demo")));
        Assert.Equal("SYNC_SOURCE_SQL_INVALID", exception.Code);
    }

    /// <summary>验证四种受支持库的建表 DDL：类型映射与长度默认值符合方言约定。</summary>
    [Fact]
    public void Create_table_sql_matches_dialect_conventions()
    {
        var writer = new PocDataSyncBulkWriter();
        var fields = new[]
        {
            new PocDataSyncCompiledField("src", "NAME", "string", null, true, v => v),
            new PocDataSyncCompiledField("src", "COUNT", "number", null, true, v => v),
            new PocDataSyncCompiledField("src", "FEE", "decimal", null, true, v => v),
            new PocDataSyncCompiledField("src", "VISIT_AT", "datetime", null, true, v => v),
            new PocDataSyncCompiledField("src", "IS_OK", "bool", null, true, v => v)
        };

        var sqlServer = writer.BuildCreateTableSql("SqlServer", "rc_target", fields);
        // SQL Server：中括号引用、NVARCHAR/DATETIME2/BIT 方言类型。
        Assert.Contains("[rc_target]", sqlServer, StringComparison.Ordinal);
        Assert.Contains("[NAME] NVARCHAR(256) NULL", sqlServer, StringComparison.Ordinal);
        Assert.Contains("[VISIT_AT] DATETIME2", sqlServer, StringComparison.Ordinal);
        Assert.Contains("[IS_OK] BIT", sqlServer, StringComparison.Ordinal);

        var oracle = writer.BuildCreateTableSql("Oracle", "rc_target", fields);
        // Oracle：双引号引用、VARCHAR2 带 CHAR 语义、金额与时间分别为 NUMBER/TIMESTAMP。
        Assert.Contains("\"NAME\" VARCHAR2(256 CHAR)", oracle, StringComparison.Ordinal);
        Assert.Contains("\"FEE\" NUMBER(28, 8)", oracle, StringComparison.Ordinal);
        Assert.Contains("\"VISIT_AT\" TIMESTAMP", oracle, StringComparison.Ordinal);

        var mysql = writer.BuildCreateTableSql("MySql", "rc_target", fields);
        // MySQL：反引号引用、DATETIME/TINYINT 方言类型。
        Assert.Contains("`NAME` VARCHAR(256)", mysql, StringComparison.Ordinal);
        Assert.Contains("`VISIT_AT` DATETIME", mysql, StringComparison.Ordinal);
        Assert.Contains("`IS_OK` TINYINT(1)", mysql, StringComparison.Ordinal);

        var postgres = writer.BuildCreateTableSql("PostgreSQL", "rc_target", fields);
        // PostgreSQL：双引号引用、TIMESTAMPTZ/BOOLEAN 方言类型。
        Assert.Contains("\"NAME\" VARCHAR(256)", postgres, StringComparison.Ordinal);
        Assert.Contains("\"VISIT_AT\" TIMESTAMPTZ", postgres, StringComparison.Ordinal);
        Assert.Contains("\"IS_OK\" BOOLEAN", postgres, StringComparison.Ordinal);
        Assert.Contains("\"NAME\" VARCHAR(256)", postgres, StringComparison.Ordinal);
        Assert.Contains("\"VISIT_AT\" TIMESTAMPTZ", postgres, StringComparison.Ordinal);
        Assert.Contains("\"IS_OK\" BOOLEAN", postgres, StringComparison.Ordinal);

        // 未声明长度的字符串列默认 256；NULL/NOT NULL 跟随映射的 Nullable 配置。
        var requiredField = new[]
        {
            new PocDataSyncCompiledField("src", "NAME", "string", 32, false, v => v)
        };
        Assert.Contains(
            "VARCHAR(32) NOT NULL",
            writer.BuildCreateTableSql("PostgreSQL", "t", requiredField),
            StringComparison.Ordinal);
    }

    /// <summary>验证调度到期判定：Daily 过点未跑到期、当日已跑不重跑；Interval 按间隔；Manual 永不自动。</summary>
    [Fact]
    public void Task_due_rules_cover_daily_interval_and_manual()
    {
        var baseTime = new DateTimeOffset(2026, 3, 10, 3, 0, 0, TimeSpan.FromHours(8));
        var daily = new PocSyncTask
        {
            Code = "daily_task",
            ScheduleType = "Daily",
            ScheduleTime = "02:30",
            IsEnabled = true
        };

        // 第一阶段：Daily 语义——过点未跑视为到期，过点前与当日已跑都不再触发。
        Assert.True(PocDataSyncService.IsTaskDue(daily, baseTime));
        Assert.False(PocDataSyncService.IsTaskDue(daily, baseTime.AddHours(-1)));

        // PocSyncTask 是类不是记录，这里用全新实例表达“今天已跑过”的场景，避免共享引用串场。
        var dailyRanToday = new PocSyncTask
        {
            Code = "daily_task",
            ScheduleType = "Daily",
            ScheduleTime = "02:30",
            IsEnabled = true,
            LastRunAt = baseTime.AddHours(-1)
        };
        Assert.False(PocDataSyncService.IsTaskDue(dailyRanToday, baseTime));

        var interval = new PocSyncTask
        {
            Code = "interval_task",
            ScheduleType = "Interval",
            IntervalMinutes = 30,
            IsEnabled = true,
            LastRunAt = baseTime.AddMinutes(-29)
        };
        // 第二阶段：Interval 语义——间隔未满不触发，间隔满或从未运行才到期。
        Assert.False(PocDataSyncService.IsTaskDue(interval, baseTime));
        Assert.True(PocDataSyncService.IsTaskDue(interval, baseTime.AddMinutes(1)));
        var intervalNeverRan = new PocSyncTask
        {
            Code = "interval_task",
            ScheduleType = "Interval",
            IntervalMinutes = 30,
            IsEnabled = true,
            LastRunAt = null
        };
        Assert.True(PocDataSyncService.IsTaskDue(intervalNeverRan, baseTime));

        var manual = new PocSyncTask { Code = "m", ScheduleType = "Manual", IsEnabled = true };
        Assert.False(PocDataSyncService.IsTaskDue(manual, baseTime));
        var disabledDaily = new PocSyncTask
        {
            Code = "m",
            ScheduleType = "Daily",
            ScheduleTime = "00:00",
            IsEnabled = false
        };
        Assert.False(PocDataSyncService.IsTaskDue(disabledDaily, baseTime));
    }
}

/// <summary>
/// 数据同步测试环境：单个共享 SQLite 内存连接承载全部数据（报表中心主库表 + 源/目标业务表），
/// 存储客户端与执行管线客户端都绑定该连接，INSERT 兜底写入器直接消费共享连接。
/// </summary>
public sealed class PocDataSyncTestEnvironment : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _database;
    private long _nextId = 70_000;

    /// <summary>共享的 SQLite 主库客户端，供测试直接播种实体或断言库内状态。</summary>
    public SqlSugarClient Database => _database;

    /// <summary>同步任务与运行日志存储。</summary>
    public PocDataSyncStore SyncStore { get; }

    /// <summary>数据资产存储（数据源/数据集）。</summary>
    public PocDataAssetStore DataAssets { get; }

    /// <summary>数据资产应用服务（合法保存数据源/数据集/报表）。</summary>
    public PocDataAssetService DataAssetService { get; }

    /// <summary>同步任务应用服务（绑定共享连接）。</summary>
    public PocDataSyncService SyncService { get; }

    /// <summary>同步包应用服务（审计目录已重定向到临时目录）。</summary>
    public PocDataSyncPackageService PackageService { get; }

    /// <summary>伪造的 Cron 调度网关：记录注册/摘除调用序列，供调度生命周期用例断言。</summary>
    public FakePocDataSyncScheduleGateway ScheduleGateway { get; }

    /// <summary>一次性导入审计目录。</summary>
    public string AuditDirectory { get; }

    /// <summary>源数据集编码。</summary>
    public string SourceDatasetCode { get; } = "sync_src_dataset";

    /// <summary>目标数据源编码（SQLite 测试方言）。</summary>
    public string TargetSourceCode { get; } = "tgt_sqlite";

    /// <summary>初始化测试环境：建库建表并装配存储与服务。</summary>
    public PocDataSyncTestEnvironment()
    {
        AuditDirectory = Path.Combine(
            Path.GetTempPath(),
            "hd-report-sync-tests",
            Guid.NewGuid().ToString("N"));
        _connection = new NonClosingSqliteConnection("Data Source=:memory:");
        _connection.Open();
        _database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (property, column) =>
                {
                    if (new NullabilityInfoContext().Create(property).WriteState is NullabilityState.Nullable)
                    {
                        column.IsNullable = true;
                    }
                }
            }
        });
        _database.Aop.DataExecuting = (_, entityInfo) =>
        {
            if (entityInfo.OperationType != DataFilterType.InsertByObject ||
                !entityInfo.EntityColumnInfo.IsPrimarykey ||
                entityInfo.EntityColumnInfo.PropertyInfo.PropertyType != typeof(long))
            {
                return;
            }

            var id = (long)entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue)!;
            if (id == 0)
            {
                entityInfo.SetValue(Interlocked.Increment(ref _nextId));
            }
        };
        // SqlSugar 允许注入既有连接：主存储客户端直接绑定共享连接且整个测试期间不释放，
        // 因此可以安全地用它建表；逐请求释放的执行管线客户端在工厂里改用 NonClosing 包装。
        _database.Ado.Connection = _connection;
        _database.Ado.Open();
        _database.CodeFirst.InitTables(
            typeof(PocReport),
            typeof(PocDataSource),
            typeof(PocReportDataset),
            typeof(PocSyncTask),
            typeof(PocSyncRunLog));

        SyncStore = new PocDataSyncStore(_database);
        DataAssets = new PocDataAssetStore(_database);
        DataAssetService = new PocDataAssetService(DataAssets);
        var executor = new PocDataSyncExecutor(CreateBoundClient);
        ScheduleGateway = new FakePocDataSyncScheduleGateway();
        // 伪造网关注入两个服务：保存/导入/删除/对账路径的调度钩子都会落到它，真实调度器不参与单测。
        SyncService = new PocDataSyncService(
            SyncStore,
            DataAssets,
            _database,
            executor,
            scheduleGateway: ScheduleGateway);
        PackageService = new PocDataSyncPackageService(
            SyncStore,
            DataAssets,
            DataAssetService,
            scheduleGateway: ScheduleGateway)
        {
            AuditDirectory = AuditDirectory
        };
    }

    /// <summary>创建绑定共享连接的新 SqlSugar 客户端，供执行管线与预览取数复用。</summary>
    /// <param name="dbType">数据源声明的数据库类型（测试忽略，一律绑定 SQLite）。</param>
    /// <param name="connectionString">数据源连接串（测试忽略）。</param>
    /// <param name="timeoutSeconds">命令超时秒数（测试忽略）。</param>
    /// <param name="cancellationToken">取消令牌（测试忽略）。</param>
    /// <returns>绑定共享连接的 SqlSugar 客户端。</returns>
    public SqlSugarClient CreateBoundClient(
        string dbType,
        string connectionString,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        _ = dbType;
        _ = connectionString;
        _ = timeoutSeconds;
        _ = cancellationToken;
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute
        });
        client.Ado.Connection = _connection;
        return client;
    }

    /// <summary>创建源数据表并插入样例行；visit_date 用 HIS 常见的 yyyyMMdd 文本形态。</summary>
    /// <param name="rows">样例行（卡号、金额、就诊日期、科室码）。</param>
    public Task SeedSourceTableAsync(params (string CardNo, string Amount, string VisitDate, string DeptCode)[] rows)
    {
        var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_src_demo (
                id INTEGER PRIMARY KEY,
                card_no TEXT,
                amount TEXT,
                visit_date TEXT,
                dept_code TEXT
            )
            """;
        command.ExecuteNonQuery();
        foreach (var row in rows)
        {
            var insert = _connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO sync_src_demo (card_no, amount, visit_date, dept_code) " +
                $"VALUES ('{row.CardNo}', '{row.Amount}', '{row.VisitDate}', '{row.DeptCode}')";
            insert.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <summary>播种一个可执行的同步任务（源数据源 + 数据集 + 目标数据源 + 映射 + 任务行）。</summary>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="conflictPolicy">冲突策略。</param>
    /// <param name="requireDeptCode">是否把科室码映射配置为必填（用于行级失败场景）。</param>
    /// <param name="sourceDbType">源数据源类型文本（SQLite 用于兜底写入器链路，Oracle 用于包链路）。</param>
    /// <param name="targetDbType">目标数据源类型文本。</param>
    /// <param name="targetSourceCode">要播种的目标数据源编码；传 null 表示由调用方另行创建。</param>
    /// <param name="taskTargetSourceCode">任务绑定与导出描述使用的目标数据源编码。</param>
    public async Task SeedTaskAsync(
        string taskCode,
        string conflictPolicy,
        bool requireDeptCode = false,
        string sourceDbType = "SQLite",
        string targetDbType = "SQLite",
        string? targetSourceCode = null,
        string? taskTargetSourceCode = null)
    {
        // SQLite 不在受支持数据源白名单内，源数据源直接落到存储表，模拟测试方言环境；
        // 数据源类型只是文本标注，连接一律走绑定工厂。
        await _database.Insertable(new PocDataSource
        {
            Code = "src_sqlite",
            Name = "同步源库",
            DbType = sourceDbType,
            ConnectionString = "Data Source=:memory:",
            DefaultTimeoutSeconds = 30,
            DefaultMaxRows = 20_000,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommandAsync();
        // 目标数据源默认由本方法播种；包链路（taskTargetSourceCode 指向服务创建的数据源）跳过播种。
        var insertedTargetCode = targetSourceCode ?? (taskTargetSourceCode is null ? TargetSourceCode : null);
        if (insertedTargetCode is not null)
        {
            await _database.Insertable(new PocDataSource
            {
                Code = insertedTargetCode,
                Name = "同步目标库",
                DbType = targetDbType,
                ConnectionString = "Data Source=:memory:",
                DefaultTimeoutSeconds = 30,
                DefaultMaxRows = 20_000,
                Revision = 1,
                IsEnabled = true
            }).ExecuteCommandAsync();
        }
        await _database.Insertable(new PocReportDataset
        {
            Code = SourceDatasetCode,
            Name = "同步源数据集",
            DataSourceCode = "src_sqlite",
            QueryText = "SELECT id, card_no AS cardNo, amount, visit_date AS visitDate, dept_code AS deptCode FROM sync_src_demo ORDER BY id",
            FieldSchemaJson = "{\"Variables\":[],\"Fields\":[" +
                "{\"Code\":\"cardNo\",\"DisplayName\":\"卡号\",\"DataType\":\"string\"}," +
                "{\"Code\":\"amount\",\"DisplayName\":\"金额\",\"DataType\":\"number\"}," +
                "{\"Code\":\"visitDate\",\"DisplayName\":\"就诊日期\",\"DataType\":\"date\"}," +
                "{\"Code\":\"deptCode\",\"DisplayName\":\"科室码\",\"DataType\":\"string\"}]}",
            TimeoutSeconds = 30,
            MaxRows = 20_000,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommandAsync();

        var mappings = new List<PocSyncColumnMapping>
        {
            new("cardNo", "CARD_NO", "string", 16, true, [new PocSyncConverterStep("trim")]),
            new("amount", "AMOUNT", "decimal", null, true, [
                new PocSyncConverterStep("toDecimal", new Dictionary<string, string?> { ["scale"] = "2" })
            ]),
            new("visitDate", "VISIT_DATE", "datetime", null, true, [
                new PocSyncConverterStep("toDate", new Dictionary<string, string?> { ["format"] = "yyyyMMdd" })
            ]),
            requireDeptCode
                ? new PocSyncColumnMapping("deptCode", "DEPT_CODE", "string", 8, false, [new PocSyncConverterStep("required")])
                : new PocSyncColumnMapping("deptCode", "DEPT_CODE", "string", 8, true, [])
        };
        var definition = new PocSyncTaskDefinition(
            mappings,
            new PocSyncScheduleDefinition("Manual", null, 0));
        await SyncStore.SaveTaskAsync(new PocSyncTaskMutation(
            taskCode,
            "同步演示任务",
            SourceDatasetCode,
            taskTargetSourceCode ?? targetSourceCode ?? TargetSourceCode,
            "sync_tgt_demo",
            PocDataSyncService.SerializeDefinition(definition),
            conflictPolicy,
            "Manual",
            null,
            0,
            null,
            true,
            0));
    }

    /// <summary>列出共享连接中的全部表名（仅诊断用）。</summary>
    /// <returns>表名列表。</returns>
    public IReadOnlyList<string> ListTablesRaw()
    {
        var tables = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    /// <summary>读取目标表全部行（列名大小写不敏感），供结果断言。</summary>
    /// <param name="tableName">目标表名。</param>
    /// <returns>以列名为键的行列表。</returns>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryTargetRows(string tableName)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {tableName} ORDER BY rowid";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>释放共享连接，销毁内存库并清理审计临时目录。</summary>
    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        try
        {
            if (Directory.Exists(AuditDirectory))
            {
                Directory.Delete(AuditDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结果。
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 测试专用 SQLite 连接：拦截 Close/Dispose 的空操作，
/// 保证 SqlSugar 客户端逐请求释放时不会关闭共享的 :memory: 连接导致内存库清空。
/// 继承 SqliteConnection 是因为 SqlSugar 的 SqliteProvider 会把连接强转回具体类型。
/// </summary>
public sealed class NonClosingSqliteConnection : SqliteConnection
{
    /// <summary>初始化 NonClosingSqliteConnection 实例。</summary>
    /// <param name="connectionString">SQLite 连接串。</param>
    public NonClosingSqliteConnection(string connectionString)
        : base(connectionString)
    {
    }

    /// <summary>拦截关闭请求：共享连接的生命周期由测试环境统一管理。</summary>
    public override void Close()
    {
    }

    /// <summary>拦截释放请求：共享连接的生命周期由测试环境统一管理。</summary>
    /// <param name="disposing">是否处于显式释放路径。</param>
    protected override void Dispose(bool disposing)
    {
    }
}
