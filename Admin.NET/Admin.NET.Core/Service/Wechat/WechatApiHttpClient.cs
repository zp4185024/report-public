// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Newtonsoft.Json;

namespace Admin.NET.Core.Service;

/// <summary>
/// 微信API客户端
/// </summary>
public partial class WechatApiClientFactory : ISingleton
{
    private readonly IHttpClientFactory _httpClientFactory;
    public readonly WechatOptions _wechatOptions;
    private readonly SysCacheService _sysCacheService;

    public WechatApiClientFactory(IHttpClientFactory httpClientFactory, IOptions<WechatOptions> wechatOptions, SysCacheService sysCacheService)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _wechatOptions = wechatOptions.Value ?? throw new ArgumentNullException(nameof(wechatOptions));
        _sysCacheService = sysCacheService;
    }

    /// <summary>
    /// 微信公众号
    /// </summary>
    /// <returns></returns>
    public WechatApiClient CreateWechatClient()
    {
        if (string.IsNullOrEmpty(_wechatOptions.WechatAppId) || string.IsNullOrEmpty(_wechatOptions.WechatAppSecret))
            throw Oops.Oh("微信公众号配置错误");

        var client = WechatApiClientBuilder.Create(new WechatApiClientOptions()
        {
            AppId = _wechatOptions.WechatAppId,
            AppSecret = _wechatOptions.WechatAppSecret,
            PushToken = _wechatOptions.WechatToken,
            PushEncodingAESKey = _wechatOptions.WechatEncodingAESKey,
        })
        .UseHttpClient(_httpClientFactory.CreateClient(), disposeClient: false) // 设置 HttpClient 不随客户端一同销毁
        .Build();

        client.Configure(config =>
        {
            JsonSerializerSettings jsonSerializerSettings = NewtonsoftJsonSerializer.GetDefaultSerializerSettings();
            jsonSerializerSettings.Formatting = Formatting.Indented;
            config.JsonSerializer = new NewtonsoftJsonSerializer(jsonSerializerSettings); // 指定 System.Text.Json JSON序列化
                                                                                          // config.JsonSerializer = new SystemTextJsonSerializer(jsonSerializerOptions); // 指定 Newtonsoft.Json  JSON序列化
        });

        return client;
    }

    /// <summary>
    /// 微信小程序
    /// </summary>
    /// <returns></returns>
    public WechatApiClient CreateWxOpenClient()
    {
        if (string.IsNullOrEmpty(_wechatOptions.WxOpenAppId) || string.IsNullOrEmpty(_wechatOptions.WxOpenAppSecret))
            throw Oops.Oh("微信小程序配置错误");

        var client = WechatApiClientBuilder.Create(new WechatApiClientOptions()
        {
            AppId = _wechatOptions.WxOpenAppId,
            AppSecret = _wechatOptions.WxOpenAppSecret,
            PushToken = _wechatOptions.WxToken,
            PushEncodingAESKey = _wechatOptions.WxEncodingAESKey,
        })
        .UseHttpClient(_httpClientFactory.CreateClient(), disposeClient: false) // 设置 HttpClient 不随客户端一同销毁
        .Build();

        client.Configure(config =>
        {
            JsonSerializerSettings jsonSerializerSettings = NewtonsoftJsonSerializer.GetDefaultSerializerSettings();
            jsonSerializerSettings.Formatting = Formatting.Indented;
            config.JsonSerializer = new NewtonsoftJsonSerializer(jsonSerializerSettings); // 指定 System.Text.Json JSON序列化
                                                                                          // config.JsonSerializer = new SystemTextJsonSerializer(jsonSerializerOptions); // 指定 Newtonsoft.Json  JSON序列化
        });

        return client;
    }

    /// <summary>
    /// 获取微信公众号AccessToken
    /// </summary>
    /// <returns></returns>
    public async Task<string> TryGetWechatAccessTokenAsync()
    {
        if (!_sysCacheService.ExistKey($"WxAccessToken_{_wechatOptions.WechatAppId}") || string.IsNullOrEmpty(_sysCacheService.Get<string>($"WxAccessToken_{_wechatOptions.WechatAppId}")))
        {
            var client = CreateWechatClient();
            var reqCgibinToken = new CgibinTokenRequest();
            var resCgibinToken = await client.ExecuteCgibinTokenAsync(reqCgibinToken);
            if (resCgibinToken.ErrorCode != (int)WechatReturnCodeEnum.请求成功)
                throw Oops.Oh(resCgibinToken.ErrorMessage + " " + resCgibinToken.ErrorCode);
            _sysCacheService.Set($"WxAccessToken_{_wechatOptions.WechatAppId}", resCgibinToken.AccessToken, TimeSpan.FromSeconds(resCgibinToken.ExpiresIn - 60));
        }

        return _sysCacheService.Get<string>($"WxAccessToken_{_wechatOptions.WechatAppId}");
    }

    /// <summary>
    /// 获取微信小程序AccessToken
    /// </summary>
    /// <returns></returns>
    public async Task<string> TryGetWxOpenAccessTokenAsync()
    {
        if (!_sysCacheService.ExistKey($"WxAccessToken_{_wechatOptions.WxOpenAppId}") || string.IsNullOrEmpty(_sysCacheService.Get<string>($"WxAccessToken_{_wechatOptions.WxOpenAppId}")))
        {
            var client = CreateWxOpenClient();
            var reqCgibinToken = new CgibinTokenRequest();
            var resCgibinToken = await client.ExecuteCgibinTokenAsync(reqCgibinToken);
            if (resCgibinToken.ErrorCode != (int)WechatReturnCodeEnum.请求成功)
                throw Oops.Oh(resCgibinToken.ErrorMessage + " " + resCgibinToken.ErrorCode);
            _sysCacheService.Set($"WxAccessToken_{_wechatOptions.WxOpenAppId}", resCgibinToken.AccessToken, TimeSpan.FromSeconds(resCgibinToken.ExpiresIn - 60));
        }

        return _sysCacheService.Get<string>($"WxAccessToken_{_wechatOptions.WxOpenAppId}");
    }

    /// <summary>
    /// 检查微信公众号AccessToken
    /// </summary>
    /// <returns></returns>
    public async Task CheckWechatAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_wechatOptions.WechatAppId) || string.IsNullOrEmpty(_wechatOptions.WechatAppSecret)) return;

        var req = new CgibinOpenApiQuotaGetRequest
        {
            AccessToken = await TryGetWechatAccessTokenAsync(),
            CgiPath = "/cgi-bin/token"
        };
        var client = CreateWechatClient();
        var res = await client.ExecuteCgibinOpenApiQuotaGetAsync(req);

        var originColor = Console.ForegroundColor;
        if (res.ErrorCode != (int)WechatReturnCodeEnum.请求成功)
        {
            _sysCacheService.Remove($"WxAccessToken_{_wechatOptions.WechatAppId}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("【" + DateTime.Now + "】" + _wechatOptions.WxOpenAppId + " 微信公众号令牌 无效");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("【" + DateTime.Now + "】" + _wechatOptions.WxOpenAppId + " 微信公众号令牌 有效");
        }
        Console.ForegroundColor = originColor;
    }

    /// <summary>
    /// 检查微信小程序AccessToken
    /// </summary>
    /// <returns></returns>
    public async Task CheckWxOpenAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_wechatOptions.WxOpenAppId) || string.IsNullOrEmpty(_wechatOptions.WxOpenAppSecret)) return;

        var req = new CgibinOpenApiQuotaGetRequest
        {
            AccessToken = await TryGetWxOpenAccessTokenAsync(),
            CgiPath = "/cgi-bin/token"
        };
        var client = CreateWxOpenClient();
        var res = await client.ExecuteCgibinOpenApiQuotaGetAsync(req);

        var originColor = Console.ForegroundColor;
        if (res.ErrorCode != (int)WechatReturnCodeEnum.请求成功)
        {
            _sysCacheService.Remove($"WxAccessToken_{_wechatOptions.WxOpenAppId}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("【" + DateTime.Now + "】" + _wechatOptions.WxOpenAppId + " 微信小程序令牌 无效");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("【" + DateTime.Now + "】" + _wechatOptions.WxOpenAppId + " 微信小程序令牌 有效");
        }
        Console.ForegroundColor = originColor;
    }

    /// <summary>
    /// 获取微信JS接口临时票据jsapi_ticket
    /// </summary>
    /// <returns></returns>
    public async Task<string> TryGetWechatJsApiTicketAsync()
    {
        if (!_sysCacheService.ExistKey($"WxJsApiTicket_{_wechatOptions.WechatAppId}") || string.IsNullOrEmpty(_sysCacheService.Get<string>($"WxJsApiTicket_{_wechatOptions.WechatAppId}")))
        {
            var accessToken = await TryGetWechatAccessTokenAsync();
            var client = CreateWechatClient();
            var request = new CgibinTicketGetTicketRequest()
            {
                AccessToken = accessToken
            };
            var response = await client.ExecuteCgibinTicketGetTicketAsync(request);
            if (!response.IsSuccessful())
                throw Oops.Oh(response.ErrorMessage + " " + response.ErrorCode);
            _sysCacheService.Set($"WxJsApiTicket_{_wechatOptions.WechatAppId}", response.Ticket, TimeSpan.FromSeconds(response.ExpiresIn - 60));
        }
        return _sysCacheService.Get<string>($"WxJsApiTicket_{_wechatOptions.WechatAppId}");
    }
}