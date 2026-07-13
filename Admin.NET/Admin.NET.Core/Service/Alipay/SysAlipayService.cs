// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Aop.Api;
using Aop.Api.Domain;
using Aop.Api.Request;
using Aop.Api.Response;
using Aop.Api.Util;
using Microsoft.AspNetCore.Hosting;
using NewLife.Reflection;

namespace Admin.NET.Core.Service;

/// <summary>
/// 支付宝支付服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 240)]
public class SysAlipayService : IDynamicApiController, ITransient
{
    private readonly IWebHostEnvironment _webHostEnvironment;
    private readonly SysConfigService _sysConfigService;
    private readonly List<IAopClient> _alipayClientList;
    private readonly IHttpContextAccessor _httpContext;
    private readonly AlipayOptions _option;
    private readonly ISqlSugarClient _db;

    public SysAlipayService(
        ISqlSugarClient db,
        IHttpContextAccessor httpContext,
        SysConfigService sysConfigService,
        IWebHostEnvironment webHostEnvironment,
        IOptions<AlipayOptions> alipayOptions)
    {
        _db = db;
        _httpContext = httpContext;
        _sysConfigService = sysConfigService;
        _option = alipayOptions.Value;
        _webHostEnvironment = webHostEnvironment;

        // 初始化支付宝客户端列表
        _alipayClientList = [];
        foreach (var account in _option.AccountList) _alipayClientList.Add(_option.GetClient(account));
    }

    /// <summary>
    /// 获取授权信息 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [NonUnify]
    [AllowAnonymous]
    [DisplayName("获取授权信息")]
    [ApiDescriptionSettings(Name = "AuthInfo"), HttpGet]
    public ActionResult GetAuthInfo([FromQuery] AlipayAuthInfoInput input)
    {
        var type = input.UserId?.Split('-').FirstOrDefault().ToInt();
        var userId = input.UserId?.Split('-').LastOrDefault().ToLong();
        var account = _option.AccountList.FirstOrDefault();
        var alipayClient = _alipayClientList.First();

        // 当前网页接口地址
        var currentUrl = $"{_option.AppAuthUrl}{_httpContext.HttpContext!.Request.Path}?userId={input.UserId}";
        if (string.IsNullOrEmpty(input.AuthCode))
        {
            // 重新授权
            var url = $"{_option.AuthUrl}?app_id={account!.AppId}&scope=auth_user&redirect_uri={currentUrl}";
            return new RedirectResult(url);
        }

        // 组装授权请求参数
        AlipaySystemOauthTokenRequest request = new()
        {
            GrantType = AlipayConst.GrantType,
            Code = input.AuthCode
        };
        AlipaySystemOauthTokenResponse response = alipayClient.CertificateExecute(request);

        // token换取用户信息
        AlipayUserInfoShareRequest infoShareRequest = new();
        AlipayUserInfoShareResponse info = alipayClient.CertificateExecute(infoShareRequest, response.AccessToken);

        // 记录授权信息
        var entity = _db.Queryable<SysAlipayAuthInfo>().First(u =>
            (!string.IsNullOrWhiteSpace(u.UserId) && u.UserId == info.UserId) ||
            (!string.IsNullOrWhiteSpace(u.OpenId) && u.OpenId == info.OpenId)) ?? new();
        entity.Copy(info, excludes: [nameof(SysAlipayAuthInfo.Gender), nameof(SysAlipayAuthInfo.Age)]);
        entity.Age = int.Parse(info.Age);
        entity.Gender = info.Gender switch
        {
            "m" => GenderEnum.Male,
            "f" => GenderEnum.Female,
            _ => GenderEnum.Unknown
        };
        entity.AppId = account!.AppId;
        if (entity.Id <= 0) _db.Insertable(entity).ExecuteCommand();
        else _db.Updateable(entity).ExecuteCommand();

        // 执行完，重定向到指定界面
        //var authPageUrl = _sysConfigService.GetConfigValueByCode<string>(ConfigConst.AlipayAuthPageUrl + type).Result;
        //return new RedirectResult(authPageUrl);
        return new RedirectResult(_option.AppAuthUrl + "/index.html");
    }

    /// <summary>
    /// 支付回调 🔖
    /// </summary>
    /// <returns></returns>
    [AllowAnonymous]
    [DisplayName("支付回调")]
    [ApiDescriptionSettings(Name = "Notify"), HttpPost]
    public string Notify()
    {
        SortedDictionary<string, string> sorted = [];
        foreach (string key in _httpContext.HttpContext!.Request.Form.Keys)
            sorted.Add(key, _httpContext.HttpContext.Request.Form[key]);

        var account = _option.AccountList.FirstOrDefault();
        string alipayPublicKey = Path.Combine(_webHostEnvironment.ContentRootPath, account!.AlipayPublicCertPath!.Replace('/', '\\').TrimStart('\\'));
        bool signVerified = AlipaySignature.RSACertCheckV1(sorted, alipayPublicKey, "UTF-8", account.SignType); // 调用SDK验证签名
        if (!signVerified) throw Oops.Oh("交易失败");

        // 更新交易记录
        var outTradeNo = sorted.GetValueOrDefault("out_trade_no");
        var transaction = _db.Queryable<SysAlipayTransaction>().First(x => x.OutTradeNo == outTradeNo) ?? throw Oops.Oh("交易记录不存在");
        transaction.TradeNo = sorted.GetValueOrDefault("trade_no");
        transaction.TradeStatus = sorted.GetValueOrDefault("trade_status");
        transaction.FinishTime = sorted.ContainsKey("gmt_payment") ? DateTime.Parse(sorted.GetValueOrDefault("gmt_payment")) : null;
        transaction.BuyerLogonId = sorted.GetValueOrDefault("buyer_logon_id");
        transaction.BuyerUserId = sorted.GetValueOrDefault("buyer_user_id");
        transaction.SellerUserId = sorted.GetValueOrDefault("seller_id");
        transaction.Remark = sorted.GetValueOrDefault("remark");
        _db.Updateable(transaction).ExecuteCommand();

        return "success";
    }

