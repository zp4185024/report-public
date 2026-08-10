using System.Collections.Concurrent;
using HD.ReportCenter.ReportEngine.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation.Export;
using System.Runtime.CompilerServices;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>异步大文件导出任务的生命周期状态。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
[Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
public enum PocExportJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// 返回给轮询接口的不可变任务快照。调用方永远拿不到可修改的内部 JobState。
/// </summary>
public sealed record PocExportJobSnapshot(
    string JobNo,
    PocExportJobStatus Status,
    int TotalRows,
    long WrittenRows,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// 单进程大文件导出任务表，用于验证“创建任务、轮询、取消、下载”的完整链路。
/// 当前实现不跨进程持久化，也不负责过期文件清理，生产版本应由数据库任务表和后台调度器替换。
/// </summary>
public sealed class PocLargeExportJobStore
{
    /// <summary>技术验证固定生成十万行，用于观察导出耗时和内存，而不是业务行数上限。</summary>
    public const int ValidationRowCount = 100_000;

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly string _outputDirectory;
    private readonly int? _validationRowCount;
    private readonly PocStreamingTemplateDataExporter _exporter = new();

    public PocLargeExportJobStore()
        : this(Path.Combine(Path.GetTempPath(), "hd-report-center-poc"), null, true)
    {
    }

    public PocLargeExportJobStore(string outputDirectory, int rowCount)
        : this(outputDirectory, rowCount, true)
    {
    }

    private PocLargeExportJobStore(string outputDirectory, int? validationRowCount, bool _)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (validationRowCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validationRowCount),
                "大文件验证行数必须大于零。");
        }

        _outputDirectory = Path.GetFullPath(outputDirectory);
        _validationRowCount = validationRowCount;
        Directory.CreateDirectory(_outputDirectory);
    }

    /// <summary>登记任务后立即在后台线程执行，HTTP 请求无需等待整个文件生成。</summary>
    public PocExportJobSnapshot Start(PocTemplateDataExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var jobNo = $"{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var filePath = Path.Combine(_outputDirectory, $"{jobNo}.xlsx");
        var totalRows = _validationRowCount ?? PocReportCatalog.PreviewRowCount;
        var state = new JobState(jobNo, filePath, totalRows);
        if (!_jobs.TryAdd(jobNo, state))
        {
            throw new InvalidOperationException("EXPORT_JOB_CREATE_FAILED");
        }

        // JobState 先进入并发字典，再启动后台任务，保证客户端拿到 jobNo 后可以立即查询。
        _ = Task.Run(() => RunAsync(state, plan));
        return state.Snapshot();
    }

    /// <summary>读取指定任务的线程安全快照。</summary>
    public PocExportJobSnapshot Get(string jobNo) => Find(jobNo).Snapshot();

    /// <summary>对 Pending/Running 任务发出协作式取消请求；已结束任务保持原状态。</summary>
    public PocExportJobSnapshot Cancel(string jobNo)
    {
        var state = Find(jobNo);
        state.Cancel();
        return state.Snapshot();
    }

    /// <summary>仅在任务完成且文件仍存在时返回下载路径，避免客户端下载半成品。</summary>
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

    private async Task RunAsync(JobState state, PocTemplateDataExportPlan plan)
    {
        try
        {
            state.MarkRunning();
            // 顺序写、1 MiB 缓冲和流式 XML 组合，避免十万行数据先堆积为内存工作簿。
            await using (var stream = new FileStream(
                             state.FilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var pages = _validationRowCount is { } rowCount
                    ? ReadValidationPages(
                        rowCount,
                        plan.Query.DeptId,
                        state.CancellationToken)
                    : PocReportCatalog.ReadExportPages(plan.Query, state.CancellationToken);
                await _exporter.WriteLargeAsync(
                    stream,
                    plan,
                    pages,
                    state.ReportProgress,
                    state.CancellationToken);
            }

            // 文件流完全关闭后才发布 Completed，下载端不会看到尚未刷盘的 ZIP 中央目录。
            state.MarkCompleted();
        }
        catch (OperationCanceledException)
        {
            // 取消和失败都删除半成品；任务状态变化不能早于清理尝试，否则下载接口可能短暂看到残缺文件。
            TryDelete(state.FilePath);
            state.MarkCancelled();
        }
        catch (Exception exception)
        {
            TryDelete(state.FilePath);
            state.MarkFailed(exception.Message);
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadValidationPages(
        int rowCount,
        string? deptId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int pageSize = 1_000;
        var page = new List<PocDataRow>(pageSize);
        foreach (var row in PocReportCatalog.EnumerateRows(rowCount, deptId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            page.Add(PocDataRow.FromWorkload(row));
            if (page.Count == pageSize)
            {
                yield return page.ToArray();
                page.Clear();
                await Task.Yield();
            }
        }
        if (page.Count > 0)
        {
            yield return page.ToArray();
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
        // 进度、状态和完成时间必须在同一把锁下更新/读取，确保每个 Snapshot 内部自洽。
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
                // CancellationTokenSource.Cancel 可重复调用，但只在活跃状态触发更准确地表达任务语义。
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
