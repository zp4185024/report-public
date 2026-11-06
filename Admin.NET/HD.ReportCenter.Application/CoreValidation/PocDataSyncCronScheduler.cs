using System.Security.Cryptography;
using System.Text;
using Furion;
using Furion.Schedule;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 数据同步 Cron 调度的派生标识规则：把任务编码映射为不超过 64 字符的 Furion 作业/触发器 ID。
/// 任务编码最长 64 字符，直接拼接前缀会超出 SysJobDetail/SysJobTrigger 的 ID 列长（64）导致持久化报错；
/// 超预算时对任务码截断并追加 8 位稳定哈希（SHA-256 前 8 位十六进制）保证唯一性且重启后稳定。
/// 截断后的派生 ID 无法直接反解完整任务码，作业体按“对启用 Cron 任务重算派生 ID 反查匹配”定位任务。
/// </summary>
public static class PocDataSyncCronScheduleIdentity
{
    /// <summary>动态作业 ID 前缀；与既有手工触发作业 job_rcDataSync 的命名风格区分开。</summary>
    public const string JobIdPrefix = "job_rcSync_";

    /// <summary>动态触发器 ID 前缀。</summary>
    public const string TriggerIdPrefix = "trigger_rcSync_";

    /// <summary>Furion 作业/触发器 ID 的存储列长上限（SysJobDetail/SysJobTrigger 的 JobId/TriggerId 均为 64）。</summary>
    public const int MaxIdLength = 64;

    /// <summary>稳定哈希后缀长度；8 位十六进制对单实例任务数量足以避免截断后撞名。</summary>
    private const int HashSuffixLength = 8;

    /// <summary>按派生规则计算任务在调度器里的作业 ID。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    /// <returns>总长不超过 64 的作业 ID。</returns>
    public static string DeriveJobId(string taskCode) => DeriveId(JobIdPrefix, taskCode);

    /// <summary>按派生规则计算任务在调度器里的触发器 ID。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    /// <returns>总长不超过 64 的触发器 ID。</returns>
    public static string DeriveTriggerId(string taskCode) => DeriveId(TriggerIdPrefix, taskCode);

    /// <summary>
    /// 统一派生规则：前缀 + 任务码直接拼接不超限时原样返回；超限时截断任务码并以 '-' 连接
    /// 8 位 SHA-256 前缀哈希。哈希基于完整任务码计算，保证不同任务截断后仍可区分。
    /// </summary>
    /// <param name="prefix">作业或触发器 ID 前缀。</param>
    /// <param name="taskCode">同步任务编码。</param>
    /// <returns>派生后的调度器 ID。</returns>
    private static string DeriveId(string prefix, string taskCode)
    {
        // 第一阶段：不超预算的快路径，直接拼接（常规任务编码都走这条，无截断无哈希）。
        if (prefix.Length + taskCode.Length <= MaxIdLength)
        {
            return prefix + taskCode;
        }

        // 第二阶段：截断 + 哈希后缀。任务码预算 = 上限 - 前缀长度 - 分隔符 1 位 - 哈希 8 位。
        var budget = MaxIdLength - prefix.Length - 1 - HashSuffixLength;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskCode)), 0, 32)
            [..HashSuffixLength].ToLowerInvariant();
        return $"{prefix}{taskCode[..Math.Min(budget, taskCode.Length)]}-{hash}";
    }
}

/// <summary>
/// 数据同步 Cron 调度网关：封装 Furion ISchedulerFactory 的动态作业注册/摘除能力，
/// 供保存、导入、删除、启动对账等生命周期钩子调用；接口便于单测伪造并断言调用序列。
/// </summary>
public interface IPocDataSyncScheduleGateway
{
    /// <summary>注册（或更新）任务的 Cron 动态作业：先移除旧作业再按表达式注册，保证幂等。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    /// <param name="taskName">任务名称，写入作业/触发器描述便于在框架定时任务页辨识。</param>
    /// <param name="cronExpression">已通过保存校验的标准 5 段 Cron 表达式。</param>
    void RegisterCronJob(string taskCode, string taskName, string cronExpression);

