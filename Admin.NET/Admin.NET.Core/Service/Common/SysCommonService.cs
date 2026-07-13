// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities.Encoders;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Admin.NET.Core.Service;

/// <summary>
/// 系统通用服务 🧩
/// </summary>
[ApiDescriptionSettings(Order = 101)]
[AllowAnonymous]
public class SysCommonService : IDynamicApiController, ITransient
{
    private readonly IApiDescriptionGroupCollectionProvider _apiProvider;
    private readonly SqlSugarRepository<SysUser> _sysUserRep;
    private readonly CDConfigOptions _cdConfigOptions;
    private readonly UserManager _userManager;
    private readonly HttpClient _httpClient;

    public SysCommonService(IApiDescriptionGroupCollectionProvider apiProvider,
        SqlSugarRepository<SysUser> sysUserRep,
        IOptions<CDConfigOptions> giteeOptions,
        IHttpClientFactory httpClientFactory,
        UserManager userManager)
    {
        _sysUserRep = sysUserRep;
        _apiProvider = apiProvider;
        _userManager = userManager;
        _cdConfigOptions = giteeOptions.Value;
        _httpClient = httpClientFactory.CreateClient();
    }

    /// <summary>
    /// 获取国密公钥私钥对 🏆
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取国密公钥私钥对")]
    public SmKeyPairOutput GetSmKeyPair()
    {
        var kp = GM.GenerateKeyPair();
        var privateKey = Hex.ToHexString(((ECPrivateKeyParameters)kp.Private).D.ToByteArray()).ToUpper();
        var publicKey = Hex.ToHexString(((ECPublicKeyParameters)kp.Public).Q.GetEncoded()).ToUpper();

        return new SmKeyPairOutput
        {
            PrivateKey = privateKey,
            PublicKey = publicKey,
        };
    }

    /// <summary>
    /// 获取所有接口/动态API 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("获取所有接口/动态API")]
    public List<ApiOutput> GetApiList()
    {
        var apiList = new List<ApiOutput>();
        foreach (var item in _apiProvider.ApiDescriptionGroups.Items)
        {
            foreach (var apiDescription in item.Items)
            {
                var displayName = apiDescription.TryGetMethodInfo(out MethodInfo apiMethodInfo) ? apiMethodInfo.GetCustomAttribute<DisplayNameAttribute>(true)?.DisplayName : "";

                apiList.Add(new ApiOutput
                {
                    GroupName = item.GroupName,
                    DisplayName = displayName,
                    RouteName = apiDescription.RelativePath
                });
            }
        }
        return apiList;
    }

    /// <summary>
    /// 下载标记错误的临时Excel（全局）
    /// </summary>
    /// <returns></returns>
    [DisplayName("下载标记错误的临时Excel（全局）")]
    public async Task<IActionResult> DownloadErrorExcelTemp([FromQuery] string fileName = null)
    {
        var userId = App.User?.FindFirst(ClaimConst.UserId)?.Value;
        var resultStream = App.GetRequiredService<SysCacheService>().Get<MemoryStream>(CacheConst.KeyExcelTemp + userId);

        if (resultStream == null) throw Oops.Oh("错误标记文件已过期。");

        return await Task.FromResult(new FileStreamResult(resultStream, "application/octet-stream")
        {
            FileDownloadName = $"{(string.IsNullOrEmpty(fileName) ? "错误标记＿" + DateTime.Now.ToString("yyyyMMddhhmmss") : fileName)}.xlsx"
        });
    }

    /// <summary>
    /// 加密字符串 🔖
    /// </summary>
    /// <returns></returns>
    [SuppressMonitor]
    [DisplayName("加密字符串")]
    public dynamic EncryptPlainText([Required] string plainText)
    {
        return CryptogramUtil.Encrypt(plainText);
    }

