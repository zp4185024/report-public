// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

public interface IOnlineUserHub
{
    /// <summary>响应 IOnlineUserHub 的 Online User List 事件，并协调相关状态和后续操作。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task OnlineUserList(OnlineUserList context);

    /// <summary>根据 IOnlineUserHub 当前输入执行 Force Offline 规则，更新关联状态并返回该操作产生的结果。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task ForceOffline(object context);

    /// <summary>根据 IOnlineUserHub 当前输入执行 Public Notice 规则，更新关联状态并返回该操作产生的结果。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task PublicNotice(SysNotice context);

    /// <summary>根据 IOnlineUserHub 当前输入执行 Receive Message 规则，更新关联状态并返回该操作产生的结果。</summary>
    /// <param name="context">本次处理共享的运行上下文和依赖状态。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    Task ReceiveMessage(object context);
}