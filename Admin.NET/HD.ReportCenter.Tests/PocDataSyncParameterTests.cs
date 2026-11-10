using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 数据同步取数参数（滚动时间窗口）链路测试：变量契约随任务保存落库并在只改参数时
/// 触发源数据集更新、正式运行按当前时间现算窗口并写审计日志、试跑与源预览支持显式
/// 取值覆盖、非法表达式在保存即拒绝。源库用 SQLite 内存库，日期比较经 date() 归一。
/// </summary>
public sealed class PocDataSyncParameterTests : IDisposable
{
    private readonly PocDataSyncTestEnvironment _environment;
    private readonly string _today;
    private readonly string _oldDate;
    private readonly string _todayIso;
    private readonly string _oldDateIso;

    /// <summary>参数化窗口 SQL：date 型参数绑定的是 yyyy-MM-dd 时刻文本，用 strftime 归一成与列一致的 yyyyMMdd 再做文本比较。</summary>
    private const string WindowQueryText =
        "SELECT card_no AS cardNo, amount, visit_date AS visitDate, dept_code AS deptCode " +
        "FROM sync_src_demo " +
        "WHERE visit_date >= strftime('%Y%m%d', @startDate) AND visit_date <= strftime('%Y%m%d', @endDate)";

    /// <summary>初始化测试实例：搭建内存库测试环境，并按运行时刻准备“今天/60天前”两个日期文本。</summary>
    public PocDataSyncParameterTests()
    {
        _environment = new PocDataSyncTestEnvironment();
        // 源表 visit_date 用 HIS 常见的 yyyyMMdd 文本形态（与播种任务的 toDate 转换器一致）。
        _today = DateTimeOffset.Now.ToString("yyyyMMdd");
        _oldDate = DateTimeOffset.Now.AddDays(-60).ToString("yyyyMMdd");
        // date 类型变量的显式取值必须用 yyyy-MM-dd（与控件输入格式一致的解析口径）。
        _todayIso = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        _oldDateIso = DateTimeOffset.Now.AddDays(-60).ToString("yyyy-MM-dd");
    }

