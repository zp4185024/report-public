using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>同步包导入模式：Create 拒绝撞码，Replace 先删旧任务再重建。</summary>
internal enum PocDataSyncImportMode
{
    /// <summary>新建模式：目标同步任务编码已存在时拒绝导入。</summary>
    Create,

    /// <summary>替换模式：先删除既有同步任务再按包内容重建。</summary>
    Replace
}

/// <summary>
/// 数据同步包应用服务：负责开发机侧的导出组装、导入前的结构化校验、真实数据源上的受限试跑，
/// 以及把包内容落库的导入编排。安全边界与报表包一致：包内绝不携带连接串/密码；
/// 导入前服务端重跑全部校验；数据集落库复用数据资产服务，任务落库复用同步任务存储。
/// </summary>
public sealed class PocDataSyncPackageService : ITransient
{
    /// <summary>包试跑的硬性行数上限，与任务试跑、报表包试跑一致。</summary>
    public const int DryRunRowLimit = 50;

    private readonly PocDataSyncStore _store;
    private readonly PocDataAssetStore _dataAssets;
    private readonly PocDataAssetService _dataAssetService;
    private readonly PocDataSyncExecutor _executor;
    private readonly IPocDataSyncScheduleGateway? _scheduleGateway;

    /// <summary>初始化 PocDataSyncPackageService 实例并保存编排所需的存储与应用服务依赖。</summary>
    /// <param name="store">同步任务存储，用于冲突探测与任务落库。</param>
    /// <param name="dataAssets">数据资产存储，用于数据集/数据源查询与引用探测。</param>
    /// <param name="dataAssetService">数据资产应用服务，导入数据集复用其保存校验。</param>
    /// <param name="executor">同步执行器，试跑复用其预览管线。</param>
    /// <param name="scheduleGateway">Cron 调度网关，导入落库后维护动态作业；缺省（单测）时跳过调度器维护。</param>
    public PocDataSyncPackageService(
        PocDataSyncStore store,
        PocDataAssetStore dataAssets,
        PocDataAssetService dataAssetService,
        PocDataSyncExecutor? executor = null,
        IPocDataSyncScheduleGateway? scheduleGateway = null)
    {
        _store = store;
        _dataAssets = dataAssets;
        _dataAssetService = dataAssetService;
        _executor = executor ?? new PocDataSyncExecutor();
        _scheduleGateway = scheduleGateway;
    }

    /// <summary>
    /// 导入审计文件目录。沿用 output/&lt;功能&gt;/ 约定写在进程当前目录下，
    /// 测试可以改写该属性把审计重定向到临时目录，避免污染仓库工作区。
    /// </summary>
    public string AuditDirectory { get; set; } = Path.Combine(
        Directory.GetCurrentDirectory(),
        "output",
        "sync-packages");

    /// <summary>
    /// 聚合读取同步任务、源数据集与两端数据源并组装成导出包。
    /// 包内只携带数据源编码与类型提示，绝不出现连接串或密码。
    /// </summary>
    /// <param name="taskCode">待导出的同步任务编码。</param>
    /// <returns>可直接序列化为 JSON 文件的完整同步包。</returns>
    /// <exception cref="KeyNotFoundException">任务编码不存在时抛出，由 API 层映射为 404。</exception>
    public async Task<PocDataSyncPackage> ExportAsync(string taskCode)
    {
        // 第一阶段：读取任务聚合与源/目标数据源，任一缺失都不允许导出。
        if (string.IsNullOrWhiteSpace(taskCode))
        {
            throw new KeyNotFoundException("同步任务编码不能为空。");
        }
        var normalizedCode = taskCode.Trim();
        var task = await _store.GetTaskAsync(normalizedCode)
            ?? throw new KeyNotFoundException($"不存在同步任务 '{normalizedCode}'。");
        var dataset = await _dataAssets.GetDatasetAsync(task.SourceDatasetCode)
            ?? throw new PocDataSyncException(
                "SYNC_SOURCE_DATASET_NOT_FOUND",
                $"任务 '{task.Code}' 绑定的源数据集 '{task.SourceDatasetCode}' 不存在，无法导出。");
        var datasetSource = await _dataAssets.GetDataSourceAsync(dataset.DataSourceCode)
            ?? throw new PocDataSyncException(
                "DATA_SOURCE_NOT_FOUND",
                $"数据集 '{dataset.Code}' 绑定的数据源 '{dataset.DataSourceCode}' 不存在，无法导出。");
        var target = await _dataAssets.GetDataSourceAsync(task.TargetDataSourceCode)
            ?? throw new PocDataSyncException(
                "DATA_SOURCE_NOT_FOUND",
                $"任务 '{task.Code}' 绑定的目标数据源 '{task.TargetDataSourceCode}' 不存在，无法导出。");

        // 第二阶段：组装包结构。字段契约经 ReadSchema 规范化并保留对象形态，映射来自任务定义；
        // 数据源类型只是导入侧的比对提示，非标准类型文本原样携带，由导入时的兼容性检查兜底。
        var definition = PocDataSyncService.DeserializeDefinition(task.DefinitionJson);
        return new PocDataSyncPackage(
            PocDataSyncPackageFormat.PackageKind,
            PocDataSyncPackageFormat.PackageVersion,
            DateTimeOffset.UtcNow,
            task.Code,
            new PocDataSyncPackageDataset(
                dataset.Code,
                dataset.Name,
                dataset.DataSourceCode,
                NormalizeTypeHint(datasetSource.DbType),
                dataset.QueryText,
                JObject.FromObject(PocDataAssetService.ReadSchema(dataset)),
                dataset.TimeoutSeconds,
                dataset.MaxRows,
                dataset.IsEnabled),
            target.Code,
            NormalizeTypeHint(target.DbType),
            task.TargetTable,
            task.ConflictPolicy,
            task.ScheduleType,
            task.ScheduleTime,
            task.IntervalMinutes,
            definition.Mappings,
            task.ScheduleCron);
    }

