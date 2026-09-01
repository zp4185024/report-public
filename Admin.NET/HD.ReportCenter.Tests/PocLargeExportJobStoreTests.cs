using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;
using ClosedXML.Excel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

public sealed class PocLargeExportJobStoreTests
{
    /// <summary>验证 Start Completes Streaming Job And Publishes File 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Start_CompletesStreamingJobAndPublishesFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hd-report-center-test-{Guid.NewGuid():N}");
        var store = new PocLargeExportJobStore(directory, 2_001);
        var request = new PocExecuteRequest(1, "H01", "D02", "P03", "2026-07-01", "2026-07-14");
        var plan = CreatePlan(request);

        try
        {
            var started = store.Start(plan);
            var completed = await WaitForTerminalStatus(store, started.JobNo);

            Assert.True(
                completed.Status == PocExportJobStatus.Completed,
                completed.Error ?? $"任务状态为 {completed.Status}");
            Assert.Equal(2_001, completed.WrittenRows);
            var path = store.GetDownloadPath(started.JobNo);
            Assert.True(File.Exists(path));
            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("科室工作量");
            Assert.Equal(2_001d, sheet.Cell("A2006").GetDouble());
            Assert.Equal("C2006+D2006", sheet.Cell("E2006").FormulaA1);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>验证 Cancel Removes The Partial Template File 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Cancel_RemovesThePartialTemplateFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hd-report-center-cancel-{Guid.NewGuid():N}");
        var store = new PocLargeExportJobStore(directory, 1_000_000);
        var request = new PocExecuteRequest(1, "H01", "D02", "P03", "2026-07-01", "2026-07-14");

        try
        {
            var started = store.Start(CreatePlan(request));
            _ = store.Cancel(started.JobNo);
            var cancelled = await WaitForTerminalStatus(store, started.JobNo);

            Assert.Equal(PocExportJobStatus.Cancelled, cancelled.Status);
            Assert.Empty(Directory.GetFiles(directory, "*.xlsx"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>调用 PocLargeExportJobStoreTests 的异步依赖完成 Wait For Terminal Status 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="store">store 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="jobNo">jobNo 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static async Task<PocExportJobSnapshot> WaitForTerminalStatus(
        PocLargeExportJobStore store,
        string jobNo)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = store.Get(jobNo);
            if (snapshot.Status is not (PocExportJobStatus.Pending or PocExportJobStatus.Running))
            {
                return snapshot;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    /// <summary>查询或读取 PocLargeExportJobStoreTests 所需的 Load Seed Snapshot 数据，并按调用约定返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject LoadSeedSnapshot()
    {
        const string resourceName =
            "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";
        using var stream = typeof(PocTemplateService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到测试模板资源 {resourceName}。");
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }

    /// <summary>构建 PocLargeExportJobStoreTests 所需的 Create Plan 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocTemplateDataExportPlan CreatePlan(PocExecuteRequest request)
    {
        var snapshot = LoadSeedSnapshot();
        return new PocTemplateDataExportPlan(
            PocReportCatalog.ReportCode,
            3,
            snapshot,
            PocTemplateValidator.Scan(snapshot.ToString(Formatting.None)),
            request,
            PocReportCatalog.GetDefinition().Export,
            CurrentRules());
    }

    /// <summary>根据 PocLargeExportJobStoreTests 当前输入计算 Current Rules 结果，并按方法声明的类型返回。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocExchangeRuleSet CurrentRules()
    {
        var rules = PocExchangeMappingDefaults.Create()
            .Select(rule => new PocExchangeRule(
                rule.RuleCode,
                rule.Direction,
                rule.Category,
                rule.SourcePath,
                rule.TargetPath,
                rule.TransformType,
                rule.SourceValue,
                rule.TargetValue,
                rule.Factor,
                rule.IsRequired,
                rule.Sort))
            .ToArray();
        return new PocExchangeRuleSet(
            PocExchangeMappingDefaults.CurrentUniverVersion,
            PocExchangeMappingDefaults.CurrentMappingVersion,
            rules);
    }
}
