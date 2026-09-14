using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using SqlSugar;

namespace HD.ReportCenter.Tests;

public sealed class PocTemplateStoreTests : IDisposable
{
    private long _nextId = 1000;
    private readonly SqlSugarClient _database;
    private readonly PocTemplateStore _store;
    private readonly PocTemplateService _templateService;
    private readonly PocTemplateExchangeService _exchangeService;
    private readonly PocTemplateDataExportService _dataExportService;

    /// <summary>初始化 PocTemplateStoreTests 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocTemplateStoreTests()
    {
        // 第一阶段：更新 PocTemplateStoreTests 当前分支对应的业务状态，并保持关联数据一致。
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

            // 第2阶段：构造 PocTemplateStoreTests 当前步骤需要的中间数据和执行参数。
            var id = (long)entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue)!;
            if (id == 0)
            {
                entityInfo.SetValue(Interlocked.Increment(ref _nextId));
            }
        };
        _database.Ado.Open();
        _database.CodeFirst.InitTables<PocReportTemplate, PocExchangeMapping>();
        _store = new PocTemplateStore(_database);
        _templateService = new PocTemplateService(_store);
        var mappingStore = new PocExchangeMappingStore(_database);
        _exchangeService = new PocTemplateExchangeService(
            mappingStore,
            new PocXlsxExchangeConverter());
        _dataExportService = new PocTemplateDataExportService(_templateService, mappingStore);
    }

    /// <summary>验证 Report entities use production table names 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Report_entities_use_production_table_names()
    {
        Assert.True(_database.DbMaintenance.IsAnyTable("RcReportTemplate", false));
        Assert.True(_database.DbMaintenance.IsAnyTable("RcExchangeMapping", false));
        Assert.False(_database.DbMaintenance.IsAnyTable("RcPocReportTemplate", false));
        Assert.False(_database.DbMaintenance.IsAnyTable("RcPocExchangeMapping", false));
    }

    /// <summary>验证 First save creates revision one 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task First_save_creates_revision_one()
    {
        var saved = await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);
        var entity = await _database.Queryable<PocReportTemplate>()
            .SingleAsync(x => x.ReportCode == "dept-workload");

        Assert.Equal(1, saved.Revision);
        Assert.Equal(1001, entity.Id);
    }

    /// <summary>验证 Repeating a first save translates the sqlite unique error to a conflict 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Repeating_a_first_save_translates_the_sqlite_unique_error_to_a_conflict()
    {
        await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);
        _database.Aop.OnError = _ =>
            throw new SqliteException("RcReportTemplate.ReportCode 违反唯一约束", 19, 2067);

        await Assert.ThrowsAsync<PocTemplateRevisionConflictException>(
            () => _store.SaveAsync("dept-workload", "{\"rev\":2}", "{\"version\":1}", 0));
    }

    /// <summary>验证 A non report code constraint is not translated to a revision conflict 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task A_non_report_code_constraint_is_not_translated_to_a_revision_conflict()
    {
        await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);
        _database.Aop.OnError = _ =>
            throw new InvalidOperationException(
                "duplicate key while inserting ReportCode",
                new SqliteException("RcReportTemplate.Id 违反唯一约束", 19, 1555));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SaveAsync("dept-workload", "{\"rev\":2}", "{\"version\":1}", 0));
    }

    /// <summary>验证 Get returns the saved json unchanged 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>验证 Second save increments the revision 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Second_save_increments_the_revision()
    {
        await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);

        var saved = await _store.SaveAsync("dept-workload", "{\"rev\":2}", "{\"version\":1}", 1);

        Assert.Equal(2, saved.Revision);
        Assert.Equal("{\"rev\":2}", saved.SnapshotJson);
    }

    /// <summary>验证 Save with a stale revision throws a conflict 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Save_with_a_stale_revision_throws_a_conflict()
    {
        await _store.SaveAsync("dept-workload", "{\"rev\":1}", "{\"version\":1}", 0);
        await _store.SaveAsync("dept-workload", "{\"rev\":2}", "{\"version\":1}", 1);

        await Assert.ThrowsAsync<PocTemplateRevisionConflictException>(
            () => _store.SaveAsync("dept-workload", "{\"rev\":3}", "{\"version\":1}", 1));
    }

    /// <summary>验证 Service returns a valid embedded seed when the database is empty 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>验证 Service saves a canonical binding and reads it back 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>验证 Service rejects a noncanonical binding without writing to the database 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>验证 Service rejects a stored binding that does not match the snapshot 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Service_rejects_a_stored_binding_that_does_not_match_the_snapshot()
    {
        var service = new PocTemplateService(_store);
        var seed = await service.GetAsync(PocReportCatalog.ReportCode);
        var mismatched = seed.Binding with { Version = seed.Binding.Version + 1 };
        await InsertStoredTemplateAsync(
            seed.Snapshot.ToString(Formatting.None),
            JsonConvert.SerializeObject(mismatched, Formatting.None));

        var exception = await Assert.ThrowsAsync<PocStoredTemplateCorruptedException>(
            () => service.GetAsync(PocReportCatalog.ReportCode));

        Assert.Equal("TEMPLATE_STORED_DATA_INVALID", exception.Code);
    }

    /// <summary>验证 Service wraps invalid stored snapshot json as corruption 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Service_wraps_invalid_stored_snapshot_json_as_corruption()
    {
        var service = new PocTemplateService(_store);
        await InsertStoredTemplateAsync("{not-json", "{}");

        var exception = await Assert.ThrowsAsync<PocStoredTemplateCorruptedException>(
            () => service.GetAsync(PocReportCatalog.ReportCode));

        Assert.Equal("TEMPLATE_STORED_DATA_INVALID", exception.Code);
    }

    /// <summary>验证 Service wraps stored snapshot validation failure as corruption 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Service_wraps_stored_snapshot_validation_failure_as_corruption()
    {
        var service = new PocTemplateService(_store);
        var snapshot = new JObject
        {
            ["sheets"] = new JObject
            {
                ["report-sheet"] = new JObject
                {
                    ["cellData"] = new JObject
                    {
                        ["0"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "{{params.unknown}}" }
                        }
                    }
                }
            }
        };
        await InsertStoredTemplateAsync(snapshot.ToString(Formatting.None), "{}");

        await Assert.ThrowsAsync<PocStoredTemplateCorruptedException>(
            () => service.GetAsync(PocReportCatalog.ReportCode));

        var editable = await service.GetEditableAsync(PocReportCatalog.ReportCode);

        Assert.Equal(1, editable.Revision);
        Assert.Equal("{{params.unknown}}", editable.Snapshot["sheets"]?["report-sheet"]?["cellData"]?["0"]?["0"]?["v"]?.Value<string>());
        Assert.NotNull(editable.ValidationWarning);
        Assert.Empty(editable.Binding.ParameterBindings);
        Assert.Null(editable.Binding.DetailBinding);
    }

    /// <summary>验证编辑器会返回多行明细冲突的具体修复位置，而不是误报字段定义变化。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Editable_service_surfaces_multi_row_detail_conflict()
    {
        var service = new PocTemplateService(_store);
        var snapshot = new JObject
        {
            ["sheets"] = new JObject
            {
                ["report-sheet"] = new JObject
                {
                    ["name"] = "科室工作量",
                    ["cellData"] = new JObject
                    {
                        ["2"] = new JObject
                        {
                            ["2"] = new JObject { ["v"] = "{{detail.sequence}}" }
                        },
                        ["3"] = new JObject
                        {
                            ["0"] = new JObject { ["v"] = "{{detail.departmentName}}" }
                        }
                    },
                    ["mergeData"] = new JArray()
                }
            },
            ["sheetOrder"] = new JArray("report-sheet")
        };
        await InsertStoredTemplateAsync(snapshot.ToString(Formatting.None), "{}");

        var editable = await service.GetEditableAsync(PocReportCatalog.ReportCode);

        Assert.Contains("已有明细区域", editable.ValidationWarning);
        Assert.Contains("第 3 行", editable.ValidationWarning);
        Assert.Contains("单元格 A4", editable.ValidationWarning);
        Assert.Equal("{{detail.sequence}}", editable.Snapshot["sheets"]?["report-sheet"]?["cellData"]?["2"]?["2"]?["v"]?.Value<string>());
        Assert.Empty(editable.Binding.ParameterBindings);
        Assert.Null(editable.Binding.DetailBinding);
    }

    /// <summary>验证 Api get template returns the seed document 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Api_get_template_returns_the_seed_document()
    {
        var api = new PocReportApi(_templateService, _exchangeService, _dataExportService);

        var document = await api.GetTemplate(PocReportCatalog.ReportCode.ToUpperInvariant());

        Assert.Equal(0, document.Revision);
        Assert.Equal(PocReportCatalog.ReportCode, document.ReportCode);
    }

    /// <summary>验证 Api put template saves the document 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Api_put_template_saves_the_document()
    {
        var api = new PocReportApi(_templateService, _exchangeService, _dataExportService);
        var seed = await api.GetTemplate(PocReportCatalog.ReportCode);
        var request = new PocSaveTemplateRequest(seed.Snapshot, seed.Binding, seed.Revision);

        var saved = await api.SaveTemplate(PocReportCatalog.ReportCode, request);

        Assert.Equal(1, saved.Revision);
    }

    /// <summary>验证 Api normal export uses the saved template and all query rows 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Api_normal_export_uses_the_saved_template_and_all_query_rows()
    {
        var seed = await _templateService.GetAsync(PocReportCatalog.ReportCode);
        var saved = await _templateService.SaveAsync(
            PocReportCatalog.ReportCode,
            new PocSaveTemplateRequest(seed.Snapshot, seed.Binding, seed.Revision));
        var api = new PocReportApi(_templateService, _exchangeService, _dataExportService);
        var query = new PocExecuteRequest(
            3,
            "H01",
            "D02",
            "P03",
            "2026-07-01",
            "2026-07-14");

        var file = await api.NormalExport(
            PocReportCatalog.ReportCode,
            new PocDataExportRequest(saved.Revision, query),
            CancellationToken.None);

        using var workbook = new XLWorkbook(file.FileStream);
        var sheet = workbook.Worksheet("科室工作量");
        Assert.Equal(137d, sheet.Cell("A142").GetDouble());
        Assert.Equal("C142+D142", sheet.Cell("E142").FormulaA1);
        Assert.Equal("科室工作量统计_20260701_20260714.xlsx", file.FileDownloadName);
    }

    /// <summary>释放 PocTemplateStoreTests 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    /// <summary>调用 PocTemplateStoreTests 的异步依赖完成 Insert Stored Template Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="snapshotJson">snapshotJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="bindingJson">bindingJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private Task<int> InsertStoredTemplateAsync(string snapshotJson, string bindingJson)
    {
        var now = DateTime.Now;
        return _database.Insertable(new PocReportTemplate
        {
            ReportCode = PocReportCatalog.ReportCode,
            SnapshotJson = snapshotJson,
            BindingJson = bindingJson,
            Revision = 1,
            CreateTime = now,
            UpdateTime = now
        }).ExecuteCommandAsync();
    }
}
