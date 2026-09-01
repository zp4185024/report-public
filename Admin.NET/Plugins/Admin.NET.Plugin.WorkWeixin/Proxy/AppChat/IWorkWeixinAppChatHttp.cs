// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Plugin.WorkWeixin.Proxy.AppChat;

/// <summary>
/// 群聊会话远程调用服务
/// </summary>
public interface IWorkWeixinAppChatHttp : IHttpDeclarative
{
    /// <summary>构建 IWorkWeixinAppChatHttp 所需的 Create 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="accessToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="body">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    /// <see href="https://developer.work.weixin.qq.com/document/path/90245"/>
    [Post("https://qyapi.weixin.qq.com/cgi-bin/appchat/create")]
    Task<CreatAppChatOutput> Create([QueryParam("access_token")] string accessToken, [Body] CreatAppChatInput body);

    /// <summary>更新 IWorkWeixinAppChatHttp 的 Update 状态，并同步相关业务数据。</summary>
    /// <param name="accessToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="body">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    /// <see href="https://developer.work.weixin.qq.com/document/path/98913"/>
    [Post("https://qyapi.weixin.qq.com/cgi-bin/appchat/update")]
    Task<CreatAppChatOutput> Update([QueryParam("access_token")] string accessToken, [Body] UpdateAppChatInput body);

    /// <summary>查询或读取 IWorkWeixinAppChatHttp 所需的 Get 数据，并按调用约定返回结果。</summary>
    /// <param name="accessToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="chatId">需要查询、更新或删除的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    /// <see href="https://developer.work.weixin.qq.com/document/path/98914"/>
    [Get("https://qyapi.weixin.qq.com/cgi-bin/appchat/get")]
    Task<CreatAppChatOutput> Get([QueryParam("access_token")] string accessToken, [QueryParam("chatid")] string chatId);

    /// <summary>执行 IWorkWeixinAppChatHttp 的 Send 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="accessToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="body">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    /// <see href="https://developer.work.weixin.qq.com/document/path/90248"/>
    [Post("https://qyapi.weixin.qq.com/cgi-bin/appchat/send")]
    Task<BaseWorkOutput> Send([QueryParam("access_token")] string accessToken, [Body] SendBaseAppChatInput body);
}