// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 支付宝交易记录表
/// </summary>
[SugarTable(null, "支付宝交易记录表")]
[SysTable]
[SugarIndex("index_{table}_U", nameof(UserId), OrderByType.Asc)]
[SugarIndex("index_{table}_T", nameof(TradeNo), OrderByType.Asc)]
[SugarIndex("index_{table}_O", nameof(OutTradeNo), OrderByType.Asc)]
public class SysAlipayTransaction : EntityBase
{
    /// <summary>
    /// 用户Id
    /// </summary>
    [SugarColumn(ColumnDescription = "用户Id", Length = 64)]
    public long UserId { get; set; }

    /// <summary>
    /// 交易号
    /// </summary>
    [SugarColumn(ColumnDescription = "交易号", Length = 64)]
    public string? TradeNo { get; set; }

    /// <summary>
    /// 商户订单号
    /// </summary>
    [SugarColumn(ColumnDescription = "商户订单号", Length = 64)]
    public string OutTradeNo { get; set; }

    /// <summary>
    /// 交易金额
    /// </summary>
    [SugarColumn(ColumnDescription = "交易金额", Length = 20)]
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// 交易状态
    /// </summary>
    [SugarColumn(ColumnDescription = "交易状态", Length = 32)]
    public string TradeStatus { get; set; }

    /// <summary>
    /// 交易完成时间
    /// </summary>
    [SugarColumn(ColumnDescription = "交易完成时间")]
    public DateTime? FinishTime { get; set; }

    /// <summary>
    /// 交易标题
    /// </summary>
    [SugarColumn(ColumnDescription = "交易标题", Length = 256)]
    public string Subject { get; set; }

    /// <summary>
    /// 交易描述
    /// </summary>
    [SugarColumn(ColumnDescription = "交易描述", Length = 512)]
    public string? Body { get; set; }

    /// <summary>
    /// 买家支付宝账号
    /// </summary>
    [SugarColumn(ColumnDescription = "买家支付宝账号", Length = 128)]
    public string? BuyerLogonId { get; set; }

    /// <summary>
    /// 买家支付宝用户ID
    /// </summary>
    [SugarColumn(ColumnDescription = "买家支付宝用户ID", Length = 32)]
    public string? BuyerUserId { get; set; }

    /// <summary>
    /// 卖家支付宝用户ID
    /// </summary>
    [SugarColumn(ColumnDescription = "卖家支付宝用户ID", Length = 32)]
    public string? SellerUserId { get; set; }

    /// <summary>
    /// 商户AppId
    /// </summary>
    [SugarColumn(ColumnDescription = "商户AppId", Length = 64)]
    public string? AppId { get; set; }

    /// <summary>
    /// 交易扩展信息
    /// </summary>
    [SugarColumn(ColumnDescription = "交易扩展信息", Length = 1024)]
    public string? ExtendInfo { get; set; }

    /// <summary>
    /// 交易异常信息
    /// </summary>
    [SugarColumn(ColumnDescription = "交易扩展信息", Length = 1024)]
    public string? ErrorInfo { get; set; }

    /// <summary>
    /// 交易备注
    /// </summary>
    [SugarColumn(ColumnDescription = "交易备注", Length = 512)]
    public string? Remark { get; set; }
}