using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocLargeExportJobStoreTests
{
    [Fact]
    public async Task Start_CompletesStreamingJobAndPublishesFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hd-report-center-test-{Guid.NewGuid():N}");
        var store = new PocLargeExportJobStore(directory, 2_001);
        var request = new PocExecuteRequest(1, "H01", "D02", "P03", "2026-07-01", "2026-07-14");

        try
        {
            var started = store.Start(request);
            var completed = await WaitForTerminalStatus(store, started.JobNo);

            Assert.True(
                completed.Status == PocExportJobStatus.Completed,
                completed.Error ?? $"任务状态为 {completed.Status}");
            Assert.Equal(2_001, completed.WrittenRows);
            Assert.True(File.Exists(store.GetDownloadPath(started.JobNo)));
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
}