    /// <summary>归一化数据源类型提示；不在支持清单内的类型原样返回，导出不因此失败。</summary>
    /// <param name="dbType">数据源声明的类型文本。</param>
    /// <returns>归一化后的类型提示。</returns>
    private static string NormalizeTypeHint(string dbType)
    {
        try
        {
            return PocDatabaseProvider.NormalizeType(dbType);
        }
        catch (PocDatasetConfigurationException)
        {
            return dbType.Trim();
        }
    }

    /// <summary>
    /// 只读校验同步包：结构、版本、映射完整性、数据集契约与 SQL 只读校验逐项检查并探测目标库冲突。
    /// 不写库、不执行 SQL；errors 非空表示包不可导入。
    /// </summary>
    /// <param name="package">前端上传的整份同步包。</param>
    /// <returns>结构化校验报告，含错误、警告、包摘要和与当前库的冲突信息。</returns>
    public async Task<PocDataSyncValidateResult> ValidateAsync(PocDataSyncPackage? package)
    {
        var (errors, warnings, summary) = ValidatePackageCore(package);
        if (summary is null || package is null)
        {
            return new PocDataSyncValidateResult(errors.Count == 0, errors, warnings, null, null);
        }

        // 冲突探测只读查询当前库；一致性/引用结论在数据集不存在时保持 null。
        var taskCode = package.TaskCode.Trim();
        var datasetCode = package.SourceDataset.Code.Trim();
        var existingDataset = await _dataAssets.GetDatasetAsync(datasetCode);
        var existingTask = await _store.GetTaskAsync(taskCode);
        var referencedByReports = existingDataset is null
            ? (bool?)null
            : (await _dataAssets.ListDatasetReferencesAsync(datasetCode)).Count > 0;
        var targetDataSource = await _dataAssets.GetDataSourceAsync(package.TargetDataSourceCode);
        var collisions = new PocDataSyncCollisions(
            existingTask is not null,
            existingDataset is not null,
            existingDataset is null ? null : IsDatasetContentIdentical(existingDataset, package.SourceDataset),
            referencedByReports,
            targetDataSource is not null,
            targetDataSource is null || string.IsNullOrWhiteSpace(package.TargetDataSourceType)
                ? null
                : IsDataSourceTypeCompatible(targetDataSource, package.TargetDataSourceType));
        return new PocDataSyncValidateResult(errors.Count == 0, errors, warnings, collisions, summary);
    }

    /// <summary>
    /// 在用户选定的目标数据源上对包内数据集做受限试跑：SQL 重过只读校验、变量按现有规则解析，
    /// 随后以最多 50 行执行真实查询并按包内映射完成端到端转换校验（不写目标库）。
    /// </summary>
    /// <param name="request">试跑请求：包 + 目标数据源 + 变量样例取值。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>试跑结果：转换前后样例、目标列结论与兼容性错误。</returns>
    public async Task<PocDataSyncTryRunResult> DryRunAsync(
        PocDataSyncDryRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsurePackageValid(request.Package);
        var package = request.Package!;
        var fields = PocDataSyncService.ValidateMappings(package.Mappings);

        var target = await ResolveTargetDataSourceAsync(
            request.TargetDataSourceCode ?? package.TargetDataSourceCode);
        if (!IsDataSourceTypeCompatible(target, package.SourceDataset.DataSourceType))
        {
            throw new PocDataSyncException(
                "SYNC_DATA_SOURCE_TYPE_MISMATCH",
                $"目标数据源类型为 {PocDatabaseProvider.NormalizeType(target.DbType)}，" +
                $"与同步包导出时的 {package.SourceDataset.DataSourceType} 不一致，不能直接试跑。");
        }

        var plan = BuildPackagePlan(package, target, request.Parameters);
        var core = await _executor.PreviewAsync(plan, fields, DryRunRowLimit, cancellationToken);
        return new PocDataSyncTryRunResult(
            core.FailedRows == 0 && core.Errors.Count == 0,
            core.TargetColumns,
            core.Samples,
            0,
            core.RowCountCapped,
            core.FailedRows,
            core.Errors);
    }

