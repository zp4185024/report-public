using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Globalization;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 报表包应用服务：负责开发机侧的导出组装、导入前的结构化校验、真实数据源上的受限试跑，
/// 以及把包内容落库的导入编排。安全边界：包内绝不携带连接串/密码；导入前服务端重跑全部校验；
/// 落库尽量编排现有保存链路（数据集/报表配置/模板服务），校验始终复用线上同一套校验器。
/// </summary>
public sealed class PocReportPackageService : ITransient
{
    private const int DryRunRowLimit = 50;
    private const int DryRunTimeoutLimitSeconds = 30;
    private const int SnapshotLengthLimit = 4 * 1024 * 1024;

    private readonly PocReportConfigurationStore _configurationStore;
    private readonly PocDataAssetStore _dataAssets;
    private readonly PocDataAssetService _dataAssetService;
    private readonly PocReportConfigurationService _configurationService;
    private readonly PocTemplateService _templateService;
    private readonly PocTemplateStore _templateStore;
    private readonly PocExternalParameterSourceService? _externalParameters;

    /// <summary>
    /// 初始化 PocReportPackageService 实例并保存编排所需的存储与应用服务依赖。
    /// </summary>
    /// <param name="configurationStore">报表配置存储，用于读取与删除报表聚合。</param>
    /// <param name="dataAssets">数据资产存储，用于数据集/数据源查询与引用探测。</param>
    /// <param name="dataAssetService">数据资产应用服务，导入数据集复用其保存校验。</param>
    /// <param name="configurationService">报表配置服务，导入报表复用其保存/删除链路。</param>
    /// <param name="templateService">模板服务，导入模板复用其"服务端重扫绑定"保存语义。</param>
    /// <param name="templateStore">模板存储，用于导出时读取已保存模板。</param>
    /// <param name="externalParameters">可选的外部参数源服务，试跑时解析 External 参数取值。</param>
    public PocReportPackageService(
        PocReportConfigurationStore configurationStore,
        PocDataAssetStore dataAssets,
        PocDataAssetService dataAssetService,
        PocReportConfigurationService configurationService,
        PocTemplateService templateService,
        PocTemplateStore templateStore,
        PocExternalParameterSourceService? externalParameters = null)
    {
        _configurationStore = configurationStore;
        _dataAssets = dataAssets;
        _dataAssetService = dataAssetService;
        _configurationService = configurationService;
        _templateService = templateService;
        _templateStore = templateStore;
        _externalParameters = externalParameters;
    }

    /// <summary>
    /// 导入审计文件目录。默认沿用 JNPF 迁移的 output/&lt;功能&gt;/ 约定写在进程当前目录下，
    /// 测试可以改写该属性把审计重定向到临时目录，避免污染仓库工作区。
    /// </summary>
    public string AuditDirectory { get; set; } = Path.Combine(
        Directory.GetCurrentDirectory(),
        "output",
        "report-packages");

    /// <summary>
    /// 聚合读取报表、参数、模板和数据集并组装成导出包。
    /// 分享保护口令摘要随 PreviewSettingsJson 一起被剥离（反序列化为公开预览设置时自动丢弃），
    /// 包内不出现任何连接串、密码、数据库 Id 或审计时间戳。
    /// </summary>
    /// <param name="reportCode">待导出的报表编码。</param>
    /// <returns>可直接序列化为 JSON 文件的完整报表包。</returns>
    /// <exception cref="KeyNotFoundException">报表编码不存在时抛出，由 API 层映射为 404。</exception>
    public async Task<PocReportPackage> ExportAsync(string reportCode)
    {
        // 第一阶段：读取报表聚合与已保存模板，任何一方缺失都不允许导出（模板缺失给稳定业务错误）。
        if (string.IsNullOrWhiteSpace(reportCode))
        {
            throw new KeyNotFoundException("报表编码不能为空。");
        }

        var normalizedCode = reportCode.Trim();
        var stored = await _configurationStore.GetAsync(normalizedCode)
            ?? throw new KeyNotFoundException($"不存在报表 '{normalizedCode}'。");
        var template = await _templateStore.GetAsync(normalizedCode)
            ?? throw new PocReportPackageException(
                "PACKAGE_TEMPLATE_REQUIRED",
                $"报表 '{normalizedCode}' 尚未保存模板，请先在编辑器保存模板再导出报表包。");

        // 第二阶段：组装包结构。PreviewSettings 反序列化为公开预览设置时自动丢弃口令摘要，
        // 数据集字段契约经 ReadSchema 规范化，模板快照与绑定保持对象形态。
        return new PocReportPackage(
            PocReportPackageFormat.PackageKind,
            PocReportPackageFormat.PackageVersion,
            DateTimeOffset.UtcNow,
            stored.Dataset.DataSourceCode,
            PocDatabaseProvider.NormalizeType(stored.DataSource.DbType),
            new PocReportPackageReport(
                stored.Report.Code,
                stored.Report.Name,
                stored.Report.SheetName,
                stored.Report.DatasetCode,
                Deserialize<PocPreviewSettings>(stored.Report.PreviewSettingsJson),
                Deserialize<PocExportSettings>(stored.Report.ExportSettingsJson),
                DeserializeList<PocFieldPresentationDefinition>(stored.Report.FieldPresentationsJson),
                stored.Report.IsEnabled),
            stored.Parameters.Select(parameter => new PocReportPackageParameter(
                parameter.Code,
                parameter.DisplayName,
                parameter.DataType,
                parameter.ControlType,
                parameter.SourceType,
                parameter.SourceCode,
                parameter.SourceFieldCode,
                parameter.UrlKey,
                parameter.DefaultExpression,
                parameter.FixedValue,
                DeserializeList<PocValueOption>(parameter.OptionsJson),
                parameter.IsVisible,
                parameter.IsReadOnly,
                parameter.IsRequired,
                parameter.AllowUrlPrefill,
                parameter.DisplayOrder)).ToArray(),
            new PocReportPackageTemplate(
                JObject.Parse(template.SnapshotJson),
                Deserialize<PocTemplateBinding>(template.BindingJson),
                template.Revision),
            new PocReportPackageDataset(
                stored.Dataset.Code,
                stored.Dataset.Name,
                stored.Dataset.QueryText,
                PocDataAssetService.ReadSchema(stored.Dataset),
                stored.Dataset.TimeoutSeconds,
                stored.Dataset.MaxRows,
                stored.Dataset.IsEnabled));
    }

