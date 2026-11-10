using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Furion.Schedule;
using Furion.TimeCrontab;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 列注释读取缝隙：按数据源实体与表名返回“列名 → 注释”字典（注释已去除首尾空白，空串不出现在字典里）。
/// 生产实现按方言走数据字典 SQL（Oracle user_col_comments、SqlServer extended_properties、
/// MySQL information_schema、PostgreSQL col_description）；单元测试注入假实现验证合并逻辑。
/// </summary>
/// <param name="source">已校验启用的数据源实体。</param>
/// <param name="tableName">表名（按目录原样大小写传入）。</param>
/// <param name="cancellationToken">用于取消异步操作的令牌。</param>
/// <returns>列名到注释的字典；读不到注释时返回空字典，不抛异常。</returns>
public delegate Task<IReadOnlyDictionary<string, string?>> ReadColumnCommentsDelegate(
    PocDataSource source,
    string tableName,
    CancellationToken cancellationToken);

/// <summary>
/// 数据同步任务应用服务：任务增删改查（含映射与调度校验）、映射预览、受限试跑、
/// 后台运行与运行日志查询。映射编译、取数与写入分别复用转换器、数据集执行器与批量写入器，
/// 本服务只做编排与契约转换。
/// </summary>
public sealed class PocDataSyncService : ITransient
{
    /// <summary>任务试跑的硬性行数上限，与报表包试跑口径保持一致。</summary>
    public const int TryRunRowLimit = 50;

    /// <summary>运行日志查询默认返回条数。</summary>
    public const int RunLogListLimit = 50;