    /// <summary>
    /// 导入同步包：服务端重跑全部校验后按冲突策略落库。数据集按“新建/复用一致/替换”决策
    /// （被报表引用时拒绝替换），任务按 Create/Replace 模式落库；每次尝试都写一条审计 JSON。
    /// </summary>
    /// <param name="request">导入请求：包 + 目标数据源 + 模式。</param>
    /// <returns>导入结果：任务编码、数据集动作、任务动作与警告清单。</returns>
    public async Task<PocDataSyncImportResult> ImportAsync(PocDataSyncImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var taskCode = request.Package?.TaskCode?.Trim();
        var modeName = "Invalid";
        var actions = new List<string>();
        try
        {
            var mode = NormalizeImportMode(request.Mode);
            modeName = mode.ToString();
            var result = await ImportCoreAsync(request, mode, actions);
            await WriteImportAuditAsync(request, result.TaskCode, modeName, actions, ok: true, error: null);
            return result;
        }
        catch (Exception exception)
        {
            // 失败也要留审计痕迹；审计写失败本身不改变导入的业务结果。
            await WriteImportAuditAsync(request, taskCode, modeName, actions, ok: false, error: exception.Message);
            throw;
        }
    }

    /// <summary>
    /// 导入核心流程：服务端重校验、目标数据源解析、数据集决策、任务删除/落库、终态检查。
    /// 与 <see cref="ImportAsync"/> 分离是为了让审计写入在成功与失败两条路径上都拿到动作序列。
    /// 数据集与任务是两个独立资源，各走各的保存事务（与本项目“资源保存之间不共用事务”的设计一致）；
    /// 若任务保存失败，先前新建/替换的数据集会保留，由管理员按审计文件决定保留或回退。
    /// </summary>
    /// <param name="request">原始导入请求。</param>
    /// <param name="mode">已归一化的导入模式。</param>
    /// <param name="actions">动作序列收集器。</param>
    /// <returns>导入结果文档。</returns>
    private async Task<PocDataSyncImportResult> ImportCoreAsync(
        PocDataSyncImportRequest request,
        PocDataSyncImportMode mode,
        List<string> actions)
    {
        // 第一阶段：服务端重跑全部校验（不信任客户端的 validate 结果）。
        await EnsurePackageValid(request.Package);
        var package = request.Package!;
        var taskCode = package.TaskCode.Trim();
        var datasetCode = package.SourceDataset.Code.Trim();

        // 第二阶段：解析目标数据源并按需记录类型差异警告（类型不一致仅在试跑时硬拒绝）。
        var target = await ResolveTargetDataSourceAsync(request.TargetDataSourceCode);
        var warnings = new List<string>();
        if (!IsDataSourceTypeCompatible(target, package.SourceDataset.DataSourceType))
        {
            warnings.Add(
                $"目标数据源类型 {PocDatabaseProvider.NormalizeType(target.DbType)} 与包导出时的 " +
                $"{package.SourceDataset.DataSourceType} 不一致，请先完成试跑确认查询兼容。");
        }

        // 第三阶段：数据集决策——新建 / 内容一致复用 / 未被报表引用时按 Replace 模式替换。
        var datasetAction = await ResolveDatasetActionAsync(package, target, mode, warnings, actions);

        // 第四阶段：任务决策——Create 撞码拒绝；Replace 先删旧任务（保留运行日志）再重建。
        var taskAction = await ResolveTaskActionAsync(package, target, mode, actions);

        // 第五阶段：终态检查——导入后任务必须处于已保存状态（修订号 ≥ 1）且数据集可用。
        var storedTask = await _store.GetTaskAsync(taskCode);
        var storedDataset = await _dataAssets.GetDatasetAsync(datasetCode);
        if (storedTask?.Revision is < 1 || storedDataset is null)
        {
            throw new PocDataSyncException(
                "SYNC_PACKAGE_IMPORT_INCOMPLETE",
                $"同步任务 '{taskCode}' 导入后未达到已保存状态，请联系管理员核查。");
        }

        return new PocDataSyncImportResult(taskCode, datasetAction, taskAction, warnings);
    }