    /// <summary>
    /// 只读校验报表包：结构、编码、参数、SQL、变量绑定、模板一致性逐项检查并探测目标库冲突。
    /// 不写库、不执行 SQL；errors 非空表示包不可导入。
    /// </summary>
    /// <param name="package">前端上传的整份报表包。</param>
    /// <returns>结构化校验报告，含错误、警告、包摘要和与当前库的冲突信息。</returns>
    public async Task<PocReportPackageValidateResult> ValidateAsync(PocReportPackage? package)
    {
        var (errors, warnings, summary) = ValidatePackageCore(package);
        if (summary is null || package is null)
        {
            return new PocReportPackageValidateResult(
                errors.Count == 0,
                errors,
                warnings,
                null,
                null);
        }

        // 冲突探测只读查询当前库；datasetIdentical/datasetReferencedByOther 在数据集不存在时保持 null。
        var datasetCode = package.Dataset.Code.Trim();
        var reportCode = package.Report.Code.Trim();
        var existingDataset = await _dataAssets.GetDatasetAsync(datasetCode);
        var referencedByOther = existingDataset is null
            ? (bool?)null
            : (await _dataAssets.ListDatasetReferencesAsync(datasetCode))
                .Any(item => !item.Code.Equals(reportCode, StringComparison.OrdinalIgnoreCase));
        var collisions = new PocReportPackageCollisions(
            await _configurationStore.ReportExistsAsync(reportCode),
            existingDataset is not null,
            existingDataset is null ? null : IsDatasetContentIdentical(existingDataset, package.Dataset),
            referencedByOther);
        return new PocReportPackageValidateResult(errors.Count == 0, errors, warnings, collisions, summary);
    }

    /// <summary>
    /// 在用户选定的目标数据源上对包内数据集做受限试跑：SQL 先重过只读校验器，
    /// 参数按现有解析规则归一（必填缺值抛出明确错误），随后以最多 50 行、
    /// 最长 min(timeoutSeconds,30) 秒的硬限制执行，并比对实际列与包内字段契约。
    /// </summary>
    /// <param name="request">试跑请求：包 + 目标数据源 + 参数样例值。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>列编码、样例行、耗时、截断标记和字段契约差异清单。</returns>
    public async Task<PocReportPackageDryRunResult> DryRunAsync(
        PocReportPackageDryRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePackageValid(request.Package);

        var package = request.Package!;
        var target = await ResolveTargetDataSourceAsync(request.TargetDataSourceCode);
        if (!IsSourceDataSourceTypeCompatible(target, package, out var sourceType))
        {
            throw new PocReportPackageException(
                "PACKAGE_DATA_SOURCE_TYPE_MISMATCH",
                $"目标数据源类型为 {PocDatabaseProvider.NormalizeType(target.DbType)}，" +
                $"与报表包导出时的 {sourceType} 不一致，不能直接试跑。");
        }

        // 参数解析复用现有规则：Fixed/System/External 拒绝客户端覆盖，必填缺值抛 PARAM_REQUIRED。
        var parameters = package.Parameters!.Select(ToParameterDefinition).ToArray();
        var externalValues = _externalParameters is null
            ? new Dictionary<string, string?>()
            : await _externalParameters.ResolveAsync(parameters, null, cancellationToken);
        var resolved = PocParameterResolver.Resolve(parameters, request.ParameterValues, externalValues);

        var definition = BuildDefinition(package.Report, parameters, package.Dataset.FieldSchema.Fields ?? []);
        var configuration = new PocRuntimeReportConfiguration(
            definition,
            target.Code,
            PocDatabaseProvider.NormalizeType(target.DbType),
            target.ConnectionString,
            package.Dataset.QueryText.Trim(),
            Math.Min(package.Dataset.TimeoutSeconds, DryRunTimeoutLimitSeconds),
            DryRunRowLimit,
            UsesFallbackData: false);
        var plan = new PocRuntimeExecutionPlan(
            configuration,
            new PocExecuteRequest(1, request.ParameterValues ?? new Dictionary<string, string?>()),
            resolved);

        var stopwatch = Stopwatch.StartNew();
        var execution = await PocSqlDatasetExecutor.ExecutePreviewAsync(plan, DryRunRowLimit, cancellationToken);
        stopwatch.Stop();

        // 字段契约比对属警告性质：差集原样返回给前端展示，不阻断导入。
        var mismatches = CompareFieldSchema(execution.Columns, package.Dataset.FieldSchema.Fields ?? []);
        return new PocReportPackageDryRunResult(
            execution.Columns,
            execution.Rows,
            stopwatch.ElapsedMilliseconds,
            execution.RowCountCapped,
            mismatches.Count == 0,
            mismatches);
    }