    /// <summary>释放测试环境持有的连接与临时资源。</summary>
    public void Dispose()
    {
        _environment.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>构造窗口变量契约：startDate/endDate 均为日期型滚动表达式（可选覆盖表达式）。</summary>
    /// <param name="startExpression">startDate 的默认表达式。</param>
    /// <param name="endExpression">endDate 的默认表达式。</param>
    /// <returns>与窗口 SQL 引用一一对应的变量列表。</returns>
    private static List<PocDatasetVariableDefinition> WindowVariables(
        string startExpression = "yesterday()",
        string endExpression = "tomorrow()") =>
    [
        new("startDate", "窗口开始", "date", false, DefaultExpression: startExpression),
        new("endDate", "窗口结束", "date", false, DefaultExpression: endExpression)
    ];

    /// <summary>构造带源节 + 变量契约的任务保存输入：映射与测试环境源表列一一对应。</summary>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="variables">取数参数契约。</param>
    /// <param name="revision">乐观并发期望修订号。</param>
    /// <returns>保存输入。</returns>
    private PocSyncTaskInput BuildInput(
        string taskCode,
        IReadOnlyList<PocDatasetVariableDefinition> variables,
        int revision = 0) =>
        new(
            taskCode,
            "窗口同步任务",
            SourceDatasetCode: null,
            TargetDataSourceCode: "tgt_sqlite",
            TargetTable: "SYNC_TGT_WINDOW",
            ConflictPolicy: "Append",
            ScheduleType: "Manual",
            ScheduleTime: null,
            IntervalMinutes: 0,
            Mappings:
            [
                new("cardNo", "CARD_NO", "string", 16, true, []),
                new("amount", "AMOUNT", "decimal", null, true,
                    [new PocSyncConverterStep("toDecimal", new Dictionary<string, string?> { ["scale"] = "2" })]),
                new("visitDate", "VISIT_DATE", "datetime", null, true,
                    [new PocSyncConverterStep("toDate", new Dictionary<string, string?> { ["format"] = "yyyy-MM-dd" })]),
                new("deptCode", "DEPT_CODE", "string", 8, true, [])
            ],
            Enabled: true,
            Revision: revision,
            Source: new PocSyncTaskSourceInput(
                "src_sqlite",
                "SELECT card_no AS cardNo, amount, visit_date AS visitDate, dept_code AS deptCode " +
                "FROM sync_src_demo " +
                "WHERE date(visit_date) >= date(@startDate) AND date(visit_date) <= date(@endDate)",
                "窗口源数据集",
                Variables: variables));

    /// <summary>播种环境：源表两行（今天一行 + 60 天前一行）与可通过保存校验的目标数据源。</summary>
    /// <param name="targetDbType">播种任务的目标数据源类型；执行类测试用默认 SQLite（绑定工厂可写），保存类测试用 SqlServer 过白名单校验。</param>
    private async Task SeedAsync(string targetDbType = "SqlServer")
    {
        await _environment.SeedSourceTableAsync(
            ("A-001", "1", _today, "CARD"),
            ("B-002", "2", _oldDate, "XYZ"));
        await _environment.SeedTaskAsync("sync_seed_parameter", "Append", targetDbType: targetDbType);
    }

    /// <summary>
    /// 把播种源数据集改写为参数化窗口查询（SQL 引用 @startDate/@endDate 并声明变量契约），
    /// 播种任务随即按滚动窗口执行——SQLite 目标类型不在保存白名单内，执行类测试走播种任务
    /// 改写数据集的路径（与既有执行器测试同构），不经 SaveAsync。
    /// </summary>
    /// <param name="variables">取数参数契约。</param>
    private async Task ParametrizeSeededSourceAsync(IReadOnlyList<PocDatasetVariableDefinition> variables)
    {
        var existing = (await _environment.DataAssetService.ListDatasetsAsync())
            .Single(item => item.Code.Equals(_environment.SourceDatasetCode, StringComparison.Ordinal));
        await _environment.DataAssetService.SaveDatasetAsync(existing.Code, existing with
        {
            QueryText = WindowQueryText,
            Variables = variables
        });
    }

    /// <summary>
    /// 验证变量契约随保存落库；只改表达式不动 SQL 时复用判定失效、源数据集按库内修订号更新，
    /// 防止“只调窗口不生效”。
    /// </summary>
    [Fact]
    public async Task SaveAsync_persists_variables_and_updates_dataset_when_only_expression_changes()
    {
        await SeedAsync();
        var input = BuildInput("sync_window_save", WindowVariables("addDays(today(),-7)", "today()"));

        await _environment.SyncService.SaveAsync("sync_window_save", input);
        var afterCreate = await _environment.DataAssets.GetDatasetAsync("sync_src_sync_window_save");
        Assert.NotNull(afterCreate);
        Assert.Equal(1, afterCreate!.Revision);
        var schema = PocDataAssetService.ReadSchema(afterCreate);
        Assert.Equal(2, schema.Variables.Count);
        Assert.Equal("addDays(today(),-7)", schema.Variables.Single(item => item.Code == "startDate").DefaultExpression);

        // 内容一致地重复保存：数据集复用不动，修订号不推高。
        await _environment.SyncService.SaveAsync("sync_window_save", input with { Revision = 1 });
        var afterReuse = await _environment.DataAssets.GetDatasetAsync("sync_src_sync_window_save");
        Assert.NotNull(afterReuse);
        Assert.Equal(1, afterReuse!.Revision);

        // 只改表达式：复用判定必须失效，数据集按库内修订号 1 更新到 2。
        var changed = (input with { Revision = 2 })
            with
        {
            Source = input.Source! with
            {
                Variables = WindowVariables("addDays(today(),-30)", "today()")
            }
        };
        await _environment.SyncService.SaveAsync("sync_window_save", changed);
        var afterUpdate = await _environment.DataAssets.GetDatasetAsync("sync_src_sync_window_save");
        Assert.NotNull(afterUpdate);
        Assert.Equal(2, afterUpdate!.Revision);
        var updatedSchema = PocDataAssetService.ReadSchema(afterUpdate);
        Assert.Equal(
            "addDays(today(),-30)",
            updatedSchema.Variables.Single(item => item.Code == "startDate").DefaultExpression);
    }

    /// <summary>
    /// 验证正式运行按当前时间现算滚动窗口：今天/60天前两行源数据只应同步今天一行，
    /// 且运行日志消息写入实际代入的取数参数供窗口审计。
    /// </summary>
    [Fact]
    public async Task ExecuteTaskNow_rolls_window_by_expression_and_logs_parameters()
    {
        await SeedAsync(targetDbType: "SQLite");
        await ParametrizeSeededSourceAsync(WindowVariables());

        var log = await _environment.SyncService.ExecuteTaskNowAsync("sync_seed_parameter");

        Assert.Equal("Completed", log.Status);
        Assert.Equal(1, log.RowsRead);
        Assert.Equal(1, log.RowsWritten);
        Assert.Contains("取数参数", log.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"startDate={DateTimeOffset.Now.AddDays(-1):yyyy-MM-dd}",
            log.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"endDate={DateTimeOffset.Now.AddDays(1):yyyy-MM-dd}",
            log.Message,
            StringComparison.Ordinal);
        var rows = _environment.QueryTargetRows("sync_tgt_demo");
        Assert.Single(rows);
        Assert.Equal("A-001", rows[0]["CARD_NO"]);
    }

    /// <summary>
    /// 验证试跑的取值优先级：不传参数时回落到表达式现算窗口（只出今天一行），
    /// 显式传参覆盖表达式（把窗口拉到 60 天前可出两行），供按历史窗口验证数据。
    /// </summary>
    [Fact]
    public async Task TryRun_expression_falls_back_and_explicit_parameters_override()
    {
        await SeedAsync(targetDbType: "SQLite");
        await ParametrizeSeededSourceAsync(WindowVariables());

        var fallback = await _environment.SyncService.TryRunAsync("sync_seed_parameter");
        Assert.Single(fallback.Samples);

        var overridden = await _environment.SyncService.TryRunAsync(
            "sync_seed_parameter",
            new PocDataSyncTryRunRequest(
                null,
                new Dictionary<string, string?>
                {
                    ["startDate"] = _oldDateIso,
                    ["endDate"] = _todayIso
                }));
        Assert.Equal(2, overridden.Samples.Count);
    }

    /// <summary>
    /// 验证源取数预览的参数语义：任务落库前，变量表达式按当前时间现算兜底（只出今天一行），
    /// 显式传参覆盖（出两行）——设计器在保存前即可按窗口核数。
    /// </summary>
    [Fact]
    public async Task PreviewSourceAsync_evaluates_expression_and_honors_overrides()
    {
        await SeedAsync();

        var fallback = await _environment.SyncService.PreviewSourceAsync(new PocSyncSourcePreviewRequest(
            "src_sqlite",
            WindowQueryText,
            Variables: WindowVariables()));
        Assert.Single(fallback.Rows);

        var overridden = await _environment.SyncService.PreviewSourceAsync(new PocSyncSourcePreviewRequest(
            "src_sqlite",
            WindowQueryText,
            Variables: WindowVariables(),
            Parameters: new Dictionary<string, string?> { ["startDate"] = _oldDateIso, ["endDate"] = _todayIso }));
        Assert.Equal(2, overridden.Rows.Count);
    }

    /// <summary>验证非法表达式在保存即拒绝（数据集校验试算），错误码沿用求值器便于定位。</summary>
    [Fact]
    public async Task SaveAsync_rejects_invalid_expression()
    {
        await SeedAsync();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(
            () => _environment.SyncService.SaveAsync(
                "sync_window_bad",
                BuildInput("sync_window_bad", WindowVariables("startoflastmonth()", "tomorrow()"))));

        Assert.Equal("DEFAULT_EXPRESSION_INVALID", exception.Code);
    }

    /// <summary>验证非表单来源的变量在同步保存入口被显式拒绝（外部接口绑定在同步链路无解析通道）。</summary>
    [Fact]
    public async Task SaveAsync_rejects_non_form_variable_source_type()
    {
        await SeedAsync();
        var variables = new List<PocDatasetVariableDefinition>
        {
            new(
                "startDate",
                "窗口开始",
                "date",
                false,
                SourceType: "External",
                SourceCode: "ext_source",
                SourceFieldCode: "field_start")
        };

        var exception = await Assert.ThrowsAsync<PocDataSyncException>(
            () => _environment.SyncService.SaveAsync(
                "sync_window_external",
                BuildInput("sync_window_external", variables)));

        Assert.Equal("SYNC_SOURCE_INVALID", exception.Code);
    }
}