    /// <summary>
    /// 接口压测 🔖
    /// </summary>
    /// <returns></returns>
    [DisplayName("接口压测")]
    public async Task<StressTestOutput> StressTest(StressTestInput input)
    {
        // 限制仅超管用户才能使用此功能
        if (!_userManager.SuperAdmin) throw Oops.Oh(ErrorCodeEnum.SA001);

        var stopwatch = new Stopwatch();
        var responseTimes = new List<double>();  //响应时间集合
        input.RequestMethod = input.RequestMethod.ToUpper();
        long totalRequests = 0, successfulRequests = 0, failedRequests = 0;

        stopwatch.Start();
        var semaphore = new SemaphoreSlim(input.MaxDegreeOfParallelism!.Value > 0 ? input.MaxDegreeOfParallelism.Value : Environment.ProcessorCount);

        #region 参数构建

        // 构建基础URI（不包括路径和查询参数）
        var baseUriBuilder = new UriBuilder(input.RequestUri);
        var queryString = HttpUtility.ParseQueryString(baseUriBuilder.Query);

        // 替换路径参数到baseUriBuilder.Path
        foreach (var param in input.PathParameters)
        {
            baseUriBuilder.Path = baseUriBuilder.Path.Replace($"{{{param.Key}}}", param.Value, StringComparison.OrdinalIgnoreCase);
        }

        // 构建Query参数
        foreach (var param in input.QueryParameters)
        {
            queryString[param.Key] = param.Value;
        }

        baseUriBuilder.Query = queryString.ToString() ?? string.Empty;
        var fullUri = baseUriBuilder.Uri;

        // 创建一次性的HttpRequestMessage模板
        HttpRequestMessage requestTemplate = CreateRequestMessage(input, fullUri);

        #endregion 参数构建

        var tasks = Enumerable.Range(0, input.NumberOfRounds!.Value * input.NumberOfRequests!.Value).Select(async _ =>
        {
            await semaphore.WaitAsync();
            try
            {
                var requestStopwatch = new Stopwatch();
                requestStopwatch.Start();

                using (var request = requestTemplate.DeepCopy())
                {
                    if (!string.Equals(input.RequestMethod, "GET", StringComparison.OrdinalIgnoreCase) && input.RequestParameters.Any())
                    {
                        var content = new FormUrlEncodedContent(input.RequestParameters);
                        request.Content = content;
                    }

                    using (var response = await _httpClient.SendAsync(request))
                    {
                        response.EnsureSuccessStatusCode(); // 抛出错误状态码异常

                        requestStopwatch.Stop();
                        responseTimes.Add(requestStopwatch.Elapsed.TotalMilliseconds);
                        Interlocked.Increment(ref successfulRequests);
                    }
                }
            }
            catch
            {
                Interlocked.Increment(ref failedRequests);
            }
            finally
            {
                Interlocked.Increment(ref totalRequests);
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        var totalTimeInSeconds = stopwatch.Elapsed.TotalSeconds;
        var qps = totalTimeInSeconds > 0 ? totalRequests / totalTimeInSeconds : 0;
        var orderResponseTimes = responseTimes.OrderBy(t => t).ToList();
        var averageResponseTime = responseTimes.Any() ? responseTimes.Average() : 0;
        var minResponseTime = responseTimes.Any() ? responseTimes.Min() : 0;
        var maxResponseTime = responseTimes.Any() ? responseTimes.Max() : 0;

        return new StressTestOutput
        {
            TotalRequests = totalRequests,
            TotalTimeInSeconds = totalTimeInSeconds,
            SuccessfulRequests = successfulRequests,
            FailedRequests = failedRequests,
            QueriesPerSecond = qps,
            MinResponseTime = minResponseTime,
            MaxResponseTime = maxResponseTime,
            AverageResponseTime = averageResponseTime,
            Percentile10ResponseTime = CalculatePercentile(orderResponseTimes, 0.1),
            Percentile25ResponseTime = CalculatePercentile(orderResponseTimes, 0.25),
            Percentile50ResponseTime = CalculatePercentile(orderResponseTimes, 0.5),
            Percentile75ResponseTime = CalculatePercentile(orderResponseTimes, 0.75),
            Percentile90ResponseTime = CalculatePercentile(orderResponseTimes, 0.9),
            Percentile99ResponseTime = CalculatePercentile(orderResponseTimes, 0.99),
            Percentile999ResponseTime = CalculatePercentile(orderResponseTimes, 0.999)
        };
    }

    /// <summary>
    /// 创建请求消息
    /// </summary>
    /// <param name="input">输入参数</param>
    /// <param name="fullUri">url</param>
    /// <returns></returns>
    private HttpRequestMessage CreateRequestMessage(StressTestInput input, Uri fullUri)
    {
        HttpRequestMessage request = input.RequestMethod switch
        {
            "GET" => new HttpRequestMessage(HttpMethod.Get, fullUri),
            "PUT" => new HttpRequestMessage(HttpMethod.Put, fullUri),
            "POST" => new HttpRequestMessage(HttpMethod.Post, fullUri),
            "DELETE" => new HttpRequestMessage(HttpMethod.Delete, fullUri),
            _ => throw Oops.Bah("请求方式异常")
        };

        // 设置请求头
        foreach (var header in input.Headers)
        {
            request.Headers.Add(header.Key, header.Value);
        }
        return request;
    }

    /// <summary>
    /// 计算百分位请求耗时
    /// </summary>
    /// <param name="times">请求耗时列表</param>
    /// <param name="percentile">百分位</param>
    /// <returns></returns>
    private double CalculatePercentile(List<double> times, double percentile)
    {
        if (!times.Any()) return 0;
        var index = (int)Math.Ceiling(percentile * times.Count) - 1;
        return times[index < times.Count ? index : times.Count - 1];
    }
}