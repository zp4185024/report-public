using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 数据同步包导出/校验/导入行为验证：导出剥离连接串与密码、校验拦截坏映射与停用数据集、
/// 导入 Create/Replace 决策与数据集被报表引用时的替换拒绝，全部在 SQLite 内存库环境完成。
/// </summary>
public sealed class PocDataSyncPackageTests : IDisposable
{
    private readonly PocDataSyncTestEnvironment _source;
    private readonly PocDataSyncTestEnvironment _target;

    /// <summary>初始化测试实例：搭建源环境与目标环境两套独立内存库。</summary>
    public PocDataSyncPackageTests()
    {
        _source = new PocDataSyncTestEnvironment();
        _target = new PocDataSyncTestEnvironment();
    }

    /// <summary>释放两套内存库环境。</summary>
    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 搭建一个可导出的同步任务：源环境播种数据与任务（数据源类型为 Oracle，走完整服务链路语义），
    /// 目标环境播种一个 Oracle 数据源供导入绑定。
    /// </summary>
    private async Task<PocDataSyncPackage> SeedPackageAsync()
    {
        await _source.SeedSourceTableAsync(("A-001", "1.5", "20240101", "CARD"));
        // 源/目标数据源都用 Oracle 类型（走服务链路的真实语义），目标数据源由 SaveDataSourceAsync 创建。
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

    /// <summary>验证导出包结构完整：任务编码、源数据集快照、映射与调度齐全，且全文不含连接串与密码。</summary>
    [Fact]
    public async Task Export_builds_clean_package_without_secrets()
    {
        var package = await SeedPackageAsync();

        Assert.Equal(PocDataSyncPackageFormat.PackageKind, package.PackageKind);
        Assert.Equal(PocDataSyncPackageFormat.PackageVersion, package.PackageVersion);
        Assert.Equal("sync_pkg_demo", package.TaskCode);
        Assert.Equal("sync_src_dataset", package.SourceDataset.Code);
        Assert.Equal("Oracle", package.SourceDataset.DataSourceType);
        Assert.Equal("tgt_oracle", package.TargetDataSourceCode);
        Assert.Equal("Oracle", package.TargetDataSourceType);
        Assert.Equal("sync_tgt_demo", package.TargetTable);
        Assert.Equal("Append", package.ConflictPolicy);
        Assert.Equal("Manual", package.ScheduleType);
        Assert.Equal(4, package.Mappings.Count);

        // 包文本剥离审计字段与连接信息：连接串、密码键与数据库时间戳都不允许出现。
        var json = PocDataSyncPackageFormat.SerializeCanonical(package);
        Assert.DoesNotContain("pkg-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connectionString", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createTime", json, StringComparison.OrdinalIgnoreCase);

        var sha256 = PocDataSyncPackageFormat.ComputeSha256(package);
        Assert.Equal(64, sha256.Length);
        Assert.Equal(sha256, PocDataSyncPackageFormat.ComputeSha256(package));
    }

    /// <summary>验证空库上的只读校验全绿：错误为空、任务与数据集不存在、目标数据源已存在且类型一致。</summary>
    [Fact]
    public async Task Validate_passes_on_empty_target_and_reports_collisions()
    {
        var package = await SeedPackageAsync();

        var validation = await _target.PackageService.ValidateAsync(package);

        Assert.True(validation.Ok, string.Join("; ", validation.Errors.Select(item => item.Message)));
        Assert.Empty(validation.Warnings);
        Assert.False(validation.Collisions!.TaskCodeExists);
        Assert.False(validation.Collisions.DatasetCodeExists);
        Assert.Null(validation.Collisions.DatasetIdentical);
        Assert.Null(validation.Collisions.DatasetReferencedByReports);
        Assert.True(validation.Collisions.TargetDataSourceExists);
        Assert.True(validation.Collisions.TargetDataSourceTypeMatch);
        Assert.Equal("sync_pkg_demo", validation.PackageSummary!.TaskCode);
        Assert.Equal(4, validation.PackageSummary.MappingCount);
    }

    /// <summary>验证校验拦截坏映射：转换器非法（未知转换器）时 validate 报 SYNC_MAPPING_INVALID。</summary>
    [Fact]
    public async Task Validate_rejects_invalid_converter_mapping()
    {
        var package = await SeedPackageAsync();
        var broken = package with
        {
            Mappings = package.Mappings
                .Take(1)
                .Append(new PocSyncColumnMapping("visitDate", "VISIT_DATE", "datetime", null, true,
                    [new PocSyncConverterStep("magic")]))
                .ToArray()
        };

        var validation = await _target.PackageService.ValidateAsync(broken);

        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, item => item.Code == "SYNC_MAPPING_INVALID");
    }

    /// <summary>验证校验拦截 DML：源数据集 SQL 不是只读 SELECT 时报 DATASET_QUERY_NOT_READ_ONLY。</summary>
    [Fact]
    public async Task Validate_rejects_dml_query()
    {
        var package = await SeedPackageAsync();
        var broken = package with
        {
            SourceDataset = package.SourceDataset with
            {
                QueryText = "DELETE FROM sync_src_demo WHERE card_no = 'A-001'"
            }
        };

        var validation = await _target.PackageService.ValidateAsync(broken);

        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, item => item.Code == "DATASET_QUERY_NOT_READ_ONLY");
    }

