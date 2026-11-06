using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 数据同步列注释（description）链路测试：Oracle 真注释无法在单测连接，统一通过
/// 假注释加载器注入“列名 → 注释”字典，验证三层透传——metadata 列清单合并、
/// 预览字段透传（preview-source 按表注释、preview-mapping 按数据集声明）与
/// 源数据集字段契约写入（有注释才覆盖、无注释保留原有、失败诚实降级为空契约）。
/// 真实数据库的注释返回由运维验收（Oracle report_center / SqlServer data_source_2）。
/// </summary>
public sealed class PocDataSyncColumnDescriptionTests : IDisposable
{
    private readonly PocDataSyncTestEnvironment _environment;

    /// <summary>初始化测试实例：搭建内存库测试环境（源/目标、主库表、绑定连接工厂）。</summary>
    public PocDataSyncColumnDescriptionTests()
    {
        _environment = new PocDataSyncTestEnvironment();
    }

    /// <summary>释放测试环境持有的连接与临时资源。</summary>
    public void Dispose()
    {
        _environment.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>按编码读取数据集文档（走应用服务，含字段契约展开）。</summary>
    /// <param name="code">数据集编码。</param>
    /// <returns>命中的数据集文档，不存在返回 null。</returns>
    private async Task<PocDatasetDocument?> GetDatasetDocumentAsync(string code) =>
        (await _environment.DataAssetService.ListDatasetsAsync())
            .FirstOrDefault(item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>构造绑定假注释加载器与 SQLite 元数据工厂的同步服务，隔离真实数据库。</summary>
    /// <param name="comments">假注释字典（键忽略大小写，与生产实现的字典口径一致）。</param>
    /// <returns>供测试专用的同步服务实例。</returns>
    private PocDataSyncService CreateServiceWithFakeComments(
        IReadOnlyDictionary<string, string?> comments)
    {
        return new PocDataSyncService(
            _environment.SyncStore,
            _environment.DataAssets,
            _environment.Database,
            new PocDataSyncExecutor(_environment.CreateBoundClient),
            _environment.DataAssetService,
            readColumnComments: (_, _, _) => Task.FromResult(comments),
            metadataClientFactory: _environment.CreateBoundClient);
    }

    /// <summary>构造假注释字典：与生产 ReadColumnCommentsFromDatabaseAsync 相同的忽略大小写口径。</summary>
    /// <param name="entries">列名 → 注释。</param>
    /// <returns>忽略大小写的注释字典。</returns>
    private static IReadOnlyDictionary<string, string?> Comments(
        params (string Name, string? Description)[] entries)
    {
        var dictionary = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, description) in entries)
        {
            dictionary[name] = description;
        }
        return dictionary;
    }

    /// <summary>验证注释解析优先级：方言数据字典命中 > SqlSugar 元数据描述 > 空白与未命中输出 null。</summary>
    [Fact]
    public void ResolveColumnDescription_prefers_dictionary_then_falls_back()
    {
        var comments = Comments(("CARD_NO", "  卡号 "), ("REMARK", "   "));

        // 第一层：字典命中并 Trim。
        Assert.Equal("卡号", PocDataSyncService.ResolveColumnDescription("card_no", comments, "元数据描述"));
        // 第二层：字典未命中回退 SqlSugar ColumnDescription。
        Assert.Equal("元数据描述", PocDataSyncService.ResolveColumnDescription("visit_date", comments, "元数据描述"));
        // 第三层：字典空白注释不算命中，仍然回退。
        Assert.Equal("元数据描述", PocDataSyncService.ResolveColumnDescription("remark", comments, "元数据描述"));
        // 都取不到输出 null，前端不渲染占位文案。
        Assert.Null(PocDataSyncService.ResolveColumnDescription("unknown", comments, null));
        Assert.Null(PocDataSyncService.ResolveColumnDescription("unknown", comments, "   "));
    }

    /// <summary>验证注释匹配按列名忽略大小写命中，空白列名与空白注释都返回 null。</summary>
    [Fact]
    public void MatchColumnDescription_is_case_insensitive_and_honest_when_missing()
    {
        var comments = Comments(("card_no", "卡号"));

        Assert.Equal("卡号", PocDataSyncService.MatchColumnDescription("CARD_NO", comments));
        Assert.Null(PocDataSyncService.MatchColumnDescription("amount", comments));
        Assert.Null(PocDataSyncService.MatchColumnDescription("", comments));
        Assert.Null(PocDataSyncService.MatchColumnDescription("card_no", Comments(("card_no", " "))));
    }

    /// <summary>
    /// 验证 preview-source 的注释透传：带 sourceTable 时按列名（忽略大小写）填充 fields 的
    /// description；不传 sourceTable（写 SQL 模式）时不硬造，description 保持 null。
    /// </summary>
    [Fact]
    public async Task PreviewSourceAsync_fills_description_only_with_source_table()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_seed_desc", "Append");
        var service = CreateServiceWithFakeComments(Comments(("card_no", "卡号")));

