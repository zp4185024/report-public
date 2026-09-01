// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 清理日志作业任务
/// </summary>
[JobDetail("job_log", Description = "清理操作日志", GroupName = "default", Concurrent = false)]
[Daily(TriggerId = "trigger_log", Description = "清理操作日志")]
public class LogJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    /// <summary>初始化 LogJob 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="scopeFactory">当前方法执行时使用的 scopeFactory 输入值。</param>
    /// <param name="loggerFactory">当前方法执行时使用的 loggerFactory 输入值。</param>
    public LogJob(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger(CommonConst.SysLogCategoryName);
    }

    /// <summary>执行 LogJob 的 Execute Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="stoppingToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task ExecuteAsync(JobExecutingContext context, CancellationToken stoppingToken)
    {
        using var serviceScope = _scopeFactory.CreateScope();

        var db = serviceScope.ServiceProvider.GetRequiredService<SqlSugarRepository<SysLogVis>>().AsSugarClient().CopyNew();
        var sysConfigService = serviceScope.ServiceProvider.GetRequiredService<SysConfigService>();

        var daysAgo = await sysConfigService.GetConfigValue<int>(ConfigConst.SysLogRetentionDays); // 日志保留天数
        await db.Deleteable<SysLogVis>().Where(u => u.CreateTime < DateTime.Now.AddDays(-daysAgo)).ExecuteCommandAsync(stoppingToken); // 删除访问日志
        await db.Deleteable<SysLogOp>().Where(u => u.CreateTime < DateTime.Now.AddDays(-daysAgo)).ExecuteCommandAsync(stoppingToken); // 删除操作日志
        await db.Deleteable<SysLogDiff>().Where(u => u.CreateTime < DateTime.Now.AddDays(-daysAgo)).ExecuteCommandAsync(stoppingToken); // 删除差异日志

        string msg = $"【{DateTime.Now}】清理系统日志成功，删除 {daysAgo} 天前的日志数据！";
        var originColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(msg);
        Console.ForegroundColor = originColor;

        // 自定义日志
        _logger.LogInformation(msg);
    }
}