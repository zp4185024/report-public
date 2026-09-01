// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Aop.Api.Response;

namespace Admin.NET.Core.Service;

/// <summary>
/// 支付宝回调接口
/// </summary>
public abstract class IAlipayNotify
{
    /// <summary>根据 IAlipayNotify 当前输入执行 Top Up Callback 规则，更新关联状态并返回该操作产生的结果。</summary>
    /// <param name="type">交易类型</param>
    /// <param name="tradeNo">交易id</param>
    public abstract bool TopUpCallback(long type, long tradeNo);

    /// <summary>根据 IAlipayNotify 当前输入执行 Scan Callback 规则，更新关联状态并返回该操作产生的结果。</summary>
    /// <param name="type">决定处理分支、数据类型或运行状态的枚举值。</param>
    /// <param name="userId">需要查询、更新或删除的业务记录标识。</param>
    /// <param name="response">上一步或远程调用返回的处理结果。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public abstract bool ScanCallback(long type, long userId, AlipayUserInfoShareResponse response);
}