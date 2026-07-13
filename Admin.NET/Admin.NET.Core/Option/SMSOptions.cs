// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 短信配置选项
/// </summary>
public sealed class SMSOptions : IConfigurableOptions
{
    /// <summary>
    /// 验证码缓存过期时间（秒）
    /// 默认: 60秒
    /// </summary>
    public int VerifyCodeExpireSeconds { get; set; } = 60;

    /// <summary>
    /// Aliyun
    /// </summary>
    public SMSSettings Aliyun { get; set; }

    /// <summary>
    /// Tencentyun
    /// </summary>
    public SMSSettings Tencentyun { get; set; }

    /// <summary>
    /// Custom 自定义短信接口
    /// </summary>
    public CustomSMSSettings Custom { get; set; }
}

public sealed class SMSSettings
{
    /// <summary>
    /// SdkAppId
    /// </summary>
    public string SdkAppId { get; set; }

    /// <summary>
    /// AccessKey ID
    /// </summary>
    public string AccessKeyId { get; set; }

    /// <summary>
    /// AccessKey Secret
    /// </summary>
    public string AccessKeySecret { get; set; }

    /// <summary>
    /// Templates
    /// </summary>
    public List<SmsTemplate> Templates { get; set; }

    /// <summary>
    /// GetTemplate
    /// </summary>
    public SmsTemplate GetTemplate(string id = "0")
    {
        foreach (var template in Templates)
        {
            if (template.Id == id) { return template; }
        }
        return null;
    }
}

public class SmsTemplate
{
    public string Id { get; set; } = string.Empty;
    public string SignName { get; set; }
    public string TemplateCode { get; set; }
    public string Content { get; set; }
}

/// <summary>
/// 自定义短信配置
/// </summary>
public sealed class CustomSMSSettings
{
    /// <summary>
    /// 是否启用自定义短信接口
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// API 接口地址模板
    /// <para>支持占位符: {mobile} - 手机号, {content} - 短信内容, {code} - 验证码</para>
    /// </summary>
    /// <remarks>示例: https://api.xxxx.com/sms?u=xxxx&amp;key=59e03f49c3dbb5033&amp;m={mobile}&amp;c={content}</remarks>
    public string ApiUrl { get; set; }

    /// <summary>
    /// 请求方法 (GET/POST)
    /// </summary>
    public string Method { get; set; } = "GET";

    /// <summary>
    /// POST 请求的 Content-Type (application/json 或 application/x-www-form-urlencoded)
    /// 默认: application/x-www-form-urlencoded
    /// </summary>
    public string ContentType { get; set; } = "application/x-www-form-urlencoded";

    /// <summary>
    /// POST 请求的数据模板（支持占位符）
    /// </summary>
    /// <remarks>
    /// JSON 格式示例: {"mobile":"{mobile}","content":"{content}","apikey":"your_key"} <br />
    /// Form 格式示例: mobile={mobile}&amp;content={content}&amp;apikey=your_key
    /// </remarks>
    public string PostData { get; set; }

    /// <summary>
    /// 成功响应标识（用于判断发送是否成功）
    /// 如果响应内容包含此字符串，则认为发送成功
    /// </summary>
    public string SuccessFlag { get; set; } = "0";

    /// <summary>
    /// 短信模板列表
    /// </summary>
    public List<SmsTemplate> Templates { get; set; }

    /// <summary>
    /// 获取模板
    /// </summary>
    public SmsTemplate GetTemplate(string id = "0")
    {
        foreach (var template in Templates)
        {
            if (template.Id == id) { return template; }
        }
        return null;
    }
}