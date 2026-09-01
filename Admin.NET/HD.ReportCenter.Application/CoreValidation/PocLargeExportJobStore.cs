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

    /// <summary>初始化 PocLargeExportJobStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocLargeExportJobStore()
        : this(Path.Combine(Path.GetTempPath(), "hd-report-center-poc"), null, true)
    {
    }

    /// <summary>初始化 PocLargeExportJobStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="outputDirectory">outputDirectory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="rowCount">控制数据范围或位置的数值。</param>
    public PocLargeExportJobStore(string outputDirectory, int rowCount)
        : this(outputDirectory, rowCount, true)
    {
    }

    /// <summary>初始化 PocLargeExportJobStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="outputDirectory">outputDirectory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="validationRowCount">控制数据范围或位置的数值。</param>
    /// <param name="_">_ 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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
        return Start(plan, null);
    }

    /// <summary>启动 PocLargeExportJobStore 的 Start 流程，并建立运行期间所需状态。</summary>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="pagesFactory">pagesFactory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public PocExportJobSnapshot Start(
        PocTemplateDataExportPlan plan,
        Func<CancellationToken, IAsyncEnumerable<IReadOnlyList<PocDataRow>>>? pagesFactory)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var jobNo = $"{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var filePath = Path.Combine(_outputDirectory, $"{jobNo}.xlsx");
        var totalRows = _validationRowCount
            ?? (pagesFactory is null ? PocReportCatalog.PreviewRowCount : 0);
        var state = new JobState(jobNo, filePath, totalRows, plan);
        if (!_jobs.TryAdd(jobNo, state))
        {
            throw new InvalidOperationException("EXPORT_JOB_CREATE_FAILED");
        }

        // JobState 先进入并发字典，再启动后台任务，保证客户端拿到 jobNo 后可以立即查询。
        _ = Task.Run(() => RunAsync(state, plan, pagesFactory));
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

    /// <summary>读取已登记任务对应的导出计划，供下载响应使用报表模板名称。</summary>
    public PocTemplateDataExportPlan GetPlan(string jobNo) => Find(jobNo).Plan;

    /// <summary>执行 PocLargeExportJobStore 的 Run Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="state">state 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="pagesFactory">pagesFactory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task RunAsync(
        JobState state,
        PocTemplateDataExportPlan plan,
        Func<CancellationToken, IAsyncEnumerable<IReadOnlyList<PocDataRow>>>? pagesFactory)
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
                var pages = pagesFactory is not null
                    ? pagesFactory(state.CancellationToken)
                    : _validationRowCount is { } rowCount
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

    /// <summary>查询或读取 PocLargeExportJobStore 所需的 Read Validation Pages 数据，并按调用约定返回结果。</summary>
    /// <param name="rowCount">控制数据范围或位置的数值。</param>
    /// <param name="deptId">需要定位的业务记录标识。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>查询或读取 PocLargeExportJobStore 所需的 Find 数据，并按调用约定返回结果。</summary>
    /// <param name="jobNo">jobNo 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private JobState Find(string jobNo)
    {
        if (string.IsNullOrWhiteSpace(jobNo) || !_jobs.TryGetValue(jobNo, out var state))
        {
            throw new KeyNotFoundException("EXPORT_JOB_NOT_FOUND");
        }

        return state;
    }

    /// <summary>尝试或判断 PocLargeExportJobStore 的 Try Delete 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="path">path 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    private sealed class JobState(
        string jobNo,
        string filePath,
        int totalRows,
        PocTemplateDataExportPlan plan)
    {
        // 进度、状态和完成时间必须在同一把锁下更新/读取，确保每个 Snapshot 内部自洽。
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private PocExportJobStatus _status = PocExportJobStatus.Pending;
        private long _writtenRows;
        private int _totalRows = totalRows;
        private string? _error;
        private DateTimeOffset? _completedAt;

        public string FilePath { get; } = filePath;
        public PocTemplateDataExportPlan Plan { get; } = plan;
        public CancellationToken CancellationToken => _cancellation.Token;

        /// <summary>负责 JobState 的 Mark Running 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        public void MarkRunning() => Update(() => _status = PocExportJobStatus.Running);

        /// <summary>负责 JobState 的 Report Progress 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="writtenRows">需要遍历、筛选或写入的业务数据集合。</param>
        public void ReportProgress(long writtenRows) => Update(() => _writtenRows = writtenRows);

        /// <summary>负责 JobState 的 Mark Completed 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        public void MarkCompleted() => Update(() =>
        {
            if (_totalRows == 0)
            {
                _totalRows = checked((int)_writtenRows);
            }
            _status = PocExportJobStatus.Completed;
            _completedAt = DateTimeOffset.Now;
        });

        /// <summary>负责 JobState 的 Mark Cancelled 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        public void MarkCancelled() => Update(() =>
        {
            _status = PocExportJobStatus.Cancelled;
            _completedAt = DateTimeOffset.Now;
        });

        /// <summary>负责 JobState 的 Mark Failed 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="error">error 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        public void MarkFailed(string error) => Update(() =>
        {
            _error = error;
            _status = PocExportJobStatus.Failed;
            _completedAt = DateTimeOffset.Now;
        });

        /// <summary>校验 PocLargeExportJobStore 的 Cancel 条件，并返回可供后续流程判断的结果。</summary>
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

        /// <summary>根据 JobState 当前输入计算 Snapshot 结果，并按方法声明的类型返回。</summary>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public PocExportJobSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new PocExportJobSnapshot(
                    jobNo,
                    _status,
                    _totalRows,
                    _writtenRows,
                    _error,
                    CreatedAt,
                    _completedAt);
            }
        }

        private DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

        /// <summary>更新 PocLargeExportJobStore 的 Update 状态，并同步相关业务数据。</summary>
        /// <param name="update">update 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        private void Update(Action update)
        {
            lock (_sync)
            {
                update();
            }
        }
    }
}