    private static readonly Regex CodeRegex = new(
        "^[A-Za-z][A-Za-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyList<string> ConflictPolicies = ["TruncateFirst", "Append", "Upsert"];
    private static readonly IReadOnlyList<string> ScheduleTypes = ["Manual", "Daily", "Interval", "Cron"];

    /// <summary>Cron 表达式的存储列长上限，与 PocSyncTask.ScheduleCron 列定义一致（超长在保存前拒绝，避免落库时报错）。</summary>
    public const int ScheduleCronMaxLength = 64;

    /// <summary>自动维护源数据集时使用的默认查询超时（秒），与数据集管理页新建默认值一致。</summary>
    public const int SourceDatasetTimeoutSeconds = 30;

    /// <summary>自动维护源数据集时使用的默认最大行数，与数据集管理页新建默认值一致。</summary>
    public const int SourceDatasetMaxRows = 20_000;

    /// <summary>自动维护源数据集时写入字段契约的列数上限，与数据集字段校验上限（256）保持一致。</summary>
    public const int SourceDatasetFieldLimit = 256;

    /// <summary>源数据集字段编码的合法性约束，与数据集编码校验规则一致（字母开头、字母数字下划线）。</summary>
    private static readonly Regex SourceFieldCodeRegex = new(
        "^[A-Za-z][A-Za-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>共享的空注释字典：注释读取被跳过或降级时复用，避免重复分配。</summary>
    private static readonly IReadOnlyDictionary<string, string?> EmptyComments =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private readonly PocDataSyncStore _store;
    private readonly PocDataAssetStore _dataAssets;
    private readonly PocDataAssetService _dataAssetService;
    private readonly ISqlSugarClient _database;
    private readonly PocDataSyncExecutor _executor;
    private readonly ReadColumnCommentsDelegate _readColumnComments;
    private readonly CreateDatasetClient _metadataClientFactory;
    private readonly IPocDataSyncScheduleGateway? _scheduleGateway;

    /// <summary>初始化 PocDataSyncService 实例，保存编排所需的存储与执行器依赖。</summary>
    /// <param name="store">同步任务与运行日志存储。</param>
    /// <param name="dataAssets">全局数据资产存储，用于读取源数据集与数据源。</param>
    /// <param name="database">报表中心主库客户端，用于后台执行时派生独立连接。</param>
    /// <param name="executor">同步执行器；测试可注入绑定内存库连接的实现。</param>
    /// <param name="dataAssetService">数据资产应用服务，保存任务的源节时复用其数据集校验落库。</param>
    /// <param name="readColumnComments">列注释读取缝隙；测试可注入假实现，缺省走方言数据字典查询。</param>
    /// <param name="metadataClientFactory">元数据连接工厂；缺省走 PocDatabaseProvider，测试可注入绑定共享连接的实现。</param>
    /// <param name="scheduleGateway">Cron 调度网关，保存/删除后维护动态作业；缺省（单测）时跳过调度器维护。</param>
    public PocDataSyncService(
        PocDataSyncStore store,
        PocDataAssetStore dataAssets,
        ISqlSugarClient database,
        PocDataSyncExecutor? executor = null,
        PocDataAssetService? dataAssetService = null,
        ReadColumnCommentsDelegate? readColumnComments = null,
        CreateDatasetClient? metadataClientFactory = null,
        IPocDataSyncScheduleGateway? scheduleGateway = null)
    {
        _store = store;
        _dataAssets = dataAssets;
        _dataAssetService = dataAssetService ?? new PocDataAssetService(dataAssets);
        _database = database;
        _executor = executor ?? new PocDataSyncExecutor();
        _readColumnComments = readColumnComments ?? ReadColumnCommentsFromDatabaseAsync;
        _metadataClientFactory = metadataClientFactory ?? PocDatabaseProvider.CreateClient;
        _scheduleGateway = scheduleGateway;
    }

    /// <summary>列出全部同步任务并展开映射定义，供任务管理列表消费。</summary>
    /// <returns>任务文档列表。</returns>
    public async Task<IReadOnlyList<PocSyncTaskDocument>> ListAsync() =>
        (await _store.ListTasksAsync()).Select(ToTaskDocument).ToArray();

    /// <summary>按编码读取单个任务文档；不存在抛 SYNC_TASK_NOT_FOUND。</summary>
    /// <param name="code">任务编码。</param>
    /// <returns>任务文档。</returns>
    public async Task<PocSyncTaskDocument> GetAsync(string code)
    {
        var task = await RequireTaskAsync(code);
        return ToTaskDocument(task);
    }

    /// <summary>
    /// 保存同步任务：校验编码、名称、源定义、目标数据源、冲突策略、调度与映射完整性后落库。
    /// 源定义支持两条路径：新契约的 Source 节（服务端自动把源 SQL 落成 `sync_src_&lt;taskCode&gt;`
    /// 数据集）或旧契约的 sourceDatasetCode（直接引用已存在数据集），前者优先，包导入不受影响。
    /// 映射在保存时即完成编译验证，确保任何保存下来的任务都能被执行器直接编译启动。
    /// </summary>
    /// <param name="code">路由上的任务编码。</param>
    /// <param name="input">任务保存输入（含乐观并发期望修订号）。</param>
    /// <returns>保存后的任务文档。</returns>
    public async Task<PocSyncTaskDocument> SaveAsync(string code, PocSyncTaskInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!code.Equals(input.Code?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new PocDataSyncException("SYNC_TASK_INVALID", "路径编码与请求内容不一致。");
        }

        // 第一阶段：校验任务基础字段与调度定义，非法配置在保存前拒绝；Cron 表达式在此完成解析级校验。
        ValidateTaskHeader(input);
        var schedule = NormalizeSchedule(input.ScheduleType, input.ScheduleTime, input.IntervalMinutes, input.ScheduleCron);
        var target = await RequireEnabledDataSourceAsync(input.TargetDataSourceCode);
        _ = PocDatabaseProvider.NormalizeType(target.DbType);

        // 第二阶段：解析源。Source 节存在时按新交互自动维护源数据集；否则回退到旧契约的显式数据集编码。
        var sourceDatasetCode = input.Source is null
            ? (await RequireEnabledDatasetAsync(input.SourceDatasetCode)).Code
            : await UpsertSourceDatasetAsync(input.Code.Trim(), input.Source);

        // 第三阶段：编译验证全部字段映射，编译错误即保存失败；Upsert 策略在此一并裁决合并键唯一性与非空。
        ValidateMappings(input.Mappings);
        var conflictPolicy = NormalizeConflictPolicy(input.ConflictPolicy);
        _ = ResolveUpsertKeyField(conflictPolicy, input.Mappings);
        var definition = new PocSyncTaskDefinition(
            input.Mappings,
            new PocSyncScheduleDefinition(schedule.Type, schedule.Time, schedule.IntervalMinutes, schedule.Cron));

        await _store.SaveTaskAsync(new PocSyncTaskMutation(
            input.Code.Trim(),
            input.Name.Trim(),
            sourceDatasetCode,
            input.TargetDataSourceCode.Trim(),
            input.TargetTable.Trim(),
            SerializeDefinition(definition),
            conflictPolicy,
            schedule.Type,
            schedule.Time,
            schedule.IntervalMinutes,
            schedule.Cron,
            input.Enabled,
            input.Revision));

        // 第四阶段：调度生命周期钩子。DB 已落库成为事实源；启用且 Cron 注册动态作业，其余摘除；
        // 注册失败不回滚保存（网关内吞异常记日志），下次保存/导入/重启对账自愈。
        ApplyCronScheduleGateway(_scheduleGateway, input.Code.Trim(), input.Name.Trim(), schedule.Type, schedule.Cron, input.Enabled);
        return ToTaskDocument((await RequireTaskAsync(input.Code.Trim())));
    }

    /// <summary>删除同步任务；运行日志按设计保留作执行审计，不随任务级联删除。</summary>
    /// <param name="code">待删除任务编码。</param>
    /// <returns>删除确认文档。</returns>
    public async Task<PocDeletedDocument> DeleteAsync(string code)
    {
        var task = await RequireTaskAsync(code);
        var deleted = await _store.DeleteTaskAsync(task.Code);
        if (!deleted)
        {
            throw new PocDataSyncException(
                "SYNC_TASK_NOT_FOUND",
                $"同步任务 '{task.Code}' 在删除瞬间已不存在。");
        }

        // 删除后同步摘除 Cron 动态作业（非 Cron 任务摘除是幂等空操作），残留触发器不允许在删除后继续触发。
        ApplyCronScheduleGateway(_scheduleGateway, task.Code, task.Name, task.ScheduleType, task.ScheduleCron, enabled: false);
        return new PocDeletedDocument(task.Code);
    }

    /// <summary>
    /// 预览源数据集的真实返回列与样例行（不做映射转换），供映射画布拉线选择源字段。
    /// 复用数据集执行器的受限预览，行数上限与包试跑一致（50 行）。
    /// fields 的 description 取源数据集字段声明里的 displayName（保存任务时由服务端把
    /// 数据库列注释写入该位置）；displayName 与列名相同视为无独立含义，输出 null 避免重复展示，
    /// 数据集没有字段声明（历史任务或写 SQL 模式）时同样为 null。
    /// </summary>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="request">可选的数据集变量取值。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>动态列与样例行。</returns>
    public async Task<PocDatasetPreviewResult> PreviewMappingAsync(
        string taskCode,
        PocDatasetPreviewRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var task = await RequireTaskAsync(taskCode);
        var definition = DeserializeDefinition(task.DefinitionJson);
        var plan = await BuildSourcePlanAsync(task, definition.Mappings, request?.Parameters, cancellationToken);
        // 第一阶段：读取源数据集的字段声明，把“列名 → 中文含义”物化为查询表，供预览字段回填注释。
        var dataset = await RequireEnabledDatasetAsync(task.SourceDatasetCode);
        var declaredFields = PocDataAssetService.ReadSchema(dataset).Fields;
        var declaredDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in declaredFields)
        {
            declaredDisplayNames[field.Code] = field.DisplayName;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var preview = await PocSqlDatasetExecutor.ExecutePreviewAsync(
            plan,
            TryRunRowLimit,
            cancellationToken,
            _executor.ClientFactory);
        started.Stop();

        // 第二阶段：按列顺序把字典行投影为列表行，列类型从首个非空样例值推断；
        // displayName 优先沿用数据集声明（含中文注释），没有声明时退回列名本身，
        // description 只在中文含义与列名不同时输出，与列名重复就不重复展示。
        var fields = preview.Columns.Select(column =>
        {
            var displayName = declaredDisplayNames.TryGetValue(column, out var name)
                && !string.IsNullOrWhiteSpace(name) ? name.Trim() : column;
            var description = displayName.Equals(column, StringComparison.OrdinalIgnoreCase)
                ? null
                : displayName;
            return new PocDatasetFieldDefinition(
                column,
                displayName,
                InferColumnDataType(preview.Rows, column),
                description);
        }).ToArray();
        var rows = preview.Rows
            .Select(row => preview.Columns
                .Select(column => row.TryGetValue(column, out var value) ? value : null)
                .ToArray())
            .ToArray();
        return new PocDatasetPreviewResult(
            fields,
            rows,
            preview.RowCountCapped,
            TryRunRowLimit,
            started.ElapsedMilliseconds);
    }

    /// <summary>
    /// 列出数据源连接库内的基础表名（仅 TABLE 类型，不含视图），供设计器源/目标表下拉。
    /// 全程只读：走 PocDatabaseProvider 收敛连接创建，SqlSugar DbMaintenance 是跨方言的元数据 API，
    /// Oracle 返回目录原样大小写（通常为大写）。任何数据库故障包装为 SYNC_METADATA_FAILED，
    /// 错误消息经安全脱敏，绝不回显连接串。
    /// </summary>
    /// <param name="dataSourceCode">数据源编码。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>表清单文档。</returns>
    public async Task<PocSyncTableMetadataDocument> ListSourceTablesAsync(
        string? dataSourceCode,
        CancellationToken cancellationToken = default)
    {
        var source = await RequireEnabledDataSourceAsync(dataSourceCode);
        try
        {
            using var client = CreateMetadataClient(source, cancellationToken);
            // GetTableInfoList 只返回基础表（视图走 GetViewInfoList），天然满足“仅 TABLE 类型”。
            var tables = client.DbMaintenance.GetTableInfoList(isCache: false)
                .Select(item => item.Name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new PocSyncTableMetadataDocument(source.Code, tables);
        }
        catch (DbException exception)
        {
            throw WrapMetadataException(exception);
        }
        catch (SqlSugarException exception)
        {
            throw WrapMetadataException(exception);
        }
    }

    /// <summary>
    /// 读取目标表的真实列清单（列名、数据库类型、可空、长度、注释），供映射表格的目标字段下拉、
    /// 类型推断与列注释展示。注释优先取方言数据字典查询结果，取不到时回退 SqlSugar 元数据的
    /// ColumnDescription，仍然为空则输出 null（前端不造占位文案）。
    /// 表不存在或列清单为空抛 SYNC_METADATA_TABLE_NOT_FOUND；连接故障包装为 SYNC_METADATA_FAILED。
    /// </summary>
    /// <param name="dataSourceCode">数据源编码。</param>
    /// <param name="table">表名（按目录原样大小写传入）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>列清单文档。</returns>
    public async Task<PocSyncTableColumnsDocument> GetTableColumnsAsync(
        string? dataSourceCode,
        string? table,
        CancellationToken cancellationToken = default)
    {
        var source = await RequireEnabledDataSourceAsync(dataSourceCode);
        var tableName = table?.Trim();
        if (string.IsNullOrWhiteSpace(tableName) || tableName.Length > 128)
        {
            throw new PocDataSyncException("SYNC_METADATA_TABLE_NOT_FOUND", "请提供有效的表名（长度 1 到 128）。");
        }

        try
        {
            using var client = CreateMetadataClient(source, cancellationToken);

            // 第一阶段：读取目标表列结构，列清单为空即视为表不存在或无权读取。
            var columnInfos = client.DbMaintenance.GetColumnInfosByTableName(tableName, isCache: false);

            // 第二阶段：合并列注释。注释读取属于展示增强，失败按“诚实留空”降级，不影响列清单。
            var comments = await TryLoadColumnCommentsAsync(source, tableName, cancellationToken);
            var columns = columnInfos
                .Select(column => new PocSyncColumnMetadataDocument(
                    column.DbColumnName,
                    column.DataType ?? string.Empty,
                    column.IsNullable,
                    column.Length > 0 ? column.Length : null,
                    ResolveColumnDescription(
                        column.DbColumnName,
                        comments,
                        column.ColumnDescription),
                    column.IsPrimarykey))
                .ToArray();
            if (columns.Length == 0)
            {
                throw new PocDataSyncException(
                    "SYNC_METADATA_TABLE_NOT_FOUND",
                    $"数据源 '{source.Code}' 中不存在表 '{tableName}' 或当前账号无权读取其结构。");
            }
            return new PocSyncTableColumnsDocument(source.Code, tableName, columns);
        }
        catch (DbException exception)
        {
            throw WrapMetadataException(exception);
        }
        catch (SqlSugarException exception)
        {
            throw WrapMetadataException(exception);
        }
    }

    /// <summary>
    /// 源取数预览（新交互专用）：任务尚未落库时对“数据源 + SQL”做真实受限查询，
    /// 返回动态列与样例行（≤50 行）。SQL 必须先过 PocReadOnlySqlValidator，
    /// 执行管线与映射预览完全同构（同样的参数绑定与字段推断），不写目标库。
    /// 请求带 SourceTable（选表模式）时按列名匹配该表的数据库注释填充 fields 的 description；
    /// 写 SQL 模式或注释读取失败时不硬造，description 保持 null。
    /// </summary>
    /// <param name="request">源取数预览请求（数据源编码 + SQL 文本 + 可选源表名）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>动态列与样例行。</returns>
    public async Task<PocDatasetPreviewResult> PreviewSourceAsync(
        PocSyncSourcePreviewRequest? request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await RequireEnabledDataSourceAsync(request.DataSourceCode);
        var queryText = request.QueryText?.Trim() ?? string.Empty;
        ValidateReadOnlySourceSql(
            queryText,
            (request.Variables ?? []).Select(item => item.Code));
        // 注释匹配只在选表模式可用：源表名来自前端已知信息，SQL 输出列与表列同名（忽略大小写）时命中。
        var sourceTable = request.SourceTable?.Trim();
        var comments = string.IsNullOrWhiteSpace(sourceTable)
            ? EmptyComments
            : await TryLoadColumnCommentsAsync(source, sourceTable, cancellationToken);
        // 变量契约与显式取值随预览请求透传：设计器在任务落库前即可按滚动窗口或指定日期预览源数据。
        var plan = BuildAdHocSourcePlan(source, queryText, request.Variables, request.Parameters);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var preview = await PocSqlDatasetExecutor.ExecutePreviewAsync(
            plan,
            TryRunRowLimit,
            cancellationToken,
            _executor.ClientFactory);
        started.Stop();

        // 按列顺序把字典行投影为列表行，列类型从首个非空样例值推断，与任务映射预览口径一致。
        var fields = preview.Columns.Select(column => new PocDatasetFieldDefinition(
            column,
            column,
            InferColumnDataType(preview.Rows, column),
            MatchColumnDescription(column, comments))).ToArray();
        var rows = preview.Rows
            .Select(row => preview.Columns
                .Select(column => row.TryGetValue(column, out var value) ? value : null)
                .ToArray())
            .ToArray();
        return new PocDatasetPreviewResult(
            fields,
            rows,
            preview.RowCountCapped,
            TryRunRowLimit,
            started.ElapsedMilliseconds);
    }

    /// <summary>
    /// 把保存输入的源节落成/更新为任务专属源数据集（编码 `sync_src_&lt;taskCode&gt;`）：
    /// SQL 先过只读校验；数据集已存在且内容一致（SQL 原文 + 变量契约）则复用不动；
    /// 内容不一致且被报表引用时拒绝（与同步包导入同一保护口径），否则以库内修订号执行更新。
    /// 返回数据集编码供任务落库。选表模式（SourceTable 提供时）会读取源表列清单与数据库注释，
    /// 把中文含义写入数据集字段的 displayName——有注释才覆盖、无注释保留原声明，元数据读取失败按
    /// “字段契约留空”降级，绝不阻断任务保存；写 SQL 模式保持字段契约为空。
    /// 变量契约（取数参数）随 FieldSchemaJson 一起落库并随同步包自动携带。
    /// </summary>
    /// <param name="taskCode">任务编码（已通过格式校验）。</param>
    /// <param name="source">保存输入的源取数节。</param>
    /// <returns>源数据集编码。</returns>
    private async Task<string> UpsertSourceDatasetAsync(string taskCode, PocSyncTaskSourceInput source)
    {
        // 第一阶段：校验源节内容与数据源可用性，SQL 过只读校验后再触碰数据集。
        var dataSourceCode = source.DataSourceCode?.Trim();
        var queryText = source.QueryText?.Trim();
        if (string.IsNullOrWhiteSpace(dataSourceCode) || string.IsNullOrWhiteSpace(queryText))
        {
            throw new PocDataSyncException(
                "SYNC_SOURCE_INVALID",
                "源取数定义无效：必须选择数据源并提供取数 SQL（选表模式由前端按方言生成）。");
        }
        var dataSource = await RequireEnabledDataSourceAsync(dataSourceCode);
        var variables = NormalizeSyncVariables(source.Variables);
        // 变量契约先行规范化：只读校验按契约放行 SQL 里的 @引用，引用与声明不一致在此拒绝。
        ValidateReadOnlySourceSql(queryText, variables.Select(item => item.Code));

        // 第二阶段：内容一致则复用，避免每次保存都推高数据集修订号；不一致时先做报表引用保护。
        // 变量契约参与一致性判断：只改取数参数不动 SQL 时也必须落库，否则滚动窗口改动不生效。
        var datasetCode = $"sync_src_{taskCode}";
        var existing = await _dataAssets.GetDatasetAsync(datasetCode);
        if (existing is not null &&
            string.Equals(existing.DataSourceCode.Trim(), dataSourceCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((existing.QueryText ?? string.Empty).Trim(), queryText, StringComparison.Ordinal) &&
            VariablesMatch(PocDataAssetService.ReadSchema(existing).Variables, variables))
        {
            return datasetCode;
        }
        if (existing is not null)
        {
            var references = await _dataAssets.ListDatasetReferencesAsync(datasetCode);
            if (references.Count > 0)
            {
                throw new PocDataSyncException(
                    "SYNC_DATASET_IN_USE",
                    $"源数据集 '{datasetCode}' 已存在且内容不一致，并被 {references.Count} 个报表引用：" +
                    $"{PocDeleteGuard.FormatReferenceSummary(references.Select(item => item.Name).ToArray())}。" +
                    "请先调整这些报表的数据集绑定，或改用其他任务编码。");
            }
        }

        // 第三阶段：构造字段契约。选表模式按源表列清单 + 注释生成（失败降级为空契约），
        // 写 SQL 模式没有可靠列来源，保持空契约；执行器按映射源字段取数，不依赖声明字段。
        var sourceTable = source.SourceTable?.Trim();
        var fields = string.IsNullOrWhiteSpace(sourceTable)
            ? []
            : await BuildSourceDatasetFieldsAsync(dataSource, sourceTable, existing);

        // 第四阶段：落库。修订号沿用库内值，启用状态固定为 true（源数据集随任务生灭）。
        // 变量契约在这里一并校验与保存（数据集校验含表达式试算），表达式非法直接拒绝保存。
        var name = source.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"同步源数据集 {taskCode}";
        }
        await _dataAssetService.SaveDatasetAsync(datasetCode, new PocDatasetDocument(
            datasetCode,
            name,
            dataSourceCode,
            queryText,
            variables,
            fields,
            SourceDatasetTimeoutSeconds,
            SourceDatasetMaxRows,
            Enabled: true,
            existing?.Revision ?? 0));
        return datasetCode;
    }

    /// <summary>
    /// 规范化保存输入的取数参数契约：同步源数据集的变量只允许 Form 来源（客户端不可见的
    /// External/Fixed 绑定在同步链路没有解析通道，收到即拒绝而非静默改写），编码/显示名/
    /// 默认值剔除首尾空白，显示名缺省回退到编码，并按编码排序得到稳定形态，供一致性比对与落库使用。
    /// </summary>
    /// <param name="variables">保存输入的变量契约，可为 null。</param>
    /// <returns>规范化后的变量列表；无变量返回空列表。</returns>
    private static IReadOnlyList<PocDatasetVariableDefinition> NormalizeSyncVariables(
        IReadOnlyList<PocDatasetVariableDefinition>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return [];
        }

        if (variables.Any(variable => !variable.SourceType.Equals("Form", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PocDataSyncException(
                "SYNC_SOURCE_INVALID",
                "源取数参数无效：同步任务暂不支持外部接口（External）或固定来源变量，请使用表单来源。");
        }

        // 第一阶段：逐个剔除首尾空白并回退空白显示名，按编码排序得到与保存输入无关的稳定形态。
        var normalized = variables
            .Select(variable => variable with
            {
                Code = (variable.Code ?? string.Empty).Trim(),
                DisplayName = string.IsNullOrWhiteSpace(variable.DisplayName)
                    ? (variable.Code ?? string.Empty).Trim()
                    : variable.DisplayName.Trim(),
                SourceCode = null,
                SourceFieldCode = null,
                DefaultValue = string.IsNullOrWhiteSpace(variable.DefaultValue)
                    ? null
                    : variable.DefaultValue.Trim(),
                DefaultExpression = string.IsNullOrWhiteSpace(variable.DefaultExpression)
                    ? null
                    : variable.DefaultExpression.Trim()
            })
            .OrderBy(variable => variable.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // 第2阶段：编码完整性检查——空编码与重复编码都会破坏 SQL 绑定的确定性，保存前拒绝。
        if (normalized.Any(variable => variable.Code.Length == 0) ||
            normalized.Select(variable => variable.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != normalized.Length)
        {
            throw new PocDataSyncException(
                "SYNC_SOURCE_INVALID",
                "源取数参数无效：变量编码必须非空且互不重复。");
        }

        return normalized;
    }

    /// <summary>
    /// 判断两份变量契约是否等价：两侧都按同步规范化形态（排序 + Form 来源）比较，
    /// 覆盖编码、显示名、类型、必填、默认值与默认表达式全部字段——任何一项变化都视为
    /// 内容变化，确保只改取数窗口的保存会真正落到源数据集。
    /// </summary>
    /// <param name="left">库内（经 ReadSchema 读出）的变量列表。</param>
    /// <param name="right">保存输入规范化后的变量列表。</param>
    /// <returns>完全一致返回 true。</returns>
    private static bool VariablesMatch(
        IReadOnlyList<PocDatasetVariableDefinition> left,
        IReadOnlyList<PocDatasetVariableDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        return left.Zip(right, (a, b) => (a, b)).All(pair =>
            string.Equals(pair.a.Code, pair.b.Code, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.a.DisplayName, pair.b.DisplayName, StringComparison.Ordinal) &&
            string.Equals(pair.a.DataType, pair.b.DataType, StringComparison.OrdinalIgnoreCase) &&
            pair.a.Required == pair.b.Required &&
            string.Equals(pair.a.DefaultValue ?? string.Empty, pair.b.DefaultValue ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(pair.a.DefaultExpression ?? string.Empty, pair.b.DefaultExpression ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 按源表列清单与数据库注释构造源数据集字段声明：字段编码取合法列名（不满足编码规则的列
    /// 直接丢弃，避免保存被数据集校验拦截），displayName 优先取数据库注释（截断到 128 字符），
    /// 无注释时保留数据集里已有的声明、再退回列名；dataType 从数据库类型收敛到字段类型白名单。
    /// 任何元数据读取失败（连接故障、方言不支持、权限不足）都降级为空契约，不阻断任务保存。
    /// </summary>
    /// <param name="dataSource">已校验启用的源数据源实体。</param>
    /// <param name="sourceTable">源表名（按目录原样大小写传入）。</param>
    /// <param name="existing">已存在的源数据集实体，可为 null。</param>
    /// <returns>可直接落库的字段契约；读取失败返回空列表。</returns>
    private async Task<IReadOnlyList<PocDatasetFieldDefinition>> BuildSourceDatasetFieldsAsync(
        PocDataSource dataSource,
        string sourceTable,
        PocReportDataset? existing)
    {
        try
        {
            using var client = CreateMetadataClient(dataSource, CancellationToken.None);
            var columnInfos = client.DbMaintenance.GetColumnInfosByTableName(sourceTable, isCache: false);
            if (columnInfos.Count == 0)
            {
                return [];
            }
            var comments = await TryLoadColumnCommentsAsync(dataSource, sourceTable, CancellationToken.None);
            // 保留既有字段声明：无注释时沿用旧 displayName，避免把上次写入的中文含义冲掉。
            var existingFields = existing is null
                ? []
                : PocDataAssetService.ReadSchema(existing).Fields;
            var existingByCode = existingFields
                .GroupBy(field => field.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var fields = new List<PocDatasetFieldDefinition>(columnInfos.Count);
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columnInfos)
            {
                var code = column.DbColumnName?.Trim() ?? string.Empty;
                if (code.Length is 0 or > 64 || !SourceFieldCodeRegex.IsMatch(code) || !seenCodes.Add(code))
                {
                    continue;
                }
                var comment = MatchColumnDescription(code, comments);
                var existingField = existingByCode.GetValueOrDefault(code);
                var displayName = comment
                    ?? existingField?.DisplayName?.Trim()
                    ?? code;
                if (displayName.Length > 128)
                {
                    displayName = displayName[..128];
                }
                fields.Add(new PocDatasetFieldDefinition(
                    code,
                    displayName,
                    NormalizeSourceFieldDataType(column.DataType),
                    null));
                if (fields.Count >= SourceDatasetFieldLimit)
                {
                    break;
                }
            }
            return fields;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 元数据读取失败按“诚实留空”降级：任务保存仍按无字段契约继续（执行链路不依赖字段声明）。
            _ = exception;
            return [];
        }
    }

    /// <summary>把数据库列类型文本收敛为数据集字段类型白名单（string/number/date/datetime/bool）。</summary>
    /// <param name="dbType">元数据返回的数据库类型文本。</param>
    /// <returns>字段类型白名单内的类型代码。</returns>
    private static string NormalizeSourceFieldDataType(string? dbType)
    {
        var text = (dbType ?? string.Empty).ToLowerInvariant();
        if (text.Contains("datetime") || text.Contains("timestamp"))
        {
            return "datetime";
        }
        if (Regex.IsMatch(text, @"(\bdate\b|\btime\b|year)"))
        {
            return "date";
        }
        if (text.Contains("bool") || text.Contains("bit"))
        {
            return "bool";
        }
        if (Regex.IsMatch(text, @"(int|byte|short|long|serial|decimal|numeric|float|double|real|money|number)"))
        {
            return "number";
        }
        return "string";
    }

    /// <summary>校验源取数 SQL 为单条只读 SELECT/WITH；失败统一收敛为 SYNC_SOURCE_SQL_INVALID。</summary>
    /// <param name="queryText">待校验的 SQL 文本。</param>
    /// <param name="parameterCodes">SQL 允许引用的变量编码（取数参数契约）；缺省为空表示不允许任何参数。</param>
    private static void ValidateReadOnlySourceSql(
        string queryText,
        IEnumerable<string>? parameterCodes = null)
    {
        try
        {
            _ = PocReadOnlySqlValidator.Validate(queryText, parameterCodes ?? [], PocPreviewMode.Full);
        }
        catch (PocDatasetConfigurationException exception)
        {
            throw new PocDataSyncException(
                "SYNC_SOURCE_SQL_INVALID",
                $"源取数 SQL 未通过只读校验：{exception.Message}");
        }
    }

    /// <summary>为元数据读取创建独立连接客户端；一律走注入的连接工厂（缺省 PocDatabaseProvider）收敛连接串与超时。</summary>
    /// <param name="source">数据源实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可释放的 SqlSugar 客户端。</returns>
    private SqlSugarClient CreateMetadataClient(PocDataSource source, CancellationToken cancellationToken) =>
        _metadataClientFactory(
            PocDatabaseProvider.NormalizeType(source.DbType),
            source.ConnectionString,
            source.DefaultTimeoutSeconds,
            cancellationToken);

    /// <summary>
    /// 用临时数据源实体拼装源预览执行计划（无字段契约），管线与映射预览同构。
    /// Variables 是设计器正在编辑的取数参数契约：默认表达式按当前时间现算兜底，
    /// Overrides 是调用方显式取值（优先于默认值）——SQL 引用的变量两者皆无时，
    /// 参数解析器的必填校验会拒绝预览，与保存后的执行语义一致。
    /// </summary>
    /// <param name="source">已校验启用的数据源实体。</param>
    /// <param name="queryText">已通过只读校验的源 SQL。</param>
    /// <param name="variables">预览请求携带的变量契约，可为 null。</param>
    /// <param name="overrides">调用方显式给定的变量取值，可为 null。</param>
    /// <returns>执行计划。</returns>
    private static PocRuntimeExecutionPlan BuildAdHocSourcePlan(
        PocDataSource source,
        string queryText,
        IReadOnlyList<PocDatasetVariableDefinition>? variables = null,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        // 第一阶段：把变量契约物化为参数定义（表达式现算兜底）并解析取值，语义与正式运行一致。
        var parameterDefinitions = (variables ?? []).Select(variable => new PocParameterDefinition(
            variable.Code,
            variable.DisplayName,
            "text",
            variable.SourceType,
            PocDataAssetService.ResolveVariableFallback(variable),
            true,
            variable.DataType,
            Required: variable.Required,
            SourceCode: variable.SourceCode,
            SourceFieldCode: variable.SourceFieldCode)).ToArray();
        var resolved = PocParameterResolver.Resolve(
            parameterDefinitions,
            overrides ?? new Dictionary<string, string?>());
        // 第二阶段：组装预览骨架；预览只关心列与样例行，字段契约留空。
        var definition = new PocReportDefinition(
            "sync-source-preview",
            "同步源取数预览",
            "sync",
            new PocPreviewSettings(PocPreviewMode.Full, TryRunRowLimit, false),
            new PocExportSettings(
                PocDataExportMode.Large,
                NormalMaxRows: source.DefaultMaxRows,
                NormalMaxCells: 0,
                NormalTimeoutSeconds: source.DefaultTimeoutSeconds,
                LargeExportEnabled: true,
                RowsPerSheet: 1_000_000,
                FileRetentionHours: 24),
            parameterDefinitions,
            []);
        // DbType 原样透传（与 BuildSourcePlanAsync 同口径），方言合法性由连接工厂在执行时收敛。
        var configuration = new PocRuntimeReportConfiguration(
            definition,
            source.Code,
            source.DbType,
            source.ConnectionString,
            queryText,
            source.DefaultTimeoutSeconds,
            source.DefaultMaxRows,
            UsesFallbackData: false);
        return new PocRuntimeExecutionPlan(
            configuration,
            new PocExecuteRequest(1, overrides ?? new Dictionary<string, string?>()),
            resolved);
    }

    /// <summary>把元数据读取的数据库故障包装为 SYNC_METADATA_FAILED，错误文本经安全脱敏。</summary>
    /// <param name="exception">底层异常。</param>
    /// <returns>带稳定错误码的领域异常。</returns>
    private static PocDataSyncException WrapMetadataException(Exception exception) =>
        new(
            "SYNC_METADATA_FAILED",
            $"读取数据源结构失败：{PocDatabaseProvider.GetSafeConnectionError(exception)}",
            exception);

    /// <summary>
    /// 安全调用列注释缝隙：注释读取属于展示增强能力，任何数据库故障都降级为空字典
    /// （“拿不到的诚实留空”），绝不影响列清单与任务保存等主链路；取消令牌触发的取消除外。
    /// </summary>
    /// <param name="source">数据源实体。</param>
    /// <param name="tableName">表名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>列名 → 注释字典；读取失败返回空字典。</returns>
    private async Task<IReadOnlyDictionary<string, string?>> TryLoadColumnCommentsAsync(
        PocDataSource source,
        string tableName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _readColumnComments(source, tableName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return EmptyComments;
        }
    }

    /// <summary>
    /// 生产实现的列注释读取：按方言执行数据字典查询（全程参数化绑定，绝不拼接 SQL 文本）。
    /// Oracle 取 user_col_comments、SqlServer 取 sys.columns + extended_properties（MS_Description）、
    /// MySQL 取 information_schema.COLUMNS.COLUMN_COMMENT、PostgreSQL 取 col_description；
    /// 表按目录原样大小写精确匹配。返回的键与值都已 Trim，空注释不进入字典。
    /// </summary>
    /// <param name="source">数据源实体。</param>
    /// <param name="tableName">表名（按目录原样大小写传入）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>列名 → 注释字典。</returns>
    private async Task<IReadOnlyDictionary<string, string?>> ReadColumnCommentsFromDatabaseAsync(
        PocDataSource source,
        string tableName,
        CancellationToken cancellationToken)
    {
        var dbType = PocDatabaseProvider.NormalizeType(source.DbType);
        var (sql, _) = BuildColumnCommentSql(dbType)
            ?? throw new PocDatasetConfigurationException(
                "DATA_SOURCE_TYPE_UNSUPPORTED",
                $"数据库类型 '{dbType}' 不支持读取列注释。");
        using var client = CreateMetadataClient(source, cancellationToken);
        var parameter = PocDatabaseProvider.CreateParameter("rcTableName", "string", tableName);
        var dataReader = await client.Ado.GetDataReaderAsync(sql, new[] { parameter });
        await using var reader = dataReader as DbDataReader
            ?? throw new PocDatasetExecutionException(
                "DATASET_QUERY_FAILED",
                "数据库驱动未返回可读取的注释结果。");
        var comments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString();
            var comment = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString();
            var key = name?.Trim();
            var value = comment?.Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                comments[key!] = value;
            }
        }
        return comments;
    }

    /// <summary>
    /// 按方言生成“列名 + 注释”数据字典查询 SQL（列名固定第 0 列、注释固定第 1 列）。
    /// 返回元组第二位保留给未来的 schema 过滤扩展；不支持的方言返回 null。
    /// </summary>
    /// <param name="dbType">已归一化的数据库类型。</param>
    /// <returns>SQL 文本与占位扩展位；不支持时为 null。</returns>
    private static (string Sql, object? Extension)? BuildColumnCommentSql(string dbType) => dbType switch
    {
        // Oracle：当前用户 schema 的表注释；表名与目录返回的原样大小写精确匹配。
        PocDatabaseProvider.Oracle => (
            "SELECT column_name, comments FROM user_col_comments WHERE table_name = @rcTableName",
            (object?)null),
        // SqlServer：列级扩展属性 MS_Description；sql_variant 显式 CAST，避免驱动映射歧义。
        PocDatabaseProvider.SqlServer => (
            "SELECT c.name AS column_name, CAST(ep.value AS nvarchar(4000)) AS comment_text " +
            "FROM sys.columns c " +
            "JOIN sys.tables t ON t.object_id = c.object_id " +
            "LEFT JOIN sys.extended_properties ep ON ep.major_id = t.object_id " +
            "AND ep.minor_id = c.column_id AND ep.class = 1 AND ep.name = 'MS_Description' " +
            "WHERE t.name = @rcTableName",
            (object?)null),
        // MySQL：连接默认 schema 内的表注释；information_schema 在部分版本大小写敏感，按原样匹配。
        PocDatabaseProvider.MySql => (
            "SELECT COLUMN_NAME AS column_name, COLUMN_COMMENT AS comment_text " +
            "FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @rcTableName",
            (object?)null),
        // PostgreSQL：col_description 取 pg_description 里 objsubid 对应的列注释；仅基础表与可追加列。
        PocDatabaseProvider.PostgreSql => (
            "SELECT a.attname AS column_name, col_description(a.attrelid, a.attnum) AS comment_text " +
            "FROM pg_attribute a " +
            "JOIN pg_class c ON c.oid = a.attrelid " +
            "WHERE c.relname = @rcTableName AND c.relkind IN ('r', 'p') " +
            "AND a.attnum > 0 AND NOT a.attisdropped",
            (object?)null),
        _ => null
    };

    /// <summary>
    /// 按列名（忽略大小写）在注释字典中查找注释；列名未命中或注释为空白返回 null。
    /// 供预览字段与源数据集字段契约等“有就填、没有就空”的注释透传场景复用。
    /// </summary>
    /// <param name="columnName">列名。</param>
    /// <param name="comments">列名 → 注释字典。</param>
    /// <returns>命中且非空的注释文本，否则 null。</returns>
    internal static string? MatchColumnDescription(
        string columnName,
        IReadOnlyDictionary<string, string?> comments)
    {
        var key = columnName?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            return null;
        }
        if (comments.TryGetValue(key, out var comment) && !string.IsNullOrWhiteSpace(comment))
        {
            return comment.Trim();
        }
        return null;
    }

    /// <summary>
    /// 解析列注释：优先取注释字典命中值（方言数据字典），取不到时回退 SqlSugar 元数据的
    /// ColumnDescription；两者都为空白返回 null，前端据此不渲染占位文案。
    /// </summary>
    /// <param name="columnName">列名。</param>
    /// <param name="comments">列名 → 注释字典。</param>
    /// <param name="fallbackDescription">SqlSugar 元数据自带的列描述，可为 null。</param>
    /// <returns>首个非空白注释文本，否则 null。</returns>
    internal static string? ResolveColumnDescription(
        string columnName,
        IReadOnlyDictionary<string, string?> comments,
        string? fallbackDescription)
    {
        var matched = MatchColumnDescription(columnName, comments);
        if (matched is not null)
        {
            return matched;
        }
        var fallback = fallbackDescription?.Trim();
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <summary>
    /// 端到端试跑：真实读取源数据（最多 50 行）并按映射转换，验证目标列结论，但绝不写目标库。
    /// 返回转换前后样例、失败明细与兼容性错误，供导入/上线前的最后一道人工确认。
    /// </summary>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="request">试跑请求（行数上限与变量取值）。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>试跑结果。</returns>
    public async Task<PocDataSyncTryRunResult> TryRunAsync(
        string taskCode,
        PocDataSyncTryRunRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var task = await RequireTaskAsync(taskCode);
        var definition = DeserializeDefinition(task.DefinitionJson);
        var fields = ValidateMappings(definition.Mappings);
        var rowLimit = Math.Clamp(request?.RowLimit ?? TryRunRowLimit, 1, TryRunRowLimit);
        var plan = await BuildSourcePlanAsync(task, definition.Mappings, request?.Parameters, cancellationToken);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var core = await _executor.PreviewAsync(plan, fields, rowLimit, cancellationToken);
        started.Stop();
        return new PocDataSyncTryRunResult(
            core.FailedRows == 0 && core.Errors.Count == 0,
            core.TargetColumns,
            core.Samples,
            started.ElapsedMilliseconds,
            core.RowCountCapped,
            core.FailedRows,
            core.Errors);
    }

    /// <summary>
    /// 启动后台同步：先落 Running 日志占位防重入，再派生独立连接在后台线程执行，
    /// 立即返回运行编号供任务页轮询。请求线程内的依赖在返回前全部物化为纯数据。
    /// </summary>
    /// <param name="taskCode">任务编码。</param>
    /// <returns>Running 状态的运行日志文档。</returns>
    public async Task<PocSyncRunLogDocument> RunAsync(string taskCode)
    {
        var (task, input, log, parameterSummary) = await StartRunAsync(taskCode);
        var backgroundStore = new PocDataSyncStore(CopyDatabaseForBackground());
        var runNo = log.RunNo;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var stats = await _executor.ExecuteAsync(input);
                    await CompleteRunAsync(backgroundStore, runNo, task.Code, stats, message: null, parameterSummary);
                }
                catch (Exception exception)
                {
                    await FailRunAsync(backgroundStore, runNo, task.Code, exception);
                }
            });
        return ToRunLogDocument(log);
    }

    /// <summary>
    /// 同步执行一次任务（创建 Running 日志 → 执行 → 回写终态与最近运行时间）。
    /// 调度作业与集成测试都走本入口：作业需要顺序执行且直接感知失败，测试需要确定性完成。
    /// </summary>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>终态运行日志文档。</returns>
    public async Task<PocSyncRunLogDocument> ExecuteTaskNowAsync(
        string taskCode,
        CancellationToken cancellationToken = default)
    {
        var (task, input, log, parameterSummary) = await StartRunAsync(taskCode);
        try
        {
            var stats = await _executor.ExecuteAsync(input, cancellationToken);
            var completed = await CompleteRunAsync(_store, log.RunNo, task.Code, stats, message: null, parameterSummary);
            return ToRunLogDocument(completed);
        }
        catch (Exception exception)
        {
            var failed = await FailRunAsync(_store, log.RunNo, task.Code, exception);
            throw new PocDataSyncException(
                "SYNC_RUN_FAILED",
                $"同步任务 '{task.Code}' 执行失败：{exception.Message}")
            {
                Data = { ["RunNo"] = log.RunNo, ["RunLog"] = failed }
            };
        }
    }

    /// <summary>按任务编码列出最近的运行日志（倒序），供任务页轮询。</summary>
    /// <param name="taskCode">任务编码。</param>
    /// <returns>运行日志文档列表。</returns>
    public async Task<IReadOnlyList<PocSyncRunLogDocument>> ListRunLogsAsync(string taskCode)
    {
        var task = await RequireTaskAsync(taskCode);
        var logs = await _store.ListRunLogsAsync(task.Code, RunLogListLimit);
        return logs.Select(ToRunLogDocument).ToArray();
    }

    /// <summary>列出当前已到期的启用任务，供调度作业逐个同步执行。</summary>
    /// <param name="now">判定时点；缺省取服务器当前时间（本地时钟，与 HH:mm 语义一致）。</param>
    /// <returns>到期任务实体列表。</returns>
    public async Task<IReadOnlyList<PocSyncTask>> ListDueTasksAsync(DateTimeOffset? now = null)
    {
        var tasks = await _store.ListTasksAsync();
        var at = now ?? DateTimeOffset.Now;
        return tasks.Where(task => IsTaskDue(task, at)).ToArray();
    }

    /// <summary>
    /// 判定任务在给定时点是否到期：Manual 永不自动执行；Daily 在当日已过 HH:mm 且今天尚未跑过时到期；
    /// Interval 在距上次运行（或从未运行）超过间隔分钟数时到期。
    /// </summary>
    /// <param name="task">待判定任务。</param>
    /// <param name="at">判定时点。</param>
    /// <returns>到期返回 true。</returns>
    public static bool IsTaskDue(PocSyncTask task, DateTimeOffset at)
    {
        if (!task.IsEnabled)
        {
            return false;
        }
        switch ((task.ScheduleType ?? string.Empty).Trim())
        {
            case "Daily":
                var local = at.LocalDateTime;
                if (!TimeSpan.TryParseExact(
                        task.ScheduleTime,
                        @"hh\:mm",
                        CultureInfo.InvariantCulture,
                        out var scheduled))
                {
                    // 时间配置损坏的任务不进入自动执行，避免调度器反复撞上无效定义。
                    return false;
                }
                if (local.TimeOfDay < scheduled)
                {
                    return false;
                }
                return task.LastRunAt is null || task.LastRunAt.Value.LocalDateTime.Date < local.Date;
            case "Interval":
                var interval = Math.Max(task.IntervalMinutes, 1);
                return task.LastRunAt is null ||
                    at - task.LastRunAt.Value >= TimeSpan.FromMinutes(interval);
            case "Cron":
                // Cron 类型由 Furion 动态触发器直接驱动，扫描作业永不触发它，避免与触发器双重执行。
                return false;
            default:
                // Manual 与未知调度类型都只允许人工触发。
                return false;
        }
    }

    /// <summary>校验映射完整性：结构、目标列唯一性与每个转换链可编译；返回编译结果供执行器复用。</summary>
    /// <param name="mappings">映射定义列表。</param>
    /// <returns>按映射顺序编译完成的字段委托。</returns>
    public static IReadOnlyList<PocDataSyncCompiledField> ValidateMappings(
        IReadOnlyList<PocSyncColumnMapping>? mappings)
    {
        if (mappings is null || mappings.Count == 0)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INCOMPLETE",
                "同步任务至少需要一个字段映射。");
        }
        if (mappings.Count > 256)
        {
            throw new PocDataSyncException(
                "SYNC_MAPPING_INCOMPLETE",
                "同步任务字段映射数量不能超过 256 个。");
        }

        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compiled = new List<PocDataSyncCompiledField>(mappings.Count);
        // 第一阶段：逐条编译映射；转换器参数错误、表达式语法错误在这里即被拒绝。
        foreach (var mapping in mappings)
        {
            // 第二阶段：先做形状校验（空值、超长、目标列与源字段去重），再交给转换器编译器。
            var sourceField = mapping.SourceField?.Trim();
            var targetField = mapping.TargetField?.Trim();
            if (string.IsNullOrWhiteSpace(sourceField) ||
                sourceField.Length > 128 ||
                string.IsNullOrWhiteSpace(targetField) ||
                targetField.Length > 128 ||
                !seenTargets.Add(targetField) ||
                !seenSources.Add(sourceField))
            {
                throw new PocDataSyncException(
                    "SYNC_MAPPING_INCOMPLETE",
                    $"字段映射无效：源字段 '{mapping.SourceField}' → 目标列 '{mapping.TargetField}' 存在空值、超长或重复。");
            }
            compiled.Add(PocDataSyncConverter.CompileField(mapping with
            {
                SourceField = sourceField,
                TargetField = targetField
            }));
        }
        return compiled;
    }

    /// <summary>把任务定义序列化为存储 JSON：camelCase + 保留 null 的紧凑形态。</summary>
    /// <param name="definition">任务定义。</param>
    /// <returns>DefinitionJson 文本。</returns>
    public static string SerializeDefinition(PocSyncTaskDefinition definition) =>
        JsonConvert.SerializeObject(
            definition,
            Formatting.None,
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Include
            });

    /// <summary>把存储 JSON 反序列化为任务定义；空文本返回空映射定义。</summary>
    /// <param name="json">DefinitionJson 文本。</param>
    /// <returns>任务定义。</returns>
    public static PocSyncTaskDefinition DeserializeDefinition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PocSyncTaskDefinition([], null);
        }
        try
        {
            var definition = JsonConvert.DeserializeObject<PocSyncTaskDefinition>(json);
            return new PocSyncTaskDefinition(
                definition?.Mappings ?? [],
                definition?.Schedule);
        }
        catch (JsonException exception)
        {
            throw new PocDataSyncException(
                "SYNC_DEFINITION_INVALID",
                $"同步任务定义不是合法 JSON：{exception.Message}");
        }
    }