    /// <summary>按冲突策略决定数据集落库动作：不存在新建、一致复用、不一致时按引用与模式决策。</summary>
    /// <param name="package">已通过校验的同步包。</param>
    /// <param name="target">用户选定的目标数据源实体。</param>
    /// <param name="mode">导入模式。</param>
    /// <param name="warnings">警告收集器。</param>
    /// <param name="actions">动作序列收集器。</param>
    /// <returns>数据集动作名：Created / Reused / Replaced。</returns>
    private async Task<string> ResolveDatasetActionAsync(
        PocDataSyncPackage package,
        PocDataSource target,
        PocDataSyncImportMode mode,
        List<string> warnings,
        List<string> actions)
    {
        var dataset = package.SourceDataset;
        var datasetCode = dataset.Code.Trim();
        var existing = await _dataAssets.GetDatasetAsync(datasetCode);
        if (existing is null)
        {
            await _dataAssetService.SaveDatasetAsync(datasetCode, ToDatasetDocument(dataset, target.Code.Trim(), 0));
            actions.Add($"DatasetCreated:{datasetCode}");
            return "Created";
        }

        // 内容一致则复用不动，绝不因导入改写其他报表正在使用的数据集定义。
        if (IsDatasetContentIdentical(existing, dataset))
        {
            actions.Add($"DatasetReused:{datasetCode}");
            return "Reused";
        }

        // 内容不一致：被任何报表引用即拒绝替换，防止报表运行时数据语义悄悄改变。
        var references = await _dataAssets.ListDatasetReferencesAsync(datasetCode);
        if (references.Count > 0)
        {
            throw new PocDataSyncException(
                "SYNC_DATASET_IN_USE",
                $"数据集 '{datasetCode}' 已存在且内容不一致，并被 {references.Count} 个报表引用：" +
                $"{PocDeleteGuard.FormatReferenceSummary(references.Select(item => item.Name).ToArray())}。" +
                "请先调整这些报表的数据集绑定，或修改包内数据集编码。");
        }
        if (mode != PocDataSyncImportMode.Replace)
        {
            throw new PocDataSyncException(
                "SYNC_DATASET_CONTENT_MISMATCH",
                $"数据集 '{datasetCode}' 已存在且内容不一致；确认覆盖请使用 Replace 模式重新导入。");
        }
        warnings.Add($"数据集 '{datasetCode}' 内容与库内版本不一致，已按 Replace 模式替换。");
        await _dataAssetService.SaveDatasetAsync(
            datasetCode,
            ToDatasetDocument(dataset, target.Code.Trim(), existing.Revision));
        actions.Add($"DatasetReplaced:{datasetCode}");
        return "Replaced";
    }

    /// <summary>按模式决定任务落库动作：Create 撞码拒绝；Replace 先删旧任务（保留运行日志）再重建。</summary>
    /// <param name="package">已通过校验的同步包。</param>
    /// <param name="target">用户选定的目标数据源实体。</param>
    /// <param name="mode">导入模式。</param>
    /// <param name="actions">动作序列收集器。</param>
    /// <returns>任务动作名：Created / Replaced。</returns>
    private async Task<string> ResolveTaskActionAsync(
        PocDataSyncPackage package,
        PocDataSource target,
        PocDataSyncImportMode mode,
        List<string> actions)
    {
        var taskCode = package.TaskCode.Trim();
        var existing = await _store.GetTaskAsync(taskCode);
        if (existing is not null)
        {
            if (mode != PocDataSyncImportMode.Replace)
            {
                throw new PocDataSyncException(
                    "SYNC_TASK_ALREADY_EXISTS",
                    $"同步任务 '{taskCode}' 已存在；确认覆盖请使用 Replace 模式重新导入。");
            }
            await _store.DeleteTaskAsync(taskCode);
        }

        // 包内不携带任务显示名，导入后名称取任务编码，管理员可在任务页改名。
        // 映射编译校验在保存前已完成（ValidateMappings 抛出即拒绝），落库仍携带原始映射定义。
        var schedule = ValidateSchedule(package.ScheduleType, package.ScheduleTime, package.IntervalMinutes, package.ScheduleCron);
        _ = PocDataSyncService.ValidateMappings(package.Mappings);
        var definition = new PocSyncTaskDefinition(
            package.Mappings,
            new PocSyncScheduleDefinition(schedule.Type, schedule.Time, schedule.IntervalMinutes, schedule.CronExpression));
        await _store.SaveTaskAsync(new PocSyncTaskMutation(
            taskCode,
            taskCode,
            package.SourceDataset.Code.Trim(),
            target.Code.Trim(),
            package.TargetTable.Trim(),
            PocDataSyncService.SerializeDefinition(definition),
            package.ConflictPolicy.Trim(),
            schedule.Type,
            schedule.Time,
            schedule.IntervalMinutes,
            schedule.CronExpression,
            IsEnabled: true,
            ExpectedRevision: 0));

        // 导入落库成功后注册 Cron 动态作业（导入任务固定启用）；缺失此步会让导入的 Cron 任务静默不跑。
        // 注册失败不回滚导入（网关内吞异常记日志），下次保存/导入/重启对账自愈。
        PocDataSyncService.ApplyCronScheduleGateway(_scheduleGateway, taskCode, taskCode, schedule.Type, schedule.CronExpression, enabled: true);
        actions.Add(existing is null ? $"TaskCreated:{taskCode}" : $"TaskReplaced:{taskCode}");
        return existing is null ? "Created" : "Replaced";
    }

