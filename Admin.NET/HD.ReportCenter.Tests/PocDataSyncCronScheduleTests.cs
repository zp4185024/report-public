using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 伪造的 Cron 调度网关：不触碰真实 Furion 调度器，把注册/摘除调用记录为
/// `taskCode|cronExpression` 与 `taskCode` 序列，供生命周期用例断言调用顺序与参数。
/// </summary>
public sealed class FakePocDataSyncScheduleGateway : IPocDataSyncScheduleGateway
{
    /// <summary>注册调用序列，元素形如 `taskCode|cronExpression`。</summary>
    public List<string> Registered { get; } = [];

    /// <summary>摘除调用序列，元素为 taskCode。</summary>
    public List<string> Removed { get; } = [];

    /// <summary>记录一次注册调用。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    /// <param name="taskName">任务名称（本伪造实现不关心，仅消费参数防忽略警告）。</param>
    /// <param name="cronExpression">Cron 表达式。</param>
    public void RegisterCronJob(string taskCode, string taskName, string cronExpression)
    {
        _ = taskName;
        Registered.Add($"{taskCode}|{cronExpression}");
    }

    /// <summary>记录一次摘除调用。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    public void RemoveCronJob(string taskCode)
    {
        Removed.Add(taskCode);
    }
}

/// <summary>
/// 数据同步 Cron 调度特性测试：覆盖 IsTaskDue 扫描排除、保存校验（含表达式口径）、
/// 落库断言（防 store 列映射遗漏）、同步包导出导入闭环与注册钩子、调度网关生命周期
/// 与派生 ID 截断规则。表达式口径来自 Furion 小样实测：TimeCrontab 默认 5 段格式，
/// `?`/L/W/# 拓展可用，6 段（含秒）被拒绝，乱串与缺段被拒绝。
/// </summary>
public sealed class PocDataSyncCronScheduleTests : IDisposable
{
    private readonly PocDataSyncTestEnvironment _environment;

    /// <summary>初始化测试实例：搭建内存库测试环境（含伪造调度网关）。</summary>
    public PocDataSyncCronScheduleTests()
    {
        _environment = new PocDataSyncTestEnvironment();
    }

    /// <summary>释放测试环境持有的连接与临时资源。</summary>
    public void Dispose()
    {
        _environment.Dispose();
        _source.Dispose();
        _target.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 搭建可执行 SaveAsync 的基础环境（源表 + 源/目标数据源 + 数据集，同一环境只播种一次），
    /// 返回启用的任务保存输入骨架。后续需要第二个任务时直接用 BuildInput 构造，不重复播种。
    /// </summary>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="cronExpression">Cron 表达式（null 表示非 Cron 调度）。</param>
    /// <param name="enabled">任务是否启用。</param>
    /// <returns>可按需修改后提交 SaveAsync 的任务保存输入。</returns>
    private async Task<PocSyncTaskInput> SeedCronInputAsync(string taskCode, string? cronExpression, bool enabled = true)
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_cron_seed_base", "Append", targetDbType: "SqlServer");
        return BuildInput(taskCode, cronExpression, enabled);
    }

    /// <summary>构造任务保存输入骨架：单映射（卡号→CARD_NO），调度类型随表达式有无自动切换。</summary>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="cronExpression">Cron 表达式（null 表示非 Cron 调度）。</param>
    /// <param name="enabled">任务是否启用。</param>
    /// <returns>可按需修改后提交 SaveAsync 的任务保存输入。</returns>
    private PocSyncTaskInput BuildInput(string taskCode, string? cronExpression, bool enabled = true)
    {
        return new PocSyncTaskInput(
            taskCode,
            "Cron 调度任务",
            _environment.SourceDatasetCode,
            "tgt_sqlite",
            "SYNC_TGT_CRON",
            "Append",
            cronExpression is null ? "Manual" : "Cron",
            null,
            0,
            new List<PocSyncColumnMapping>
            {
                new("cardNo", "CARD_NO", "string", 16, true, [])
            },
            enabled,
            0,
            ScheduleCron: cronExpression);
    }