    /// <summary>摘除任务对应的 Cron 动态作业；作业不存在时静默返回。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    void RemoveCronJob(string taskCode);
}

/// <summary>
/// 基于 Furion ISchedulerFactory 的调度网关生产实现。所有操作在实现内部吞掉异常并记日志：
/// 调度器不是事实源（DB 才是），注册失败不回滚保存/导入、不抛给前端，
/// 下次保存、导入或服务重启的启动对账会自动恢复。
/// </summary>
public sealed class PocDataSyncScheduleGateway : IPocDataSyncScheduleGateway, ISingleton
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<PocDataSyncScheduleGateway> _logger;

    /// <summary>初始化 PocDataSyncScheduleGateway 实例，保存调度工厂与日志依赖。</summary>
    /// <param name="schedulerFactory">Furion 调度工厂，由宿主 AddSchedule 注册。</param>
    /// <param name="logger">日志器，用于记录注册/摘除与失败原因。</param>
    public PocDataSyncScheduleGateway(
        ISchedulerFactory schedulerFactory,
        ILogger<PocDataSyncScheduleGateway> logger)
    {
        _schedulerFactory = schedulerFactory;
        _logger = logger;
    }

    /// <summary>
    /// 注册（或更新）任务的 Cron 动态作业。任何失败只记 Error 日志不抛出：
    /// 表达式在保存/导入时已通过 CronTrigger 构造校验，这里的失败通常来自调度器自身故障，
    /// 抛出会误伤已成功的保存/导入结果（DB 已落库，重启对账可自愈）。
    /// </summary>
    /// <param name="taskCode">同步任务编码。</param>
    /// <param name="taskName">任务名称。</param>
    /// <param name="cronExpression">已校验的标准 5 段 Cron 表达式。</param>
    public void RegisterCronJob(string taskCode, string taskName, string cronExpression)
    {
        var jobId = PocDataSyncCronScheduleIdentity.DeriveJobId(taskCode);
        try
        {
            // 第一阶段：先摘除旧作业再注册，幂等覆盖“修改表达式”“停用后重新启用”两类场景，
            // 避免旧触发器定义残留导致到点仍按旧表达式触发。作业不存在时 RemoveJob 静默。
            _schedulerFactory.RemoveJob(new[] { jobId });

            // 第二阶段：组装动态作业（宿主类型 PocSyncCronJob + 派生 JobId）与 Cron 触发器，
            // 触发器在 AddJob 时创建真实实例并解析表达式；表达式已在保存/导入时校验过，不会在此失败。
            var jobBuilder = JobBuilder.Create(typeof(PocSyncCronJob))
                .SetJobId(jobId)
                .SetDescription($"数据同步 Cron 触发：{taskName}");
            var triggerBuilder = Triggers.Cron(cronExpression)
                .SetTriggerId(PocDataSyncCronScheduleIdentity.DeriveTriggerId(taskCode))
                .SetDescription($"数据同步 Cron：{taskName}");
            _schedulerFactory.AddJob(jobBuilder, new[] { triggerBuilder });
            _logger.LogInformation(
                "数据同步任务 {TaskCode} 的 Cron 触发器已注册（表达式 {CronExpression}）。",
                taskCode,
                cronExpression);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "数据同步任务 {TaskCode} 的 Cron 触发器注册失败，等待下次保存/导入/重启对账自愈：{Message}",
                taskCode,
                exception.Message);
        }
    }

    /// <summary>摘除任务对应的 Cron 动态作业；作业不存在时 RemoveJob 静默，任何异常降级为警告日志。</summary>
    /// <param name="taskCode">同步任务编码。</param>
    public void RemoveCronJob(string taskCode)
    {
        try
        {
            _schedulerFactory.RemoveJob(new[] { PocDataSyncCronScheduleIdentity.DeriveJobId(taskCode) });
            _logger.LogInformation("数据同步任务 {TaskCode} 的 Cron 动态作业已摘除。", taskCode);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "数据同步任务 {TaskCode} 的 Cron 动态作业摘除失败（可能是调度器瞬时故障）：{Message}",
                taskCode,
                exception.Message);
        }
    }
}