    /// <summary>同步包静态校验核心：结构、映射、数据集契约、SQL 与调度逐项检查。</summary>
    /// <param name="package">待校验的同步包。</param>
    /// <returns>错误清单、警告清单和包摘要；结构不完整时摘要为 null。</returns>
    private static (List<PocDataSyncIssue> Errors, List<PocDataSyncIssue> Warnings, PocDataSyncPackageSummary? Summary)
        ValidatePackageCore(PocDataSyncPackage? package)
    {
        var errors = new List<PocDataSyncIssue>();
        var warnings = new List<PocDataSyncIssue>();
        if (package is null)
        {
            errors.Add(new PocDataSyncIssue("SYNC_PACKAGE_MISSING", "请求未携带同步包。"));
            return (errors, warnings, null);
        }

        // 第一阶段：结构与版本检查。结构不完整时后续检查无法安全进行，直接返回。
        if (!string.Equals(package.PackageKind, PocDataSyncPackageFormat.PackageKind, StringComparison.Ordinal))
        {
            errors.Add(new PocDataSyncIssue(
                "SYNC_PACKAGE_KIND_INVALID",
                $"同步包类型 '{package.PackageKind}' 不是受支持的同步包文件。"));
        }
        if (package.PackageVersion != PocDataSyncPackageFormat.PackageVersion)
        {
            errors.Add(new PocDataSyncIssue(
                "SYNC_PACKAGE_VERSION_UNSUPPORTED",
                $"同步包版本 {package.PackageVersion} 不受支持，当前仅支持版本 {PocDataSyncPackageFormat.PackageVersion}。"));
        }
        if (package.SourceDataset is null ||
            package.Mappings is null ||
            package.Mappings.Count == 0 ||
            string.IsNullOrWhiteSpace(package.TaskCode) ||
            string.IsNullOrWhiteSpace(package.TargetTable) ||
            string.IsNullOrWhiteSpace(package.ConflictPolicy))
        {
            errors.Add(new PocDataSyncIssue(
                "SYNC_PACKAGE_STRUCTURE_INVALID",
                "同步包缺少必需内容：taskCode / targetTable / conflictPolicy / sourceDataset / mappings。"));
            return (errors, warnings, null);
        }

        var dataset = package.SourceDataset;
        PocDatasetSchemaDocument schema;
        try
        {
            schema = dataset.FieldSchema.ToObject<PocDatasetSchemaDocument>()
                ?? throw new JsonException("fieldSchema 反序列化为空。");
        }
        catch (JsonException exception)
        {
            errors.Add(new PocDataSyncIssue(
                "SYNC_PACKAGE_STRUCTURE_INVALID",
                $"同步包数据集 fieldSchema 不是合法结构：{exception.Message}"));
            return (errors, warnings, null);
        }

        // 第二阶段：数据集契约与 SQL 只读校验，复用数据资产服务与只读校验器，不维护第二套规则。
        try
        {
            PocDataAssetService.ValidateDataset(ToDatasetDocument(dataset, package.SourceDataset.DataSourceCode, 0));
        }
        catch (PocDatasetConfigurationException exception)
        {
            errors.Add(new PocDataSyncIssue(exception.Code, exception.Message));
        }
        try
        {
            _ = PocReadOnlySqlValidator.Validate(
                dataset.QueryText ?? string.Empty,
                (schema.Variables ?? []).Select(item => item.Code),
                PocPreviewMode.Full);
        }
        catch (PocDatasetConfigurationException exception)
        {
            errors.Add(new PocDataSyncIssue(exception.Code, exception.Message));
        }
        if (!dataset.IsEnabled)
        {
            errors.Add(new PocDataSyncIssue(
                "SYNC_SOURCE_DATASET_DISABLED",
                "包内源数据集处于停用状态；导入同步任务要求启用数据集，请在开发机启用后重新导出。"));
        }

        // 第三阶段：映射编译校验（转换器白名单、参数完整性、表达式语法）。
        try
        {
            _ = PocDataSyncService.ValidateMappings(package.Mappings);
        }
        catch (PocDataSyncException exception)
        {
            errors.Add(new PocDataSyncIssue(exception.Code, exception.Message));
        }

        // 第四阶段：调度与冲突策略合法性。
        try
        {
            _ = ValidateSchedule(package.ScheduleType, package.ScheduleTime, package.IntervalMinutes, package.ScheduleCron);
        }
        catch (PocDataSyncException exception)
        {
            errors.Add(new PocDataSyncIssue(exception.Code, exception.Message));
        }
        var policy = package.ConflictPolicy.Trim();
        if (!policy.Equals("TruncateFirst", StringComparison.OrdinalIgnoreCase) &&
            !policy.Equals("Append", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new PocDataSyncIssue(
                "SYNC_CONFLICT_POLICY_INVALID",
                $"冲突策略 '{package.ConflictPolicy}' 无效，仅支持 TruncateFirst 或 Append。"));
        }

        // 第五阶段：非阻断警告——目标数据源类型提示非法由管理员在导入时选择真实数据源兜底。
        try
        {
            _ = PocDatabaseProvider.NormalizeType(package.TargetDataSourceType ?? string.Empty);
        }
        catch (PocDatasetConfigurationException)
        {
            warnings.Add(new PocDataSyncIssue(
                "SYNC_TARGET_TYPE_HINT_INVALID",
                $"包内目标数据源类型提示 '{package.TargetDataSourceType}' 非法，导入时请以实际数据源为准。"));
        }

        var summary = new PocDataSyncPackageSummary(
            package.TaskCode.Trim(),
            dataset.Code.Trim(),
            package.TargetTable.Trim(),
            package.Mappings.Count,
            package.TargetDataSourceType ?? string.Empty);
        return (errors, warnings, summary);
    }

