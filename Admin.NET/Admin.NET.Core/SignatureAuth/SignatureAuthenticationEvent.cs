// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// Signature 身份验证事件
/// </summary>
public class SignatureAuthenticationEvent
{
    /// <summary>初始化 SignatureAuthenticationEvent 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public SignatureAuthenticationEvent()
    {
    }

    /// <summary>
    /// 获取或设置获取 AccessKey 的 AccessSecret 的逻辑处理
    /// </summary>
    public Func<GetAccessSecretContext, Task<string>> OnGetAccessSecret { get; set; }

    /// <summary>
    /// 获取或设置质询的逻辑处理
    /// </summary>
    public Func<SignatureChallengeContext, Task> OnChallenge { get; set; } = _ => Task.CompletedTask;

    /// <summary>
    /// 获取或设置已验证的逻辑处理
    /// </summary>
    public Func<SignatureValidatedContext, Task> OnValidated { get; set; } = _ => Task.CompletedTask;

    /// <summary>
    /// 获取 AccessKey 的 AccessSecret
    /// </summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public virtual Task<string> GetAccessSecret(GetAccessSecretContext context) => OnGetAccessSecret?.Invoke(context) ?? throw new NotImplementedException($"需要提供 {nameof(OnGetAccessSecret)} 实现");

    /// <summary>负责 SignatureAuthenticationEvent 的 Challenge 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public virtual Task Challenge(SignatureChallengeContext context) => OnChallenge?.Invoke(context) ?? Task.CompletedTask;

    /// <summary>校验 SignatureAuthenticationEvent 的 Validated 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public virtual Task Validated(SignatureValidatedContext context) => OnValidated?.Invoke(context) ?? Task.CompletedTask;
}