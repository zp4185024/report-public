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

    private static JObject LoadSeedSnapshot()
    {
        const string resourceName =
            "HD.ReportCenter.Application.CoreValidation.Seeds.dept-workload.json";
        using var stream = typeof(PocTemplateService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到测试模板资源 {resourceName}。");
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }

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
