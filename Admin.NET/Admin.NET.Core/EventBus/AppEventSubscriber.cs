// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 事件订阅
/// </summary>
public class AppEventSubscriber : IEventSubscriber, ISingleton, IDisposable
{
    private static readonly ISugarQueryable<SysTenant> SysTenantQueryable = App.GetService<ISqlSugarClient>().Queryable<SysTenant>();
    private readonly IServiceScope _serviceScope;

    /// <summary>初始化 AppEventSubscriber 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="scopeFactory">当前方法执行时使用的 scopeFactory 输入值。</param>
    public AppEventSubscriber(IServiceScopeFactory scopeFactory)
    {
        _serviceScope = scopeFactory.CreateScope();
    }

    /// <summary>构建 AppEventSubscriber 所需的 Create Ex Log 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(CommonConst.AddExLog)]
    public async Task CreateExLog(EventHandlerExecutingContext context)
    {
        // 切换日志独立数据库
        var db = SqlSugarSetup.ITenant.IsAnyConnection(SqlSugarConst.LogConfigId)
            ? SqlSugarSetup.ITenant.GetConnectionScope(SqlSugarConst.LogConfigId)
            : SqlSugarSetup.ITenant.GetConnectionScope(SqlSugarConst.MainConfigId);
        await db.CopyNew().Insertable((SysLogEx)context.Source.Payload).ExecuteCommandAsync();
    }

    /// <summary>执行 AppEventSubscriber 的 Send Order Error Mail 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(CommonConst.SendErrorMail)]
    public async Task SendOrderErrorMail(EventHandlerExecutingContext context)
    {
        long.TryParse(App.HttpContext?.User.FindFirst(ClaimConst.TenantId)?.Value, out var tenantId);
        var tenant = await SysTenantQueryable.FirstAsync(t => t.Id == tenantId);
        var title = $"{tenant?.Title} 系统异常";
        await _serviceScope.ServiceProvider.GetRequiredService<SysEmailService>().SendEmail(JSON.Serialize(context.Source.Payload), title);
    }

    /// <summary>释放 AppEventSubscriber 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _serviceScope.Dispose();
    }
}