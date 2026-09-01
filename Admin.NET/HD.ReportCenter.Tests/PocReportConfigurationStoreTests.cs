using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.Sqlite;
using SqlSugar;
using System.Reflection;

namespace HD.ReportCenter.Tests;

public sealed class PocReportConfigurationStoreTests : IDisposable
{
    private long _nextId = 20_000;
    private readonly SqlSugarClient _database;
    private readonly PocReportConfigurationStore _store;
    private readonly PocDataAssetStore _dataAssetStore;
    private readonly PocDataAssetService _dataAssetService;
    private readonly PocReportConfigurationService _configurationService;

    /// <summary>初始化 PocReportConfigurationStoreTests 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocReportConfigurationStoreTests()
    {
        _database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (property, column) =>
                // 第一阶段：根据 PocReportConfigurationStoreTests 当前分支转换输入数据，并保存该阶段产生的中间结果。
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
        _store = new PocReportConfigurationStore(_database);
        _dataAssetStore = new PocDataAssetStore(_database);
        _dataAssetService = new PocDataAssetService(_dataAssetStore);
        // 第2阶段：更新 PocReportConfigurationStoreTests 当前分支对应的业务状态，并保持关联数据一致。
        _configurationService = new PocReportConfigurationService(
            _store,
            _dataAssetStore,
            _database,
            new PocTemplateStore(_database));
        _database.Insertable(new PocDataSource
        {
            Code = "report-center",
            Name = "报表中心",
            DbType = "SqlServer",
            ConnectionString = "Server=localhost;Database=test;User Id=test;Password=test;",
            DefaultTimeoutSeconds = 30,
            DefaultMaxRows = 20_000,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommand();
        _database.Insertable(new PocReportDataset
        {
            Code = "detail",
            Name = "明细",
            DataSourceCode = "report-center",
            QueryText = "SELECT @hospitalId AS hospitalId",
            FieldSchemaJson = "[]",
            TimeoutSeconds = 30,
            MaxRows = 20_000,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommand();
    }

    /// <summary>验证 Configuration entities use production table names 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Configuration_entities_use_production_table_names()
    {
        Assert.True(_database.DbMaintenance.IsAnyTable("RcReport", false));
        Assert.True(_database.DbMaintenance.IsAnyTable("RcDataSource", false));
        Assert.True(_database.DbMaintenance.IsAnyTable("RcDataset", false));
        Assert.True(_database.DbMaintenance.IsAnyTable("RcReportParameter", false));
    }

    /// <summary>验证 Save Async Inserts And Reads Whole Configuration Graph 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task SaveAsync_InsertsAndReadsWholeConfigurationGraph()
    {
        var revision = await _store.SaveAsync(CreateMutation(0));

        var stored = await _store.GetAsync("dept-workload");

        Assert.Equal(1, revision);
        Assert.NotNull(stored);
        Assert.Equal("科室工作量统计", stored.Report.Name);
        Assert.Equal("detail", stored.Report.DatasetCode);
        Assert.Equal("report-center", stored.DataSource.Code);
        Assert.Equal("detail", stored.Dataset.Code);
        Assert.Equal("SELECT @hospitalId AS hospitalId", stored.Dataset.QueryText);
        Assert.Equal(2, stored.Parameters.Count);
        Assert.Equal(new[] { "hospitalId", "startDate" }, stored.Parameters.Select(item => item.Code));
    }

    /// <summary>验证 Save Async Updates By Revision And Replaces Parameters 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task SaveAsync_UpdatesByRevisionAndReplacesParameters()
    {
        await _store.SaveAsync(CreateMutation(0));
        var mutation = CreateMutation(1) with
        {
            ReportName = "更新后的报表",
            Parameters =
            [
                Parameter("deptId", 0)
            ]
        };

        var revision = await _store.SaveAsync(mutation);
        var stored = await _store.GetAsync("dept-workload");

        Assert.Equal(2, revision);
        Assert.Equal("更新后的报表", stored!.Report.Name);
        Assert.Equal("deptId", Assert.Single(stored.Parameters).Code);
    }

    /// <summary>验证 Save Async Rejects Stale Revision Without Partial Writes 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task SaveAsync_RejectsStaleRevisionWithoutPartialWrites()
    {
        await _store.SaveAsync(CreateMutation(0));

        await Assert.ThrowsAsync<PocReportConfigurationRevisionConflictException>(
            () => _store.SaveAsync(CreateMutation(0) with { ReportName = "不应写入" }));

        var stored = await _store.GetAsync("dept-workload");
        Assert.Equal("科室工作量统计", stored!.Report.Name);
        Assert.Equal(1, stored.Report.Revision);
        Assert.Equal(2, stored.Parameters.Count);
    }

    /// <summary>验证 Data assets are saved independently and dataset is reusable 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Data_assets_are_saved_independently_and_dataset_is_reusable()
    {
        var dataSourceRevision = await _dataAssetStore.SaveDataSourceAsync(new PocDataSourceMutation(
            "report-center",
            "共享只读库",
            "SqlServer",
            "Server=localhost;Database=test;User Id=test;Password=test;",
            45,
            30_000,
            true,
            1));
        var datasetRevision = await _dataAssetStore.SaveDatasetAsync(new PocDatasetMutation(
            "detail",
            "共享工作量明细",
            "report-center",
            "SELECT @deptId AS departmentName",
            "[]",
            45,
            30_000,
            true,
            1));

        Assert.Equal(2, dataSourceRevision);
        Assert.Equal(2, datasetRevision);
        Assert.Equal("共享只读库", (await _dataAssetStore.GetDataSourceAsync("report-center"))!.Name);
        Assert.Equal("共享工作量明细", (await _dataAssetStore.GetDatasetAsync("detail"))!.Name);
    }

    /// <summary>验证 Dataset references return every report using the shared dataset 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Dataset_references_return_every_report_using_the_shared_dataset()
    {
        await _store.SaveAsync(CreateMutation(0));
        await _store.SaveAsync(CreateMutation(0) with
        {
            ReportCode = "outpatient-log",
            ReportName = "门诊日志",
            SheetName = "门诊日志"
        });

        var references = await _dataAssetService.ListDatasetReferencesAsync("detail");

        Assert.Equal(2, references.Count);
        Assert.Equal(["dept-workload", "outpatient-log"], references.Select(item => item.ReportCode));
        Assert.All(references, item => Assert.True(item.Enabled));
    }

    /// <summary>验证 Report list combines report and template revisions 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Report_list_combines_report_and_template_revisions()
    {
        await _store.SaveAsync(CreateMutation(0));
        await _store.SaveAsync(CreateMutation(0) with
        {
            ReportCode = "outpatient-log",
            ReportName = "门诊日志",
            SheetName = "门诊日志"
        });
        _database.Insertable(new PocReportTemplate
        {
            ReportCode = "outpatient-log",
            SnapshotJson = "{}",
            BindingJson = "{}",
            Revision = 4,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        }).ExecuteCommand();

        var reports = await _configurationService.ListReportsAsync();

        Assert.Equal(2, reports.Count);
        var outpatient = Assert.Single(reports, item => item.ReportCode == "outpatient-log");
        Assert.Equal("门诊日志", outpatient.Name);
        Assert.Equal("detail", outpatient.DatasetCode);
        Assert.Equal(1, outpatient.ConfigurationRevision);
        Assert.Equal(4, outpatient.TemplateRevision);
        Assert.NotNull(outpatient.UpdatedAt);
    }

    /// <summary>验证 Configuration service reads the report selected from the list 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Configuration_service_reads_the_report_selected_from_the_list()
    {
        await _store.SaveAsync(CreateMutation(0) with
        {
            ReportCode = "outpatient-log",
            ReportName = "门诊日志",
            SheetName = "门诊日志"
        });

        var document = await _configurationService.GetDocumentAsync("outpatient-log");

        Assert.Equal("outpatient-log", document.ReportCode);
        Assert.Equal("门诊日志", document.Name);
        Assert.Equal("detail", document.DatasetCode);
    }

    /// <summary>验证 Create Async creates report and parameters from the selected dataset 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task CreateAsync_creates_report_and_parameters_from_the_selected_dataset()
    {
        var created = await _configurationService.CreateAsync(new PocCreateReportRequest(
            "Outpatient-Log",
            "门诊日志",
            "门诊日志",
            "detail"));

        Assert.Equal("outpatient-log", created.ReportCode);
        Assert.Equal("门诊日志", created.Name);
        Assert.Equal("detail", created.DatasetCode);
        Assert.Equal(1, created.Revision);
        var parameter = Assert.Single(created.Parameters);
        Assert.Equal("hospitalId", parameter.Code);
        Assert.Equal("text", parameter.ControlType);
        Assert.True(parameter.AllowUrlPrefill);
        Assert.Equal("hospitalId", parameter.UrlKey);
        Assert.Contains(
            await _configurationService.ListReportsAsync(),
            item => item.ReportCode == "outpatient-log");
    }

    /// <summary>验证 Create Async rejects an existing report code 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task CreateAsync_rejects_an_existing_report_code()
    {
        var request = new PocCreateReportRequest(
            "outpatient-log",
            "门诊日志",
            "门诊日志",
            "detail");
        await _configurationService.CreateAsync(request);

        var exception = await Assert.ThrowsAsync<PocReportAlreadyExistsException>(
            () => _configurationService.CreateAsync(request));

        Assert.Contains("outpatient-log", exception.Message);
        Assert.Single(
            await _configurationService.ListReportsAsync(),
            item => item.ReportCode == "outpatient-log");
    }

    /// <summary>验证 Configuration service persists parameter options and field presentations separately 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Configuration_service_persists_parameter_options_and_field_presentations_separately()
    {
        _database.Updateable<PocReportDataset>()
            .SetColumns(dataset => new PocReportDataset
            {
                QueryText = "SELECT 'M' AS GENDER_CODE",
                FieldSchemaJson = "{\"Variables\":[],\"Fields\":[{\"Code\":\"GENDER_CODE\",\"DisplayName\":\"人员类别\",\"DataType\":\"string\"}]}"
            })
            .Where(dataset => dataset.Code == "detail")
            .ExecuteCommand();
        // 第一阶段：构造 Configuration_service_persists_parameter_options_and_field_presentations_separately 当前步骤需要的中间数据和执行参数。
        var parameterOptions = new[]
        {
            new PocValueOption("M", "男性条件"),
            new PocValueOption("F", "女性条件")
        };
        var fieldMappings = new[]
        {
            new PocValueOption("M", "男"),
            new PocValueOption("F", "女")
        };

        var document = await _configurationService.SaveAsync(
            "generic-people",
            new PocSaveReportConfigurationRequest(
                "人员明细",
                "人员明细",
                new PocPreviewSettings(PocPreviewMode.Full, 20, false),
                new PocExportSettings(
                    PocDataExportMode.Normal,
                    20_000,
                    500_000,
                    60,
                    true,
                    1_000_000,
                    24),
                "detail",
                [new PocParameterDefinition(
                    "GENDER_CODE",
                    "人员类别",
                    "select",
                    "Form",
                    null,
                    true,
                    Options: parameterOptions)],
                0,
                [new PocFieldPresentationDefinition("GENDER_CODE", fieldMappings)]));

        // 第2阶段：构造 Configuration_service_persists_parameter_options_and_field_presentations_separately 当前步骤需要的中间数据和执行参数。
        var parameter = Assert.Single(document.Parameters);
        Assert.Equal("男性条件", parameter.Options![0].Label);
        var presentation = Assert.Single(document.FieldPresentations!);
        Assert.Equal("男", presentation.ValueMappings[0].Label);
        Assert.NotEqual(parameter.Options[0].Label, presentation.ValueMappings[0].Label);
        var stored = await _store.GetAsync("generic-people");
        Assert.Contains("男性条件", Assert.Single(stored!.Parameters).OptionsJson);
        Assert.Contains("男", stored.Report.FieldPresentationsJson);
    }

    /// <summary>验证 Share protection password is hashed and preserved when configuration changes 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Share_protection_password_is_hashed_and_preserved_when_configuration_changes()
    {
        const string password = "Protect-123";
        var created = await _configurationService.SaveAsync(
            "protected-report",
            CreateSaveRequest(
                0,
                new PocSaveShareProtectionRequest(true, password)));

        Assert.True(created.ShareProtection?.Enabled);
        Assert.True(created.ShareProtection?.PasswordConfigured);
        Assert.True(await _configurationService.VerifyShareProtectionPasswordAsync(
            "protected-report",
            password));
        Assert.False(await _configurationService.VerifyShareProtectionPasswordAsync(
            "protected-report",
            "wrong-password"));

        var stored = await _store.GetAsync("protected-report");
        Assert.DoesNotContain(password, stored!.Report.PreviewSettingsJson);
        Assert.Contains("ShareProtection", stored.Report.PreviewSettingsJson);

        var updated = await _configurationService.SaveAsync(
            "protected-report",
            CreateSaveRequest(
                created.Revision,
                new PocSaveShareProtectionRequest(true, null)));

        Assert.True(updated.ShareProtection?.PasswordConfigured);
        Assert.True(await _configurationService.VerifyShareProtectionPasswordAsync(
            "protected-report",
            password));
    }

    /// <summary>验证 Date control with string dataset type supports dynamic default 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Date_control_with_string_dataset_type_supports_dynamic_default()
    {
        var request = CreateSaveRequest(
            0,
            new PocSaveShareProtectionRequest(false, null)) with
        {
            Parameters =
            [
                new PocParameterDefinition(
                    "hospitalId",
                    "医院",
                    "text",
                    "Form",
                    null,
                    true),
                new PocParameterDefinition(
                    "queryDate",
                    "查询日期",
                    "date",
                    "Form",
                    null,
                    true,
                    DataType: "string",
                    DefaultExpression: "yesterday()")
            ]
        };

        await _configurationService.SaveAsync("date-default-report", request);
        var document = await _configurationService.GetDocumentAsync(
            "date-default-report",
            new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.FromHours(8)));

        var dateParameter = Assert.Single(document.Parameters, parameter => parameter.Code == "queryDate");
        Assert.Equal("2026-08-17", dateParameter.DefaultValue);
    }

    /// <summary>验证 Calendar period control formats dynamic default 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="controlType">controlType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Theory]
    [InlineData("month", "2026-08")]
    [InlineData("year", "2026")]
    public async Task Calendar_period_control_formats_dynamic_default(string controlType, string expected)
    {
        var request = CreateSaveRequest(
            0,
            new PocSaveShareProtectionRequest(false, null)) with
        {
            Parameters =
            [
                new PocParameterDefinition(
                    "hospitalId",
                    "医院",
                    "text",
                    "Form",
                    null,
                    true),
                new PocParameterDefinition(
                    "queryDate",
                    "查询日期",
                    controlType,
                    "Form",
                    null,
                    true,
                    DataType: "string",
                    DefaultExpression: "today()")
            ]
        };

        await _configurationService.SaveAsync($"{controlType}-default-report", request);
        var document = await _configurationService.GetDocumentAsync(
            $"{controlType}-default-report",
            new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.FromHours(8)));

        var parameter = Assert.Single(document.Parameters, item => item.Code == "queryDate");
        Assert.Equal(expected, parameter.DefaultValue);
    }

    /// <summary>验证 Share protection cannot be enabled without an unlock password 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Share_protection_cannot_be_enabled_without_an_unlock_password()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            _configurationService.SaveAsync(
                "protected-report",
                CreateSaveRequest(
                    0,
                    new PocSaveShareProtectionRequest(true, null))));

