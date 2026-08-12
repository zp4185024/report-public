using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>数据导出所需模板尚未保存，或请求修订号已经落后于数据库当前版本。</summary>
public sealed class PocDataExportConflictException : Exception
{
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

    public async Task<PocPreparedDataExport> CreatePreparedAsync(
        string reportCode,
        PocDataExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        if (request.TemplateRevision <= 0)
        {
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

    private static PocDataExportConflictException SaveRequired() =>
        new(
            "TEMPLATE_SAVE_REQUIRED",
            "当前模板尚未保存，请先保存模板后再导出数据。");
}
