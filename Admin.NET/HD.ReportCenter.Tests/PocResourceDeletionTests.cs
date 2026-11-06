using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.Sqlite;
using SqlSugar;
using System.Net.Http;
using System.Reflection;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 删除功能的引用校验与级联清理测试：覆盖数据源、数据集、接口参数源和报表四类资源的删除守卫。
/// </summary>
public sealed class PocResourceDeletionTests : IDisposable
{
    private long _nextId = 90_000;
    private readonly SqlSugarClient _database;
    private readonly PocDataAssetStore _dataAssetStore;
    private readonly PocDataAssetService _dataAssetService;
    private readonly PocReportConfigurationStore _configurationStore;
    private readonly PocReportConfigurationService _configurationService;
    private readonly PocExternalParameterSourceService _externalParameterService;

    /// <summary>初始化 PocResourceDeletionTests 实例，建立内存数据库并写入可被引用的基础资源。</summary>
    public PocResourceDeletionTests()
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
        // 六张表超出泛型重载的参数上限，改用 Type 数组一次建齐全部依赖表。
        _database.CodeFirst.InitTables(
            typeof(PocReport),
            typeof(PocDataSource),
            typeof(PocReportDataset),
            typeof(PocReportParameter),
            typeof(PocReportTemplate),
            typeof(PocExternalParameterSource));
        _dataAssetStore = new PocDataAssetStore(_database);
        // 第二阶段：装配全部被测服务，让删除链路在共享的内存数据库上验证真实存储行为。
        _dataAssetService = new PocDataAssetService(_dataAssetStore);
        _configurationStore = new PocReportConfigurationStore(_database);
        _configurationService = new PocReportConfigurationService(
            _configurationStore,
            _dataAssetStore,
            _database,
            new PocTemplateStore(_database));
        _externalParameterService = new PocExternalParameterSourceService(
            new PocExternalParameterSourceStore(_database),
            new StubHttpClientFactory(new HttpClient()));
        _database.Insertable(new PocDataSource
        {
            Code = "report_center",
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
            DataSourceCode = "report_center",
            QueryText = "SELECT @hospitalId AS hospitalId",
            FieldSchemaJson = "{\"Variables\":[{\"Code\":\"hospitalId\",\"DisplayName\":\"医院\",\"DataType\":\"string\",\"Required\":false,\"SourceType\":\"Form\",\"SourceCode\":null,\"SourceFieldCode\":null}],\"Fields\":[]}",
            TimeoutSeconds = 30,
            MaxRows = 20_000,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommand();
    }

    /// <summary>验证被报表引用的数据集删除被拒绝并回传引用报表名称，防止报表运行时缺数据。</summary>
    [Fact]
    public async Task Dataset_delete_is_blocked_while_reports_reference_it()
    {
        await _configurationService.CreateAsync(new PocCreateReportRequest(
            "outpatient-log",
            "门诊日志",
            "门诊日志",
            "detail"));

        var exception = await Assert.ThrowsAsync<PocResourceInUseException>(
            () => _dataAssetService.DeleteDatasetAsync("detail"));

        Assert.Equal("DATASET_IN_USE", exception.Code);
        Assert.Contains("门诊日志", exception.Message);
        Assert.NotNull(await _dataAssetStore.GetDatasetAsync("detail"));
    }

    /// <summary>验证未被引用的数据集可以删除，删除后列表不再返回该编码。</summary>
    [Fact]
    public async Task Dataset_delete_removes_unreferenced_dataset()
    {
        var deleted = await _dataAssetService.DeleteDatasetAsync("detail");

        Assert.Equal("detail", deleted.Code);
        Assert.Null(await _dataAssetStore.GetDatasetAsync("detail"));
        Assert.Empty(await _dataAssetStore.ListDatasetsAsync());
    }

    /// <summary>验证不存在或非法编码的数据集删除请求按既有的 DATASET_NOT_FOUND 语义拒绝。</summary>
    [Fact]
    public async Task Dataset_delete_reports_missing_code_as_configuration_error()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(
            () => _dataAssetService.DeleteDatasetAsync("missing_dataset"));

        Assert.Equal("DATASET_NOT_FOUND", exception.Code);
    }