    /// <summary>验证扫描到期判定：Cron 类型由 Furion 触发器直接驱动，扫描作业在任意时点都不触发它。</summary>
    [Fact]
    public void IsTaskDue_cron_type_is_never_due_in_scan()
    {
        var baseTime = new DateTimeOffset(2026, 11, 4, 3, 0, 0, TimeSpan.FromHours(8));
        var cronTask = new PocSyncTask
        {
            Code = "cron_task",
            ScheduleType = "Cron",
            ScheduleCron = "*/2 * * * *",
            IsEnabled = true
        };

        // 从未运行（LastRunAt 为空）在任意时点都不进入扫描执行，避免与触发器双重执行。
        Assert.False(PocDataSyncService.IsTaskDue(cronTask, baseTime));

        // 已运行过的任务（LastRunAt 为过去时间）同样不进入扫描，扫描对 Cron 永不负责。
        var ranRecently = new PocSyncTask
        {
            Code = "cron_task",
            ScheduleType = "Cron",
            ScheduleCron = "*/2 * * * *",
            IsEnabled = true,
            LastRunAt = baseTime.AddMinutes(-1)
        };
        Assert.False(PocDataSyncService.IsTaskDue(ranRecently, baseTime));

        // 停用的 Cron 任务也不触发（与所有类型的停用语义一致）。
        var disabled = new PocSyncTask { Code = "cron_task", ScheduleType = "Cron", IsEnabled = false };
        Assert.False(PocDataSyncService.IsTaskDue(disabled, baseTime));
    }

    /// <summary>
    /// 验证保存校验的表达式口径：合法 5 段（含 `?` 与 L 拓展）通过；乱串、缺段（4 段）、
    /// 6 段（含秒）被拒绝且错误文案携带原始表达式（与 Furion 小样实测结论一致）。
    /// </summary>
    /// <param name="cronExpression">待保存的 Cron 表达式。</param>
    /// <param name="shouldPass">是否应保存成功。</param>
    [Theory]
    [InlineData("*/2 * * * *", true)]
    [InlineData("0 2 * * ?", true)]
    [InlineData("0 2 L * ?", true)]
    [InlineData("* * * * *", true)]
    [InlineData("bad-cron", false)]
    [InlineData("0 2 * *", false)]
    [InlineData("0 2 * * * ?", false)]
    [InlineData("0/30 * * * * *", false)]
    public async Task SaveAsync_validates_cron_expression_with_five_segment_rule(
        string cronExpression,
        bool shouldPass)
    {
        var input = await SeedCronInputAsync("sync_cron_rule", cronExpression);

        if (shouldPass)
        {
            var saved = await _environment.SyncService.SaveAsync("sync_cron_rule", input);
            Assert.Equal("Cron", saved.ScheduleType);
            Assert.Equal(cronExpression, saved.ScheduleCron);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<PocDataSyncException>(
                () => _environment.SyncService.SaveAsync("sync_cron_rule", input));
            Assert.Equal("SYNC_SCHEDULE_INVALID", exception.Code);
            // 错误文案必须携带原始表达式，让管理员能定位是哪一个表达式出了问题。
            Assert.Contains(cronExpression, exception.Message, StringComparison.Ordinal);
            // 校验失败的任务不允许落库（不允许“保存成功但永不触发”的带病状态）。
            Assert.Null(await _environment.SyncStore.GetTaskAsync("sync_cron_rule"));
        }
    }

    /// <summary>验证 Cron 类型表达式必填：空白表达式在保存时被拒绝，不产生落库记录。</summary>
    [Fact]
    public async Task SaveAsync_rejects_cron_type_with_blank_expression()
    {
        var input = await SeedCronInputAsync("sync_cron_blank", "   ");

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(
            () => _environment.SyncService.SaveAsync("sync_cron_blank", input));

        Assert.Equal("SYNC_SCHEDULE_INVALID", exception.Code);
        Assert.Null(await _environment.SyncStore.GetTaskAsync("sync_cron_blank"));
    }