    /// <summary>试跑/导入入口共用的包有效性断言：任一静态校验错误即拒绝并携带首条错误。</summary>
    /// <param name="package">待断言的同步包。</param>
    private async Task EnsurePackageValid(PocDataSyncPackage? package)
    {
        var (errors, _, _) = await Task.Run(() => ValidatePackageCore(package));
        if (errors.Count > 0)
        {
            throw new PocDataSyncException(
                "SYNC_PACKAGE_VALIDATION_FAILED",
                $"同步包校验未通过：{errors[0].Message}" +
                (errors.Count > 1 ? $"（共 {errors.Count} 项错误）" : string.Empty));
        }
    }

    /// <summary>解析并校验目标数据源：必须存在且已启用，返回完整实体供连接使用。</summary>
    /// <param name="targetDataSourceCode">目标数据源编码。</param>
    /// <returns>目标数据源实体。</returns>
    private async Task<PocDataSource> ResolveTargetDataSourceAsync(string? targetDataSourceCode)
    {
        if (string.IsNullOrWhiteSpace(targetDataSourceCode))
        {
            throw new PocDataSyncException(
                "DATA_SOURCE_NOT_FOUND",
                "请选择目标数据源。");
        }
        var target = await _dataAssets.GetDataSourceAsync(targetDataSourceCode.Trim())
            ?? throw new PocDataSyncException(
                "DATA_SOURCE_NOT_FOUND",
                $"不存在数据源 '{targetDataSourceCode.Trim()}'。");
        if (!target.IsEnabled)
        {
            throw new PocDataSyncException(
                "DATA_SOURCE_DISABLED",
                $"数据源 '{target.Code}' 已停用，请先启用后再试跑或导入。");
        }
        return target;
    }