    /// <summary>验证被数据集引用的数据源删除被拒绝并回传数据集名称，避免留下断链数据集。</summary>
    [Fact]
    public async Task Data_source_delete_is_blocked_while_datasets_reference_it()
    {
        var exception = await Assert.ThrowsAsync<PocResourceInUseException>(
            () => _dataAssetService.DeleteDataSourceAsync("report_center"));

        Assert.Equal("DATA_SOURCE_IN_USE", exception.Code);
        Assert.Contains("明细", exception.Message);
        Assert.NotNull(await _dataAssetStore.GetDataSourceAsync("report_center"));
    }

    /// <summary>验证没有数据集挂载的数据源可以删除，删除后连接信息不再返回。</summary>
    [Fact]
    public async Task Data_source_delete_removes_unreferenced_source()
    {
        await _dataAssetService.DeleteDatasetAsync("detail");

        var deleted = await _dataAssetService.DeleteDataSourceAsync("report_center");

        Assert.Equal("report_center", deleted.Code);
        Assert.Null(await _dataAssetStore.GetDataSourceAsync("report_center"));
    }

    /// <summary>验证被报表外部参数引用的接口源删除被拒绝，提示定位到报表与参数编码。</summary>
    [Fact]
    public async Task External_source_delete_is_blocked_by_report_parameter_binding()
    {
        SeedExternalSource("staff", "人员接口");
        await _configurationStore.SaveAsync(new PocReportConfigurationMutation(
            "staff-report",
            "人员报表",
            "人员报表",
            "{\"Mode\":\"Full\",\"PageSize\":20,\"ShowExactTotal\":false}",
            "{\"DefaultMode\":\"Normal\"}",
            "detail",
            [ExternalParameter("hisUserId", "hisUserId")],
            0));

        var exception = await Assert.ThrowsAsync<PocResourceInUseException>(
            () => _externalParameterService.DeleteAsync("staff"));

        Assert.Equal("EXTERNAL_PARAMETER_SOURCE_IN_USE", exception.Code);
        Assert.Contains("人员报表", exception.Message);
        Assert.Contains("hisUserId", exception.Message);
    }

