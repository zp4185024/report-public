// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core.Service;

/// <summary>
/// 作业执行监视器
/// </summary>
public class JobMonitor : IJobMonitor
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<JobMonitor> _logger;

    /// <summary>初始化 JobMonitor 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="serviceScopeFactory">serviceScopeFactory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="eventPublisher">eventPublisher 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="logger">logger 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public JobMonitor(IServiceScopeFactory serviceScopeFactory, IEventPublisher eventPublisher, ILogger<JobMonitor> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <summary>响应 JobMonitor 的 On Executing Async 事件，并协调相关状态和后续操作。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="stoppingToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task OnExecutingAsync(JobExecutingContext context, CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>响应 JobMonitor 的 On Executed Async 事件，并协调相关状态和后续操作。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="stoppingToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task OnExecutedAsync(JobExecutedContext context, CancellationToken stoppingToken)
    {
        if (context.Exception == null) return;

        var exception = $"定时任务【{context.Trigger.Description}】错误：{context.Exception}";
        // 将作业异常信息记录到本地
        _logger.LogError(exception);

        using var scope = _serviceScopeFactory.CreateScope();
        var sysConfigService = scope.ServiceProvider.GetRequiredService<SysConfigService>();
        if (await sysConfigService.GetConfigValue<bool>(ConfigConst.SysErrorMail))
        {
            // 将作业异常信息发送到邮件
            await _eventPublisher.PublishAsync(CommonConst.SendErrorMail, exception, stoppingToken);
        }
    }
}