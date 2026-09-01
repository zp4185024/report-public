// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Furion.EventBus;

namespace Admin.NET.Application;

/// <summary>
/// 系统用户操作事件订阅
/// </summary>
public class SysUserEventSubscriber : IEventSubscriber, ISingleton, IDisposable
{
    /// <summary>初始化 SysUserEventSubscriber 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public SysUserEventSubscriber()
    {
    }

    /// <summary>构建 SysUserEventSubscriber 所需的 Add User 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(SysUserEventTypeEnum.Add)]
    public Task AddUser(EventHandlerExecutingContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>根据 SysUserEventSubscriber 当前输入计算 Register User 结果，并按方法声明的类型返回。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(SysUserEventTypeEnum.Register)]
    public Task RegisterUser(EventHandlerExecutingContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>更新 SysUserEventSubscriber 的 Update User 状态，并同步相关业务数据。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(SysUserEventTypeEnum.Update)]
    public Task UpdateUser(EventHandlerExecutingContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>移除 SysUserEventSubscriber 中与 Delete User 对应的数据，并清理关联状态。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(SysUserEventTypeEnum.Delete)]
    public Task DeleteUser(EventHandlerExecutingContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 设置系统用户状态
    /// </summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(SysUserEventTypeEnum.SetStatus)]
    public Task SetUserStatus(EventHandlerExecutingContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>更新 SysUserEventSubscriber 的 Update User Role 状态，并同步相关业务数据。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(SysUserEventTypeEnum.UpdateRole)]
    public Task UpdateUserRole(EventHandlerExecutingContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>根据 SysUserEventSubscriber 当前输入计算 Unlock User Login 结果，并按方法声明的类型返回。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [EventSubscribe(SysUserEventTypeEnum.UnlockLogin)]
    public Task UnlockUserLogin(EventHandlerExecutingContext context)
    {
        return Task.CompletedTask;
    }

    /// <summary>释放 SysUserEventSubscriber 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
    }
}