// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.K3Cloud.Service;

/// <summary>
/// 金蝶云星空ERP接口
/// </summary>
[HttpClientName("K3Cloud")]
public interface IK3CloudApi : IHttpDeclarative
{
    /// <summary>校验 IK3CloudApi 的 Validate User 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <param name="action">action 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("Kingdee.BOS.WebApi.ServicesStub.AuthService.ValidateUser.common.kdsvc")]
    Task<K3CloudLoginOutput> ValidateUser([Body] K3CloudLoginInput input, Action<HttpClient, HttpResponseMessage> action = default);

    /// <summary>更新 IK3CloudApi 的 Save 状态，并同步相关业务数据。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <param name="action">action 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("Kingdee.BOS.WebApi.ServicesStub.DynamicFormService.Save.common.kdsvc")]
    Task<K3CloudPushResultOutput> Save<T>([Body] K3CloudBaeInput<T> input, Action<HttpClient, HttpRequestMessage> action = default);

    /// <summary>执行 IK3CloudApi 的 Submit 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <param name="action">action 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("Kingdee.BOS.WebApi.ServicesStub.DynamicFormService.Submit.common.kdsvc")]
    Task<K3CloudPushResultOutput> Submit<T>([Body] K3CloudBaeInput<T> input, Action<HttpClient, HttpRequestMessage> action = default);

    /// <summary>执行 IK3CloudApi 的 Audit 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="input">符合当前接口契约的请求输入数据。</param>
    /// <param name="action">action 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Post("Kingdee.BOS.WebApi.ServicesStub.DynamicFormService.Audit.common.kdsvc")]
    Task<K3CloudPushResultOutput> Audit<T>([Body] K3CloudBaeInput<T> input, Action<HttpClient, HttpRequestMessage> action = default);
}