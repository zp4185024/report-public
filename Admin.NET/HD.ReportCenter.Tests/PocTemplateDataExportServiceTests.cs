using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;
using Newtonsoft.Json.Linq;
using SqlSugar;
using System.Reflection;

namespace HD.ReportCenter.Tests;

public sealed class PocTemplateDataExportServiceTests : IDisposable
{
    private long _nextId = 5000;
    private readonly SqlSugarClient _database;
    private readonly PocTemplateService _templateService;
    private readonly PocTemplateDataExportService _exportService;

    /// <summary>初始化 PocTemplateDataExportServiceTests 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocTemplateDataExportServiceTests()
    {
        // 第一阶段：更新 PocTemplateDataExportServiceTests 当前分支对应的业务状态，并保持关联数据一致。
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
                // 第2阶段：整理 PocTemplateDataExportServiceTests 已计算的数据，并按方法契约返回最终结果。
                return;
            }

            var id = (long)entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue)!;
            if (id == 0)
            {
                entityInfo.SetValue(Interlocked.Increment(ref _nextId));
            }
        };
        _database.Ado.Open();
        _database.CodeFirst.InitTables<PocReportTemplate, PocExchangeMapping>();

        _templateService = new PocTemplateService(new PocTemplateStore(_database));
        _exportService = new PocTemplateDataExportService(
            _templateService,
            new PocExchangeMappingStore(_database));
    }

    /// <summary>验证 Create Plan Rejects Unsaved Revision Zero 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task CreatePlan_RejectsUnsavedRevisionZero()
    {
        var exception = await Assert.ThrowsAsync<PocDataExportConflictException>(() =>
            _exportService.CreatePlanAsync(
                PocReportCatalog.ReportCode,
                new PocDataExportRequest(0, CreateQuery()),
                CancellationToken.None));

        Assert.Equal("TEMPLATE_SAVE_REQUIRED", exception.Code);
        Assert.Contains("保存", exception.Message);
    }

    /// <summary>验证 Create Plan Rejects Stale Revision 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task CreatePlan_RejectsStaleRevision()
    {
        var saved = await SaveSeedAsync();

        var exception = await Assert.ThrowsAsync<PocDataExportConflictException>(() =>
            _exportService.CreatePlanAsync(
                PocReportCatalog.ReportCode,
                new PocDataExportRequest(saved.Revision + 1, CreateQuery()),
                CancellationToken.None));

        Assert.Equal("TEMPLATE_REVISION_CHANGED", exception.Code);
        Assert.Contains(saved.Revision.ToString(), exception.Message);
    }

    /// <summary>验证 Create Plan Freezes Saved Snapshot Binding Query And Mappings 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task CreatePlan_FreezesSavedSnapshotBindingQueryAndMappings()
    {
        var saved = await SaveSeedAsync();
        var request = new PocDataExportRequest(saved.Revision, CreateQuery());

        var plan = await _exportService.CreatePlanAsync(
            PocReportCatalog.ReportCode,
            request,
            CancellationToken.None);

        Assert.Equal(saved.Revision, plan.TemplateRevision);
        Assert.True(JToken.DeepEquals(saved.Snapshot, plan.Snapshot));
        Assert.NotSame(saved.Snapshot, plan.Snapshot);
        Assert.NotSame(saved.Binding.ParameterBindings, plan.Binding.ParameterBindings);
        Assert.NotSame(request.Query, plan.Query);
        Assert.Equal(PocDataExportMode.Normal, plan.Export.DefaultMode);
        Assert.Equal("0.25.1", plan.Mappings.UniverVersion);
        Assert.Equal(1, plan.Mappings.MappingVersion);
        Assert.Equal(25, plan.Mappings.Rules.Count(rule =>
            rule.Direction == PocExchangeDirections.UniverToExcel));
    }

    /// <summary>验证 Create Plan Uses The Report Selected From The Template List 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task CreatePlan_UsesTheReportSelectedFromTheTemplateList()
    {
        const string reportCode = "outpatient-log";
        var seed = await _templateService.GetAsync(reportCode);
        var saved = await _templateService.SaveAsync(
            reportCode,
            new PocSaveTemplateRequest(seed.Snapshot, seed.Binding, seed.Revision));

        var plan = await _exportService.CreatePlanAsync(
            reportCode,
            new PocDataExportRequest(saved.Revision, CreateQuery()),
            CancellationToken.None);

        Assert.Equal(reportCode, plan.ReportCode);
        Assert.Equal(saved.Revision, plan.TemplateRevision);
    }

    /// <summary>释放 PocTemplateDataExportServiceTests 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    /// <summary>更新 PocTemplateDataExportServiceTests 的 Save Seed Async 状态，并同步相关业务数据。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<PocTemplateDocument> SaveSeedAsync()
    {
        var seed = await _templateService.GetAsync(PocReportCatalog.ReportCode);
        return await _templateService.SaveAsync(
            PocReportCatalog.ReportCode,
            new PocSaveTemplateRequest(seed.Snapshot, seed.Binding, seed.Revision));
    }

    /// <summary>构建 PocTemplateDataExportServiceTests 所需的 Create Query 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocExecuteRequest CreateQuery() =>
        new(3, "H01", "D02", "P03", "2026-07-01", "2026-07-14");
}