    /// <summary>
    /// 导入报表包：服务端重跑全部校验后按冲突策略落库。数据集按"新建/复用一致/替换"决策，
    /// 报表按 Create/Replace 模式走现有保存与删除链路，模板经服务端重扫绑定保存；
    /// 每次尝试（成功/失败）都写一条审计 JSON。
    /// </summary>
    /// <param name="request">导入请求：包 + 目标数据源 + 模式与数据集替换开关。</param>
    /// <returns>导入结果：报表编码、数据集动作、报表动作与警告清单。</returns>
    public async Task<PocReportPackageImportResult> ImportAsync(PocReportPackageImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reportCode = request.Package?.Report?.Code?.Trim();
        var actions = new List<string>();
        // 模式名先占位，归一化在审计范围内执行：非法模式同样属于"失败的导入尝试"，必须留痕。
        var modeName = "Invalid";
        try
        {
            var mode = NormalizeImportMode(request.Mode);
            modeName = mode.ToString();
            var result = await ImportCoreAsync(request, mode, actions);
            await WriteImportAuditAsync(request, reportCode, modeName, actions, ok: true, error: null);
            return result;
        }
        catch (Exception exception)
        {
            // 失败也要留审计痕迹；审计写失败本身不改变导入的业务结果。
            await WriteImportAuditAsync(request, reportCode, modeName, actions, ok: false, error: exception.Message);
            throw;
        }
    }

    /// <summary>
    /// 导入核心流程：校验、目标数据源解析、数据集决策、报表删除/新建、配置与模板落库及终态检查。
    /// 与 <see cref="ImportAsync"/> 分离是为了让审计写入在成功与失败两条路径上都能拿到动作序列。
    /// 数据集与报表是两个独立资源，各走各的保存事务（与本项目"资源保存之间不共用事务"的设计一致）；
    /// 若报表保存失败，先前新建/替换的数据集会保留，由管理员按审计文件决定保留或回退。
    /// </summary>
    /// <param name="request">原始导入请求。</param>
    /// <param name="mode">已归一化的导入模式。</param>
    /// <param name="actions">动作序列收集器，随导入推进追加"数据集新建/复用/替换、报表新建/替换"。</param>
    /// <returns>导入结果文档。</returns>
    private async Task<PocReportPackageImportResult> ImportCoreAsync(
        PocReportPackageImportRequest request,
        PocReportPackageImportMode mode,
        List<string> actions)
    {
        // 第一阶段：服务端重跑全部校验（不信任客户端的 validate 结果）。
        EnsurePackageValid(request.Package);
        var package = request.Package!;
        var reportCode = package.Report.Code.Trim();
        var datasetCode = package.Dataset.Code.Trim();

        // 第二阶段：解析目标数据源并按需记录类型差异警告（类型不一致仅在试跑时硬拒绝）。
        var target = await ResolveTargetDataSourceAsync(request.TargetDataSourceCode);
        var warnings = new List<string>
        {
            "分享保护口令不会随包导入；如需启用请在导入后重新设置。"
        };
        if (!IsSourceDataSourceTypeCompatible(target, package, out var sourceType))
        {
            warnings.Add(
                $"目标数据源类型 {PocDatabaseProvider.NormalizeType(target.DbType)} 与包导出时的 {sourceType} 不一致，" +
                "请先完成试跑确认查询兼容。");
        }

        // 第三阶段：数据集决策——新建 / 内容一致复用 / 未被他人引用时按开关替换。
        var datasetAction = await ResolveDatasetActionAsync(package, target, request.ReplaceDataset, actions);

        // 第四阶段：报表决策——Create 撞码拒绝；Replace 先走现有删除事务再新建。
        var reportExists = await _configurationStore.ReportExistsAsync(reportCode);
        if (mode == PocReportPackageImportMode.Create && reportExists)
        {
            throw new PocReportAlreadyExistsException(reportCode);
        }
        var reportAction = "Created";
        if (reportExists)
        {
            await _configurationService.DeleteReportAsync(reportCode);
            reportAction = "Replaced";
            actions.Add($"ReportReplaced:{reportCode}");
        }
        else
        {
            actions.Add($"ReportCreated:{reportCode}");
        }

        // 第五阶段：保存报表配置与模板。两者都复用现有服务，校验与绑定重扫由服务内部完成。
        var parameters = package.Parameters!.Select(ToParameterDefinition).ToArray();
        await _configurationService.SaveAsync(reportCode, BuildSaveRequest(package.Report, parameters));
        await _templateService.SaveAsync(
            reportCode,
            new PocSaveTemplateRequest(package.Template.Snapshot, package.Template.Binding, 0));

        // 第六阶段：终态检查——导入后必须同时处于"已保存配置 + 已保存模板"状态。
        var stored = await _configurationStore.GetAsync(reportCode);
        var template = await _templateStore.GetAsync(reportCode);
        if (stored?.Report.Revision is < 1 || template?.Revision is < 1)
        {
            throw new PocReportPackageException(
                "PACKAGE_IMPORT_INCOMPLETE",
                $"报表 '{reportCode}' 导入后未达到已保存配置与模板状态，请联系管理员核查。");
        }

        return new PocReportPackageImportResult(reportCode, datasetAction, reportAction, warnings);
    }

