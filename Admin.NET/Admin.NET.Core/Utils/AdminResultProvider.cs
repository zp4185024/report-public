// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

namespace Admin.NET.Core;

/// <summary>
/// 全局规范化结果
/// </summary>
[UnifyModel(typeof(AdminResult<>))]
public class AdminResultProvider : IUnifyResultProvider
{
    /// <summary>
    /// JWT 授权异常返回值
    /// </summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="metadata">metadata 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public IActionResult OnAuthorizeException(DefaultHttpContext context, ExceptionMetadata metadata)
    {
        return new JsonResult(RESTfulResult(metadata.StatusCode, data: metadata.Data, msg: metadata.Errors), UnifyContext.GetSerializerSettings(context));
    }

    /// <summary>响应 AdminResultProvider 的 On Exception 事件，并协调相关状态和后续操作。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="metadata">metadata 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public IActionResult OnException(ExceptionContext context, ExceptionMetadata metadata)
    {
        return new JsonResult(RESTfulResult(metadata.StatusCode, data: metadata.Data, msg: metadata.Errors), UnifyContext.GetSerializerSettings(context));
    }

    /// <summary>响应 AdminResultProvider 的 On Succeeded 事件，并协调相关状态和后续操作。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="data">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public IActionResult OnSucceeded(ActionExecutedContext context, object data)
    {
        return new JsonResult(RESTfulResult(StatusCodes.Status200OK, true, data), UnifyContext.GetSerializerSettings(context));
    }

    /// <summary>响应 AdminResultProvider 的 On Validate Failed 事件，并协调相关状态和后续操作。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="metadata">metadata 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public IActionResult OnValidateFailed(ActionExecutingContext context, ValidationMetadata metadata)
    {
        return new JsonResult(RESTfulResult(metadata.StatusCode ?? StatusCodes.Status400BadRequest, data: metadata.Data, msg: metadata.ValidationResult), UnifyContext.GetSerializerSettings(context));
    }

    /// <summary>
    /// 特定状态码返回值
    /// </summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="statusCode">需要读取、校验或转换的业务值。</param>
    /// <param name="unifyResultSettings">控制本次处理行为的配置选项。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task OnResponseStatusCodes(HttpContext context, int statusCode, UnifyResultSettingsOptions unifyResultSettings)
    {
        // 设置响应状态码
        UnifyContext.SetResponseStatusCodes(context, statusCode, unifyResultSettings);

        switch (statusCode)
        {
            // 处理 401 状态码
            case StatusCodes.Status401Unauthorized:
                var msg = "401 登录已过期，请重新登录";
                // 若存在身份验证失败消息，则返回消息内容
                if (context.Items.TryGetValue(SignatureAuthenticationDefaults.AuthenticateFailMsgKey, out var authFailMsg))
                    msg = authFailMsg + "";
                await context.Response.WriteAsJsonAsync(RESTfulResult(statusCode, msg: msg),
                    App.GetOptions<JsonOptions>()?.JsonSerializerOptions);
                break;
            // 处理 403 状态码
            case StatusCodes.Status403Forbidden:
                await context.Response.WriteAsJsonAsync(RESTfulResult(statusCode, msg: "403 禁止访问，没有权限"),
                    App.GetOptions<JsonOptions>()?.JsonSerializerOptions);
                break;
            // 处理 302 状态码
            case StatusCodes.Status302Found:
                if (context.Response.Headers.TryGetValue("Location", out var redirectUrl))
                {
                    context.Response.Redirect(redirectUrl);
                }
                else
                {
                    var errorMessage = "302 跳转失败，没有提供 Location 头信息";
                    await context.Response.WriteAsJsonAsync(RESTfulResult(statusCode, msg: errorMessage),
                        App.GetOptions<JsonOptions>()?.JsonSerializerOptions);
                }
                break;
        }
    }

    /// <summary>根据 AdminResultProvider 当前输入计算 Ok 结果，并按方法声明的类型返回。</summary>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="data">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static AdminResult<object> Ok(string message, object data = default)
    {
        return RESTfulResult(StatusCodes.Status200OK, true, data, message);
    }

    /// <summary>根据 AdminResultProvider 当前输入计算 Error 结果，并按方法声明的类型返回。</summary>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="data">符合当前接口契约的请求输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static AdminResult<object> Error(string message, int code = StatusCodes.Status400BadRequest, object data = default)
    {
        return RESTfulResult(code, false, data, message);
    }

    /// <summary>
    /// 返回 RESTful 风格结果集
    /// </summary>
    /// <param name="statusCode">需要读取、校验或转换的业务值。</param>
    /// <param name="succeeded">succeeded 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="data">符合当前接口契约的请求输入数据。</param>
    /// <param name="msg">msg 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static AdminResult<object> RESTfulResult(int statusCode, bool succeeded = default, object data = default, object msg = default)
    {
        //// 统一返回值脱敏处理
        //if (data?.GetType() == typeof(String))
        //{
        //    data = App.GetRequiredService<ISensitiveDetectionProvider>().ReplaceAsync(data.ToString(), '*').GetAwaiter().GetResult();
        //}
        //else if (data?.GetType() == typeof(JsonResult))
        //{
        //    data = App.GetRequiredService<ISensitiveDetectionProvider>().ReplaceAsync(JSON.Serialize(data), '*').GetAwaiter().GetResult();
        //}

        return new AdminResult<object>
        {
            Code = statusCode,
            Message = msg is null or string ? (msg + "") : JSON.Serialize(msg),
            Result = data,
            Type = succeeded ? "success" : "error",
            Extras = UnifyContext.Take(),
            Time = DateTime.Now
        };
    }
}

/// <summary>
/// 全局返回结果
/// </summary>
/// <typeparam name="T"></typeparam>
public class AdminResult<T>
{
    /// <summary>
    /// 状态码
    /// </summary>
    public int Code { get; set; }

    /// <summary>
    /// 类型success、warning、error
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// 数据
    /// </summary>
    public T Result { get; set; }

    /// <summary>
    /// 附加数据
    /// </summary>
    public object Extras { get; set; }

    /// <summary>
    /// 时间
    /// </summary>
    public DateTime Time { get; set; }
}