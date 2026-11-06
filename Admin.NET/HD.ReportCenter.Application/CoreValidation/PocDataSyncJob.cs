using Furion.Schedule;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 数据同步调度作业：每分钟扫描一次启用的同步任务，把到期的（Daily 过点未跑、Interval 到间隔）
/// 任务逐个同步执行。作业本身不解释调度语义（判定收敛在 PocDataSyncService.IsTaskDue，
/// Cron 类型由 Furion 动态触发器直接驱动，不进入本扫描），执行复用
/// PocDataSyncService.ExecuteTaskNowAsync（先置 Running 再执行，天然防重入）。
/// </summary>
[JobDetail("job_rcDataSync", Description = "扫描并执行到期的数据同步任务", GroupName = "default", Concurrent = false)]
[PeriodMinutes(1, TriggerId = "trigger_rcDataSync", Description = "每分钟扫描到期同步任务", RunOnStart = false)]
public sealed class PocDataSyncJob : IJob
{
    /// <summary>首轮 Cron 对账标志：0 表示尚未对账。本作业由调度器自身驱动（启动后 ≤1 分钟必跑），
    /// 用进程内标志保证对账只执行一次。</summary>
    private static int _cronReconciled;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    /// <summary>初始化 PocDataSyncJob 实例，保存作用域工厂与日志依赖。</summary>
    /// <param name="scopeFactory">用于在作业内创建独立依赖作用域的工厂。</param>
    /// <param name="loggerFactory">日志工厂，用于记录任务执行结果与失败原因。</param>
    public PocDataSyncJob(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger("HD.ReportCenter.DataSync");
    }

    /// <summary>
    /// 执行调度扫描：首轮先做一次性 Cron 对账（补挂所有启用 Cron 任务的动态作业，幂等），
    /// 然后在独立作用域内解析同步服务，逐个执行到期任务；
    /// 单个任务失败只记日志不影响其余任务，保证调度器不会被一个坏任务拖垮。
    /// </summary>
    /// <param name="context">本次调度触发上下文。</param>
    /// <param name="stoppingToken">宿主停机令牌，用于取消仍在执行的任务。</param>
    /// <returns>扫描完成的异步任务。</returns>
    public async Task ExecuteAsync(JobExecutingContext context, CancellationToken stoppingToken)
    {
        using var serviceScope = _scopeFactory.CreateScope();
        var syncService = serviceScope.ServiceProvider.GetRequiredService<PocDataSyncService>();

        // 第零阶段：首轮 Cron 对账（一次性）。此时调度器必然就绪（本作业就是被它驱动的），
        // 补挂“启用且 Cron”任务恢复重启前的触发器；对账失败不阻断扫描，等待下次保存/导入/重启自愈。
        if (Interlocked.Exchange(ref _cronReconciled, 1) == 0)
        {
            try
            {
                var reconciled = await syncService.ReconcileCronSchedulesAsync();
                if (reconciled > 0)
                {
                    _logger.LogInformation("数据同步 Cron 调度首轮对账完成：补挂 {Count} 个启用任务。", reconciled);
                }
            }
            catch (Exception reconciliationException)
            {
                _logger.LogError(
                    reconciliationException,
                    "数据同步 Cron 调度首轮对账失败：{Message}",
                    reconciliationException.Message);
            }
        }

        // 第一阶段：扫描到期任务；判定逻辑与人工运行共用同一份服务规则。
        var dueTasks = await syncService.ListDueTasksAsync();
        if (dueTasks.Count == 0)
        {
            return;
        }

        // 第二阶段：逐个同步执行。ExecuteTaskNowAsync 内部先落 Running 日志，重复触发会被拒绝。
        foreach (var task in dueTasks)
        {
            try
            {
                var log = await syncService.ExecuteTaskNowAsync(task.Code, stoppingToken);
                _logger.LogInformation(
                    "数据同步任务 {TaskCode} 完成：读取 {RowsRead} 行，写入 {RowsWritten} 行，失败 {RowsFailed} 行。",
                    task.Code,
                    log.RowsRead,
                    log.RowsWritten,
                    log.RowsFailed);
            }
            catch (PocDataSyncException exception) when (exception.Code == "SYNC_TASK_ALREADY_RUNNING")
            {
                // 上一次运行尚未结束：本轮跳过，留给下一次扫描，不视为错误。
                _logger.LogWarning("数据同步任务 {TaskCode} 上一次运行尚未结束，本轮跳过。", task.Code);
            }
            catch (OperationCanceledException)
            {
                // 宿主停机：立即结束本轮扫描，未完成的任务下个周期重新判定到期。
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "数据同步任务 {TaskCode} 执行失败：{Message}",
                    task.Code,
                    exception.Message);
            }
        }
    }
}
