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

    /// <summary>初始化 PocTemplateExchangeServiceTests 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocTemplateExchangeServiceTests()
    {
        // 第一阶段：更新 PocTemplateExchangeServiceTests 当前分支对应的业务状态，并保持关联数据一致。
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
                // 第2阶段：整理 PocTemplateExchangeServiceTests 已计算的数据，并按方法契约返回最终结果。
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

    /// <summary>验证 Current template can round trip through the exchange service 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>验证 Import rejects a non xlsx extension 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Import_rejects_a_non_xlsx_extension()
    {
        await using var stream = new MemoryStream([0x50, 0x4B]);

        var exception = await Assert.ThrowsAsync<PocExchangeConversionException>(
            () => _exchangeService.ImportAsync(stream, "模板.xls"));

        Assert.Equal("TEMPLATE_XLSX_EXTENSION_REQUIRED", exception.Code);
    }

    /// <summary>验证 Import rejects a file larger than the configured limit 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Import_rejects_a_file_larger_than_the_configured_limit()
    {
        await using var stream = new MemoryStream(
            new byte[PocTemplateExchangeService.MaximumImportFileBytes + 1]);

        var exception = await Assert.ThrowsAsync<PocExchangeConversionException>(
            () => _exchangeService.ImportAsync(stream, "模板.xlsx"));

        Assert.Equal("TEMPLATE_XLSX_FILE_TOO_LARGE", exception.Code);
    }

    /// <summary>释放 PocTemplateExchangeServiceTests 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _database.Dispose();
    }
}
