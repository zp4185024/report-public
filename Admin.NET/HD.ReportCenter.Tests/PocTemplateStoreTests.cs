using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using SqlSugar;

namespace HD.ReportCenter.Tests;

public sealed class PocTemplateStoreTests : IDisposable
{
    private readonly SqlSugarClient _database;
    private readonly PocTemplateStore _store;

    public PocTemplateStoreTests()
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
        _database.Ado.Open();
        _database.CodeFirst.InitTables<PocReportTemplate>();
        _store = new PocTemplateStore(_database);
    }

    [Fact]
    public async Task First_save_creates_revision_one()
    {
        var saved = await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);

        Assert.Equal(1, saved.Revision);
    }

    [Fact]
    public async Task Get_returns_the_saved_json_unchanged()
    {
        const string snapshotJson = "{\"id\":\"report-validation-workbook\"}";
        const string bindingJson = "{\"version\":1,\"parameterBindings\":[]}";
        await _store.SaveAsync("dept-workload", snapshotJson, bindingJson, 0);

        var stored = await _store.GetAsync("dept-workload");

        Assert.NotNull(stored);
        Assert.Equal(snapshotJson, stored.SnapshotJson);
        Assert.Equal(bindingJson, stored.BindingJson);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task Second_save_increments_the_revision()
    {
        await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);

        var saved = await _store.SaveAsync("dept-workload", "{\"rev\":2}", "{\"version\":1}", 1);

        Assert.Equal(2, saved.Revision);
        Assert.Equal("{\"rev\":2}", saved.SnapshotJson);
    }

    [Fact]
    public async Task Save_with_a_stale_revision_throws_a_conflict()
    {
        await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);
        await _store.SaveAsync("dept-workload", "{\"rev\":2}", "{\"version\":1}", 1);

        await Assert.ThrowsAsync<PocTemplateRevisionConflictException>(
            () => _store.SaveAsync("dept-workload", "{\"rev\":3}", "{\"version\":1}", 1));
    }

    [Fact]
    public async Task Service_returns_a_valid_embedded_seed_when_the_database_is_empty()
    {
        var service = new PocTemplateService(_store);

        var document = await service.GetAsync(PocReportCatalog.ReportCode);
        var scanned = PocTemplateValidator.Scan(document.Snapshot.ToString(Formatting.None));

        Assert.Equal(PocReportCatalog.ReportCode, document.ReportCode);
        Assert.Equal(0, document.Revision);
        Assert.Equal(3, document.Snapshot["styles"]?["header"]?["tb"]?.Value<int>());
        Assert.NotEmpty(scanned.ParameterBindings);
        Assert.NotNull(scanned.DetailBinding);
    }

    [Fact]
    public async Task Service_saves_a_canonical_binding_and_reads_it_back()
    {
        var service = new PocTemplateService(_store);
        var seed = await service.GetAsync(PocReportCatalog.ReportCode);

        var saved = await service.SaveAsync(
            PocReportCatalog.ReportCode,
            new PocSaveTemplateRequest(seed.Snapshot, seed.Binding, seed.Revision));
        var reloaded = await service.GetAsync(PocReportCatalog.ReportCode);

        Assert.Equal(1, saved.Revision);
        Assert.Equal(1, reloaded.Revision);
        Assert.True(JToken.DeepEquals(seed.Snapshot, reloaded.Snapshot));
        Assert.True(JToken.DeepEquals(JToken.FromObject(seed.Binding), JToken.FromObject(reloaded.Binding)));
    }

    [Fact]
    public async Task Service_rejects_a_noncanonical_binding_without_writing_to_the_database()
    {
        var service = new PocTemplateService(_store);
        var seed = await service.GetAsync(PocReportCatalog.ReportCode);
        var mismatched = seed.Binding with { Version = seed.Binding.Version + 1 };

        await Assert.ThrowsAsync<PocTemplateBindingMismatchException>(
            () => service.SaveAsync(
                PocReportCatalog.ReportCode,
                new PocSaveTemplateRequest(seed.Snapshot, mismatched, seed.Revision)));

        Assert.Null(await _store.GetAsync(PocReportCatalog.ReportCode));
    }

    [Fact]
    public async Task Service_rejects_a_stored_binding_that_does_not_match_the_snapshot()
    {
        var service = new PocTemplateService(_store);
        var seed = await service.GetAsync(PocReportCatalog.ReportCode);
        var mismatched = seed.Binding with { Version = seed.Binding.Version + 1 };
        await _store.SaveAsync(
            PocReportCatalog.ReportCode,
            seed.Snapshot.ToString(Formatting.None),
            JsonConvert.SerializeObject(mismatched, Formatting.None),
            0);

        var exception = await Assert.ThrowsAsync<PocTemplateBindingMismatchException>(
            () => service.GetAsync(PocReportCatalog.ReportCode));

        Assert.Equal("TEMPLATE_BINDING_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task Api_get_template_returns_the_seed_document()
    {
        var api = new PocReportApi(new PocTemplateService(_store));

        var document = await api.GetTemplate(PocReportCatalog.ReportCode.ToUpperInvariant());

        Assert.Equal(0, document.Revision);
        Assert.Equal(PocReportCatalog.ReportCode, document.ReportCode);
    }

    [Fact]
    public async Task Api_put_template_saves_the_document()
    {
        var api = new PocReportApi(new PocTemplateService(_store));
        var seed = await api.GetTemplate(PocReportCatalog.ReportCode);
        var request = new PocSaveTemplateRequest(seed.Snapshot, seed.Binding, seed.Revision);

        var saved = await api.SaveTemplate(PocReportCatalog.ReportCode, request);

        Assert.Equal(1, saved.Revision);
    }

    public void Dispose()
    {
        _database.Dispose();
    }
}
