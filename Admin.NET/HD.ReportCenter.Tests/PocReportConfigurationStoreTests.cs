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
            PocReportParameter>();
        _store = new PocReportConfigurationStore(_database);
        _dataAssetStore = new PocDataAssetStore(_database);
        _dataAssetService = new PocDataAssetService(_dataAssetStore, _database);
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

    [Fact]
    public void Configuration_entities_use_production_table_names()
    {
        Assert.True(_database.DbMaintenance.IsAnyTable("RcReport", false));
        Assert.True(_database.DbMaintenance.IsAnyTable("RcDataSource", false));
        Assert.True(_database.DbMaintenance.IsAnyTable("RcDataset", false));
        Assert.True(_database.DbMaintenance.IsAnyTable("RcReportParameter", false));
    }

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

    [Fact]
    public void Dataset_parameter_extraction_ignores_string_literals()
    {
        var parameters = PocDataAssetService.ExtractParameterCodes(
            "SELECT '@ignored' AS literalValue, @deptId AS departmentId");

        Assert.Equal(["deptId"], parameters);
    }

    [Fact]
    public void Dataset_parameter_extraction_also_enforces_read_only_sql()
    {
        var exception = Assert.Throws<PocDatasetConfigurationException>(() =>
            PocDataAssetService.ExtractParameterCodes("UPDATE Workload SET Value = @value"));

        Assert.Equal("DATASET_QUERY_NOT_READ_ONLY", exception.Code);
    }

    [Fact]
    public async Task Dataset_preview_requires_every_referenced_parameter()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            _dataAssetService.PreviewDatasetAsync(new PocDatasetPreviewRequest(
                "report_center",
                "SELECT @deptId AS departmentName",
                new Dictionary<string, string?>(),
                30)));

        Assert.Equal("DATASET_PREVIEW_PARAMETER_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task Dataset_preview_rejects_mutating_sql_before_connecting()
    {
        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            _dataAssetService.PreviewDatasetAsync(new PocDatasetPreviewRequest(
                "report_center",
                "DELETE FROM Workload",
                new Dictionary<string, string?>(),
                30)));

        Assert.Equal("DATASET_QUERY_NOT_READ_ONLY", exception.Code);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
    }

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