        Assert.Equal("SHARE_PROTECTION_PASSWORD_REQUIRED", exception.Code);
    }

    /// <summary>验证 Newly created report gets its own editable initial template 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Newly_created_report_gets_its_own_editable_initial_template()
    {
        await _configurationService.CreateAsync(new PocCreateReportRequest(
            "outpatient-log",
            "门诊日志",
            "门诊日志",
            "detail"));
        var templateService = new PocTemplateService(
            new PocTemplateStore(_database),
            _configurationService);

        var template = await templateService.GetEditableAsync("outpatient-log");

        Assert.Equal(0, template.Revision);
        Assert.Equal("门诊日志（可编辑模板）", template.Snapshot.Value<string>("name"));
        Assert.DoesNotContain("科室工作量", template.Snapshot.ToString());
        var binding = Assert.Single(template.Binding.ParameterBindings);
        Assert.Equal("hospitalId", binding.Code);
    }

    /// <summary>验证 Report create options only include enabled datasets 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Report_create_options_only_include_enabled_datasets()
    {
        _database.Updateable<PocReportDataset>()
            .SetColumns(item => item.IsEnabled == false)
            .Where(item => item.Code == "detail")
            .ExecuteCommand();

        var options = await _configurationService.GetCreateOptionsAsync();

        Assert.Empty(options.Datasets);
    }

    /// <summary>验证 Data source round trips supported database types 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="dbType">dbType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="port">port 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Theory]
    [InlineData("SqlServer", "source_sqlserver", 1433)]
    [InlineData("Oracle", "source_oracle", 1521)]
    [InlineData("MySql", "source_mysql", 3306)]
    [InlineData("PostgreSQL", "source_postgresql", 5432)]
    public async Task Data_source_round_trips_supported_database_types(
        string dbType,
        string code,
        int port)
    {
        var saved = await _dataAssetService.SaveDataSourceAsync(
            code,
            new PocDataSourceInput(
                code,
                $"{dbType} 报表库",
                dbType,
                "db.example.local",
                port,
                "reporting",
                "report_reader",
                "secret",
                30,
                20_000,
                true,
                0));

        Assert.Equal(dbType, saved.DbType);
        Assert.Equal("db.example.local", saved.Server);
        Assert.Equal(port, saved.Port);
        Assert.Equal("reporting", saved.Database);
        Assert.Equal("report_reader", saved.UserName);
        Assert.True(saved.PasswordConfigured);
        Assert.Equal(dbType, (await _dataAssetStore.GetDataSourceAsync(code))!.DbType);
    }

    /// <summary>验证 Data source rejects an unsupported database type 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Data_source_rejects_an_unsupported_database_type()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            _dataAssetService.SaveDataSourceAsync(
                "unsupported_source",
                new PocDataSourceInput(
                    "unsupported_source",
                    "不支持的数据源",
                    "Access",
                    "db.example.local",
                    1000,
                    "reporting",
                    "report_reader",
                    "secret",
                    30,
                    20_000,
                    true,
                    0)));

        Assert.Equal("DATA_SOURCE_TYPE_UNSUPPORTED", exception.Code);
    }

    /// <summary>验证 Data source preserves password whitespace 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Data_source_preserves_password_whitespace()
    {
        const string password = " secret with spaces ";
        await _dataAssetService.SaveDataSourceAsync(
            "source_password_spaces",
            new PocDataSourceInput(
                "source_password_spaces",
                "密码空格测试",
                "Oracle",
                "db.example.local",
                1521,
                "reporting",
                "report_reader",
                password,
                30,
                20_000,
                true,
                0));

        var stored = await _dataAssetStore.GetDataSourceAsync("source_password_spaces");
        var endpoint = PocDatabaseProvider.ParseEndpoint(stored!.DbType, stored.ConnectionString);

        Assert.Equal(password, endpoint.Password);
    }

    /// <summary>验证 Database provider returns oracle error without wrapper details 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Database_provider_returns_oracle_error_without_wrapper_details()
    {
        var exception = new SqlSugarException(
            "SqlSugar error: ORA-01017: invalid username/password; logon denied\r\n" +
            "ConnectionString: User Id=report_reader;Password=must-not-leak;");

        var message = PocDatabaseProvider.GetSafeConnectionError(exception);

        Assert.Equal("ORA-01017: invalid username/password; logon denied", message);
        Assert.DoesNotContain("must-not-leak", message);
        Assert.DoesNotContain("ConnectionString", message);
    }

    /// <summary>验证 Database provider recognizes database errors nested in wrappers 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Database_provider_recognizes_database_errors_nested_in_wrappers()
    {
        var exception = new InvalidOperationException(
            "数据库打开失败。",
            new TestDatabaseException("MySQL 1045: Access denied for user 'root'@'172.26.0.1'"));

        Assert.True(PocDatabaseProvider.IsDatabaseException(exception));
        Assert.Contains("MySQL 1045", PocDatabaseProvider.GetSafeConnectionError(exception));
    }

    /// <summary>验证 Database provider extracts mysql access denied from sqlsugar message 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Database_provider_extracts_mysql_access_denied_from_sqlsugar_message()
    {
        var exception = new SqlSugarException(
            "Connection open error: Access denied for user 'root'@'172.26.0.1' " +
            "(using password: YES); Password=must-not-leak;");

        var message = PocDatabaseProvider.GetSafeConnectionError(exception);

        Assert.Equal(
            "MySQL 1045: Access denied for user 'root'@'172.26.0.1' (using password: YES)",
            message);
        Assert.DoesNotContain("must-not-leak", message);
    }

    private sealed class TestDatabaseException : System.Data.Common.DbException
    {
        /// <summary>初始化 TestDatabaseException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
        /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        public TestDatabaseException(string message) : base(message) { }
    }

    /// <summary>验证 Database provider uses the expected default port and paging dialect 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="dbType">dbType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="defaultPort">defaultPort 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="pagingClause">pagingClause 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    [Theory]
    [InlineData("SqlServer", 1433, "OFFSET @rcPageOffset ROWS FETCH NEXT @rcPageTake ROWS ONLY")]
    [InlineData("Oracle", 1521, "OFFSET @rcPageOffset ROWS FETCH NEXT @rcPageTake ROWS ONLY")]
    [InlineData("MySql", 3306, "LIMIT @rcPageTake OFFSET @rcPageOffset")]
    [InlineData("PostgreSQL", 5432, "LIMIT @rcPageTake OFFSET @rcPageOffset")]
    public void Database_provider_uses_the_expected_default_port_and_paging_dialect(
        string dbType,
        int defaultPort,
        string pagingClause)
    {
        Assert.Equal(defaultPort, PocDatabaseProvider.DefaultPort(dbType));
        Assert.EndsWith(
            pagingClause,
            PocDatabaseProvider.BuildPagedQuery(dbType, "SELECT 1 ORDER BY 1"));
    }

    /// <summary>验证 Data asset save rejects stale revision 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Data_asset_save_rejects_stale_revision()
    {
        await Assert.ThrowsAsync<PocDataAssetRevisionConflictException>(() =>
            _dataAssetStore.SaveDatasetAsync(new PocDatasetMutation(
                "detail",
                "不应写入",
                "report-center",
                "SELECT 1 AS value",
                "[]",
                30,
                20_000,
                true,
                0)));

        Assert.Equal("明细", (await _dataAssetStore.GetDatasetAsync("detail"))!.Name);
    }

    /// <summary>验证 Dataset parameter extraction ignores string literals 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Dataset_parameter_extraction_ignores_string_literals()
    {
        var parameters = PocDataAssetService.ExtractParameterCodes(
            "SELECT '@ignored' AS literalValue, @deptId AS departmentId");

        Assert.Equal(["deptId"], parameters);
    }

    /// <summary>验证 Dataset parameter extraction also enforces read only sql 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Dataset_parameter_extraction_also_enforces_read_only_sql()
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(() =>
            PocDataAssetService.ExtractParameterCodes("UPDATE Workload SET Value = @value"));

        Assert.Equal("DATASET_QUERY_NOT_READ_ONLY", exception.Code);
    }

    /// <summary>验证 Dataset preview requires every referenced parameter 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Dataset_preview_requires_every_referenced_parameter()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            _dataAssetService.PreviewDatasetAsync(
                "detail",
                new PocDatasetPreviewRequest(new Dictionary<string, string?>())));

        Assert.Equal("DATASET_PREVIEW_PARAMETER_REQUIRED", exception.Code);
    }

    /// <summary>验证 Dataset preview rejects mutating sql before connecting 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Dataset_preview_rejects_mutating_sql_before_connecting()
    {
        _database.Updateable<PocReportDataset>()
            .SetColumns(dataset => dataset.QueryText == "DELETE FROM Workload")
            .Where(dataset => dataset.Code == "detail")
            .ExecuteCommand();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            _dataAssetService.PreviewDatasetAsync(
                "detail",
                new PocDatasetPreviewRequest(new Dictionary<string, string?>())));

        Assert.Equal("DATASET_QUERY_NOT_READ_ONLY", exception.Code);
    }

    /// <summary>验证 Legacy dataset schema infers variables and drops excel formula fields 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Legacy_dataset_schema_infers_variables_and_drops_excel_formula_fields()
    {
        var dataset = new PocReportDataset
        {
            QueryText = "SELECT @hospitalId AS hospitalId",
            FieldSchemaJson = """
                [
                  {"Code":"hospitalId","Header":"医院","DataType":"string","Width":180,"FormulaTemplate":null},
                  {"Code":"displayValue","Header":"显示值","DataType":"formula","Width":120,"FormulaTemplate":"=A{row}"}
                ]
                """
        };

        var schema = PocDataAssetService.ReadSchema(dataset);

        Assert.Equal("hospitalId", Assert.Single(schema.Variables).Code);
        var field = Assert.Single(schema.Fields);
        Assert.Equal("hospitalId", field.Code);
        Assert.Equal("医院", field.DisplayName);
    }

    /// <summary>验证 Report configuration requires a value binding for each dataset variable 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Report_configuration_requires_a_value_binding_for_each_dataset_variable()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            _configurationService.SaveAsync(
                "dept-workload",
                new PocSaveReportConfigurationRequest(
                    "科室工作量统计",
                    "科室工作量",
                    new PocPreviewSettings(PocPreviewMode.Full, 20, false),
                    new PocExportSettings(
                        PocDataExportMode.Normal,
                        20_000,
                        500_000,
                        60,
                        true,
                        1_000_000,
                        24),
                    "detail",
                    [],
                    0)));

        Assert.Equal("DATASET_PARAMETER_UNMAPPED", exception.Code);
    }

    /// <summary>释放 PocReportConfigurationStoreTests 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>构建 PocReportConfigurationStoreTests 所需的 Create Mutation 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="revision">revision 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocReportConfigurationMutation CreateMutation(int revision) =>
        new(
            "dept-workload",
            "科室工作量统计",
            "科室工作量",
            "{\"Mode\":\"Full\",\"PageSize\":20,\"ShowExactTotal\":false}",
            "{\"DefaultMode\":\"Normal\"}",
            "detail",
            [Parameter("hospitalId", 0), Parameter("startDate", 1)],
            revision);

    /// <summary>构建 PocReportConfigurationStoreTests 所需的 Create Save Request 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="revision">revision 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="protection">protection 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocSaveReportConfigurationRequest CreateSaveRequest(
        int revision,
        PocSaveShareProtectionRequest protection) =>
        new(
            "受保护报表",
            "受保护报表",
            new PocPreviewSettings(PocPreviewMode.Full, 20, false),
            new PocExportSettings(
                PocDataExportMode.Normal,
                20_000,
                500_000,
                60,
                true,
                1_000_000,
                24),
            "detail",
            [new PocParameterDefinition(
                "hospitalId",
                "医院",
                "text",
                "Form",
                null,
                true)],
            revision,
            [],
            protection);

    /// <summary>负责 PocReportConfigurationStoreTests 的 Parameter 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="order">order 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocReportParameter Parameter(string code, int order) => new()
    {
        Code = code,
        DisplayName = code,
        DataType = code.EndsWith("Date", StringComparison.Ordinal) ? "date" : "string",
        ControlType = code.EndsWith("Date", StringComparison.Ordinal) ? "date" : "text",
        SourceType = "Form",
        DisplayOrder = order,
        IsVisible = true
    };
}
