// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Aop.Api;

namespace Admin.NET.Core;

/// <summary>
/// 支付宝支付配置选项
/// </summary>
public sealed class AlipayOptions : IConfigurableOptions
{
    /// <summary>
    /// 支付宝网关地址
    /// </summary>
    public string ServerUrl { get; init; }

    /// <summary>
    /// 支付宝授权回调地址
    /// </summary>
    public string AuthUrl { get; init; }

    /// <summary>
    /// 应用授权回调地址
    /// </summary>
    public string AppAuthUrl { get; init; }

    /// <summary>
    /// 支付宝 websocket 服务地址
    /// </summary>
    public string WebsocketUrl { get; init; }

    /// <summary>
    /// 应用回调地址
    /// </summary>
    public string NotifyUrl { get; init; }

    /// <summary>
    /// 支付宝根证书存放路径
    /// </summary>
    public string RootCertPath { get; init; }

    /// <summary>
    /// 支付宝商户账号列表
    /// </summary>
    public List<AlipayMerchantAccount> AccountList { get; init; }

    /// <summary>
    /// 获取支付宝客户端
    /// </summary>
    /// <param name="account"></param>
    public DefaultAopClient GetClient(AlipayMerchantAccount account)
    {
        account = account ?? throw new Exception("未找到支付宝商户账号");
        string path = App.WebHostEnvironment.ContentRootPath;
        return new DefaultAopClient(new AlipayConfig
        {
            Format = "json",
            Charset = "UTF-8",
            ServerUrl = ServerUrl,
            AppId = account.AppId,
            SignType = account.SignType,
            PrivateKey = account.PrivateKey,
            EncryptKey = account.EncryptKey,
            RootCertPath = Path.Combine(path, RootCertPath),
            AppCertPath = Path.Combine(path, account.AppCertPath),
            AlipayPublicCertPath = Path.Combine(path, account.AlipayPublicCertPath)
        });
    }
}

/// <summary>
/// 支付宝商户账号信息
/// </summary>
public class AlipayMerchantAccount
{
    /// <summary>
    /// 配置Id
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// 商户名称
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// 商户AppId
    /// </summary>
    public string AppId { get; init; }

    /// <summary>
    /// 应用私钥
    /// </summary>
    public string PrivateKey { get; init; }

    /// <summary>
    /// 从支付宝获取敏感信息时的加密密钥（可选）
    /// </summary>
    public string EncryptKey { get; init; }

    /// <summary>
    /// 加密算法
    /// </summary>
    public string SignType { get; init; }

    /// <summary>
    /// 应用公钥证书路径
    /// </summary>
    public string AppCertPath { get; init; }

    /// <summary>
    /// 支付宝公钥证书路径
    /// </summary>
    public string AlipayPublicCertPath { get; init; }
}