    /// <summary>判断数据源类型与提示类型是否兼容；提示非法按不兼容处理。</summary>
    /// <param name="dataSource">数据源实体。</param>
    /// <param name="hintType">包内类型提示。</param>
    /// <returns>兼容返回 true。</returns>
    private static bool IsDataSourceTypeCompatible(PocDataSource dataSource, string? hintType)
    {
        try
        {
            return string.Equals(
                PocDatabaseProvider.NormalizeType(dataSource.DbType),
                PocDatabaseProvider.NormalizeType(hintType ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (PocDatasetConfigurationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 校验包内调度定义：类型白名单、Daily 的 HH:mm、Interval 的分钟区间与 Cron 的表达式解析。
    /// Cron 分支与保存链路共用 PocDataSyncService.ValidateCronExpression（同一 Furion 解析内核），
    /// 非 Cron 类型一律把表达式裁剪为 null（旧包无 scheduleCron 字段反序列化为 null，天然兼容）。
    /// </summary>
    /// <param name="scheduleType">调度类型。</param>
    /// <param name="scheduleTime">每日时间。</param>
    /// <param name="intervalMinutes">间隔分钟。</param>
    /// <param name="scheduleCron">Cron 表达式（仅 Cron 类型使用）。</param>
    /// <returns>归一化后的调度定义。</returns>
    private static PocSyncScheduleDefinition ValidateSchedule(
        string? scheduleType,
        string? scheduleTime,
        int intervalMinutes,
        string? scheduleCron)
    {
        var type = scheduleType?.Trim();

        // 第一阶段：按类型分发校验。每种类型只保留自己的调度字段，非法配置在导入前即被拒绝。
        switch (type?.ToLowerInvariant())
        {
            case "manual":
                return new PocSyncScheduleDefinition("Manual", null, 0);
            case "daily":
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
                return new PocSyncScheduleDefinition(
                    "Daily",
                    time.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                    0);
            case "interval":
                if (intervalMinutes is < 1 or > 525_600)
                {
                    throw new PocDataSyncException(
                        "SYNC_SCHEDULE_INVALID",
                        "间隔调度分钟数必须在 1 到 525600 之间。");
                }
                return new PocSyncScheduleDefinition("Interval", null, intervalMinutes);
            case "cron":
                var cron = scheduleCron?.Trim();
                if (string.IsNullOrWhiteSpace(cron))
                {
                    throw new PocDataSyncException(
                        "SYNC_SCHEDULE_INVALID",
                        "Cron 调度必须填写表达式（标准 5 段：分 时 日 月 周）。");
                }
                if (cron.Length > PocDataSyncService.ScheduleCronMaxLength)
                {
                    throw new PocDataSyncException(
                        "SYNC_SCHEDULE_INVALID",
                        $"Cron 表达式 '{cron}' 长度超过 {PocDataSyncService.ScheduleCronMaxLength} 个字符。");
                }

                // 第二阶段：表达式解析级校验，与保存链路共用同一 Furion 解析内核，非法表达式导入前拒绝。
                PocDataSyncService.ValidateCronExpression(cron);
                return new PocSyncScheduleDefinition("Cron", null, 0, cron);
            default:
                throw new PocDataSyncException(
                    "SYNC_SCHEDULE_INVALID",
                    $"调度类型 '{scheduleType}' 无效，仅支持 Manual、Daily、Interval 或 Cron。");
        }
    }

    /// <summary>把包内数据集节转换为数据资产服务可消费的文档；Revision 由调用方传入。</summary>
    /// <param name="dataset">包内数据集节。</param>
    /// <param name="dataSourceCode">数据集绑定的数据源编码。</param>
    /// <param name="expectedRevision">乐观并发期望修订号。</param>
    /// <returns>数据集保存文档。</returns>
    private static PocDatasetDocument ToDatasetDocument(
        PocDataSyncPackageDataset dataset,
        string dataSourceCode,
        int expectedRevision)
    {
        var schema = dataset.FieldSchema.ToObject<PocDatasetSchemaDocument>()
            ?? throw new PocDataSyncException(
                "SYNC_PACKAGE_STRUCTURE_INVALID",
                "同步包数据集 fieldSchema 不是合法结构。");
        return new PocDatasetDocument(
            dataset.Code,
            dataset.Name,
            dataSourceCode,
            dataset.QueryText,
            schema.Variables ?? [],
            schema.Fields ?? [],
            dataset.TimeoutSeconds,
            dataset.MaxRows,
            dataset.IsEnabled,
            expectedRevision);
    }

    /// <summary>
    /// 用包内容构建源数据集的临时执行计划：数据源用用户选择的目标数据源，
    /// 变量按包内契约解析绑定，与报表包试跑同构。
    /// </summary>
    /// <param name="package">同步包。</param>
    /// <param name="target">目标数据源实体。</param>
    /// <param name="parameters">变量样例取值。</param>
    /// <returns>执行计划。</returns>
    private static PocRuntimeExecutionPlan BuildPackagePlan(
        PocDataSyncPackage package,
        PocDataSource target,
        IReadOnlyDictionary<string, string?>? parameters)
    {
        // 第一阶段：把包内数据集契约物化为参数定义并解析变量取值，绑定语义与报表包试跑一致。
        var schema = ToDatasetDocument(package.SourceDataset, target.Code, 0);
        var parameterDefinitions = schema.Variables.Select(variable => new PocParameterDefinition(
            variable.Code,
            variable.DisplayName,
            "text",
            variable.SourceType,
            null,
            true,
            variable.DataType,
            Required: variable.Required,
            SourceCode: variable.SourceCode,
            SourceFieldCode: variable.SourceFieldCode)).ToArray();
        var resolved = PocParameterResolver.Resolve(
            parameterDefinitions,
            parameters ?? new Dictionary<string, string?>());

        // 第二阶段：组装 Full 模式的运行配置，查询文本与超时全部来自包内快照，连接用目标数据源。
        var definition = new PocReportDefinition(
            package.TaskCode.Trim(),
            "同步包试跑",
            "sync",
            new PocPreviewSettings(PocPreviewMode.Full, DryRunRowLimit, false),
            new PocExportSettings(
                PocDataExportMode.Large,
                NormalMaxRows: 20_000,
                NormalMaxCells: 0,
                NormalTimeoutSeconds: 30,
                LargeExportEnabled: true,
                RowsPerSheet: 1_000_000,
                FileRetentionHours: 24),
            parameterDefinitions,
            []);
        var configuration = new PocRuntimeReportConfiguration(
            definition,
            target.Code,
            PocDatabaseProvider.NormalizeType(target.DbType),
            target.ConnectionString,
            package.SourceDataset.QueryText,
            package.SourceDataset.TimeoutSeconds,
            package.SourceDataset.MaxRows,
            UsesFallbackData: false);
        return new PocRuntimeExecutionPlan(
            configuration,
            new PocExecuteRequest(1, parameters ?? new Dictionary<string, string?>()),
            resolved);
    }

    /// <summary>判断库内数据集与包内数据集的 queryText/fieldSchema 内容是否一致。</summary>
    /// <param name="existing">库内已存在的数据集实体。</param>
    /// <param name="packageDataset">包内数据集节。</param>
    /// <returns>查询文本与字段契约完全一致返回 true。</returns>
    private static bool IsDatasetContentIdentical(
        PocReportDataset existing,
        PocDataSyncPackageDataset packageDataset)
    {
        if (!string.Equals(
                (existing.QueryText ?? string.Empty).Trim(),
                (packageDataset.QueryText ?? string.Empty).Trim(),
                StringComparison.Ordinal))
        {
            return false;
        }

        // 比对前把包内 schema 经 ReadSchema 规范化，避免默认值差异造成误判。
        var existingSchema = JsonConvert.SerializeObject(
            PocDataAssetService.ReadSchema(existing),
            Formatting.None);
        var packageSchema = JsonConvert.SerializeObject(
            PocDataAssetService.ReadSchema(new PocReportDataset
            {
                Code = packageDataset.Code ?? string.Empty,
                QueryText = packageDataset.QueryText ?? string.Empty,
                FieldSchemaJson = JsonConvert.SerializeObject(packageDataset.FieldSchema) ?? "null"
            }),
            Formatting.None);
        return string.Equals(existingSchema, packageSchema, StringComparison.Ordinal);
    }

    /// <summary>归一化导入模式：缺省按 Create 处理，非 Create/Replace 文本拒绝导入。</summary>
    /// <param name="mode">请求中的模式文本。</param>
    /// <returns>归一化后的导入模式。</returns>
    private static PocDataSyncImportMode NormalizeImportMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) ||
            mode.Trim().Equals("Create", StringComparison.OrdinalIgnoreCase))
        {
            return PocDataSyncImportMode.Create;
        }
        if (mode.Trim().Equals("Replace", StringComparison.OrdinalIgnoreCase))
        {
            return PocDataSyncImportMode.Replace;
        }
        throw new PocDataSyncException(
            "SYNC_PACKAGE_MODE_INVALID",
            $"导入模式 '{mode}' 无效，仅支持 Create 或 Replace。");
    }

    /// <summary>把一次导入尝试写入审计 JSON（文件名含任务编码与 ok/failed），写失败不影响导入结果。</summary>
    /// <param name="request">原始导入请求。</param>
    /// <param name="taskCode">包内任务编码，缺失时可能为 null。</param>
    /// <param name="mode">导入模式名。</param>
    /// <param name="actions">本次导入已执行的动作序列。</param>
    /// <param name="ok">导入是否成功。</param>
    /// <param name="error">失败时的错误消息。</param>
    private async Task WriteImportAuditAsync(
        PocDataSyncImportRequest request,
        string? taskCode,
        string mode,
        IReadOnlyList<string> actions,
        bool ok,
        string? error)
    {
        try
        {
            Directory.CreateDirectory(AuditDirectory);
            var safeCode = taskCode ?? "unknown";
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                safeCode = safeCode.Replace(invalidCharacter, '_');
            }
            var fileName =
                $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{safeCode}-{(ok ? "ok" : "failed")}.json";
            var payload = new
            {
                timestamp = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                taskCode,
                mode,
                targetDataSourceCode = request.TargetDataSourceCode,
                actions,
                packageSha256 = PocDataSyncPackageFormat.ComputeSha256(request.Package),
                dryRunPerformed = request.DryRunPerformed,
                result = ok ? "ok" : "failed",
                errors = error is null ? Array.Empty<string>() : new[] { error }
            };
            await File.WriteAllTextAsync(
                Path.Combine(AuditDirectory, fileName),
                JsonConvert.SerializeObject(payload, Formatting.Indented));
        }
        catch
        {
            // 审计属于尽力而为的旁路记录：目录权限或磁盘故障不应改写导入本身的成败。
        }
    }
}