    /// <summary>验证落库与裁剪：Cron 保存后 ScheduleCron 列有值（防 store 列映射遗漏的静默丢数），DefinitionJson 同步携带；切回非 Cron 后表达式被裁剪为空。</summary>
    [Fact]
    public async Task SaveAsync_persists_schedule_cron_and_trims_it_for_non_cron_types()
    {
        var input = await SeedCronInputAsync("sync_cron_store", "0 2 * * ?");

        // 第一阶段：Cron 保存——实体列与调度定义 JSON 都必须携带表达式。
        await _environment.SyncService.SaveAsync("sync_cron_store", input);
        var stored = await _environment.SyncStore.GetTaskAsync("sync_cron_store");
        Assert.NotNull(stored);
        Assert.Equal("0 2 * * ?", stored!.ScheduleCron);
        var definition = PocDataSyncService.DeserializeDefinition(stored.DefinitionJson);
        Assert.Equal("0 2 * * ?", definition.Schedule!.CronExpression);

        // 第二阶段：切回 Manual 保存——ScheduleCron 列与定义 JSON 都被裁剪，调度摘除钩子同步触发。
        await _environment.SyncService.SaveAsync(
            "sync_cron_store",
            input with { ScheduleType = "Manual", ScheduleCron = null, Revision = stored.Revision });
        var trimmed = await _environment.SyncStore.GetTaskAsync("sync_cron_store");
        Assert.NotNull(trimmed);
        Assert.Null(trimmed!.ScheduleCron);
        var trimmedDefinition = PocDataSyncService.DeserializeDefinition(trimmed.DefinitionJson);
        Assert.Null(trimmedDefinition.Schedule!.CronExpression);
    }

    /// <summary>
    /// 验证保存/启停/删除生命周期下的调度网关调用：启用 Cron 注册、停用摘除、
    /// 修改表达式重新启用按新表达式注册、删除再摘除。
    /// </summary>
    [Fact]
    public async Task Cron_lifecycle_registers_and_removes_across_save_toggle_and_delete()
    {
        var input = await SeedCronInputAsync("sync_cron_life", "*/2 * * * *");
        var gateway = _environment.ScheduleGateway;

        // 第一阶段：启用保存 → 注册一次。
        await _environment.SyncService.SaveAsync("sync_cron_life", input);
        Assert.Contains("sync_cron_life|*/2 * * * *", gateway.Registered);
        Assert.Empty(gateway.Removed);

        // 第二阶段：停用 → 摘除一次。
        var stored = await _environment.SyncStore.GetTaskAsync("sync_cron_life");
        await _environment.SyncService.SaveAsync(
            "sync_cron_life",
            input with { Enabled = false, Revision = stored!.Revision });
        Assert.Equal(["sync_cron_life"], gateway.Removed);

        // 第三阶段：改表达式重新启用 → 按新表达式再次注册（幂等覆盖旧触发器定义）。
        await _environment.SyncService.SaveAsync(
            "sync_cron_life",
            input with { ScheduleCron = "0 2 * * ?", Enabled = true, Revision = 2 });
        Assert.Contains("sync_cron_life|0 2 * * ?", gateway.Registered);
        Assert.Equal(2, gateway.Registered.Count);

        // 第四阶段：删除 → 摘除第二次。
        await _environment.SyncService.DeleteAsync("sync_cron_life");
        Assert.Equal(2, gateway.Removed.Count(code => code == "sync_cron_life"));
    }

    /// <summary>验证启动对账：把所有“启用且 Cron”任务批量补挂，停用与非 Cron 任务不注册。</summary>
    [Fact]
    public async Task Reconcile_registers_enabled_cron_tasks_only()
    {
        var cronInput = await SeedCronInputAsync("sync_cron_reconcile", "*/5 * * * *");
        await _environment.SyncService.SaveAsync("sync_cron_reconcile", cronInput);
        var manualInput = BuildInput("sync_manual_reconcile", null);
        await _environment.SyncService.SaveAsync("sync_manual_reconcile", manualInput);
        var gateway = _environment.ScheduleGateway;
        gateway.Registered.Clear();
        gateway.Removed.Clear();

        var reconciled = await _environment.SyncService.ReconcileCronSchedulesAsync();

        Assert.Equal(1, reconciled);
        Assert.Equal(["sync_cron_reconcile|*/5 * * * *"], gateway.Registered);
        Assert.Empty(gateway.Removed);
    }

