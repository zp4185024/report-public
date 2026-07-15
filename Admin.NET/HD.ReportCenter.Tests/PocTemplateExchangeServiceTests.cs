using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using System.Reflection;
using SqlSugar;

namespace HD.ReportCenter.Tests;

public sealed class PocTemplateExchangeServiceTests : IDisposable
{
    private long _nextId = 3000;
    private readonly SqlSugarClient _database;
    private readonly PocTemplateService _templateService;
    private readonly PocTemplateExchangeService _exchangeService;

    public PocTemplateExchangeServiceTests()
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
        _exchangeService = new PocTemplateExchangeService(
            new PocExchangeMappingStore(_database),
            new PocXlsxExchangeConverter());
    }

    [Fact]
    public async Task Current_template_can_round_trip_through_the_exchange_service()
    {
        var seed = await _templateService.GetAsync(PocReportCatalog.ReportCode);

        using var exported = await _exchangeService.ExportAsync(
            seed.Snapshot,
            "科室工作量模板");
        var signature = new byte[2];
        _ = await exported.Content.ReadAsync(signature);
        exported.Content.Position = 0;

        var imported = await _exchangeService.ImportAsync(
            exported.Content,
            exported.FileName);

        Assert.Equal([0x50, 0x4B], signature);
        Assert.Equal("0.25.1", exported.UniverVersion);
        Assert.Equal(1, exported.MappingVersion);
        Assert.Equal(exported.UniverVersion, imported.UniverVersion);
        Assert.Equal(exported.MappingVersion, imported.MappingVersion);
        Assert.Equal(seed.Binding.ParameterBindings.Count, imported.Binding.ParameterBindings.Count);
        Assert.Equal(seed.Binding.DetailBinding?.Fields.Count, imported.Binding.DetailBinding?.Fields.Count);
        Assert.NotNull(imported.Snapshot["sheets"]);
        Assert.True(imported.UsedCellCount > 0);
    }

    [Fact]
    public async Task Import_rejects_a_non_xlsx_extension()
    {
        await using var stream = new MemoryStream([0x50, 0x4B]);

        var exception = await Assert.ThrowsAsync<PocExchangeConversionException>(
            () => _exchangeService.ImportAsync(stream, "模板.xls"));

        Assert.Equal("TEMPLATE_XLSX_EXTENSION_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task Import_rejects_a_file_larger_than_the_configured_limit()
    {
        await using var stream = new MemoryStream(
            new byte[PocTemplateExchangeService.MaximumImportFileBytes + 1]);

        var exception = await Assert.ThrowsAsync<PocExchangeConversionException>(
            () => _exchangeService.ImportAsync(stream, "模板.xlsx"));

        Assert.Equal("TEMPLATE_XLSX_FILE_TOO_LARGE", exception.Code);
    }

    public void Dispose()
    {
        _database.Dispose();
    }
}