    /// <summary>
    /// 按冲突策略决定数据集落库动作：不存在则新建（绑定目标数据源）、内容一致则原样复用；
    /// 内容不一致时被其他报表引用则拒绝（DATASET_IN_USE），未启用替换开关也拒绝，
    /// 其余情况按目标数据源替换。所有分支动作都会写入动作序列供审计。
    /// </summary>
    /// <param name="package">已通过校验的报表包。</param>
    /// <param name="target">用户选定的目标数据源实体。</param>
    /// <param name="replaceDataset">是否允许替换内容不一致的同编码数据集。</param>
    /// <param name="actions">动作序列收集器。</param>
    /// <returns>数据集动作名：Created / Reused / Replaced。</returns>
    private async Task<string> ResolveDatasetActionAsync(
        PocReportPackage package,
        PocDataSource target,
        bool replaceDataset,
        List<string> actions)
    {
        // 第一阶段：探测库内同编码数据集；不存在时按目标数据源新建并记录动作。
        var datasetCode = package.Dataset.Code.Trim();
        var existing = await _dataAssets.GetDatasetAsync(datasetCode);
        if (existing is null)
        {
            await _dataAssetService.SaveDatasetAsync(
                datasetCode,
                ToDatasetDocument(package.Dataset, target.Code.Trim()));
            actions.Add($"DatasetCreated:{datasetCode}");
            return "Created";
        }

        // 第二阶段：内容一致则复用不动，绝不因导入改写其他报表正在使用的数据集定义。
        if (IsDatasetContentIdentical(existing, package.Dataset))
        {
            actions.Add($"DatasetReused:{datasetCode}");
            return "Reused";
        }

        // 第三阶段：内容不一致时先检查是否被其他报表引用，再按替换开关决定替换或拒绝。
        var referencedByOther = (await _dataAssets.ListDatasetReferencesAsync(datasetCode))
            .Where(item => !item.Code.Equals(
                package.Report.Code.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (referencedByOther.Length > 0)
        {
            throw new PocReportPackageException(
                "DATASET_IN_USE",
                $"数据集 '{datasetCode}' 已存在且内容不一致，并被其他报表引用：" +
                $"{PocDeleteGuard.FormatReferenceSummary(referencedByOther.Select(item => item.Name).ToArray())}。" +
                "请先调整这些报表的数据集绑定，或修改包内数据集编码。");
        }

        if (!replaceDataset)
        {
            throw new PocReportPackageException(
                "DATASET_CONTENT_MISMATCH",
                $"数据集 '{datasetCode}' 已存在且内容不一致；确认覆盖请选择“替换数据集”后重试。");
        }

        await _dataAssetService.SaveDatasetAsync(
            datasetCode,
            ToDatasetDocument(package.Dataset, target.Code.Trim(), existing.Revision));
        actions.Add($"DatasetReplaced:{datasetCode}");
        return "Replaced";
    }

    /// <summary>
    /// 报表包静态校验核心：结构、编码、参数、SQL、变量绑定与模板一致性。
    /// 校验/试跑/导入三个入口共用，保证导入前规则与独立 validate 完全一致；
    /// 各节检查复用现有保存链路的校验器（ValidateDataset/ValidateRequest/ValidateDatasetVariableBindings/
    /// PocReadOnlySqlValidator/PocTemplateValidator），不维护第二套白名单。
    /// </summary>
    /// <param name="package">待校验的报表包。</param>
    /// <returns>错误清单、警告清单和包摘要；结构不完整时摘要为 null。</returns>
    private static (List<PocReportPackageIssue> Errors, List<PocReportPackageIssue> Warnings, PocReportPackageSummary? Summary)
        ValidatePackageCore(PocReportPackage? package)
    {
        var errors = new List<PocReportPackageIssue>();
        var warnings = new List<PocReportPackageIssue>();
        if (package is null)
        {
            errors.Add(new PocReportPackageIssue("PACKAGE_MISSING", "请求未携带报表包。"));
            return (errors, warnings, null);
        }

        // 第一阶段：结构与版本检查。结构不完整时后续检查无法安全进行，直接返回。
        if (!string.Equals(package.PackageKind, PocReportPackageFormat.PackageKind, StringComparison.Ordinal))
        {
            errors.Add(new PocReportPackageIssue(
                "PACKAGE_KIND_INVALID",
                $"报表包类型 '{package.PackageKind}' 不是受支持的报表包文件。"));
        }
        if (package.PackageVersion != PocReportPackageFormat.PackageVersion)
        {
            errors.Add(new PocReportPackageIssue(
                "PACKAGE_VERSION_UNSUPPORTED",
                $"报表包版本 {package.PackageVersion} 不受支持，当前仅支持版本 {PocReportPackageFormat.PackageVersion}。"));
        }
        if (package.Report is null ||
            package.Parameters is null ||
            package.Template is null ||
            package.Dataset is null)
        {
            errors.Add(new PocReportPackageIssue(
                "PACKAGE_STRUCTURE_INVALID",
                "报表包缺少必需节：report / parameters / template / dataset。"));
            return (errors, warnings, null);
        }
        if (package.Report.PreviewSettings is null || package.Report.ExportSettings is null)
        {
            errors.Add(new PocReportPackageIssue(
                "PACKAGE_STRUCTURE_INVALID",
                "报表节缺少 previewSettings 或 exportSettings。"));
            return (errors, warnings, null);
        }
        if (package.Template.Snapshot is null || package.Template.Binding is null)
        {
            errors.Add(new PocReportPackageIssue(
                "PACKAGE_STRUCTURE_INVALID",
                "模板节缺少 snapshot 或 binding。"));
            return (errors, warnings, null);
        }
        if (package.Dataset.FieldSchema is null)
        {
            errors.Add(new PocReportPackageIssue(
                "PACKAGE_STRUCTURE_INVALID",
                "数据集节缺少 fieldSchema。"));
            return (errors, warnings, null);
        }

        var report = package.Report;
        var dataset = package.Dataset;
        var parameters = package.Parameters.Select(ToParameterDefinition).ToArray();

        // 第二阶段：数据集与报表/参数定义检查，直接调用现有保存链路的校验器。
        try
        {
            PocDataAssetService.ValidateDataset(ToDatasetDocument(dataset, package.SourceDataSourceCode));
        }
        catch (PocDatasetConfigurationException exception)
        {
            errors.Add(new PocReportPackageIssue(exception.Code, exception.Message));
        }
        try
        {
            PocReportConfigurationService.ValidateRequest(BuildSaveRequest(report, parameters));
        }
        catch (PocDatasetConfigurationException exception)
        {
            errors.Add(new PocReportPackageIssue(exception.Code, exception.Message));
        }

        // 第三阶段：变量绑定检查——每个数据集变量必须有同名同类型报表参数承接。
        try
        {
            PocReportConfigurationService.ValidateDatasetVariableBindings(
                dataset.FieldSchema.Variables ?? [],
                parameters);
        }
        catch (PocDatasetConfigurationException exception)
        {
            errors.Add(new PocReportPackageIssue(exception.Code, exception.Message));
        }

        // 第四阶段：SQL 按包内预览模式重过只读校验器（含 @变量必须已定义、分页需 ORDER BY）。
        try
        {
            _ = PocReadOnlySqlValidator.Validate(
                dataset.QueryText ?? string.Empty,
                (dataset.FieldSchema.Variables ?? []).Select(item => item.Code),
                report.PreviewSettings.Mode);
        }
        catch (PocDatasetConfigurationException exception)
        {
            errors.Add(new PocReportPackageIssue(exception.Code, exception.Message));
        }
        if (!dataset.IsEnabled)
        {
            errors.Add(new PocReportPackageIssue(
                "DATASET_DISABLED",
                "包内数据集处于停用状态；导入报表要求启用数据集，请在开发机启用后重新导出。"));
        }

        // 第五阶段：模板检查——快照体积上限、按包定义重扫快照并比对规范绑定。
        var snapshotJson = package.Template.Snapshot.ToString(Formatting.None);
        if (snapshotJson.Length > SnapshotLengthLimit)
        {
            errors.Add(new PocReportPackageIssue(
                "TEMPLATE_SNAPSHOT_TOO_LARGE",
                "模板快照超过 4 MiB 限制。"));
        }
        else
        {
            try
            {
                var definition = BuildDefinition(report, parameters, dataset.FieldSchema.Fields ?? []);
                var canonicalBinding = PocTemplateValidator.Scan(snapshotJson, definition);
                if (!string.Equals(
                        JsonConvert.SerializeObject(canonicalBinding, Formatting.None),
                        JsonConvert.SerializeObject(package.Template.Binding, Formatting.None),
                        StringComparison.Ordinal))
                {
                    errors.Add(new PocReportPackageIssue(
                        "TEMPLATE_BINDING_MISMATCH",
                        "包内模板绑定与按快照重扫的规范绑定不一致，可能是包文件被改动或版本过旧。"));
                }
            }
            catch (Exception exception) when (exception is PocTemplateValidationException or JsonException)
            {
                errors.Add(new PocReportPackageIssue("TEMPLATE_VALIDATION_FAILED", exception.Message));
            }
        }

        // 第六阶段：非阻断警告——外部参数源依赖需要在目标环境人工确认。
        var externalSourceCodes = package.Parameters
            .Where(item => item.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SourceCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (externalSourceCodes.Length > 0)
        {
            warnings.Add(new PocReportPackageIssue(
                "PACKAGE_EXTERNAL_PARAMETERS_REQUIRED",
                $"包内参数依赖外部参数源：{string.Join("、", externalSourceCodes)}；目标环境必须已配置同名外部参数源。"));
        }

        var summary = new PocReportPackageSummary(
            report.Code,
            report.Name,
            dataset.Code,
            package.Parameters.Count,
            dataset.FieldSchema.Fields?.Count ?? 0,
            package.SourceDataSourceType);
        return (errors, warnings, summary);
    }

    /// <summary>
    /// 试跑/导入入口共用的包有效性断言：静态校验任一错误都以 PocReportPackageException 拒绝，
    /// 消息携带首条错误与错误总数，便于向导直接展示。
    /// </summary>
    /// <param name="package">待断言的报表包。</param>
    private static void EnsurePackageValid(PocReportPackage? package)
    {
        var (errors, _, _) = ValidatePackageCore(package);
        if (errors.Count > 0)
        {
            throw new PocReportPackageException(
                "PACKAGE_VALIDATION_FAILED",
                $"报表包校验未通过：{errors[0].Message}" +
                (errors.Count > 1 ? $"（共 {errors.Count} 项错误）" : string.Empty));
        }
    }

    /// <summary>
    /// 解析并校验试跑/导入的目标数据源：必须存在且已启用，返回完整实体供连接使用。
    /// 错误沿用现有数据资产错误码（DATA_SOURCE_NOT_FOUND / DATA_SOURCE_DISABLED）。
    /// </summary>
    /// <param name="targetDataSourceCode">用户选择的目标数据源编码。</param>
    /// <returns>目标数据源实体。</returns>
    private async Task<PocDataSource> ResolveTargetDataSourceAsync(string? targetDataSourceCode)
    {
        if (string.IsNullOrWhiteSpace(targetDataSourceCode))
        {
            throw new PocDatasetConfigurationException(
                "DATA_SOURCE_NOT_FOUND",
                "请选择目标数据源。");
        }

        var target = await _dataAssets.GetDataSourceAsync(targetDataSourceCode.Trim())
            ?? throw new PocDatasetConfigurationException(
                "DATA_SOURCE_NOT_FOUND",
                $"不存在数据源 '{targetDataSourceCode.Trim()}'。");
        if (!target.IsEnabled)
        {
            throw new PocDatasetConfigurationException(
                "DATA_SOURCE_DISABLED",
                $"数据源 '{target.Code}' 未启用，请先启用后再试。");
        }
        return target;
    }

    /// <summary>
    /// 判断目标数据源类型是否与包导出时的数据源类型一致；包内类型文本非法时按不一致处理，
    /// 由调用方决定是拒绝（试跑）还是给出警告（导入）。
    /// </summary>
    /// <param name="target">目标数据源实体。</param>
    /// <param name="package">报表包。</param>
    /// <param name="sourceType">输出：解析后的包内数据源类型（非法时为原始文本）。</param>
    /// <returns>类型一致返回 true。</returns>
    private static bool IsSourceDataSourceTypeCompatible(
        PocDataSource target,
        PocReportPackage package,
        out string sourceType)
    {
        sourceType = package.SourceDataSourceType ?? string.Empty;
        try
        {
            sourceType = PocDatabaseProvider.NormalizeType(sourceType);
        }
        catch (PocDatasetConfigurationException)
        {
            return false;
        }
        return string.Equals(
            PocDatabaseProvider.NormalizeType(target.DbType),
            sourceType,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把试跑实际返回的列集合与包内字段契约做差集比对；差异属警告性质，
    /// 返回"缺少字段/多余字段"清单供前端提示，不参与导入阻断。
    /// </summary>
    /// <param name="actualColumns">目标库实际返回的列编码。</param>
    /// <param name="fields">包内数据集字段契约。</param>
    /// <returns>差异描述清单；空列表表示完全匹配。</returns>
    private static List<string> CompareFieldSchema(
        IReadOnlyList<string> actualColumns,
        IReadOnlyList<PocDatasetFieldDefinition> fields)
    {
        var mismatches = new List<string>();
        var actual = new HashSet<string>(actualColumns, StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            expected.Add(field.Code);
            if (!actual.Contains(field.Code))
            {
                mismatches.Add($"缺少字段 '{field.Code}'");
            }
        }
        foreach (var column in actualColumns.Where(column => !expected.Contains(column)))
        {
            mismatches.Add($"多余字段 '{column}'");
        }
        return mismatches;
    }

    /// <summary>
    /// 判断库内数据集与包内数据集的 queryText/fieldSchema 内容是否一致。
    /// 比对前把包内 schema 经过与库内读取相同的规范化（ReadSchema），避免默认值差异造成误判；
    /// 名称、超时、行数上限与启用状态差异不算内容不一致。
    /// </summary>
    /// <param name="existing">库内已存在的数据集实体。</param>
    /// <param name="packageDataset">包内数据集节。</param>
    /// <returns>查询文本与字段契约完全一致返回 true。</returns>
    private static bool IsDatasetContentIdentical(
        PocReportDataset existing,
        PocReportPackageDataset packageDataset)
    {
        if (!string.Equals(
                (existing.QueryText ?? string.Empty).Trim(),
                (packageDataset.QueryText ?? string.Empty).Trim(),
                StringComparison.Ordinal))
        {
            return false;
        }

        var existingSchema = JsonConvert.SerializeObject(
            PocDataAssetService.ReadSchema(existing),
            Formatting.None);
        var packageSchema = JsonConvert.SerializeObject(
            PocDataAssetService.ReadSchema(new PocReportDataset
            {
                Code = packageDataset.Code,
                QueryText = packageDataset.QueryText,
                FieldSchemaJson = JsonConvert.SerializeObject(packageDataset.FieldSchema)
            }),
            Formatting.None);
        return string.Equals(existingSchema, packageSchema, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把包内报表节与参数列表转换为现有保存链路的请求对象，使 ValidateRequest 与
    /// SaveAsync 消费同一形态；Revision 固定为 0，表示首次落库。
    /// </summary>
    /// <param name="report">包内报表节。</param>
    /// <param name="parameters">转换后的参数定义。</param>
    /// <returns>可直接交给 PocReportConfigurationService 的保存请求。</returns>
    private static PocSaveReportConfigurationRequest BuildSaveRequest(
        PocReportPackageReport report,
        IReadOnlyList<PocParameterDefinition> parameters) =>
        new(
            report.Name,
            report.SheetName,
            report.PreviewSettings,
            report.ExportSettings,
            report.DatasetCode,
            parameters,
            0,
            report.FieldPresentations);

    /// <summary>
    /// 用包内容构建引擎可消费的报表定义，供模板重扫与试跑执行计划使用；
    /// 字段允许完全来自包内数据集字段契约，与线上读取路径保持同构。
    /// </summary>
    /// <param name="report">包内报表节。</param>
    /// <param name="parameters">转换后的参数定义。</param>
    /// <param name="fields">包内数据集返回字段契约。</param>
    /// <returns>引擎层报表定义。</returns>
    private static PocReportDefinition BuildDefinition(
        PocReportPackageReport report,
        IReadOnlyList<PocParameterDefinition> parameters,
        IReadOnlyList<PocDatasetFieldDefinition> fields) =>
        new(
            report.Code,
            report.Name,
            report.SheetName,
            report.PreviewSettings,
            report.ExportSettings,
            parameters,
            fields,
            report.FieldPresentations);

    /// <summary>
    /// 把包内数据集节转换为现有数据集保存服务可消费的文档。
    /// DataSourceCode 由调用方指定（校验用包源编码占位，落库用目标数据源编码），
    /// Revision 传期望修订号：新建为 0，替换为库内当前值。
    /// </summary>
    /// <param name="dataset">包内数据集节。</param>
    /// <param name="dataSourceCode">数据集绑定的数据源编码。</param>
    /// <param name="expectedRevision">乐观并发期望修订号，默认 0 表示新建。</param>
    /// <returns>数据集保存文档。</returns>
    private static PocDatasetDocument ToDatasetDocument(
        PocReportPackageDataset dataset,
        string dataSourceCode,
        int expectedRevision = 0) =>
        new(
            dataset.Code,
            dataset.Name,
            dataSourceCode,
            dataset.QueryText,
            dataset.FieldSchema.Variables ?? [],
            dataset.FieldSchema.Fields ?? [],
            dataset.TimeoutSeconds,
            dataset.MaxRows,
            dataset.IsEnabled,
            expectedRevision);

    /// <summary>
    /// 把包内参数定义转换为引擎参数定义；DefaultValue 留空，运行时由固定值/默认表达式解析。
    /// </summary>
    /// <param name="parameter">包内参数定义。</param>
    /// <returns>引擎层参数定义。</returns>
    private static PocParameterDefinition ToParameterDefinition(PocReportPackageParameter parameter) =>
        new(
            parameter.Code,
            parameter.DisplayName,
            parameter.ControlType,
            parameter.SourceType,
            DefaultValue: null,
            Visible: parameter.IsVisible,
            DataType: parameter.DataType,
            DefaultExpression: parameter.DefaultExpression,
            FixedValue: parameter.FixedValue,
            ReadOnly: parameter.IsReadOnly,
            Required: parameter.IsRequired,
            AllowUrlPrefill: parameter.AllowUrlPrefill,
            UrlKey: parameter.UrlKey,
            Options: parameter.Options,
            SourceCode: parameter.SourceCode,
            SourceFieldCode: parameter.SourceFieldCode);

    /// <summary>
    /// 归一化导入模式：缺省按 Create 处理，非 Create/Replace 文本拒绝导入。
    /// </summary>
    /// <param name="mode">请求中的模式文本。</param>
    /// <returns>归一化后的导入模式。</returns>
    private static PocReportPackageImportMode NormalizeImportMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) ||
            mode.Trim().Equals("Create", StringComparison.OrdinalIgnoreCase))
        {
            return PocReportPackageImportMode.Create;
        }
        if (mode.Trim().Equals("Replace", StringComparison.OrdinalIgnoreCase))
        {
            return PocReportPackageImportMode.Replace;
        }

        throw new PocReportPackageException(
            "PACKAGE_MODE_INVALID",
            $"导入模式 '{mode}' 无效，仅支持 Create 或 Replace。");
    }

    /// <summary>
    /// 把一次导入尝试写入审计 JSON：文件名 yyyyMMdd-HHmmss-报表编码-ok|failed，
    /// 内容含时间、模式、动作序列、包 SHA-256、试跑标记与错误信息。
    /// 写入失败只吞掉异常，不影响导入业务结果。
    /// </summary>
    /// <param name="request">原始导入请求（用于包摘要与试跑标记）。</param>
    /// <param name="reportCode">包内报表编码，包缺失时可能为 null。</param>
    /// <param name="mode">导入模式名。</param>
    /// <param name="actions">本次导入已执行的动作序列。</param>
    /// <param name="ok">导入是否成功。</param>
    /// <param name="error">失败时的错误消息。</param>
    private async Task WriteImportAuditAsync(
        PocReportPackageImportRequest request,
        string? reportCode,
        string mode,
        IReadOnlyList<string> actions,
        bool ok,
        string? error)
    {
        try
        {
            Directory.CreateDirectory(AuditDirectory);
            var safeCode = (reportCode ?? "unknown");
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                safeCode = safeCode.Replace(invalidCharacter, '_');
            }
            var fileName =
                $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{safeCode}-{(ok ? "ok" : "failed")}.json";
            var payload = new
            {
                timestamp = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                reportCode,
                mode,
                targetDataSourceCode = request.TargetDataSourceCode,
                actions,
                packageSha256 = PocReportPackageFormat.ComputeSha256(request.Package),
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

    /// <summary>
    /// 反序列化存储 JSON 并把 null 结果视为数据损坏，与配置服务现有语义一致。
    /// </summary>
    /// <param name="json">待反序列化的 JSON 文本。</param>
    /// <returns>目标类型实例。</returns>
    private static T Deserialize<T>(string json) =>
        JsonConvert.DeserializeObject<T>(json)
            ?? throw new InvalidDataException($"Stored JSON '{typeof(T).Name}' is null.");

    /// <summary>
    /// 反序列化可空 JSON 列表；空文本返回空列表，供参数选项与字段显示映射复用。
    /// </summary>
    /// <param name="json">待反序列化的 JSON 文本，允许为 null。</param>
    /// <returns>列表实例，永不返回 null。</returns>
    private static IReadOnlyList<T> DeserializeList<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonConvert.DeserializeObject<IReadOnlyList<T>>(json) ?? [];
}

/// <summary>报表包导入模式：Create 拒绝撞码，Replace 先删旧报表再新建。</summary>
internal enum PocReportPackageImportMode
{
    /// <summary>新建模式：目标报表编码已存在时拒绝导入。</summary>
    Create,

    /// <summary>替换模式：先删除既有报表（模板、参数、配置）再按包内容重建。</summary>
    Replace
}