    /// <summary>
    /// 验证派生 ID 规则：短编码直接拼接、超长编码截断加 8 位哈希后总长 ≤64（列长约束），
    /// 不同任务码截断后哈希可区分，同一任务码多次派生保持稳定。
    /// </summary>
    [Fact]
    public void Derived_ids_stay_within_column_length_and_stay_distinct()
    {
        // 短编码快路径：前缀 + 任务码原样返回。
        Assert.Equal("job_rcSync_sync_cron", PocDataSyncCronScheduleIdentity.DeriveJobId("sync_cron"));
        Assert.Equal("trigger_rcSync_sync_cron", PocDataSyncCronScheduleIdentity.DeriveTriggerId("sync_cron"));

        // 超长编码：最长 64 字符任务码与另一个仅末位不同的任务码，派生 ID 都不超 64 且彼此可区分。
        var baseCode = new string('a', 64);
        var otherCode = new string('a', 63) + "b";
        var baseJobId = PocDataSyncCronScheduleIdentity.DeriveJobId(baseCode);
        var otherJobId = PocDataSyncCronScheduleIdentity.DeriveJobId(otherCode);
        Assert.True(baseJobId.Length <= PocDataSyncCronScheduleIdentity.MaxIdLength, baseJobId);
        Assert.True(otherJobId.Length <= PocDataSyncCronScheduleIdentity.MaxIdLength, otherJobId);
        Assert.NotEqual(baseJobId, otherJobId);
        var baseTriggerId = PocDataSyncCronScheduleIdentity.DeriveTriggerId(baseCode);
        Assert.True(baseTriggerId.Length <= PocDataSyncCronScheduleIdentity.MaxIdLength, baseTriggerId);

        // 稳定性：同一任务码重复派生结果一致（重启后触发器 ID 不漂移）。
        Assert.Equal(baseJobId, PocDataSyncCronScheduleIdentity.DeriveJobId(baseCode));
    }

    /// <summary>
    /// 验证同步包闭环：含 Cron 的任务导出→JSON 往返→导入后表达式不丢失
    /// （实体列与 DefinitionJson 都携带），且导入成功后注册钩子按包内表达式被调用。
    /// </summary>
    [Fact]
    public async Task Package_round_trip_preserves_cron_and_invokes_registration_hook()
    {
        // 源环境：播种 Oracle 链路任务并改写为 Cron 调度（直接走存储层，绕过 SaveAsync 的数据源校验）。
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync(
            "sync_pkg_cron",
            "Append",
            sourceDbType: "Oracle",
            targetDbType: "Oracle",
            targetSourceCode: null,
            taskTargetSourceCode: "tgt_oracle");
        // 导出链路要求目标数据源真实存在（读取实体组装包），与同步包测试同构地用服务创建。
        await _environment.DataAssetService.SaveDataSourceAsync("tgt_oracle", new PocDataSourceInput(
            "tgt_oracle",
            "Oracle 目标",
            "Oracle",
            "188.188.2.5",
            1521,
            "orclpdb",
            "writer",
            "pkg-secret",
            30,
            20_000,
            Enabled: true,
            Revision: 0));
        var task = await _environment.SyncStore.GetTaskAsync("sync_pkg_cron");
        var definition = PocDataSyncService.DeserializeDefinition(task!.DefinitionJson);
        var cronDefinition = new PocSyncTaskDefinition(
            definition.Mappings,
            new PocSyncScheduleDefinition("Cron", null, 0, "0 2 * * ?"));
        await _environment.SyncStore.SaveTaskAsync(new PocSyncTaskMutation(
            task.Code,
            task.Name,
            task.SourceDatasetCode,
            task.TargetDataSourceCode,
            task.TargetTable,
            PocDataSyncService.SerializeDefinition(cronDefinition),
            task.ConflictPolicy,
            "Cron",
            null,
            0,
            "0 2 * * ?",
            task.IsEnabled,
            task.Revision));

        // 导出 → 序列化/反序列化往返（模拟跨环境文件传输）。
        var package = await _environment.PackageService.ExportAsync("sync_pkg_cron");
        Assert.Equal("Cron", package.ScheduleType);
        Assert.Equal("0 2 * * ?", package.ScheduleCron);
        var roundTripped = JsonConvert.DeserializeObject<PocDataSyncPackage>(
            PocDataSyncPackageFormat.SerializeCanonical(package));
        Assert.NotNull(roundTripped);
        Assert.Equal("0 2 * * ?", roundTripped!.ScheduleCron);

        // 目标环境导入：任务实体列、定义 JSON 与注册钩子三处都要命中表达式。
        var target = new PocDataSyncTestEnvironment();
        try
        {
            await target.DataAssetService.SaveDataSourceAsync("tgt_oracle", new PocDataSourceInput(
                "tgt_oracle",
                "Oracle 目标",
                "Oracle",
                "188.188.2.5",
                1521,
                "orclpdb",
                "writer",
                "pkg-secret",
                30,
                20_000,
                Enabled: true,
                Revision: 0));
            var result = await target.PackageService.ImportAsync(new PocDataSyncImportRequest(
                roundTripped,
                "tgt_oracle",
                "Create",
                DryRunPerformed: true));
            Assert.Equal("Created", result.TaskAction);
            var stored = await target.SyncStore.GetTaskAsync("sync_pkg_cron");
            Assert.NotNull(stored);
            Assert.Equal("Cron", stored!.ScheduleType);
            Assert.Equal("0 2 * * ?", stored.ScheduleCron);
            var storedDefinition = PocDataSyncService.DeserializeDefinition(stored.DefinitionJson);
            Assert.Equal("0 2 * * ?", storedDefinition.Schedule!.CronExpression);
            Assert.Contains("sync_pkg_cron|0 2 * * ?", target.ScheduleGateway.Registered);
        }
        finally
        {
            target.Dispose();
        }
    }

