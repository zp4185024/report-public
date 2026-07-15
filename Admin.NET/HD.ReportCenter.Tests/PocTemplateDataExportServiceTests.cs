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

    public PocTemplateDataExportServiceTests()
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
        _database.CodeFirst.InitTables<PocReportTemplate, PocExchangeMapping>();

        _templateService = new PocTemplateService(new PocTemplateStore(_database));
        _exportService = new PocTemplateDataExportService(
            _templateService,
            new PocExchangeMappingStore(_database));
    }

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

    public void Dispose()
    {
        _database.Dispose();
    }

    private async Task<PocTemplateDocument> SaveSeedAsync()
    {
        var seed = await _templateService.GetAsync(PocReportCatalog.ReportCode);
        return await _templateService.SaveAsync(
            PocReportCatalog.ReportCode,
            new PocSaveTemplateRequest(seed.Snapshot, seed.Binding, seed.Revision));
    }

    private static PocExecuteRequest CreateQuery() =>
        new(3, "H01", "D02", "P03", "2026-07-01", "2026-07-14");
}
