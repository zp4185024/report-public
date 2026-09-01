// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using AlibabaCloud.SDK.Dysmsapi20170525.Models;
using TencentCloud.Common;
using TencentCloud.Common.Profile;
using TencentCloud.Sms.V20190711;

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统短信服务 🧩
/// </summary>
[AllowAnonymous]
[ApiDescriptionSettings(Order = 150)]
public class SysSmsService : IDynamicApiController, ITransient
{
    private readonly SMSOptions _smsOptions;
    private readonly SysCacheService _sysCacheService;

    /// <summary>初始化 SysSmsService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="smsOptions">控制本次处理行为的配置选项。</param>
    /// <param name="sysCacheService">sysCacheService 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public SysSmsService(IOptions<SMSOptions> smsOptions,
        SysCacheService sysCacheService)
    {
        _smsOptions = smsOptions.Value;
        _sysCacheService = sysCacheService;
    }

    /// <summary>执行 SysSmsService 的 Send Sms 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="phoneNumber">phoneNumber 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="templateId">短信模板id</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("发送短信")]
    public async Task SendSms([Required] string phoneNumber, string templateId = "0")
    {
        if (_smsOptions.Custom != null && _smsOptions.Custom.Enabled && !string.IsNullOrWhiteSpace(_smsOptions.Custom.ApiUrl))
        {
            await CustomSendSms(phoneNumber, templateId);
        }
        else if (!string.IsNullOrWhiteSpace(_smsOptions.Aliyun.AccessKeyId) && !string.IsNullOrWhiteSpace(_smsOptions.Aliyun.AccessKeySecret))
        {
            await AliyunSendSms(phoneNumber, templateId);
        }
        else
        {
            await TencentSendSms(phoneNumber, templateId);
        }
    }

    /// <summary>校验 SysSmsService 的 Verify Code 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("校验短信验证码")]
    public bool VerifyCode(SmsVerifyCodeInput input)
    {
        var verifyCode = _sysCacheService.Get<string>($"{CacheConst.KeyPhoneVerCode}{input.Phone}");

        if (string.IsNullOrWhiteSpace(verifyCode)) throw Oops.Oh("验证码不存在或已失效，请重新获取！");

        if (verifyCode != input.Code) throw Oops.Oh("验证码错误！");

        return true;
    }

    /// <summary>调用 SysSmsService 的异步依赖完成 Aliyun Send Sms 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="templateId">短信模板id</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("阿里云发送短信")]
    public async Task AliyunSendSms([Required] string phoneNumber, string templateId = "0")
    {
        if (!phoneNumber.TryValidate(ValidationTypes.PhoneNumber).IsValid) throw Oops.Oh("请正确填写手机号码");

        // 生成随机验证码
        var random = new Random();
        var verifyCode = random.Next(100000, 999999);

        var templateParam = new
        {
            code = verifyCode
        };

        var client = CreateAliyunClient();
        var template = _smsOptions.Aliyun.GetTemplate(templateId);
        var sendSmsRequest = new SendSmsRequest
        {
            PhoneNumbers = phoneNumber, // 待发送手机号, 多个以逗号分隔
            SignName = template.SignName, // 短信签名
            TemplateCode = template.TemplateCode, // 短信模板
            TemplateParam = templateParam.ToJson(), // 模板中的变量替换JSON串
            OutId = YitIdHelper.NextId().ToString()
        };
        var sendSmsResponse = await client.SendSmsAsync(sendSmsRequest);
        if (sendSmsResponse.Body.Code == "OK" && sendSmsResponse.Body.Message == "OK")
        {
            // var bizId = sendSmsResponse.Body.BizId;
            _sysCacheService.Set($"{CacheConst.KeyPhoneVerCode}{phoneNumber}", verifyCode, TimeSpan.FromSeconds(_smsOptions.VerifyCodeExpireSeconds));
        }
        else
        {
            throw Oops.Oh($"短信发送失败：{sendSmsResponse.Body.Code}-{sendSmsResponse.Body.Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>调用 SysSmsService 的异步依赖完成 Aliyun Send Sms Template 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="templateParam">短信内容</param>
    /// <param name="templateId">短信模板id</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("发送短信模板")]
    public async Task AliyunSendSmsTemplate([Required] string phoneNumber, [Required] dynamic templateParam, string templateId)
    {
        if (!phoneNumber.TryValidate(ValidationTypes.PhoneNumber).IsValid) throw Oops.Oh("请正确填写手机号码");

        if (string.IsNullOrWhiteSpace(templateParam.ToString())) throw Oops.Oh("短信内容不能为空");

        var client = CreateAliyunClient();
        var template = _smsOptions.Aliyun.GetTemplate(templateId);
        var sendSmsRequest = new SendSmsRequest
        {
            PhoneNumbers = phoneNumber, // 待发送手机号, 多个以逗号分隔
            SignName = template.SignName, // 短信签名
            TemplateCode = template.TemplateCode, // 短信模板
            TemplateParam = templateParam.ToString(), // 模板中的变量替换JSON串
            OutId = YitIdHelper.NextId().ToString()
        };
        var sendSmsResponse = await client.SendSmsAsync(sendSmsRequest);
        if (sendSmsResponse.Body.Code == "OK" && sendSmsResponse.Body.Message == "OK")
        {
        }
        else
        {
            throw Oops.Oh($"短信发送失败：{sendSmsResponse.Body.Code}-{sendSmsResponse.Body.Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>调用 SysSmsService 的异步依赖完成 Tencent Send Sms 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="phoneNumber">phoneNumber 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="templateId">短信模板id</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("腾讯云发送短信")]
    public async Task TencentSendSms([Required] string phoneNumber, string templateId = "0")
    {
        if (!phoneNumber.TryValidate(ValidationTypes.PhoneNumber).IsValid) throw Oops.Oh("请正确填写手机号码");

        // 生成随机验证码
        var random = new Random();
        var verifyCode = random.Next(100000, 999999);

        // 实例化要请求产品的client对象，clientProfile是可选的
        var client = new SmsClient(CreateTencentClient(), "ap-guangzhou", new ClientProfile() { HttpProfile = new HttpProfile() { Endpoint = ("sms.tencentcloudapi.com") } });
        var template = _smsOptions.Tencentyun.GetTemplate(templateId);
        // 实例化一个请求对象,每个接口都会对应一个request对象
        var req = new TencentCloud.Sms.V20190711.Models.SendSmsRequest
        {
            PhoneNumberSet = new string[] { "+86" + phoneNumber.Trim(',') },
            SmsSdkAppid = _smsOptions.Tencentyun.SdkAppId,
            Sign = template.SignName,
            TemplateID = template.TemplateCode,
            TemplateParamSet = new string[] { verifyCode.ToString() }
        };

        // 返回的resp是一个SendSmsResponse的实例，与请求对象对应
        TencentCloud.Sms.V20190711.Models.SendSmsResponse resp = client.SendSmsSync(req);

        if (resp.SendStatusSet[0].Code == "Ok" && resp.SendStatusSet[0].Message == "send success")
        {
            // var bizId = sendSmsResponse.Body.BizId;
            _sysCacheService.Set($"{CacheConst.KeyPhoneVerCode}{phoneNumber}", verifyCode, TimeSpan.FromSeconds(_smsOptions.VerifyCodeExpireSeconds));
        }
        else
        {
            throw Oops.Oh($"短信发送失败：{resp.SendStatusSet[0].Code}-{resp.SendStatusSet[0].Message}");
        }

        await Task.CompletedTask;
    }

    /// <summary>构建 SysSmsService 所需的 Create Aliyun Client 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private AlibabaCloud.SDK.Dysmsapi20170525.Client CreateAliyunClient()
    {
        var config = new AlibabaCloud.OpenApiClient.Models.Config
        {
            AccessKeyId = _smsOptions.Aliyun.AccessKeyId,
            AccessKeySecret = _smsOptions.Aliyun.AccessKeySecret,
            Endpoint = "dysmsapi.aliyuncs.com"
        };
        return new AlibabaCloud.SDK.Dysmsapi20170525.Client(config);
    }

    /// <summary>构建 SysSmsService 所需的 Create Tencent Client 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private Credential CreateTencentClient()
    {
        var cred = new Credential
        {
            SecretId = _smsOptions.Tencentyun.AccessKeyId,
            SecretKey = _smsOptions.Tencentyun.AccessKeySecret
        };
        return cred;
    }

    /// <summary>
    /// 自定义短信接口发送短信 📨
    /// </summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="templateId">短信模板id</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [AllowAnonymous]
    [DisplayName("自定义短信接口发送短信")]
    public async Task CustomSendSms([DataValidation(ValidationTypes.PhoneNumber)] string phoneNumber, string templateId = "0")
    {
        if (_smsOptions.Custom == null || !_smsOptions.Custom.Enabled)
            throw Oops.Oh("自定义短信接口未启用");

        if (string.IsNullOrWhiteSpace(_smsOptions.Custom.ApiUrl))
            throw Oops.Oh("自定义短信接口地址未配置");

        // 生成随机验证码
        var verifyCode = Random.Shared.Next(100000, 999999);

        // 获取模板
        var template = _smsOptions.Custom.GetTemplate(templateId);
        if (template == null)
            throw Oops.Oh($"短信模板[{templateId}]不存在");

        // 替换模板内容中的占位符
        var content = template.Content.Replace("{code}", verifyCode.ToString());

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            HttpResponseMessage response;

            //替换URL占位符
            var url = _smsOptions.Custom.ApiUrl
                .Replace("{templateId}", templateId)
                .Replace("{mobile}", phoneNumber)
                .Replace("{content}", Uri.EscapeDataString(content))
                .Replace("{code}", verifyCode.ToString());

            if (_smsOptions.Custom.Method.ToUpper() == "POST")
            {
                // 替换占位符
                var postData = _smsOptions.Custom.PostData?
                    .Replace("{templateId}", templateId)
                    .Replace("{mobile}", phoneNumber)
                    .Replace("{content}", content)
                    .Replace("{code}", verifyCode.ToString());
                HttpContent httpContent = new StringContent(postData ?? string.Empty, Encoding.UTF8, _smsOptions.Custom.ContentType ?? "application/x-www-form-urlencoded");
                response = await httpClient.PostAsync(url, httpContent);
            }
            else
            {
                // GET 请求
                response = await httpClient.GetAsync(url);
            }

            var responseContent = await response.Content.ReadAsStringAsync();

            // 判断是否发送成功
            if (response.IsSuccessStatusCode && responseContent.Contains(_smsOptions.Custom.SuccessFlag))
            {
                if (_smsOptions.Custom.ApiUrl.Contains("{code}") || template.Content.Contains("{code}") || (_smsOptions.Custom.PostData?.Contains("{code}") == true))
                {
                    // 如果模板含有验证码，则添加到缓存
                    _sysCacheService.Set($"{CacheConst.KeyPhoneVerCode}{phoneNumber}", verifyCode, TimeSpan.FromSeconds(_smsOptions.VerifyCodeExpireSeconds));
                }
            }
            else
            {
                throw Oops.Oh($"短信发送失败：{responseContent}");
            }
        }
        catch (Exception ex)
        {
            throw Oops.Oh($"短信发送异常：{ex.Message}");
        }
    }
}