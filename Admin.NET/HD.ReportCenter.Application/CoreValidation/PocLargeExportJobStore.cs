using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PocExportJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record PocExportJobSnapshot(
    string JobNo,
    PocExportJobStatus Status,
    int TotalRows,
    long WrittenRows,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed class PocLargeExportJobStore
{
    public const int ValidationRowCount = 100_000;

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly string _outputDirectory;
    private readonly int _rowCount;

    public PocLargeExportJobStore()
        : this(Path.Combine(Path.GetTempPath(), "hd-report-center-poc"), ValidationRowCount)
    {
    }

    public PocLargeExportJobStore(string outputDirectory, int rowCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);

        _outputDirectory = Path.GetFullPath(outputDirectory);
        _rowCount = rowCount;
        Directory.CreateDirectory(_outputDirectory);
    }

    public PocExportJobSnapshot Start(PocExecuteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var jobNo = $"{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var filePath = Path.Combine(_outputDirectory, $"{jobNo}.xlsx");
        var state = new JobState(jobNo, filePath, _rowCount);
        if (!_jobs.TryAdd(jobNo, state))
        {
            throw new InvalidOperationException("EXPORT_JOB_CREATE_FAILED");
        }

        _ = Task.Run(() => Run(state, request));
        return state.Snapshot();
    }

    public PocExportJobSnapshot Get(string jobNo) => Find(jobNo).Snapshot();

    public PocExportJobSnapshot Cancel(string jobNo)
    {
        var state = Find(jobNo);
        state.Cancel();
        return state.Snapshot();
    }

    public string GetDownloadPath(string jobNo)
    {
        var state = Find(jobNo);
        var snapshot = state.Snapshot();
        if (snapshot.Status != PocExportJobStatus.Completed || !File.Exists(state.FilePath))
        {
            throw new InvalidOperationException("EXPORT_JOB_NOT_READY");
        }

        return state.FilePath;
    }

    private void Run(JobState state, PocExecuteRequest request)
    {
        try
        {
            state.MarkRunning();
            using (var stream = new FileStream(
                       state.FilePath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       1024 * 1024,
                       FileOptions.SequentialScan))
            {
                PocExcelExporter.Write(
                    stream,
                    PocReportCatalog.EnumerateRows(_rowCount, request.DeptId),
                    state.ReportProgress,
                    state.CancellationToken);
            }

            state.MarkCompleted();
        }
        catch (OperationCanceledException)
        {
            TryDelete(state.FilePath);
            state.MarkCancelled();
        }
        catch (Exception exception)
        {
            TryDelete(state.FilePath);
            state.MarkFailed(exception.Message);
        }
    }

    private JobState Find(string jobNo)
    {
        if (string.IsNullOrWhiteSpace(jobNo) || !_jobs.TryGetValue(jobNo, out var state))
        {
            throw new KeyNotFoundException("EXPORT_JOB_NOT_FOUND");
        }

        return state;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // POC 清理失败不覆盖原始任务状态。
        }
    }

    private sealed class JobState(string jobNo, string filePath, int totalRows)
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private PocExportJobStatus _status = PocExportJobStatus.Pending;
        private long _writtenRows;
        private string? _error;
        private DateTimeOffset? _completedAt;

        public string FilePath { get; } = filePath;
        public CancellationToken CancellationToken => _cancellation.Token;

        public void MarkRunning() => Update(() => _status = PocExportJobStatus.Running);

        public void ReportProgress(long writtenRows) => Update(() => _writtenRows = writtenRows);

        public void MarkCompleted() => Update(() =>
        {
            _writtenRows = totalRows;
            _status = PocExportJobStatus.Completed;
            _completedAt = DateTimeOffset.Now;
        });

        public void MarkCancelled() => Update(() =>
        {
            _status = PocExportJobStatus.Cancelled;
            _completedAt = DateTimeOffset.Now;
        });

        public void MarkFailed(string error) => Update(() =>
        {
            _error = error;
            _status = PocExportJobStatus.Failed;
            _completedAt = DateTimeOffset.Now;
        });

        public void Cancel()
        {
            lock (_sync)
            {
                if (_status is PocExportJobStatus.Pending or PocExportJobStatus.Running)
                {
                    _cancellation.Cancel();
                }
            }
        }

        public PocExportJobSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new PocExportJobSnapshot(
                    jobNo,
                    _status,
                    totalRows,
                    _writtenRows,
                    _error,
                    CreatedAt,
                    _completedAt);
            }
        }

        private DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

        private void Update(Action update)
        {
            lock (_sync)
            {
                update();
            }
        }
    }
}