    /// <summary>读取任务；不存在抛 SYNC_TASK_NOT_FOUND。</summary>
    /// <param name="code">任务编码。</param>
    /// <returns>任务实体。</returns>
    private async Task<PocSyncTask> RequireTaskAsync(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new PocDataSyncException("SYNC_TASK_INVALID", "同步任务编码不能为空。");
        }
        return await _store.GetTaskAsync(code.Trim())
            ?? throw new PocDataSyncException(
                "SYNC_TASK_NOT_FOUND",
                $"不存在同步任务 '{code.Trim()}'。");
    }

    /// <summary>读取并校验源数据集：必须存在且启用。</summary>
    /// <param name="datasetCode">数据集编码。</param>
    /// <returns>数据集实体。</returns>
    private async Task<PocReportDataset> RequireEnabledDatasetAsync(string? datasetCode)
    {
        if (string.IsNullOrWhiteSpace(datasetCode))
        {
            throw new PocDataSyncException("SYNC_SOURCE_DATASET_REQUIRED", "同步任务必须绑定源数据集。");
        }
        var dataset = await _dataAssets.GetDatasetAsync(datasetCode.Trim())
            ?? throw new PocDataSyncException(
                "SYNC_SOURCE_DATASET_NOT_FOUND",
                $"不存在源数据集 '{datasetCode.Trim()}'。");
        if (!dataset.IsEnabled)
        {
            throw new PocDataSyncException(
                "SYNC_SOURCE_DATASET_DISABLED",
                $"源数据集 '{dataset.Code}' 已停用，请先启用后再运行同步。");
        }
        return dataset;
    }

    /// <summary>读取并校验数据源：必须存在且启用。</summary>
    /// <param name="dataSourceCode">数据源编码。</param>
    /// <returns>数据源实体。</returns>
    private async Task<PocDataSource> RequireEnabledDataSourceAsync(string? dataSourceCode)
    {
        if (string.IsNullOrWhiteSpace(dataSourceCode))
        {
            throw new PocDataSyncException("DATA_SOURCE_NOT_FOUND", "请选择目标数据源。");
        }
        var dataSource = await _dataAssets.GetDataSourceAsync(dataSourceCode.Trim())
            ?? throw new PocDataSyncException(
                "DATA_SOURCE_NOT_FOUND",
                $"不存在数据源 '{dataSourceCode.Trim()}'。");
        if (!dataSource.IsEnabled)
        {
            throw new PocDataSyncException(
                "DATA_SOURCE_DISABLED",
                $"数据源 '{dataSource.Code}' 已停用，请先启用后再运行同步。");
        }
        return dataSource;
    }

    /// <summary>校验任务基础字段：编码格式、名称长度、冲突策略白名单。</summary>
    /// <param name="input">任务保存输入。</param>
    private static void ValidateTaskHeader(PocSyncTaskInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Code) ||
            !CodeRegex.IsMatch(input.Code.Trim()) ||
            string.IsNullOrWhiteSpace(input.Name) ||
            input.Name.Trim().Length > 128 ||
            string.IsNullOrWhiteSpace(input.TargetTable) ||
            input.TargetTable.Trim().Length > 128 ||
            input.Revision < 0)
        {
            throw new PocDataSyncException(
                "SYNC_TASK_INVALID",
                "同步任务配置无效：编码、名称与目标表必须填写且长度合规。");
        }
        _ = NormalizeConflictPolicy(input.ConflictPolicy);
    }

    /// <summary>归一化冲突策略：大小写不敏感地收敛到白名单形态。</summary>
    /// <param name="conflictPolicy">原始冲突策略文本。</param>
    /// <returns>归一化后的策略。</returns>
    private static string NormalizeConflictPolicy(string? conflictPolicy)
    {
        var policy = ConflictPolicies.FirstOrDefault(item =>
            item.Equals(conflictPolicy?.Trim(), StringComparison.OrdinalIgnoreCase));
        return policy
            ?? throw new PocDataSyncException(
                "SYNC_CONFLICT_POLICY_INVALID",
                $"冲突策略 '{conflictPolicy}' 无效，仅支持 TruncateFirst、Append 或 Upsert。");
    }

    /// <summary>
    /// 解析并校验 Upsert 冲突策略的合并键：策略为 Upsert 时，映射中必须有且只有一条
    /// IsKey=true，且该列不允许空（键为空的行无法按主键合并）；其他策略忽略合并键标志。
    /// 保存、导入校验与执行输入冻结三处共用同一份裁决，保证设计器、同步包与运行期口径一致。
    /// </summary>
    /// <param name="conflictPolicy">冲突策略（任意大小写）。</param>
    /// <param name="mappings">字段映射定义。</param>
    /// <returns>Upsert 时返回唯一合并键的目标列名；其他策略返回 null。</returns>
    public static string? ResolveUpsertKeyField(string? conflictPolicy, IReadOnlyList<PocSyncColumnMapping>? mappings)
    {
        var isUpsert = "Upsert".Equals(conflictPolicy?.Trim(), StringComparison.OrdinalIgnoreCase);
        var keyMappings = (mappings ?? [])
            .Where(item => item.IsKey)
            .Select(item => item.TargetField?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!isUpsert)
        {
            return null;
        }
        if (keyMappings.Length != 1)
        {
            throw new PocDataSyncException(
                "SYNC_UPSERT_KEY_INVALID",
                $"冲突策略为 Upsert 时必须有且只有一个合并键列，当前配置了 {keyMappings.Length} 个。");
        }
        var keyMapping = (mappings ?? []).First(item => item.IsKey);
        if (keyMapping.Nullable)
        {
            throw new PocDataSyncException(
                "SYNC_UPSERT_KEY_INVALID",
                $"合并键列 '{keyMapping.TargetField}' 不允许配置为可空：键为空的行无法按主键合并。");
        }
        return keyMappings[0];
    }

    /// <summary>
    /// 归一化调度定义：Manual 清空时间与间隔；Daily 校验 HH:mm；Interval 校验分钟区间；
    /// Cron 校验表达式必填且能被 Furion 解析。非 Cron 类型一律把 ScheduleCron 裁剪为 null，
    /// 与既有“按类型裁剪调度字段”的规则保持一致。
    /// </summary>
    /// <param name="scheduleType">调度类型。</param>
    /// <param name="scheduleTime">每日调度时间。</param>
    /// <param name="intervalMinutes">间隔分钟数。</param>
    /// <param name="scheduleCron">Cron 调度表达式（仅 Cron 类型使用）。</param>
    /// <returns>归一化后的调度四元组。</returns>
    private static (string Type, string? Time, int IntervalMinutes, string? Cron) NormalizeSchedule(
        string? scheduleType,
        string? scheduleTime,
        int intervalMinutes,
        string? scheduleCron)
    {
        var type = ScheduleTypes.FirstOrDefault(item =>
            item.Equals(scheduleType?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new PocDataSyncException(
                "SYNC_SCHEDULE_INVALID",
                $"调度类型 '{scheduleType}' 无效，仅支持 Manual、Daily、Interval 或 Cron。");

        // 第一阶段：按类型分发归一化。每种类型只保留自己的调度字段，其余字段裁剪为默认值。
        switch (type)
        {
            case "Daily":
                if (!TimeSpan.TryParseExact(
                        scheduleTime?.Trim(),
                        @"hh\:mm",
                        CultureInfo.InvariantCulture,
                        out var time))
                {
                    throw new PocDataSyncException(
                        "SYNC_SCHEDULE_INVALID",
                        "每日调度时间必须使用 HH:mm 格式，例如 02:30。");
                }
                return (type, time.ToString(@"hh\:mm", CultureInfo.InvariantCulture), 0, null);
            case "Interval":
                if (intervalMinutes is < 1 or > 525_600)
                {
                    throw new PocDataSyncException(
                        "SYNC_SCHEDULE_INVALID",
                        "间隔调度分钟数必须在 1 到 525600 之间。");
                }
                return (type, null, intervalMinutes, null);
            case "Cron":
                var cron = scheduleCron?.Trim();
                if (string.IsNullOrWhiteSpace(cron))
                {
                    throw new PocDataSyncException(
                        "SYNC_SCHEDULE_INVALID",
                        "Cron 调度必须填写表达式（标准 5 段：分 时 日 月 周）。");
                }
                if (cron.Length > ScheduleCronMaxLength)
                {
                    throw new PocDataSyncException(
                        "SYNC_SCHEDULE_INVALID",
                        $"Cron 表达式 '{cron}' 长度超过 {ScheduleCronMaxLength} 个字符。");
                }

                // 第二阶段：表达式解析级校验（与运行时触发器同一 Furion 内核），非法表达式保存前拒绝。
                ValidateCronExpression(cron);
                return (type, null, 0, cron);
            default:
                return (type, null, 0, null);
        }
    }

    /// <summary>
    /// 用 Furion Cron 触发器解析校验表达式，与运行时触发器同一解析内核（内嵌 TimeCrontab）。
    /// 只支持默认 5 段格式（分 时 日 月 周）：CronTrigger 构造即解析，非法表达式在保存/导入前
    /// 被拒绝，杜绝“保存成功但触发器注册失败”的带病上线。Triggers.Cron 是惰性构建器不会立即解析，
    /// 不能用于此处校验（小样实测结论）。
    /// </summary>
    /// <param name="cronExpression">待校验的标准 5 段 Cron 表达式（已 Trim）。</param>
    internal static void ValidateCronExpression(string cronExpression)
    {
        try
        {
            _ = new CronTrigger(cronExpression, CronStringFormat.Default);
        }
        catch (Exception exception)
        {
            throw new PocDataSyncException(
                "SYNC_SCHEDULE_INVALID",
                $"Cron 表达式 '{cronExpression}' 无法解析：{exception.Message}" +
                "（只支持标准 5 段：分 时 日 月 周，不支持含秒的 6 段写法）。");
        }
    }

    /// <summary>
    /// 调度生命周期钩子的统一决策：启用且 Cron 且表达式非空则注册动态作业，
    /// 其余情况（停用、非 Cron、表达式缺失）一律摘除。网关为 null（单测缺省构造）时直接跳过。
    /// </summary>
    /// <param name="gateway">调度网关，可为 null。</param>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="taskName">任务名称（写入作业描述）。</param>
    /// <param name="scheduleType">调度类型。</param>
    /// <param name="cronExpression">Cron 表达式。</param>
    /// <param name="enabled">任务是否启用。</param>
    internal static void ApplyCronScheduleGateway(
        IPocDataSyncScheduleGateway? gateway,
        string taskCode,
        string taskName,
        string? scheduleType,
        string? cronExpression,
        bool enabled)
    {
        if (gateway is null)
        {
            return;
        }
        var isCron = string.Equals((scheduleType ?? string.Empty).Trim(), "Cron", StringComparison.OrdinalIgnoreCase);
        if (enabled && isCron && !string.IsNullOrWhiteSpace(cronExpression))
        {
            gateway.RegisterCronJob(taskCode, taskName, cronExpression.Trim());
        }
        else
        {
            gateway.RemoveCronJob(taskCode);
        }
    }

    /// <summary>按派生作业 ID 反查启用的 Cron 任务编码；找不到匹配返回 null（作业体触发入口使用）。</summary>
    /// <param name="jobId">调度器里的作业 ID（可能为截断派生 ID）。</param>
    /// <param name="cancellationToken">取消令牌（当前全量比对为纯内存计算，仅保留签名）。</param>
    /// <returns>匹配的任务编码或 null。</returns>
    public async Task<string?> ResolveCronTaskCodeAsync(string jobId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var tasks = await _store.ListTasksAsync();
        return tasks.FirstOrDefault(task => task.IsEnabled &&
            string.Equals((task.ScheduleType ?? string.Empty).Trim(), "Cron", StringComparison.OrdinalIgnoreCase) &&
            PocDataSyncCronScheduleIdentity.DeriveJobId(task.Code) == jobId)?.Code;
    }

    /// <summary>
    /// 启动对账：把所有“启用且 Cron”任务批量补挂到调度器（幂等），供 PocDataSyncJob 首轮扫描
    /// 前一次性执行；失败不抛出（网关内记日志），由后续保存/导入/重启自愈。
    /// </summary>
    /// <returns>本次补挂的任务数量。</returns>
    public async Task<int> ReconcileCronSchedulesAsync()
    {
        var tasks = (await _store.ListTasksAsync())
            .Where(task => task.IsEnabled &&
                string.Equals((task.ScheduleType ?? string.Empty).Trim(), "Cron", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(task.ScheduleCron))
            .ToArray();
        foreach (var task in tasks)
        {
            ApplyCronScheduleGateway(_scheduleGateway, task.Code, task.Name, task.ScheduleType, task.ScheduleCron, task.IsEnabled);
        }
        return tasks.Length;
    }

    /// <summary>
    /// 构造源数据集执行计划：把数据集与数据源冻结为引擎可消费的运行配置。
    /// 定义字段取映射涉及的源字段（作为 Full 流式读取的行键），变量按数据集契约解析绑定；
    /// DbType 原样透传，由连接工厂决定方言支持范围。
    /// </summary>
    /// <param name="task">同步任务。</param>
    /// <param name="mappings">字段映射定义。</param>
    /// <param name="parameters">数据集变量取值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行计划与相关实体。</returns>
    private async Task<PocRuntimeExecutionPlan> BuildSourcePlanAsync(
        PocSyncTask task,
        IReadOnlyList<PocSyncColumnMapping> mappings,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken)
    {
        var dataset = await RequireEnabledDatasetAsync(task.SourceDatasetCode);
        var source = await RequireEnabledDataSourceAsync(dataset.DataSourceCode);
        var schema = PocDataAssetService.ReadSchema(dataset);

        // 第一阶段：把数据集变量物化为参数定义并解析取值，绑定语义与数据集预览完全一致。
        // 默认表达式在此按本次运行时间现算并作为兜底值——正式跑（parameters=null）每次
        // 自动滚动到当前窗口；试跑传值时客户端取值优先，便于按历史窗口验证数据。
        var parameterDefinitions = schema.Variables.Select(variable => new PocParameterDefinition(
            variable.Code,
            variable.DisplayName,
            "text",
            variable.SourceType,
            PocDataAssetService.ResolveVariableFallback(variable),
            true,
            variable.DataType,
            Required: variable.Required,
            SourceCode: variable.SourceCode,
            SourceFieldCode: variable.SourceFieldCode)).ToArray();
        var resolved = PocParameterResolver.Resolve(
            parameterDefinitions,
            parameters ?? new Dictionary<string, string?>());

        // 第二阶段：组装运行配置。字段集合用映射源字段去重，Full 模式走一次查询的流式游标。
        var definitionFields = mappings
            .Select(item => item.SourceField.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => new PocDatasetFieldDefinition(code, code, "string"))
            .ToArray();
        var definition = new PocReportDefinition(
            task.Code,
            task.Name,
            "sync",
            new PocPreviewSettings(PocPreviewMode.Full, TryRunRowLimit, false),
            new PocExportSettings(
                PocDataExportMode.Large,
                NormalMaxRows: dataset.MaxRows,
                NormalMaxCells: 0,
                NormalTimeoutSeconds: dataset.TimeoutSeconds,
                LargeExportEnabled: true,
                RowsPerSheet: 1_000_000,
                FileRetentionHours: 24),
            parameterDefinitions,
            definitionFields);
        var configuration = new PocRuntimeReportConfiguration(
            definition,
            source.Code,
            source.DbType,
            source.ConnectionString,
            dataset.QueryText,
            dataset.TimeoutSeconds,
            dataset.MaxRows,
            UsesFallbackData: false);
        return new PocRuntimeExecutionPlan(
            configuration,
            new PocExecuteRequest(1, parameters ?? new Dictionary<string, string?>()),
            resolved);
    }

    /// <summary>
    /// 启动一次运行的前置阶段：加载任务、编译映射、冻结执行输入（含按当前时间现算的
    /// 取数参数兜底值）、防重入检查并落 Running 日志。返回任务、执行输入、日志实体与
    /// 取数参数摘要，供同步/后台两条路径继续执行。
    /// </summary>
    /// <param name="taskCode">任务编码。</param>
    /// <returns>任务、执行输入、已落库的 Running 日志与取数参数摘要（无参数为空串）。</returns>
    private async Task<(PocSyncTask Task, PocDataSyncExecutionInput Input, PocSyncRunLog Log, string ParameterSummary)>
        StartRunAsync(string taskCode)
    {
        var task = await RequireTaskAsync(taskCode);
        if (!task.IsEnabled)
        {
            throw new PocDataSyncException(
                "SYNC_TASK_DISABLED",
                $"同步任务 '{task.Code}' 已停用，请先启用再运行。");
        }
        if (await _store.HasRunningRunLogAsync(task.Code))
        {
            throw new PocDataSyncException(
                "SYNC_TASK_ALREADY_RUNNING",
                $"同步任务 '{task.Code}' 已有正在执行的运行，请等待其结束或先取消。");
        }

        // 第一阶段：在请求线程内冻结执行输入——任务、映射编译结果、源计划与目标连接信息。
        // Upsert 策略在此再次裁决合并键（防御历史任务被改坏键定义后仍被调度拉起）。
        var definition = DeserializeDefinition(task.DefinitionJson);
        var fields = ValidateMappings(definition.Mappings);
        var upsertKeyField = ResolveUpsertKeyField(task.ConflictPolicy, definition.Mappings);
        var plan = await BuildSourcePlanAsync(task, definition.Mappings, null, CancellationToken.None);
        var parameterSummary = BuildParameterSummary(plan);
        var target = await RequireEnabledDataSourceAsync(task.TargetDataSourceCode);
        var input = new PocDataSyncExecutionInput(
            plan,
            target.DbType,
            target.ConnectionString,
            target.DefaultTimeoutSeconds,
            task.TargetTable,
            task.ConflictPolicy,
            fields,
            UpsertKeyField: upsertKeyField);

        // 第二阶段：先置 Running 再执行，保证防重入与轮询状态一致。
        var log = new PocSyncRunLog
        {
            RunNo = $"{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}",
            TaskCode = task.Code,
            Status = "Running",
            StartedAt = DateTimeOffset.Now
        };
        await _store.InsertRunLogAsync(log);
        return (task, input, log, parameterSummary);
    }

    /// <summary>
    /// 汇总本次运行实际代入的取数参数：只列源 SQL 真正引用的变量及其解析后取值，
    /// 写入运行日志消息，使“每次跑了哪个时间窗口”可审计——滚动表达式每天算出的值不同，
    /// 不落日志就无法回溯历史运行的实际窗口。
    /// </summary>
    /// <param name="plan">已解析取值的执行计划。</param>
    /// <returns>形如 startDate=2026-10-01, endDate=2026-10-31 的摘要；无引用变量返回空串。</returns>
    private static string BuildParameterSummary(PocRuntimeExecutionPlan plan)
    {
        var referenced = PocDataAssetService.ExtractParameterCodes(plan.Configuration.QueryText);
        return string.Join(
            ", ",
            referenced
                .Select(code => (Code: code, Value: plan.Parameters.DisplayValues.TryGetValue(code, out var value) ? value : null))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => $"{item.Code}={item.Value}"));
    }

    /// <summary>回写终态为 Completed：更新日志统计、失败明细并刷新任务最近运行时间。</summary>
    /// <param name="logStore">用于回写的存储（请求作用域或后台独立连接）。</param>
    /// <param name="runNo">运行编号。</param>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="stats">执行统计。</param>
    /// <param name="message">结果消息。</param>
    /// <param name="parameterSummary">本次运行实际代入的取数参数摘要（滚动窗口审计用），空值不追加。</param>
    /// <returns>更新后的日志实体。</returns>
    private async Task<PocSyncRunLog> CompleteRunAsync(
        PocDataSyncStore logStore,
        string runNo,
        string taskCode,
        PocDataSyncRunStats stats,
        string? message,
        string? parameterSummary = null)
    {
        var log = await logStore.GetRunLogAsync(runNo)
            ?? throw new PocDataSyncException(
                "SYNC_RUN_LOG_MISSING",
                $"运行日志 '{runNo}' 不存在，无法回写终态。");
        log.Status = "Completed";
        log.FinishedAt = DateTimeOffset.Now;
        log.RowsRead = stats.RowsRead;
        log.RowsWritten = stats.RowsWritten;
        log.RowsFailed = stats.RowsFailed;
        log.ErrorSummaryJson = SerializeFailures(stats.Failures);
        // 第1阶段：拼装结果消息——取数参数前缀置顶（窗口可审计），统计信息保持原有口径。
        var statistics = message ?? $"读取 {stats.RowsRead} 行，写入 {stats.RowsWritten} 行，失败 {stats.RowsFailed} 行。";
        log.Message = string.IsNullOrWhiteSpace(parameterSummary)
            ? statistics
            : $"取数参数 {parameterSummary}；{statistics}";
        await logStore.UpdateRunLogAsync(log);
        await logStore.UpdateTaskLastRunAsync(taskCode, DateTimeOffset.Now);
        return log;
    }

    /// <summary>回写终态为 Failed：记录错误消息并刷新任务最近运行时间，供调度判断冷却。</summary>
    /// <param name="logStore">用于回写的存储。</param>
    /// <param name="runNo">运行编号。</param>
    /// <param name="taskCode">任务编码。</param>
    /// <param name="exception">导致失败的异常。</param>
    /// <returns>更新后的日志实体。</returns>
    private async Task<PocSyncRunLog> FailRunAsync(
        PocDataSyncStore logStore,
        string runNo,
        string taskCode,
        Exception exception)
    {
        PocSyncRunLog? log = null;
        try
        {
            log = await logStore.GetRunLogAsync(runNo);
        }
        catch
        {
            // 日志读取失败时仍需尝试原位回写，避免 Running 状态悬挂阻塞下一次运行。
        }

        var entity = log ?? new PocSyncRunLog
        {
            RunNo = runNo,
            TaskCode = taskCode,
            StartedAt = DateTimeOffset.Now
        };
        entity.Status = "Failed";
        entity.FinishedAt = DateTimeOffset.Now;
        entity.Message = exception.Message.Length > 4000 ? exception.Message[..4000] : exception.Message;
        if (exception.Data["RunLog"] is PocSyncRunLog seeded)
        {
            entity.RowsRead = seeded.RowsRead;
            entity.RowsWritten = seeded.RowsWritten;
            entity.RowsFailed = seeded.RowsFailed;
            entity.ErrorSummaryJson = seeded.ErrorSummaryJson;
        }
        await logStore.UpdateRunLogAsync(entity);
        await logStore.UpdateTaskLastRunAsync(taskCode, DateTimeOffset.Now);
        return entity;
    }

    /// <summary>把失败明细序列化为存储 JSON（封顶 200 条）。</summary>
    /// <param name="failures">失败明细。</param>
    /// <returns>JSON 数组文本；无失败返回 null。</returns>
    private static string? SerializeFailures(IReadOnlyList<PocDataSyncFailureDetail> failures)
    {
        if (failures.Count == 0)
        {
            return null;
        }
        var capped = failures.Take(PocDataSyncExecutor.FailureDetailLimit).ToArray();
        return JsonConvert.SerializeObject(
            capped,
            Formatting.None,
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Include
            });
    }

    /// <summary>把失败明细 JSON 反序列化回文档；损坏文本返回空列表，不影响日志展示。</summary>
    /// <param name="json">ErrorSummaryJson 文本。</param>
    /// <returns>失败明细列表。</returns>
    private static IReadOnlyList<PocDataSyncFailureDetail> DeserializeFailures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonConvert.DeserializeObject<IReadOnlyList<PocDataSyncFailureDetail>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>派生一个独立于当前请求作用域的主库连接，供后台线程回写运行日志；生产注册是 SqlSugarScope，须走 ISqlSugarClient.CopyNew（与框架 Job 同构）。</summary>
    /// <returns>新连接的 SqlSugar 客户端。</returns>
    private SqlSugarClient CopyDatabaseForBackground() => _database.CopyNew();

    /// <summary>从首个非空样例值推断列的数据类型，预览契约与查询字段推断保持同构。</summary>
    /// <param name="rows">样例行。</param>
    /// <param name="column">列名。</param>
    /// <returns>string/number/bool/datetime 类型名。</returns>
    private static string InferColumnDataType(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string column)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue(column, out var value) || value is null)
            {
                continue;
            }
            return value switch
            {
                bool => "bool",
                DateTime or DateTimeOffset => "datetime",
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => "number",
                _ => "string"
            };
        }
        return "string";
    }

    /// <summary>把任务实体转换为对外文档：调度取列字段（含 Cron 表达式），映射取 DefinitionJson。</summary>
    /// <param name="task">任务实体。</param>
    /// <returns>任务文档。</returns>
    private static PocSyncTaskDocument ToTaskDocument(PocSyncTask task)
    {
        var definition = DeserializeDefinition(task.DefinitionJson);
        return new PocSyncTaskDocument(
            task.Code,
            task.Name,
            task.SourceDatasetCode,
            task.TargetDataSourceCode,
            task.TargetTable,
            task.ConflictPolicy,
            task.ScheduleType,
            task.ScheduleTime,
            task.IntervalMinutes,
            task.IsEnabled,
            task.Revision,
            task.LastRunAt,
            definition.Mappings,
            task.ScheduleCron);
    }

    /// <summary>把运行日志实体转换为对外文档：失败明细从 JSON 展开为结构化列表。</summary>
    /// <param name="log">日志实体。</param>
    /// <returns>运行日志文档。</returns>
    private static PocSyncRunLogDocument ToRunLogDocument(PocSyncRunLog log) =>
        new(
            log.RunNo,
            log.TaskCode,
            log.Status,
            log.StartedAt,
            log.FinishedAt,
            log.RowsRead,
            log.RowsWritten,
            log.RowsFailed,
            log.Message,
            DeserializeFailures(log.ErrorSummaryJson));
}