    /// <summary>
    ///  统一收单下单并支付页面接口 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("统一收单下单并支付页面接口")]
    [ApiDescriptionSettings(Name = "AlipayTradePagePay"), HttpPost]
    public string AlipayTradePagePay(AlipayTradePagePayInput input)
    {
        // 创建交易记录，状态为等待支付
        var transactionRecord = new SysAlipayTransaction
        {
            AppId = _option.AccountList.First().AppId,
            OutTradeNo = input.OutTradeNo,
            TotalAmount = input.TotalAmount.ToDecimal(),
            TradeStatus = "WAIT_PAY", // 等待支付
            CreateTime = DateTime.Now,
            Subject = input.Subject,
            Body = input.Body,
            Remark = "等待用户支付"
        };
        _db.Insertable(transactionRecord).ExecuteCommand();

        // 设置支付页面请求，并组装业务参数model，设置异步通知接收地址
        AlipayTradeWapPayRequest request = new();
        request.SetBizModel(new AlipayTradeWapPayModel()
        {
            Subject = input.Subject,
            OutTradeNo = input.OutTradeNo,
            TotalAmount = input.TotalAmount,
            Body = input.Body,
            ProductCode = "QUICK_WAP_WAY",
            TimeExpire = input.TimeoutExpress
        });
        request.SetNotifyUrl(_option.NotifyUrl);

        var alipayClient = _alipayClientList.First();
        var response = alipayClient.SdkExecute(request);
        if (response.IsError) throw Oops.Oh(response.SubMsg);
        return $"{_option.ServerUrl}?{response.Body}";
    }

    /// <summary>
    ///  交易预创建 🔖
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    [DisplayName("交易预创建")]
    [ApiDescriptionSettings(Name = "AlipayPreCreate"), HttpPost]
    public string AlipayPreCreate(AlipayPreCreateInput input)
    {
        // 创建交易记录，状态为等待支付
        var transactionRecord = new SysAlipayTransaction
        {
            AppId = _option.AccountList.First().AppId,
            OutTradeNo = input.OutTradeNo,
            TotalAmount = input.TotalAmount.ToDecimal(),
            TradeStatus = "WAIT_PAY", // 等待支付
            CreateTime = DateTime.Now,
            Subject = input.Subject,
            Remark = "等待用户支付"
        };
        _db.Insertable(transactionRecord).ExecuteCommand();

        // 设置异步通知接收地址，并组装业务参数model
        AlipayTradePrecreateRequest request = new();
        request.SetNotifyUrl(_option.NotifyUrl);
        request.SetBizModel(new AlipayTradePrecreateModel()
        {
            Subject = input.Subject,
            OutTradeNo = input.OutTradeNo,
            TotalAmount = input.TotalAmount,
            TimeoutExpress = input.TimeoutExpress
        });

        var alipayClient = _alipayClientList.First();
        var response = alipayClient.CertificateExecute(request);
        if (response.IsError) throw Oops.Oh(response.SubMsg);
        return response.QrCode;
    }

    /// <summary>
    /// 单笔转账到支付宝账户
    ///  https://opendocs.alipay.com/open/62987723_alipay.fund.trans.uni.transfer
    /// </summary>
    [NonAction]
    public async Task<AlipayFundTransUniTransferResponse> Transfer(AlipayFundTransUniTransferInput input)
    {
        var account = _option.AccountList.FirstOrDefault(u => u.AppId == input.AppId) ?? throw Oops.Oh("未找到商户支付宝账号");
        var alipayClient = _option.GetClient(account);

        // 构造请求参数以调用接口
        AlipayFundTransUniTransferRequest request = new();
        AlipayFundTransUniTransferModel model = new()
        {
            BizScene = AlipayConst.BizScene,
            ProductCode = AlipayConst.ProductCode,
            OutBizNo = input.OutBizNo, // 商家订单
            TransAmount = $"{input.TransAmount}:F2", // 订单总金额
            OrderTitle = input.OrderTitle, // 业务标题
            Remark = input.Remark, // 业务备注
            PayeeInfo = new() // 收款方信息
            {
                CertType = input.CertType?.ToString(),
                CertNo = input.CertNo,
                Identity = input.Identity,
                Name = input.Name,
                IdentityType = input.IdentityType.ToString()
            },
            BusinessParams = input.PayerShowNameUseAlias ? "{\"payer_show_name_use_alias\":\"true\"}" : null
        };

        request.SetBizModel(model);
        var response = alipayClient.CertificateExecute(request);

        // 保存转账记录
        await _db.Insertable(new SysAlipayTransaction
        {
            UserId = input.UserId,
            AppId = input.AppId,
            TradeNo = response.OrderId,
            OutTradeNo = input.OutBizNo,
            TotalAmount = response.Amount.ToDecimal(),
            TradeStatus = response.Code == "10000" ? "SUCCESS" : "FAILED",
            Subject = input.OrderTitle,
            ErrorInfo = response.SubMsg,
            Remark = input.Remark
        }).ExecuteCommandAsync();

        return response;
    }
}