using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>数据导出所需模板尚未保存，或请求修订号已经落后于数据库当前版本。</summary>
public sealed class PocDataExportConflictException : Exception
{
    /// <summary>初始化 PocDataExportConflictException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocDataExportConflictException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record PocPreparedDataExport(
    PocTemplateDataExportPlan TemplatePlan,
    PocRuntimeExecutionPlan? RuntimePlan);

/// <summary>
/// 从数据库模板、报表定义和转换映射创建导出计划。该服务是数据导出的版本可信边界。
/// </summary>
public sealed class PocTemplateDataExportService : ITransient
{
    private readonly PocTemplateService _templateService;
    private readonly PocExchangeMappingStore _mappingStore;
    private readonly PocSqlDatasetExecutor? _datasetExecutor;

    /// <summary>初始化 PocTemplateDataExportService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="templateService">templateService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="mappingStore">mappingStore 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="datasetExecutor">datasetExecutor 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocTemplateDataExportService(
        PocTemplateService templateService,
        PocExchangeMappingStore mappingStore,
        PocSqlDatasetExecutor? datasetExecutor = null)
    {
        _templateService = templateService;
        _mappingStore = mappingStore;
        _datasetExecutor = datasetExecutor;
    }

    /// <summary>校验精确模板修订号，并把本次导出使用的所有可变输入复制到独立计划。</summary>
    public async Task<PocTemplateDataExportPlan> CreatePlanAsync(
        string reportCode,
        PocDataExportRequest request,
        CancellationToken cancellationToken = default)
    {
        return (await CreatePreparedAsync(reportCode, request, cancellationToken)).TemplatePlan;
    }

    /// <summary>构建 PocTemplateDataExportService 所需的 Create Prepared Async 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocPreparedDataExport> CreatePreparedAsync(
        string reportCode,
        PocDataExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        if (request.TemplateRevision <= 0)
        {
            // 第一阶段：根据 CreatePreparedAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
            throw SaveRequired();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedReportCode = reportCode.Equals(
            PocReportCatalog.ReportCode,
            StringComparison.OrdinalIgnoreCase)
                ? PocReportCatalog.ReportCode
                : reportCode.Trim();
        var document = await _templateService.GetAsync(normalizedReportCode);
        if (document.Revision <= 0)
        {
            throw SaveRequired();
        }
        if (document.Revision != request.TemplateRevision)
        {
            throw new PocDataExportConflictException(
                "TEMPLATE_REVISION_CHANGED",
                $"模板版本已经变化，当前数据库版本为 {document.Revision}，请刷新后重新导出。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mappings = await _mappingStore.GetCurrentAsync(
            PocExchangeMappingDefaults.CurrentUniverVersion);
        cancellationToken.ThrowIfCancellationRequested();

        // 第2阶段：调用 CreatePreparedAsync 依赖的异步能力，等待并校验返回数据。
        var runtimePlan = _datasetExecutor is null
            ? null
            : await _datasetExecutor.PrepareAsync(reportCode, request.Query, cancellationToken);
        var definition = runtimePlan?.Configuration.Definition ?? PocReportCatalog.GetDefinition();
        var rawParameters = runtimePlan?.Parameters.DisplayValues
            ?? PocReportCatalog.Execute(request.Query with { Page = 1 }).Parameters;
        var parameters = PocLocalDataProjection.ApplyParameterLabels(definition, rawParameters);
        var templatePlan = new PocTemplateDataExportPlan(
            normalizedReportCode,
            document.Revision,
            (Newtonsoft.Json.Linq.JObject)document.Snapshot.DeepClone(),
            CloneBinding(document.Binding),
            request.Query with { Page = 1 },
            definition.Export with { },
            new PocExchangeRuleSet(
                mappings.UniverVersion,
                mappings.MappingVersion,
                mappings.Rules.Select(rule => rule with { }).ToArray()),
            definition,
            new PocResolvedParameters(parameters));
        return new PocPreparedDataExport(templatePlan, runtimePlan);
    }

    /// <summary>按照 PocTemplateDataExportService 的业务约束完成 Clone Binding 数据合并或复制。</summary>
    /// <param name="binding">模板字段与工作簿坐标之间的绑定定义。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocTemplateBinding CloneBinding(PocTemplateBinding binding) =>
        new(
            binding.Version,
            binding.ParameterBindings
                .Select(item => item with { })
                .ToArray(),
            binding.DetailBinding is null
                ? null
                : new PocDetailBinding(
                    binding.DetailBinding.DatasetCode,
                    binding.DetailBinding.SheetId,
                    binding.DetailBinding.TemplateRow,
                    binding.DetailBinding.Fields
                        .Select(field => field with { })
                        .ToArray()));

    /// <summary>更新 PocTemplateDataExportService 的 Save Required 状态，并同步相关业务数据。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDataExportConflictException SaveRequired() =>
        new(
            "TEMPLATE_SAVE_REQUIRED",
            "当前模板尚未保存，请先保存模板后再导出数据。");
}