    /// <summary>验证被数据集外部变量引用的接口源删除被拒绝，变量契约存在 JSON 中也要被守卫。</summary>
    [Fact]
    public async Task External_source_delete_is_blocked_by_dataset_variable_binding()
    {
        SeedExternalSource("staff", "人员接口");
        _database.Insertable(new PocReportDataset
        {
            Code = "staff-detail",
            Name = "人员明细",
            DataSourceCode = "report_center",
            QueryText = "SELECT @hisUserId AS userId",
            FieldSchemaJson = "{\"Variables\":[{\"Code\":\"hisUserId\",\"DisplayName\":\"人员\",\"DataType\":\"string\",\"Required\":false,\"SourceType\":\"External\",\"SourceCode\":\"staff\",\"SourceFieldCode\":\"hisUserId\"}],\"Fields\":[]}",
            TimeoutSeconds = 30,
            MaxRows = 20_000,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommand();

        var exception = await Assert.ThrowsAsync<PocResourceInUseException>(
            () => _externalParameterService.DeleteAsync("staff"));

        Assert.Equal("EXTERNAL_PARAMETER_SOURCE_IN_USE", exception.Code);
        Assert.Contains("人员明细", exception.Message);
    }

    /// <summary>验证没有任何绑定的接口参数源可以删除，删除后不再出现在列表中。</summary>
    [Fact]
    public async Task External_source_delete_removes_unbound_source()
    {
        SeedExternalSource("staff", "人员接口");

        var deleted = await _externalParameterService.DeleteAsync("staff");

        Assert.Equal("staff", deleted.Code);
        Assert.DoesNotContain(
            await _externalParameterService.ListAsync(),
            item => item.Code == "staff");
    }

    /// <summary>验证不存在的接口参数源删除请求按 EXTERNAL_PARAMETER_SOURCE_NOT_FOUND 拒绝。</summary>
    [Fact]
    public async Task External_source_delete_reports_missing_code()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(
            () => _externalParameterService.DeleteAsync("missing_source"));

        Assert.Equal("EXTERNAL_PARAMETER_SOURCE_NOT_FOUND", exception.Code);
    }

    /// <summary>验证报表删除会连带清理参数与模板快照，且不影响共享的数据集和数据源。</summary>
    [Fact]
    public async Task Report_delete_cascades_parameters_and_template()
    {
        await _configurationService.CreateAsync(new PocCreateReportRequest(
            "outpatient-log",
            "门诊日志",
            "门诊日志",
            "detail"));
        _database.Insertable(new PocReportTemplate
        {
            ReportCode = "outpatient-log",
            SnapshotJson = "{}",
            BindingJson = "{}",
            Revision = 3,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        }).ExecuteCommand();

        var deleted = await _configurationService.DeleteReportAsync("outpatient-log");

        Assert.Equal("outpatient-log", deleted.Code);
        Assert.Null(await _configurationStore.GetAsync("outpatient-log"));
        Assert.Empty(_database.Queryable<PocReportParameter>()
            .Where(item => item.ReportCode == "outpatient-log")
            .ToList());
        Assert.Empty(_database.Queryable<PocReportTemplate>()
            .Where(item => item.ReportCode == "outpatient-log")
            .ToList());
        Assert.NotNull(await _dataAssetStore.GetDatasetAsync("detail"));
        Assert.NotNull(await _dataAssetStore.GetDataSourceAsync("report_center"));
    }

    /// <summary>验证删除不存在的报表抛出 KeyNotFoundException，供 API 层映射为 404。</summary>
    [Fact]
    public async Task Report_delete_throws_key_not_found_for_missing_report()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _configurationService.DeleteReportAsync("missing-report"));
    }

    /// <summary>释放 PocResourceDeletionTests 持有的内存数据库连接，避免连接泄漏。</summary>
    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>写入一个可被报表参数或数据集变量绑定的外部参数源。</summary>
    /// <param name="code">接口参数源编码。</param>
    /// <param name="name">接口参数源显示名称。</param>
    private void SeedExternalSource(string code, string name)
    {
        _database.Insertable(new PocExternalParameterSource
        {
            Code = code,
            Name = name,
            BaseUrl = "http://127.0.0.1:48080/api/",
            Endpoint = "staff/current",
            HttpMethod = "POST",
            RequestBodyTemplate = "{}",
            HeadersTemplate = "{}",
            RequestParameterSchemaJson = "[]",
            FieldSchemaJson = "[{\"Code\":\"hisUserId\",\"DisplayName\":\"人员ID\",\"DataType\":\"string\",\"ResponsePath\":\"content.userId\"}]",
            TimeoutSeconds = 10,
            Revision = 1,
            IsEnabled = true,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        }).ExecuteCommand();
    }

    /// <summary>构造绑定 staff 接口源的报表参数实体，用于验证删除守卫的引用定位。</summary>
    /// <param name="code">参数编码。</param>
    /// <param name="sourceFieldCode">接口源返回字段编码。</param>
    /// <returns>可直接落库的报表参数实体。</returns>
    private static PocReportParameter ExternalParameter(string code, string sourceFieldCode) => new()
    {
        ReportCode = "staff-report",
        Code = code,
        DisplayName = code,
        DataType = "string",
        ControlType = "text",
        SourceType = "External",
        SourceCode = "staff",
        SourceFieldCode = sourceFieldCode,
        IsVisible = false,
        IsReadOnly = true,
        DisplayOrder = 0
    };

    /// <summary>删除链路不发起 HTTP 调用，用固定客户端满足构造依赖。</summary>
    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        /// <summary>返回构造时传入的固定 HttpClient，测试内不会真正发出请求。</summary>
        /// <param name="name">逻辑客户端名称，本测试不区分。</param>
        /// <returns>固定的 HttpClient 实例。</returns>
        public HttpClient CreateClient(string name) => client;
    }
}