    /// <summary>验证旧包回归：无 scheduleCron 字段的旧版包导入行为不变（表达式保持空、不触发注册）。</summary>
    [Fact]
    public async Task Package_import_without_cron_field_keeps_legacy_behavior()
    {
        var package = await SeedPackageAsync();

        // 构造“旧导出端”的包文本：显式移除 scheduleCron 字段，模拟旧版本导出的 JSON。
        var jobject = JObject.Parse(PocDataSyncPackageFormat.SerializeCanonical(package));
        ((JObject)jobject).Property("scheduleCron")?.Remove();
        var legacyJson = jobject.ToString();
        var legacyPackage = JsonConvert.DeserializeObject<PocDataSyncPackage>(legacyJson);
        Assert.NotNull(legacyPackage);
        Assert.Null(legacyPackage!.ScheduleCron);

        var result = await _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
            legacyPackage,
            "tgt_oracle",
            "Create",
            DryRunPerformed: true));
        Assert.Equal("Created", result.TaskAction);
        var stored = await _target.SyncStore.GetTaskAsync("sync_pkg_demo");
        Assert.NotNull(stored);
        Assert.Equal("Manual", stored!.ScheduleType);
        Assert.Null(stored.ScheduleCron);
        // 非 Cron 导入不允许出现注册调用（摘除调用对伪造网关是幂等空操作，允许发生）。
        Assert.DoesNotContain(_target.ScheduleGateway.Registered, item => item.StartsWith("sync_pkg_demo", StringComparison.Ordinal));
    }

    /// <summary>
    /// 搭建一个可导出的同步任务（与同步包测试同构）：源环境播种任务，目标环境播种 Oracle 数据源。
    /// </summary>
    /// <returns>源环境导出的同步包。</returns>
    private async Task<PocDataSyncPackage> SeedPackageAsync()
    {
        await _source.SeedSourceTableAsync(("A-001", "1.5", "20240101", "CARD"));
        await _source.SeedTaskAsync(
            "sync_pkg_demo",
            "Append",
            sourceDbType: "Oracle",
            targetDbType: "Oracle",
            targetSourceCode: null,
            taskTargetSourceCode: "tgt_oracle");
        await _source.DataAssetService.SaveDataSourceAsync("tgt_oracle", new PocDataSourceInput(
            "tgt_oracle",
            "Oracle 目标",
            "Oracle",
            "188.188.2.5",
            1521,
            "orclpdb",
            "writer",
            "pkg-secret",
            30,
            20_000,
            Enabled: true,
            Revision: 0));
        await _target.DataAssetService.SaveDataSourceAsync("tgt_oracle", new PocDataSourceInput(
            "tgt_oracle",
            "Oracle 目标",
            "Oracle",
            "188.188.2.5",
            1521,
            "orclpdb",
            "writer",
            "pkg-secret",
            30,
            20_000,
            Enabled: true,
            Revision: 0));
        return await _source.PackageService.ExportAsync("sync_pkg_demo");
    }

    /// <summary>包回归用例的源环境（与本类主环境分离，保持各用例内存库互不干扰）。</summary>
    private readonly PocDataSyncTestEnvironment _source = new();

    /// <summary>包回归用例的目标环境。</summary>
    private readonly PocDataSyncTestEnvironment _target = new();
}
