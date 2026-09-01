// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Admin.NET.Plugin.ApprovalFlow.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Admin.NET.Plugin.ApprovalFlow;

/// <summary>
/// 扩展审批流中间件
/// </summary>
public static class ApprovalFlowMiddlewareExtensions
{
    /// <summary>根据 ApprovalFlowMiddlewareExtensions 当前输入计算 Use Approval Flow 结果，并按方法声明的类型返回。</summary>
    /// <param name="builder">builder 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static IApplicationBuilder UseApprovalFlow(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ApprovalFlowMiddleware>();
    }
}

/// <summary>
/// 审批流中间件
/// </summary>
public class ApprovalFlowMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SysApprovalService _sysApprovalService;

    /// <summary>初始化 ApprovalFlowMiddleware 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="next">next 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public ApprovalFlowMiddleware(RequestDelegate next)
    {
        _next = next;
        _sysApprovalService = App.GetRequiredService<SysApprovalService>();
    }

    /// <summary>执行 ApprovalFlowMiddleware 的 Invoke Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        await _sysApprovalService.MatchApproval(context);

        // 调用下一个中间件
        await _next(context);
    }
}