/// <summary>
/// 数据同步 Cron 触发宿主作业：每个启用 Cron 的同步任务对应一个动态作业实例
/// （作业类型为本类、JobId/触发器按任务码派生），触发到点后由作业体定位任务并复用
/// ExecuteTaskNowAsync 执行。本类自身显式声明为无触发器的宿主作业：Furion 扫描 IJob
/// 实现时会为未加特性的类生成“幽灵作业”记录，显式特性让它成为已知宿主作业（永不运行，
/// 框架定时任务页可见一行属预期）。
/// </summary>
[JobDetail("job_rcSyncCronHost", Description = "数据同步 Cron 触发宿主（无触发器，不运行，仅占位）", GroupName = "default")]
public sealed class PocSyncCronJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    /// <summary>初始化 PocSyncCronJob 实例，保存作用域工厂与日志依赖。</summary>
    /// <param name="scopeFactory">用于在作业内创建独立依赖作用域的工厂。</param>
    /// <param name="loggerFactory">日志工厂，用于记录触发结果与失败原因。</param>
    public PocSyncCronJob(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger("HD.ReportCenter.DataSync");
    }

    /// <summary>
    /// Cron 触发入口：按派生 ID 反查任务编码后复用人工执行链路。异常处理与每分钟扫描作业
    /// （PocDataSyncJob）保持同一三层结构——防重静默跳过、停机直接结束、其余兜底捕获，
    /// 保证单次异常不会打断后续调度（动态触发器持续存在，异常处理后作业仍在调度计划内）。
    /// </summary>
    /// <param name="context">本次调度触发上下文（JobId 用于反查任务编码）。</param>
    /// <param name="stoppingToken">宿主停机令牌，用于取消仍在执行的任务。</param>
    /// <returns>触发处理完成的异步任务。</returns>
    public async Task ExecuteAsync(JobExecutingContext context, CancellationToken stoppingToken)
    {
        using var serviceScope = _scopeFactory.CreateScope();
        var syncService = serviceScope.ServiceProvider.GetRequiredService<PocDataSyncService>();
        try
        {
            // 第一阶段：反查任务编码。截断派生 ID 无法直接反解，对启用 Cron 任务重算派生 ID 匹配；
            // 每次触发时启用任务数量有限，全量比对开销可忽略。未匹配通常是任务刚被停用/删除。
            var taskCode = await syncService.ResolveCronTaskCodeAsync(context.JobDetail.JobId, stoppingToken);
            if (taskCode is null)
            {
                _logger.LogWarning(
                    "数据同步 Cron 作业 {JobId} 未匹配到启用的 Cron 任务，本轮跳过（任务可能已停用或删除）。",
                    context.JobDetail.JobId);
                return;
            }

            // 第二阶段：复用人工执行链路（先落 Running 日志防重入），与手动触发天然互斥安全。
            var log = await syncService.ExecuteTaskNowAsync(taskCode, stoppingToken);
            _logger.LogInformation(
                "数据同步任务 {TaskCode} 由 Cron 触发完成：读取 {RowsRead} 行，写入 {RowsWritten} 行，失败 {RowsFailed} 行。",
                taskCode,
                log.RowsRead,
                log.RowsWritten,
                log.RowsFailed);
        }
        catch (PocDataSyncException exception) when (exception.Code == "SYNC_TASK_ALREADY_RUNNING")
        {
            // 上一次运行尚未结束：本轮跳过，Running 防重与手动触发互斥，不视为错误。
            _logger.LogWarning("数据同步 Cron 触发命中运行中的任务 {Message}，本轮跳过。", exception.Message);
        }
        catch (OperationCanceledException)
        {
            // 宿主停机：直接结束本次触发，下个到期点由触发器重新调度。
        }
        catch (Exception exception)
        {
            // 兜底捕获：一次触发异常不得打断后续调度；DB 是事实源，动态作业与触发器保持不变。
            _logger.LogError(
                exception,
                "数据同步 Cron 触发执行失败：{Message}",
                exception.Message);
        }
    }
}
