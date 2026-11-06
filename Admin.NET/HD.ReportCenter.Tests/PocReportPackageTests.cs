using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;
using System.Reflection;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 报表包导出/校验/试跑/导入的后端行为验证。
/// 使用 SQLite 内存库 CodeFirst 搭建源环境与目标环境：源环境构造完整报表后导出包，
/// 目标环境验证只读校验、冲突策略与导入终态；试跑仅覆盖参数校验与拦截分支
/// （SQLite 不在受支持数据源之列，不执行真实查询）。
/// </summary>
public sealed class PocReportPackageTests
{
    private const string DatasetCode = "pkg_dept_workload";
    private const string ReportCode = "pkg_demo_report";

    private const string SnapshotJson = """
        {
          "id": "report-pkg_demo_report",
          "rev": 1,
          "name": "报表包演示",
          "locale": "zhCN",
          "sheetOrder": ["sheet-1"],
          "styles": {},
          "sheets": {
            "sheet-1": {
              "id": "sheet-1",
              "name": "科室工作量",
              "cellData": {
                "0": {"0": {"v": "科室：{{params.deptId}}"}},
                "3": {
                  "0": {"v": "{{detail.sequence}}"},
                  "1": {"v": "{{detail.departmentName}}"},
                  "2": {"v": "{{detail.outpatientCount}}"}
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// 验证导出组装：包结构完整、不含数据库 Id/时间戳/修订号（模板修订号除外）、
    /// 不含数据源连接串与密码，且分享保护口令摘要被剥离。
    /// </summary>
    [Fact]
    public async Task ExportAsync_BuildsCleanPackageWithoutSecrets()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDemoReportAsync(enableShareProtection: true);

        var package = await environment.PackageService.ExportAsync(ReportCode);

        Assert.Equal(PocReportPackageFormat.PackageKind, package.PackageKind);
        Assert.Equal(PocReportPackageFormat.PackageVersion, package.PackageVersion);
        Assert.Equal(ReportCode, package.Report.Code);
        Assert.Equal(DatasetCode, package.Report.DatasetCode);
        Assert.Equal("src_oracle", package.SourceDataSourceCode);
        Assert.Equal("Oracle", package.SourceDataSourceType);
        Assert.Equal(1, package.Template.Revision);
        Assert.Contains("@deptId", package.Dataset.QueryText);
        Assert.Single(package.Dataset.FieldSchema.Variables);
        Assert.Equal(3, package.Dataset.FieldSchema.Fields.Count);
        Assert.Single(package.Parameters!);

        // 包文本必须剥离口令摘要与连接信息：ShareProtection 段、密码和连接串键都不允许出现；
        // 模板快照里的 Univer 工作表 id 是业务内容，而数据库审计字段（createTime/updateTime）不允许出现。
        var json = PocReportPackageFormat.SerializeCanonical(package);
        Assert.DoesNotContain("ShareProtection", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pkg-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connectionString", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createTime", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updateTime", json, StringComparison.OrdinalIgnoreCase);

        var sha256 = PocReportPackageFormat.ComputeSha256(package);
        Assert.Equal(64, sha256.Length);
        Assert.Equal(sha256, PocReportPackageFormat.ComputeSha256(package));
    }

    /// <summary>
    /// 验证导出→校验→导入全链路：空目标库上校验通过且无冲突，导入后报表、参数、模板、
    /// 数据集四类数据齐全，报表处于已保存配置（Revision≥1）+ 已保存模板（Revision≥1）状态，
    /// 并落一条 ok 审计文件。
    /// </summary>
    [Fact]
    public async Task ExportValidateImport_RoundTripImportsFullConfigurationGraph()
    {
        using var source = new PocReportPackageTestEnvironment();
        using var target = new PocReportPackageTestEnvironment();
        await source.SeedDemoReportAsync(enableShareProtection: true);
        await target.SeedDataSourceAsync("tgt_oracle", "Oracle");
        var package = await source.PackageService.ExportAsync(ReportCode);

        // 阶段一：目标库只读校验必须通过且四类冲突全部为空，这是导入向导放行下一步的前提。
        var validation = await target.PackageService.ValidateAsync(package);
        Assert.True(validation.Ok, string.Join("; ", validation.Errors.Select(item => item.Message)));
        Assert.False(validation.Collisions!.ReportCodeExists);
        Assert.False(validation.Collisions.DatasetCodeExists);
        Assert.Null(validation.Collisions.DatasetIdentical);
        Assert.Null(validation.Collisions.DatasetReferencedByOther);
        Assert.Equal(1, validation.PackageSummary!.ParameterCount);
        Assert.Equal(3, validation.PackageSummary.FieldCount);
        Assert.Equal("Oracle", validation.PackageSummary.SourceDataSourceType);

        // 阶段二：Create 导入后逐表核对四类数据与终态修订号，并确认落了一条 ok 审计文件。
        var result = await target.PackageService.ImportAsync(new PocReportPackageImportRequest(
            package,
            "tgt_oracle",
            "Create",
            ReplaceDataset: false,
            DryRunPerformed: false));

        Assert.Equal("Created", result.DatasetAction);
        Assert.Equal("Created", result.ReportAction);
        // 终态检查：报表配置修订号与模板修订号都必须从 1 开始，证明已保存配置与模板。
        var report = Assert.Single(target.Database.Queryable<PocReport>().ToList());
        Assert.Equal(ReportCode, report.Code);
        Assert.Equal(1, report.Revision);
        var parameter = Assert.Single(target.Database.Queryable<PocReportParameter>().ToList());
        Assert.Equal("deptId", parameter.Code);
        var template = Assert.Single(target.Database.Queryable<PocReportTemplate>().ToList());
        Assert.Equal(1, template.Revision);
        var dataset = Assert.Single(target.Database.Queryable<PocReportDataset>().ToList());
        Assert.Equal("tgt_oracle", dataset.DataSourceCode);
        Assert.Equal(
            1,
            (await target.ConfigurationService.GetDocumentAsync(ReportCode)).Revision);

        var auditFiles = Directory.GetFiles(target.AuditDirectory, $"*-{ReportCode}-ok.json");
        var audit = Assert.Single(auditFiles);
        var auditJson = await File.ReadAllTextAsync(audit);
        Assert.Contains("packageSha256", auditJson, StringComparison.Ordinal);
        Assert.Contains("DatasetCreated", auditJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证校验拦截 DML SQL：包内查询不是单条只读 SELECT/WITH 时 validate 报
    /// DATASET_QUERY_NOT_READ_ONLY，且导入在写库前即被拒绝（目标库不留下任何报表或数据集痕迹）。
    /// </summary>
    [Fact]
    public async Task Validate_RejectsDmlQueryAndImportRejectsBeforeWriting()
    {
        using var source = new PocReportPackageTestEnvironment();
        using var target = new PocReportPackageTestEnvironment();
        await source.SeedDemoReportAsync();
        await target.SeedDataSourceAsync("tgt_oracle", "Oracle");
        var package = await source.PackageService.ExportAsync(ReportCode);
        var broken = package with
        {
            Dataset = package.Dataset with
            {
                QueryText = "DELETE FROM rc_report WHERE code = @deptId"
            }
        };

        var validation = await source.PackageService.ValidateAsync(broken);
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, item => item.Code == "DATASET_QUERY_NOT_READ_ONLY");

        var exception = await Assert.ThrowsAsync<PocReportPackageException>(() =>
            target.PackageService.ImportAsync(new PocReportPackageImportRequest(
                broken,
                "tgt_oracle",
                "Create",
                ReplaceDataset: false,
                DryRunPerformed: false)));
        Assert.Equal("PACKAGE_VALIDATION_FAILED", exception.Code);
        // 服务端校验先于一切写入：目标库里不允许留下任何包内报表或数据集痕迹。
        Assert.Empty(target.Database.Queryable<PocReport>().ToList());
        Assert.Empty(target.Database.Queryable<PocReportDataset>().ToList());
        Assert.Single(Directory.GetFiles(target.AuditDirectory, $"*-{ReportCode}-failed.json"));
    }

    /// <summary>
    /// 验证校验拦截未定义的 SQL 变量：查询引用了 fieldSchema.variables 之外的 @变量时，
    /// validate 返回 DATASET_PARAMETER_UNDEFINED 而不是让导入带着悬空变量落库。
    /// </summary>
    [Fact]
    public async Task Validate_RejectsUndefinedSqlVariable()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDemoReportAsync();
        var package = await environment.PackageService.ExportAsync(ReportCode);
        var broken = package with
        {
            Dataset = package.Dataset with
            {
                QueryText =
                    "SELECT 1 AS sequence FROM dual WHERE @deptId IS NOT NULL AND @deptCode IS NOT NULL ORDER BY sequence"
            }
        };

        var validation = await environment.PackageService.ValidateAsync(broken);

        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, item => item.Code == "DATASET_PARAMETER_UNDEFINED");
    }

    /// <summary>
    /// 验证变量绑定对齐：数据集变量缺少同名同类型报表参数承接时，validate 返回
    /// DATASET_PARAMETER_UNMAPPED，与线上保存链路 ValidateDatasetVariableBindings 同规则。
    /// </summary>
    [Fact]
    public async Task Validate_RejectsDatasetVariableWithoutParameter()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDemoReportAsync();
        var package = await environment.PackageService.ExportAsync(ReportCode);
        var broken = package with { Parameters = [] };

        var validation = await environment.PackageService.ValidateAsync(broken);

        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, item => item.Code == "DATASET_PARAMETER_UNMAPPED");
    }

    /// <summary>
    /// 验证模板绑定一致性：包内 binding 与按快照重扫的规范绑定不一致时，
    /// validate 返回 TEMPLATE_BINDING_MISMATCH，防止被改动的包绕过绑定重扫。
    /// </summary>
    [Fact]
    public async Task Validate_RejectsTemplateBindingMismatch()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDemoReportAsync();
        var package = await environment.PackageService.ExportAsync(ReportCode);
        var tamperedBinding = package.Template.Binding with
        {
            ParameterBindings =
            [
                .. package.Template.Binding.ParameterBindings,
                new PocParameterBinding("deptId", "sheet-1", 9, 9)
            ]
        };
        var broken = package with { Template = package.Template with { Binding = tamperedBinding } };

        var validation = await environment.PackageService.ValidateAsync(broken);

        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, item => item.Code == "TEMPLATE_BINDING_MISMATCH");
    }

    /// <summary>
    /// 验证包校验兼容 JNPF 迁移快照的字符串合并坐标：存量模板 mergeData 使用
    /// {"startRow":"0","rangeType":"0"} 这类数字字符串（浏览器归一化只覆盖编辑器保存路径），
    /// 坐标可解析且区域不触碰明细行时 validate 必须放行，否则迁移模板的包全部导不进去。
    /// </summary>
    [Fact]
    public async Task Validate_AcceptsNumericStringMergeCoordinatesFromJnpfSnapshot()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDemoReportAsync();
        var package = await environment.PackageService.ExportAsync(ReportCode);
        // 明细 token 位于第 3 行（零基），合并区域 0..1 不与其相交，模拟真实 JNPF 表头合并。
        var snapshot = (JObject)package.Template.Snapshot.DeepClone();
        snapshot["sheets"]!["sheet-1"]!["mergeData"] = new JArray
        {
            new JObject
            {
                ["startRow"] = "0",
                ["endRow"] = "1",
                ["startColumn"] = "0",
                ["endColumn"] = "5",
                ["rangeType"] = "0"
            }
        };
        var migrated = package with { Template = package.Template with { Snapshot = snapshot } };

        var validation = await environment.PackageService.ValidateAsync(migrated);

        Assert.True(validation.Ok, string.Join("; ", validation.Errors.Select(item => item.Message)));
        Assert.DoesNotContain(
            validation.Errors,
            item => item.Code == "TEMPLATE_VALIDATION_FAILED");
    }

    /// <summary>
    /// 验证包校验仍拒绝不可解析的合并坐标：startRow 为非数字文本时 validate 返回
    /// TEMPLATE_VALIDATION_FAILED，保证兼容字符串坐标不会掩盖损坏的包文件。
    /// </summary>
    [Fact]
    public async Task Validate_RejectsNonNumericMergeCoordinatesAsTemplateValidationFailed()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDemoReportAsync();
        var package = await environment.PackageService.ExportAsync(ReportCode);
        var snapshot = (JObject)package.Template.Snapshot.DeepClone();
        snapshot["sheets"]!["sheet-1"]!["mergeData"] = new JArray
        {
            new JObject
            {
                ["startRow"] = "not-a-row",
                ["endRow"] = "1",
                ["startColumn"] = "0",
                ["endColumn"] = "5"
            }
        };
        var corrupted = package with { Template = package.Template with { Snapshot = snapshot } };

        var validation = await environment.PackageService.ValidateAsync(corrupted);

        Assert.False(validation.Ok);
        var issue = Assert.Single(
            validation.Errors.Where(item => item.Code == "TEMPLATE_VALIDATION_FAILED"));
        Assert.Contains("must contain valid row coordinates", issue.Message);
    }

    /// <summary>
    /// 验证 Create 模式撞码拒绝：目标库已有同编码报表时再次 Create 导入抛
    /// PocReportAlreadyExistsException（API 层映射 409），不会覆盖既有报表。
    /// </summary>
    [Fact]
    public async Task Import_CreateModeRejectsExistingReportCode()
    {
        using var environment = new PocReportPackageTestEnvironment();
        var package = await environment.SeedDemoReportAsync();

        await Assert.ThrowsAsync<PocReportAlreadyExistsException>(() =>
            environment.PackageService.ImportAsync(new PocReportPackageImportRequest(
                package,
                "src_oracle",
                "Create",
                ReplaceDataset: false,
                DryRunPerformed: false)));

        Assert.Single(environment.Database.Queryable<PocReport>().ToList());
    }

    /// <summary>
    /// 验证 Replace 模式删除重建：环境里先删除源报表模拟空目标（保留同编码且内容一致的数据集），
    /// 首次 Create 导入复用数据集并新建报表；再次以 Replace 导入改名包时先删旧报表再新建，
    /// 报表名被更新、模板修订号回到 1、参数不重复、数据集不新增行。
    /// </summary>
    [Fact]
    public async Task Import_ReplacesReportAndReusesIdenticalDataset()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDataSourceAsync("tgt_oracle", "Oracle");
        var package = await environment.SeedDemoReportAsync(dataSourceCode: "src_oracle");
        await environment.ConfigurationService.DeleteReportAsync(ReportCode);
        var firstImport = await environment.PackageService.ImportAsync(new PocReportPackageImportRequest(
            package,
            "tgt_oracle",
            "Create",
            ReplaceDataset: false,
            DryRunPerformed: false));
        Assert.Equal("Reused", firstImport.DatasetAction);
        Assert.Equal("Created", firstImport.ReportAction);

        var renamed = package with { Report = package.Report with { Name = "报表包演示报表V2" } };
        var result = await environment.PackageService.ImportAsync(new PocReportPackageImportRequest(
            renamed,
            "tgt_oracle",
            "Replace",
            ReplaceDataset: false,
            DryRunPerformed: true));

        Assert.Equal("Reused", result.DatasetAction);
        Assert.Equal("Replaced", result.ReportAction);
        var report = Assert.Single(environment.Database.Queryable<PocReport>().ToList());
        Assert.Equal("报表包演示报表V2", report.Name);
        Assert.Equal(1, report.Revision);
        Assert.Equal(1, Assert.Single(environment.Database.Queryable<PocReportTemplate>().ToList()).Revision);
        Assert.Single(environment.Database.Queryable<PocReportParameter>().ToList());
        // 数据集复用意味着行数不增加：同编码数据集仍只有一行。
        Assert.Single(environment.Database.Queryable<PocReportDataset>().ToList());
    }

    /// <summary>
    /// 验证数据集内容不一致且被其他报表引用时拒绝替换：抛 DATASET_IN_USE（API 层映射 409），
    /// 并在消息中带引用方名称，保护其他报表的运行数据。
    /// </summary>
    [Fact]
    public async Task Import_RejectsDatasetInUseByOtherReport()
    {
        using var environment = new PocReportPackageTestEnvironment();
        var package = await environment.SeedDemoReportAsync();
        await environment.CreateOtherReportAsync("pkg_other_report");
        var modified = package with
        {
            Dataset = package.Dataset with
            {
                QueryText =
                    "SELECT 2 AS sequence, N'外科' AS departmentName, 43 AS outpatientCount FROM dual WHERE @deptId IS NOT NULL ORDER BY sequence"
            }
        };

        var exception = await Assert.ThrowsAsync<PocReportPackageException>(() =>
            environment.PackageService.ImportAsync(new PocReportPackageImportRequest(
                modified,
                "src_oracle",
                "Create",
                ReplaceDataset: false,
                DryRunPerformed: false)));

        Assert.Equal("DATASET_IN_USE", exception.Code);
        Assert.Contains("其他引用报表", exception.Message, StringComparison.Ordinal);
        // 拒绝必须发生在数据集写入之前：原数据集查询保持不变。
        Assert.Contains(
            "@deptId IS NOT NULL",
            Assert.Single(environment.Database.Queryable<PocReportDataset>().ToList()).QueryText);
    }

    /// <summary>
    /// 验证数据集替换开关：删除源报表模拟空目标后，数据集内容不一致且未被其他报表引用时，
    /// 未开开关先拒绝（DATASET_CONTENT_MISMATCH），开启 ReplaceDataset 后按目标数据源替换，
    /// 查询文本与数据源编码都更新为包内容。
    /// </summary>
    [Fact]
    public async Task Import_ReplacesConflictingDatasetOnlyWhenAllowed()
    {
        using var environment = new PocReportPackageTestEnvironment();
        await environment.SeedDataSourceAsync("tgt_oracle", "Oracle");
        var package = await environment.SeedDemoReportAsync(dataSourceCode: "src_oracle");
        await environment.ConfigurationService.DeleteReportAsync(ReportCode);
        var modified = package with
        {
            Dataset = package.Dataset with
            {
                QueryText =
                    "SELECT 3 AS sequence, N'儿科' AS departmentName, 44 AS outpatientCount FROM dual WHERE @deptId IS NOT NULL ORDER BY sequence"
            }
        };

        var rejected = await Assert.ThrowsAsync<PocReportPackageException>(() =>
            environment.PackageService.ImportAsync(new PocReportPackageImportRequest(
                modified,
                "tgt_oracle",
                "Create",
                ReplaceDataset: false,
                DryRunPerformed: false)));
        Assert.Equal("DATASET_CONTENT_MISMATCH", rejected.Code);

        var result = await environment.PackageService.ImportAsync(new PocReportPackageImportRequest(
            modified,
            "tgt_oracle",
            "Create",
            ReplaceDataset: true,
            DryRunPerformed: false));

        Assert.Equal("Replaced", result.DatasetAction);
        var dataset = Assert.Single(environment.Database.Queryable<PocReportDataset>().ToList());
        Assert.StartsWith("SELECT 3", dataset.QueryText, StringComparison.Ordinal);
        Assert.Equal("tgt_oracle", dataset.DataSourceCode);
    }

    /// <summary>
    /// 验证试跑的目标数据源类型拦截：目标 DbType 与包导出数据源类型不一致时抛
    /// PACKAGE_DATA_SOURCE_TYPE_MISMATCH，避免在错误方言上得到误导性试跑结论。
    /// </summary>
    [Fact]
    public async Task DryRun_RejectsDataSourceTypeMismatch()
    {
        using var environment = new PocReportPackageTestEnvironment();
        var package = await environment.SeedDemoReportAsync();
        await environment.SeedDataSourceAsync("tgt_sqlserver", "SqlServer");

        var exception = await Assert.ThrowsAsync<PocReportPackageException>(() =>
            environment.PackageService.DryRunAsync(new PocReportPackageDryRunRequest(
                package,
                "tgt_sqlserver",
                new Dictionary<string, string?> { ["deptId"] = "D001" })));

        Assert.Equal("PACKAGE_DATA_SOURCE_TYPE_MISMATCH", exception.Code);
    }

    /// <summary>
    /// 验证试跑的目标数据源必须存在：选择不存在的编码抛 DATA_SOURCE_NOT_FOUND，
    /// 与数据资产接口的现有错误语义一致。
    /// </summary>
    [Fact]
    public async Task DryRun_RejectsUnknownTargetDataSource()
    {
        using var environment = new PocReportPackageTestEnvironment();
        var package = await environment.SeedDemoReportAsync();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            environment.PackageService.DryRunAsync(new PocReportPackageDryRunRequest(
                package,
                "missing_source",
                new Dictionary<string, string?>())));

        Assert.Equal("DATA_SOURCE_NOT_FOUND", exception.Code);
    }

    /// <summary>
    /// 验证试跑的必填参数拦截：包内变量与参数都标记必填而请求未提供取值时，
    /// 参数解析抛 PARAM_REQUIRED（API 层映射 400 并回传参数编码）。
    /// </summary>
    [Fact]
    public async Task DryRun_RejectsMissingRequiredParameter()
    {
        using var environment = new PocReportPackageTestEnvironment();
        var package = await environment.SeedDemoReportAsync();
        var required = package with
        {
            Parameters = [package.Parameters![0] with { IsRequired = true }],
            Dataset = package.Dataset with
            {
                FieldSchema = package.Dataset.FieldSchema with
                {
                    Variables = [package.Dataset.FieldSchema.Variables[0] with { Required = true }]
                }
            }
        };

        var exception = await Assert.ThrowsAsync<PocParameterResolutionException>(() =>
            environment.PackageService.DryRunAsync(new PocReportPackageDryRunRequest(
                required,
                "src_oracle",
                new Dictionary<string, string?>())));

        Assert.Equal("PARAM_REQUIRED", exception.Code);
        Assert.Equal("deptId", exception.ParameterCode);
    }

    /// <summary>
    /// 钉住校验报告的冲突语义：源环境自身 validate 已导出的包时，报表与数据集编码都已存在、
    /// 内容一致（datasetIdentical=true）、且数据集只被包内报表引用（datasetReferencedByOther=false），
    /// 该组合是导入向导允许 Replace+复用数据集的判定依据。
    /// </summary>
    [Fact]
    public async Task Validate_ReportsCollisionsAgainstCurrentDatabase()
    {
        using var environment = new PocReportPackageTestEnvironment();
        var package = await environment.SeedDemoReportAsync();

        var validation = await environment.PackageService.ValidateAsync(package);

        Assert.True(validation.Ok);
        Assert.True(validation.Collisions!.ReportCodeExists);
        Assert.True(validation.Collisions.DatasetCodeExists);
        Assert.True(validation.Collisions.DatasetIdentical);
        Assert.False(validation.Collisions.DatasetReferencedByOther);
    }

    /// <summary>
    /// 钉住生成器产物与后端校验器的同步：读取仓库内 AI 设计流水线样板包
    /// （scripts/report-design/samples/report-asset-inventory.report-package.json，
    /// 由 New-ReportPackage.ps1 从 spec 编译产出），反序列化为 PocReportPackage 后
    /// 在空目标库上执行 ValidateAsync，必须 ok=true 且报表/数据集编码均不存在、
    /// 数据集无一致性结论。该测试保证 PowerShell 生成器的布局与绑定规则一旦
    /// 偏离 PocTemplateValidator/PocReportPackageService 的现行契约就会在 CI 失败。
    /// </summary>
    [Fact]
    public async Task Validate_AcceptsGeneratorSamplePackageFromRepository()
    {
        // 阶段一：从测试程序集目录向上查找仓库根的样板包文件；缺失时给出再生成指引而非模糊失败。
        var samplePath = FindRepositoryFile(
            "scripts", "report-design", "samples", "report-asset-inventory.report-package.json");
        Assert.True(
            samplePath is not null,
            "未找到样板包 scripts/report-design/samples/report-asset-inventory.report-package.json。" +
            "请先运行 scripts/report-design/New-ReportPackage.ps1 -SpecPath scripts/report-design/samples/report-asset-inventory.spec.json " +
            "并把产出文件复制到 samples 目录。");
        var json = await File.ReadAllTextAsync(samplePath!);
        var package = JsonConvert.DeserializeObject<PocReportPackage>(json);
        Assert.NotNull(package);
        Assert.Equal(PocReportPackageFormat.PackageKind, package!.PackageKind);

        // 阶段二：空目标库上的只读校验必须全绿——样板包结构、SQL、变量绑定、模板绑定全部与现行契约一致。
        using var environment = new PocReportPackageTestEnvironment();
        var validation = await environment.PackageService.ValidateAsync(package);
        Assert.True(validation.Ok, string.Join("; ", validation.Errors.Select(item => item.Message)));
        Assert.False(validation.Collisions!.ReportCodeExists);
        Assert.False(validation.Collisions.DatasetCodeExists);
        Assert.Null(validation.Collisions.DatasetIdentical);
        Assert.Null(validation.Collisions.DatasetReferencedByOther);
        Assert.Equal("report_asset_inventory", validation.PackageSummary!.ReportCode);
        Assert.Equal(1, validation.PackageSummary.ParameterCount);
        Assert.Equal(6, validation.PackageSummary.FieldCount);
    }

    /// <summary>从测试程序集目录逐级向上查找仓库根下的相对文件，找不到返回 null。</summary>
    /// <param name="segments">相对仓库根的路径段。</param>
    /// <returns>命中的文件完整路径；未命中为 null。</returns>
    private static string? FindRepositoryFile(params string[] segments)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 报表包测试专用环境：SQLite 内存库 + 与生产一致的存储/服务装配，
    /// 并把导入审计目录重定向到一次性临时目录，避免污染仓库工作区。
    /// </summary>
    private sealed class PocReportPackageTestEnvironment : IDisposable
    {
        private long _nextId = 30_000;
        private readonly SqlSugarClient _database;

        public SqlSugarClient Database => _database;
        public PocDataAssetStore DataAssets { get; }
        public PocDataAssetService DataAssetService { get; }
        public PocReportConfigurationStore ConfigurationStore { get; }
        public PocReportConfigurationService ConfigurationService { get; }
        public PocTemplateStore TemplateStore { get; }
        public PocTemplateService TemplateService { get; }
        public PocReportPackageService PackageService { get; }
        public string AuditDirectory { get; }

        /// <summary>
        /// 初始化测试环境：建库建表、装配数据资产/配置/模板/报表包服务，
        /// 并为 long 主键挂接自增 AOP，模拟生产雪花 Id 行为。
        /// </summary>
        public PocReportPackageTestEnvironment()
        {
            // 阶段一：准备一次性审计目录与 SQLite 内存库，配置可空列映射和 long 主键自增 AOP。
            AuditDirectory = Path.Combine(
                Path.GetTempPath(),
                "hd-report-package-tests",
                Guid.NewGuid().ToString("N"));
            _database = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = "Data Source=:memory:",
                DbType = DbType.Sqlite,
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
            _database.Ado.Open();
            _database.CodeFirst.InitTables<
                PocReport,
                PocDataSource,
                PocReportDataset,
                PocReportParameter,
                PocReportTemplate>();
            // 阶段二：按生产装配方式串起数据资产、报表配置、模板与报表包服务，审计目录指向临时目录。
            DataAssets = new PocDataAssetStore(_database);
            DataAssetService = new PocDataAssetService(DataAssets);
            ConfigurationStore = new PocReportConfigurationStore(_database);
            TemplateStore = new PocTemplateStore(_database);
            ConfigurationService = new PocReportConfigurationService(
                ConfigurationStore,
                DataAssets,
                _database,
                TemplateStore);
            TemplateService = new PocTemplateService(TemplateStore, ConfigurationService);
            PackageService = new PocReportPackageService(
                ConfigurationStore,
                DataAssets,
                DataAssetService,
                ConfigurationService,
                TemplateService,
                TemplateStore)
            {
                AuditDirectory = AuditDirectory
            };
        }

        /// <summary>插入一个已启用的全局数据源，密码写入连接串模拟生产形态。</summary>
        /// <param name="code">数据源编码。</param>
        /// <param name="dbType">数据库类型（Oracle/SqlServer 等）。</param>
        public async Task SeedDataSourceAsync(string code, string dbType)
        {
            await DataAssetService.SaveDataSourceAsync(code, new PocDataSourceInput(
                code,
                $"{code}数据源",
                dbType,
                "188.188.2.5",
                dbType == "Oracle" ? 1521 : 1433,
                dbType == "Oracle" ? "orclpdb" : "hd_report_target",
                "report_reader",
                "pkg-secret",
                30,
                20_000,
                Enabled: true,
                Revision: 0));
        }

        /// <summary>
        /// 构造完整的演示报表（数据源 + 数据集 + 报表配置 + 模板）并返回导出的报表包，
        /// 供校验/导入/试跑各用例直接消费。
        /// </summary>
        /// <param name="dataSourceCode">演示报表绑定的数据源编码。</param>
        /// <param name="enableShareProtection">是否开启分享保护口令（用于验证导出剥离摘要）。</param>
        /// <returns>导出的完整报表包。</returns>
        public async Task<PocReportPackage> SeedDemoReportAsync(
            string dataSourceCode = "src_oracle",
            bool enableShareProtection = false)
        {
            // 阶段一：落库数据源与数据集，字段契约、变量与只读 SQL 与生产形态一致。
            await SeedDataSourceAsync(dataSourceCode, "Oracle");
            await DataAssetService.SaveDatasetAsync(DatasetCode, new PocDatasetDocument(
                DatasetCode,
                "科室工作量明细",
                dataSourceCode,
                "SELECT 1 AS sequence, N'内科' AS departmentName, 42 AS outpatientCount FROM dual WHERE @deptId IS NOT NULL ORDER BY sequence",
                [new PocDatasetVariableDefinition("deptId", "科室编码", "string", false)],
                [
                    new PocDatasetFieldDefinition("sequence", "序号", "number"),
                    new PocDatasetFieldDefinition("departmentName", "科室名称", "string"),
                    new PocDatasetFieldDefinition("outpatientCount", "门诊人次", "number")
                ],
                TimeoutSeconds: 30,
                MaxRows: 20_000,
                Enabled: true,
                Revision: 0));
            await ConfigurationService.SaveAsync(ReportCode, new PocSaveReportConfigurationRequest(
                "报表包演示报表",
                "科室工作量",
                new PocPreviewSettings(PocPreviewMode.Full, 20, false),
                new PocExportSettings(
                    PocDataExportMode.Normal,
                    NormalMaxRows: 20_000,
                    NormalMaxCells: 500_000,
                    NormalTimeoutSeconds: 60,
                    LargeExportEnabled: true,
                    RowsPerSheet: 1_000_000,
                    FileRetentionHours: 24),
                DatasetCode,
                [DemoParameter()],
                0,
                ShareProtection: enableShareProtection
                    ? new PocSaveShareProtectionRequest(true, "share-password-123")
                    : null));
            // 阶段二：保存模板（绑定取自后台重扫结果，模拟合法编辑器提交），最后导出报表包。
            var definition = await ConfigurationService.GetDefinitionAsync(ReportCode);
            await TemplateService.SaveAsync(
                ReportCode,
                new PocSaveTemplateRequest(
                    JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(SnapshotJson)!,
                    PocTemplateValidator.Scan(SnapshotJson, definition),
                    0));
            return await PackageService.ExportAsync(ReportCode);
        }

        /// <summary>在演示数据集上再建一张引用同一数据集的报表，用于数据集被他人引用的冲突场景。</summary>
        /// <param name="reportCode">新报表编码。</param>
        public async Task CreateOtherReportAsync(string reportCode)
        {
            await ConfigurationService.SaveAsync(reportCode, new PocSaveReportConfigurationRequest(
                "其他引用报表",
                "其他工作表",
                new PocPreviewSettings(PocPreviewMode.Full, 20, false),
                new PocExportSettings(
                    PocDataExportMode.Normal,
                    NormalMaxRows: 20_000,
                    NormalMaxCells: 500_000,
                    NormalTimeoutSeconds: 60,
                    LargeExportEnabled: true,
                    RowsPerSheet: 1_000_000,
                    FileRetentionHours: 24),
                DatasetCode,
                [DemoParameter()],
                0));
        }

        /// <summary>构造演示参数定义：Form 来源、string 类型、可选、显示为文本框。</summary>
        /// <returns>与演示数据集变量 deptId 对齐的参数定义。</returns>
        private static PocParameterDefinition DemoParameter() =>
            new("deptId", "科室编码", "text", "Form", null, true, "string");

        /// <summary>释放数据库连接并清理一次性审计临时目录。</summary>
        public void Dispose()
        {
            _database.Dispose();
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
}