        // 带源表名：CARD_NO 输出列与表列 card_no 忽略大小写命中注释。
        var withTable = await service.PreviewSourceAsync(new PocSyncSourcePreviewRequest(
            "src_sqlite",
            "SELECT card_no AS CARD_NO, amount FROM sync_src_demo",
            "sync_src_demo"));
        var cardNo = withTable.Fields.Single(field => field.Code == "CARD_NO");
        var amount = withTable.Fields.Single(field => field.Code == "amount");
        Assert.Equal("卡号", cardNo.Description);
        Assert.Null(amount.Description);

        // 不带源表名：别名派生列没有可靠注释来源，全部 null。
        var withoutTable = await service.PreviewSourceAsync(new PocSyncSourcePreviewRequest(
            "src_sqlite",
            "SELECT card_no AS CARD_NO, amount FROM sync_src_demo"));
        Assert.All(withoutTable.Fields, field => Assert.Null(field.Description));
    }

    /// <summary>
    /// 验证 preview-mapping 的注释透传：fields 的 description 取源数据集字段声明的 displayName，
    /// 与列名相同时置 null 避免重复展示；没有字段声明的列同样为 null。
    /// </summary>
    [Fact]
    public async Task PreviewMappingAsync_copies_dataset_display_name_into_description()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_seed_mapping_desc", "Append");
        var service = CreateServiceWithFakeComments(Comments());

        // 预置数据集声明：cardNo 中文含义、deptCode 声明与列名重复（应输出 null）。
        var dataset = await GetDatasetDocumentAsync(_environment.SourceDatasetCode);
        Assert.NotNull(dataset);
        await _environment.DataAssetService.SaveDatasetAsync(dataset!.Code, dataset with
        {
            Fields =
            [
                new PocDatasetFieldDefinition("cardNo", "卡号", "string"),
                new PocDatasetFieldDefinition("amount", "金额", "number"),
                new PocDatasetFieldDefinition("deptCode", "deptCode", "string"),
            ],
            Revision = dataset.Revision,
        });

        var result = await service.PreviewMappingAsync("sync_seed_mapping_desc");

        Assert.Equal("卡号", result.Fields.Single(field => field.Code == "cardNo").Description);
        Assert.Equal("金额", result.Fields.Single(field => field.Code == "amount").Description);
        // 声明与列名重复（deptCode==deptCode）不重复展示。
        Assert.Null(result.Fields.Single(field => field.Code == "deptCode").Description);
        // 没有字段声明的列（id）诚实留空。
        Assert.Null(result.Fields.Single(field => field.Code == "id").Description);
    }

    /// <summary>
    /// 验证保存任务的新契约：Source 节带 sourceTable 时，服务端把源表列清单与注释写入
    /// 源数据集字段契约（displayName=中文注释、dataType 从数据库类型收敛到白名单）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_with_source_table_writes_comments_into_dataset_fields()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        // 源/目标数据源都用受支持类型（SqlServer）过 NormalizeType；连接由测试工厂绑定 SQLite。
        await _environment.SeedTaskAsync(
            "sync_seed_meta", "Append", targetDbType: "SqlServer", sourceDbType: "SqlServer");
        var service = CreateServiceWithFakeComments(Comments(("card_no", "卡号"), ("amount", "金额")));

        var saved = await service.SaveAsync("sync_from_meta", new PocSyncTaskInput(
            "sync_from_meta",
            "选表同步带注释",
            SourceDatasetCode: null,
            TargetDataSourceCode: "tgt_sqlite",
            TargetTable: "SYNC_TGT_META",
            ConflictPolicy: "Append",
            ScheduleType: "Manual",
            ScheduleTime: null,
            IntervalMinutes: 0,
            Mappings: [new PocSyncColumnMapping("card_no", "CARD_NO", "string", 16, true, [])],
            Enabled: true,
            Revision: 0,
            Source: new PocSyncTaskSourceInput(
                "src_sqlite",
                "SELECT card_no, amount FROM sync_src_demo",
                null,
                "sync_src_demo")));

        var dataset = await GetDatasetDocumentAsync(saved.SourceDatasetCode);
        Assert.NotNull(dataset);
        var cardNo = Assert.Single(dataset!.Fields, field => field.Code == "card_no");
        Assert.Equal("卡号", cardNo.DisplayName);
        var amount = Assert.Single(dataset.Fields, field => field.Code == "amount");
        Assert.Equal("金额", amount.DisplayName);
        // SQLite INTEGER 列（id）收敛为 number 白名单类型；TEXT 文本列收敛为 string。
        var id = Assert.Single(dataset.Fields, field => field.Code == "id");
        Assert.Equal("number", id.DataType);
        Assert.Equal("string", amount.DataType);
        Assert.Equal("string", cardNo.DataType);
        // 保留原有规则未触发的列（visit_date/dept_code 无注释、无既有声明）回退列名本身。
        Assert.Equal("visit_date", dataset.Fields.Single(field => field.Code == "visit_date").DisplayName);
    }

    /// <summary>
    /// 验证源数据集字段契约的“保留原有”规则：无注释的列沿用数据集里已有的 displayName，
    /// 不被列名回填冲掉；内容一致的重复保存复用数据集（修订号不推高、契约不再改写）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_keeps_existing_display_name_when_comment_missing()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync(
            "sync_seed_keep", "Append", targetDbType: "SqlServer", sourceDbType: "SqlServer");
        // 假注释只覆盖 amount：card_no 无注释，应保留数据集既有声明。
        var service = CreateServiceWithFakeComments(Comments(("amount", "金额")));
        var input = new PocSyncTaskInput(
            "sync_keep_names",
            "保留原有含义",
            SourceDatasetCode: null,
            TargetDataSourceCode: "tgt_sqlite",
            TargetTable: "SYNC_TGT_KEEP",
            ConflictPolicy: "Append",
            ScheduleType: "Manual",
            ScheduleTime: null,
            IntervalMinutes: 0,
            Mappings: [new PocSyncColumnMapping("card_no", "CARD_NO", "string", 16, true, [])],
            Enabled: true,
            Revision: 0,
            Source: new PocSyncTaskSourceInput(
                "src_sqlite",
                "SELECT card_no, amount FROM sync_src_demo",
                null,
                "sync_src_demo"));
        await service.SaveAsync("sync_keep_names", input);

        var dataset = await GetDatasetDocumentAsync("sync_src_sync_keep_names");
        Assert.NotNull(dataset);
        // 有注释的 amount 覆盖为中文；无注释的 card_no 没有“既有声明”时回退列名本身。
        Assert.Equal("金额", dataset!.Fields.Single(field => field.Code == "amount").DisplayName);
        Assert.Equal("card_no", dataset.Fields.Single(field => field.Code == "card_no").DisplayName);

        // 给 card_no 预置中文声明（模拟人工维护的数据集含义），修订号随之推高。
        await _environment.DataAssetService.SaveDatasetAsync(dataset.Code, dataset with
        {
            Fields = dataset.Fields.Select(field => field.Code == "card_no"
                ? field with { DisplayName = "就诊卡号" }
                : field).ToArray(),
            Revision = dataset.Revision,
        });
        var afterManual = await GetDatasetDocumentAsync("sync_src_sync_keep_names");
        Assert.NotNull(afterManual);
        var revisionAfterManual = afterManual!.Revision;

        // 第二次保存：源内容一致复用数据集，字段契约（含人工保留的中文声明）不再改写。
        await service.SaveAsync("sync_keep_names", input with { Revision = 1 });
        var reused = await GetDatasetDocumentAsync("sync_src_sync_keep_names");
        Assert.NotNull(reused);
        Assert.Equal(revisionAfterManual, reused!.Revision);
        Assert.Equal("就诊卡号", reused.Fields.Single(field => field.Code == "card_no").DisplayName);
        Assert.Equal("金额", reused.Fields.Single(field => field.Code == "amount").DisplayName);
    }

    /// <summary>
    /// 验证元数据读取失败的诚实降级：注释加载器抛数据库异常时，preview-source 的
    /// description 全部为 null，预览本身照常返回列与样例行，不因注释失败而失败。
    /// </summary>
    [Fact]
    public async Task PreviewSourceAsync_degrades_to_null_description_when_comment_loader_fails()
    {
        await _environment.SeedSourceTableAsync(("A-001", "1", "20240101", "CARD"));
        await _environment.SeedTaskAsync("sync_seed_fail", "Append");
        var failing = new PocDataSyncService(
            _environment.SyncStore,
            _environment.DataAssets,
            _environment.Database,
            new PocDataSyncExecutor(_environment.CreateBoundClient),
            _environment.DataAssetService,
            readColumnComments: (_, _, _) => throw new CommentQueryFailureException());

        var result = await failing.PreviewSourceAsync(new PocSyncSourcePreviewRequest(
            "src_sqlite",
            "SELECT card_no FROM sync_src_demo",
            "sync_src_demo"));

        Assert.NotEmpty(result.Fields);
        Assert.All(result.Fields, field => Assert.Null(field.Description));
        Assert.Single(result.Rows);
    }
}

/// <summary>
/// 测试专用数据库异常：模拟“注释查询被数据库拒绝”的真实故障形态，
/// 供降级逻辑断言复用（SqlException 构造受限，用 DbException 基类继承代替）。
/// </summary>
internal sealed class CommentQueryFailureException : System.Data.Common.DbException
{
    /// <summary>初始化 CommentQueryFailureException 实例，携带可定位的失败说明。</summary>
    public CommentQueryFailureException()
        : base("注释查询被数据库拒绝（测试注入故障）。")
    {
    }
}