    /// <summary>验证校验拦截停用数据集：包内数据集停用时导入会被拒绝。</summary>
    [Fact]
    public async Task Validate_rejects_disabled_source_dataset()
    {
        var package = await SeedPackageAsync();
        var broken = package with
        {
            SourceDataset = package.SourceDataset with { IsEnabled = false }
        };

        var validation = await _target.PackageService.ValidateAsync(broken);

        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, item => item.Code == "SYNC_SOURCE_DATASET_DISABLED");
    }

    /// <summary>验证 Create 导入全链路：数据集与任务落库、终态修订号为 1、审计文件落一条 ok 记录。</summary>
    [Fact]
    public async Task Import_create_writes_dataset_and_task_with_audit()
    {
        var package = await SeedPackageAsync();

        var result = await _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
            package,
            "tgt_oracle",
            "Create",
            DryRunPerformed: true));

        Assert.Equal("Created", result.DatasetAction);
        Assert.Equal("Created", result.TaskAction);
        var dataset = Assert.Single(await _target.DataAssets.ListDatasetsAsync());
        Assert.Equal("tgt_oracle", dataset.DataSourceCode);
        Assert.Equal("sync_src_dataset", dataset.Code);
        var task = await _target.SyncStore.GetTaskAsync("sync_pkg_demo");
        Assert.NotNull(task);
        Assert.Equal(1, task!.Revision);
        Assert.Equal("sync_tgt_demo", task.TargetTable);
        Assert.Equal("tgt_oracle", task.TargetDataSourceCode);
        Assert.Equal(4, PocDataSyncService.DeserializeDefinition(task.DefinitionJson).Mappings.Count);

        var auditFiles = Directory.GetFiles(_target.PackageService.AuditDirectory, "*-sync_pkg_demo-ok.json");
        var auditJson = await File.ReadAllTextAsync(auditFiles.Single());
        Assert.Contains("packageSha256", auditJson, StringComparison.Ordinal);
        Assert.Contains("DatasetCreated", auditJson, StringComparison.Ordinal);
        Assert.Contains("TaskCreated", auditJson, StringComparison.Ordinal);
    }

    /// <summary>验证 Create 撞码拒绝：任务已存在时按 SYNC_TASK_ALREADY_EXISTS 拒绝且数据不被覆盖。</summary>
    [Fact]
    public async Task Import_create_rejects_existing_task_code()
    {
        var package = await SeedPackageAsync();
        await _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
            package, "tgt_oracle", "Create", DryRunPerformed: true));
        var before = await _target.SyncStore.GetTaskAsync("sync_pkg_demo");

        // 修改包内映射制造差异，验证 Create 模式不会覆盖既有任务。
        var changed = package with
        {
            Mappings = [package.Mappings[0]]
        };
        var exception = await Assert.ThrowsAsync<PocDataSyncException>(() =>
            _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
                changed, "tgt_oracle", "Create", DryRunPerformed: true)));

        Assert.Equal("SYNC_TASK_ALREADY_EXISTS", exception.Code);
        var after = await _target.SyncStore.GetTaskAsync("sync_pkg_demo");
        Assert.Equal(before!.Revision, after!.Revision);
        Assert.Equal(4, PocDataSyncService.DeserializeDefinition(after.DefinitionJson).Mappings.Count);
    }

    /// <summary>验证 Replace 导入：旧任务被删除重建，数据集内容一致时复用不重写。</summary>
    [Fact]
    public async Task Import_replace_rebuilds_task_and_reuses_identical_dataset()
    {
        var package = await SeedPackageAsync();
        await _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
            package, "tgt_oracle", "Create", DryRunPerformed: true));
        var datasetBefore = Assert.Single(await _target.DataAssets.ListDatasetsAsync());
        _ = (await _target.SyncStore.GetTaskAsync("sync_pkg_demo"))!.Revision;

        // 映射改为 2 列制造任务差异；数据集保持一致，期望任务 Replaced、数据集 Reused。
        var changed = package with
        {
            Mappings = package.Mappings.Take(2).ToArray()
        };
        var result = await _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
            changed, "tgt_oracle", "Replace", DryRunPerformed: true));

        Assert.Equal("Reused", result.DatasetAction);
        Assert.Equal("Replaced", result.TaskAction);
        var datasetAfter = Assert.Single(await _target.DataAssets.ListDatasetsAsync());
        Assert.Equal(datasetBefore.Revision, datasetAfter.Revision);
        var taskAfter = await _target.SyncStore.GetTaskAsync("sync_pkg_demo");
        // Replace 是先删后建：修订号重新从 1 开始，映射按包内容重建为 2 列。
        Assert.Equal(1, taskAfter!.Revision);
        Assert.Equal(2, PocDataSyncService.DeserializeDefinition(taskAfter.DefinitionJson).Mappings.Count);
    }

    /// <summary>验证数据集被报表引用时的一致性替换拒绝：内容不一致且被引用 → SYNC_DATASET_IN_USE。</summary>
    [Fact]
    public async Task Import_replace_rejects_dataset_referenced_by_reports()
    {
        var package = await SeedPackageAsync();
        await _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
            package, "tgt_oracle", "Create", DryRunPerformed: true));
        // 目标环境造一张引用同编码数据集的报表，制造“内容不一致 + 被引用”冲突。
        await _target.Database.Insertable(new PocReport
        {
            Code = "other_report",
            Name = "其他报表",
            SheetName = "其他",
            DatasetCode = package.SourceDataset.Code,
            PreviewSettingsJson = "{}",
            ExportSettingsJson = "{}",
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommandAsync();
        var changed = package with
        {
            SourceDataset = package.SourceDataset with
            {
                QueryText = "SELECT card_no AS cardNo FROM sync_src_demo ORDER BY id"
            }
        };

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(() =>
            _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
                changed, "tgt_oracle", "Replace", DryRunPerformed: true)));

        Assert.Equal("SYNC_DATASET_IN_USE", exception.Code);
        // 引用清单展示的是报表名称，便于管理员直接定位。
        Assert.Contains("其他报表", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>验证导入模式非法时按 SYNC_PACKAGE_MODE_INVALID 拒绝并留下 failed 审计文件。</summary>
    [Fact]
    public async Task Import_rejects_invalid_mode_and_writes_failed_audit()
    {
        var package = await SeedPackageAsync();

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(() =>
            _target.PackageService.ImportAsync(new PocDataSyncImportRequest(
                package, "tgt_oracle", "Upsert", DryRunPerformed: false)));

        Assert.Equal("SYNC_PACKAGE_MODE_INVALID", exception.Code);
        Assert.Null(await _target.SyncStore.GetTaskAsync("sync_pkg_demo"));
        var auditFiles = Directory.GetFiles(_target.PackageService.AuditDirectory, "*-sync_pkg_demo-failed.json");
        var auditJson = await File.ReadAllTextAsync(auditFiles.Single());
        Assert.Contains("\"result\": \"failed\"", auditJson, StringComparison.Ordinal);
    }

    /// <summary>验证包 JSON 反序列化回环：camelCase 规范形态可以完整还原为强类型包对象。</summary>
    [Fact]
    public async Task Package_json_round_trip_preserves_contract()
    {
        var package = await SeedPackageAsync();
        var json = PocDataSyncPackageFormat.SerializeCanonical(package);

        // 反序列化校验前端镜像契约：camelCase 字段名与包内结构一一对应。
        Assert.Contains("\"taskCode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceDataset\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targetDataSourceType\"", json, StringComparison.Ordinal);
        Assert.Contains("\"conflictPolicy\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scheduleType\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mappings\"", json, StringComparison.Ordinal);
        var restored = JsonConvert.DeserializeObject<PocDataSyncPackage>(json);
        Assert.NotNull(restored);
        Assert.Equal(package.TaskCode, restored!.TaskCode);
        Assert.Equal(package.SourceDataset.Code, restored.SourceDataset.Code);
        Assert.Equal(package.Mappings.Count, restored.Mappings.Count);
        Assert.Equal(
            JsonConvert.SerializeObject(package.Mappings, Formatting.None),
            JsonConvert.SerializeObject(restored.Mappings, Formatting.None));
    }
}
