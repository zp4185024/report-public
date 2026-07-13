// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 支付宝支付常量
/// </summary>
[SuppressSniffer]
public class AlipayConst
{
    /// <summary>
    /// 单笔无密转账【业务场景】固定值
    /// </summary>
    public const string BizScene = "DIRECT_TRANSFER";

    /// <summary>
    /// 单笔无密转账【销售产品码】固定值
    /// </summary>
    public const string ProductCode = "TRANS_ACCOUNT_NO_PWD";

    /// <summary>
    /// 交易状态参数名
    /// </summary>
    public const string TradeStatus = "trade_status";

    /// <summary>
    /// 交易成功标识
    /// </summary>
    public const string TradeSuccess = "TRADE_SUCCESS";

    /// <summary>
    /// 授权类型
    /// </summary>
    public const string GrantType = "authorization_code";
}