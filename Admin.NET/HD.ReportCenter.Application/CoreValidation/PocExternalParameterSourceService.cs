using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed class PocExternalParameterException : Exception
{
    /// <summary>初始化 PocExternalParameterException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="innerException">innerException 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocExternalParameterException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record PocExternalParameterSourceTestRequest(
    IReadOnlyDictionary<string, string?> Context);

public sealed record PocExternalParameterSourceTestResult(
    IReadOnlyDictionary<string, string> Values);

/// <summary>维护外部参数源，并把固定 JSON 接口响应收敛为报表可消费的标量参数。</summary>
public sealed class PocExternalParameterSourceService : ITransient
{
    internal const string HttpClientName = "ReportCenterExternalParameters";
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly Regex CodeRegex = new(
        "^[A-Za-z][A-Za-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex JsonPathRegex = new(
        "^[A-Za-z][A-Za-z0-9_]{0,63}(\\.[A-Za-z][A-Za-z0-9_]{0,63})*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HeaderNameRegex = new(
        "^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,128}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContextTokenRegex = new(
        "^\\{\\{(?<key>[A-Za-z][A-Za-z0-9_]{0,63})\\}\\}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContextPlaceholderRegex = new(
        "\\{\\{(?<key>[A-Za-z][A-Za-z0-9_]{0,63})\\}\\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> DataTypes = new(
        ["string", "text", "int", "long", "decimal", "date", "datetime", "bool"],
        StringComparer.OrdinalIgnoreCase);
    private readonly PocExternalParameterSourceStore _store;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>初始化 PocExternalParameterSourceService 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="store">store 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="httpClientFactory">httpClientFactory 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocExternalParameterSourceService(
        PocExternalParameterSourceStore store,
        IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>调用 PocExternalParameterSourceService 的异步依赖完成 List Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocExternalParameterSourceDocument>> ListAsync()
    {
        var sources = await _store.ListAsync();
        return sources.Select(ToDocument).ToArray();
    }

    /// <summary>调用 PocExternalParameterSourceService 的异步依赖完成 List Definitions Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocExternalParameterSourceDefinition>> ListDefinitionsAsync()
    {
        var documents = await ListAsync();
        return documents.Select(ToDefinition).ToArray();
    }

    /// <summary>更新 PocExternalParameterSourceService 的 Save Async 状态，并同步相关业务数据。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocExternalParameterSourceDocument> SaveAsync(
        string code,
        PocExternalParameterSourceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!code.Equals(input.Code, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("路径编码与请求内容不一致。");
        }
        Validate(input);
        await _store.SaveAsync(new PocExternalParameterSourceMutation(
            input.Code.Trim(),
            input.Name.Trim(),
            NormalizeBaseUrl(input.BaseUrl),
            input.Endpoint.TrimStart('/'),
            input.HttpMethod.Trim().ToUpperInvariant(),
            NormalizeRequestTemplate(input.RequestBodyTemplate),
            NormalizeHeadersTemplate(input.HeadersTemplate),
            JsonConvert.SerializeObject(input.RequestParameters ?? [], Formatting.None),
            JsonConvert.SerializeObject(input.Fields, Formatting.None),
            input.TimeoutSeconds,
            input.Enabled,
            input.Revision));
        return ToDocument((await _store.GetAsync(input.Code.Trim()))!);
    }

    /// <summary>调用 PocExternalParameterSourceService 的异步依赖完成 Test Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="input">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocExternalParameterSourceTestResult> TestAsync(
        string code,
        PocExternalParameterSourceTestRequest input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = await RequireSourceAsync(code);
        var fields = ReadFields(source);
        var values = await RequestAsync(source, fields, input.Context, cancellationToken);
        return new PocExternalParameterSourceTestResult(values);
    }

    /// <summary>解析并计算 PocExternalParameterSourceService 的 Resolve Async 结果，处理必要的边界条件。</summary>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyDictionary<string, string?>> ResolveAsync(
        IReadOnlyList<PocParameterDefinition> parameters,
        IReadOnlyDictionary<string, string?>? context,
        CancellationToken cancellationToken = default)
    {
        var external = parameters
            .Where(item => item.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (external.Length == 0)
        // 第一阶段：根据 ResolveAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in external.GroupBy(
            item => item.SourceCode ?? string.Empty,
            StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                throw Invalid("外部参数未配置接口源编码。");
            }
            var source = await RequireSourceAsync(group.Key);
            var fields = ReadFields(source);
            var fieldByCode = fields.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
            var requested = group.Select(parameter =>
            {
                if (string.IsNullOrWhiteSpace(parameter.SourceFieldCode) ||
                    !fieldByCode.TryGetValue(parameter.SourceFieldCode, out var field))
                {
                    // 第2阶段：根据 ResolveAsync 当前分支转换输入数据，并保存该阶段产生的中间结果。
                    throw Invalid($"外部参数 '{parameter.Code}' 的字段映射不存在。");
                }
                if (!parameter.DataType.Equals(field.DataType, StringComparison.OrdinalIgnoreCase))
                {
                    throw Invalid($"外部参数 '{parameter.Code}' 的数据类型与字段映射不一致。");
                }
                return field;
            }).DistinctBy(item => item.Code, StringComparer.OrdinalIgnoreCase).ToArray();
            var values = await RequestAsync(source, requested, context, cancellationToken);
            foreach (var parameter in group)
            {
                result[parameter.Code] = values.GetValueOrDefault(parameter.SourceFieldCode!);
            }
        }
        return result;
    }

    /// <summary>执行 PocExternalParameterSourceService 的 Require Source Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<PocExternalParameterSource> RequireSourceAsync(string code)
    {
        var source = await _store.GetAsync(code.Trim())
            ?? throw new PocExternalParameterException(
                "EXTERNAL_PARAMETER_SOURCE_NOT_FOUND",
                $"不存在外部参数源 '{code}'。");
        if (!source.IsEnabled)
        {
            throw new PocExternalParameterException(
                "EXTERNAL_PARAMETER_SOURCE_DISABLED",
                $"外部参数源 '{code}' 已停用。");
        }
        return source;
    }

    /// <summary>调用 PocExternalParameterSourceService 的异步依赖完成 Request Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="fields">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<Dictionary<string, string>> RequestAsync(
        PocExternalParameterSource source,
        IReadOnlyList<PocExternalParameterFieldDefinition> fields,
        IReadOnlyDictionary<string, string?>? context,
        CancellationToken cancellationToken)
    {
        context ??= new Dictionary<string, string?>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
        var endpoint = ReplaceEndpointTokens(source.Endpoint, context);
        var requestParameters = ReadRequestParameters(source);
        // 第一阶段：构造 RequestAsync 当前步骤需要的中间数据和执行参数。
        var requestUri = BuildRequestUri(
            new Uri(new Uri(source.BaseUrl, UriKind.Absolute), endpoint),
            requestParameters,
            context);
        using var request = new HttpRequestMessage(new HttpMethod(source.HttpMethod), requestUri);
        var headers = (JObject)ReplaceJsonTokens(JObject.Parse(source.HeadersTemplate), context);
        ApplyHeaderParameters(headers, requestParameters, context);
        foreach (var header in headers.Properties())
        {
            var value = ScalarValue(header.Value);
            if (!request.Headers.TryAddWithoutValidation(header.Name, value))
            {
                throw Invalid($"请求头 '{header.Name}' 不受支持。");
            }
        }
        if (!source.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var body = ReplaceJsonTokens(JToken.Parse(source.RequestBodyTemplate), context);
            ApplyBodyParameters(body, requestParameters, context);
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            // 第2阶段：检查 RequestAsync 当前条件，并按匹配分支执行对应处理。
            if (!response.IsSuccessStatusCode)
            {
                throw new PocExternalParameterException(
                    "EXTERNAL_PARAMETER_REQUEST_FAILED",
                    $"外部参数源 '{source.Name}' 返回 HTTP {(int)response.StatusCode}。");
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (bytes.Length > MaximumResponseBytes)
            {
                throw new PocExternalParameterException(
                    "EXTERNAL_PARAMETER_RESPONSE_TOO_LARGE",
                    $"外部参数源 '{source.Name}' 的响应超过 1 MiB 限制。");
            }
            var responseJson = JToken.Parse(Encoding.UTF8.GetString(bytes));
            var result = fields.ToDictionary(
                field => field.Code,
                field => ScalarValue(responseJson.SelectToken(field.ResponsePath, false)),
                StringComparer.OrdinalIgnoreCase);
            if (result.Count > 0 && result.Values.All(string.IsNullOrWhiteSpace))
            {
                var externalMessage = ExternalErrorMessage(responseJson);
                throw new PocExternalParameterException(
                    "EXTERNAL_PARAMETER_RESPONSE_INVALID",
                    string.IsNullOrWhiteSpace(externalMessage)
                        ? $"外部参数源 '{source.Name}' 未返回已映射字段，请检查凭据和响应路径。"
                        : $"外部参数源 '{source.Name}' 返回失败：{externalMessage}");
            }
            // 第3阶段：整理 RequestAsync 已计算的数据，并按方法契约返回最终结果。
            return result;
        }
        catch (PocExternalParameterException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PocExternalParameterException(
                "EXTERNAL_PARAMETER_TIMEOUT",
                $"外部参数源 '{source.Name}' 请求超时。",
                exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new PocExternalParameterException(
                "EXTERNAL_PARAMETER_REQUEST_FAILED",
                $"外部参数源 '{source.Name}' 请求或响应解析失败。",
                exception);
        }
    }

    /// <summary>遍历 PocExternalParameterSourceService 当前数据并执行 Replace Json Tokens 转换，生成调用方需要的结果集合。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JToken ReplaceJsonTokens(
        JToken token,
        IReadOnlyDictionary<string, string?> context)
    {
        if (token is JValue { Type: JTokenType.String } value)
        {
            var text = value.Value<string>() ?? string.Empty;
            var match = ContextTokenRegex.Match(text);
            if (match.Success)
            {
                var key = match.Groups["key"].Value;
                return new JValue(RequiredContext(context, key));
            }
            return new JValue(ContextPlaceholderRegex.Replace(
                text,
                placeholder => RequiredContext(
                    context,
                    placeholder.Groups["key"].Value)));
        }
        if (token is JArray array)
        {
            return new JArray(array.Select(item => ReplaceJsonTokens(item, context)));
        }
        if (token is JObject obj)
        {
            return new JObject(obj.Properties().Select(property =>
                new JProperty(property.Name, ReplaceJsonTokens(property.Value, context))));
        }
        return token.DeepClone();
    }

    /// <summary>负责 PocExternalParameterSourceService 的 Replace Endpoint Tokens 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="endpoint">endpoint 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ReplaceEndpointTokens(
        string endpoint,
        IReadOnlyDictionary<string, string?> context) =>
        Regex.Replace(
            endpoint,
            "\\{\\{(?<key>[A-Za-z][A-Za-z0-9_]{0,63})\\}\\}",
            match => Uri.EscapeDataString(RequiredContext(context, match.Groups["key"].Value)),
            RegexOptions.CultureInvariant);

    /// <summary>构建 PocExternalParameterSourceService 所需的 Build Request Uri 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="endpoint">endpoint 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static Uri BuildRequestUri(
        Uri endpoint,
        IReadOnlyList<PocExternalRequestParameterDefinition> parameters,
        IReadOnlyDictionary<string, string?> context)
    {
        var query = endpoint.Query.TrimStart('?');
        foreach (var parameter in parameters)
        {
            if (!parameter.Location.Equals("Query", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = ContextValue(context, parameter.UrlKey);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (parameter.Required)
                {
                    throw MissingContext(parameter.UrlKey);
                }
                continue;
            }

            var item = $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(value)}";
            query = string.IsNullOrEmpty(query) ? item : $"{query}&{item}";
        }

        var builder = new UriBuilder(endpoint) { Query = query };
        return builder.Uri;
    }

    /// <summary>执行 PocExternalParameterSourceService 的 Apply Header Parameters 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="headers">headers 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    private static void ApplyHeaderParameters(
        JObject headers,
        IReadOnlyList<PocExternalRequestParameterDefinition> parameters,
        IReadOnlyDictionary<string, string?> context)
    {
        foreach (var parameter in parameters.Where(item =>
            item.Location.Equals("Header", StringComparison.OrdinalIgnoreCase)))
        {
            var value = OptionalContext(parameter, context);
            if (value is not null)
            {
                headers[parameter.Name] = value;
            }
        }
    }

    /// <summary>执行 PocExternalParameterSourceService 的 Apply Body Parameters 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="body">符合当前接口契约的请求输入数据。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    private static void ApplyBodyParameters(
        JToken body,
        IReadOnlyList<PocExternalRequestParameterDefinition> parameters,
        IReadOnlyDictionary<string, string?> context)
    {
        foreach (var parameter in parameters.Where(item =>
            item.Location.Equals("Body", StringComparison.OrdinalIgnoreCase)))
        {
            var value = OptionalContext(parameter, context);
            if (value is null)
            {
                continue;
            }

            var segments = parameter.Name.Split('.');
            var current = body as JObject
                ?? throw Invalid("请求体动态参数只能写入 JSON 对象。");
            foreach (var segment in segments[..^1])
            {
                if (current[segment] is null)
                {
                    current[segment] = new JObject();
                }
                current = current[segment] as JObject
                    ?? throw Invalid($"请求体 JSON 路径 '{parameter.Name}' 与模板结构冲突。");
            }
            current[segments[^1]] = value;
        }
    }

    /// <summary>检查 PocExternalParameterSourceService 的 Optional Context 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="parameter">parameter 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? OptionalContext(
        PocExternalRequestParameterDefinition parameter,
        IReadOnlyDictionary<string, string?> context)
    {
        var value = ContextValue(context, parameter.UrlKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (parameter.Required)
            {
                throw MissingContext(parameter.UrlKey);
            }
            return null;
        }
        return value;
    }

    /// <summary>执行 PocExternalParameterSourceService 的 Required Context 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string RequiredContext(
        IReadOnlyDictionary<string, string?> context,
        string key)
    {
        var value = ContextValue(context, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw MissingContext(key);
        }
        return value;
    }

    /// <summary>负责 PocExternalParameterSourceService 的 Context Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="context">本次操作共享的运行上下文。</param>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? ContextValue(
        IReadOnlyDictionary<string, string?> context,
        string key) => context.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>负责 PocExternalParameterSourceService 的 Missing Context 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocExternalParameterException MissingContext(string key) =>
        new(
            "EXTERNAL_PARAMETER_CONTEXT_REQUIRED",
            $"外部参数接口缺少必填 URL 参数 '{key}'。");

    /// <summary>检查 PocExternalParameterSourceService 的 Scalar Value 约束；条件不满足时抛出包含业务上下文的异常。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ScalarValue(JToken? token)
    {
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return string.Empty;
        }
        if (token is JValue value)
        {
            return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        throw new PocExternalParameterException(
            "EXTERNAL_PARAMETER_RESPONSE_INVALID",
            "外部参数字段必须映射到字符串、数字、日期或布尔值。" );
    }

    /// <summary>遍历 PocExternalParameterSourceService 当前数据并执行 External Error Message 转换，生成调用方需要的结果集合。</summary>
    /// <param name="response">上一步或远程调用返回的处理结果。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? ExternalErrorMessage(JToken response)
    {
        if (response is not JObject obj)
        {
            return null;
        }

        foreach (var name in new[] { "resultMsg", "message", "msg" })
        {
            var value = obj.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (value is not JValue { Type: JTokenType.String } text)
            {
                continue;
            }

            var message = text.Value<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message.Length <= 512 ? message : message[..512];
            }
        }

        return null;
    }

    /// <summary>将 PocExternalParameterSourceService 的 To Document 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocExternalParameterSourceDocument ToDocument(PocExternalParameterSource source) =>
        new(
            source.Code,
            source.Name,
            source.BaseUrl,
            source.Endpoint,
            source.HttpMethod,
            source.RequestBodyTemplate,
            source.HeadersTemplate,
            ReadRequestParameters(source),
            source.TimeoutSeconds,
            source.IsEnabled,
            source.Revision,
            ReadFields(source));

    /// <summary>将 PocExternalParameterSourceService 的 To Definition 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal static PocExternalParameterSourceDefinition ToDefinition(
        PocExternalParameterSourceDocument source) =>
        new(
            source.Code,
            source.Name,
            source.Enabled,
            source.Fields,
            ReadContextKeys(
                source.RequestParameters,
                source.Endpoint,
                source.RequestBodyTemplate,
                source.HeadersTemplate));

    /// <summary>查询或读取 PocExternalParameterSourceService 所需的 Read Fields 数据，并按调用约定返回结果。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal static IReadOnlyList<PocExternalParameterFieldDefinition> ReadFields(
        PocExternalParameterSource source) =>
        JsonConvert.DeserializeObject<PocExternalParameterFieldDefinition[]>(source.FieldSchemaJson)
        ?? [];

    /// <summary>查询或读取 PocExternalParameterSourceService 所需的 Read Request Parameters 数据，并按调用约定返回结果。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal static IReadOnlyList<PocExternalRequestParameterDefinition> ReadRequestParameters(
        PocExternalParameterSource source) =>
        JsonConvert.DeserializeObject<PocExternalRequestParameterDefinition[]>(
            source.RequestParameterSchemaJson ?? "[]") ?? [];

    /// <summary>校验 PocExternalParameterSourceService 的 Validate 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="input">本次操作使用的输入数据。</param>
    private static void Validate(PocExternalParameterSourceInput input)
    {
        if (!CodeRegex.IsMatch(input.Code?.Trim() ?? string.Empty) ||
            string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128 ||
            !Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(input.Endpoint) || input.Endpoint.Length > 512 ||
            input.HttpMethod.Trim().ToUpperInvariant() is not ("GET" or "POST") ||
            input.TimeoutSeconds is < 1 or > 120 ||
            input.Revision < 0 || input.Fields is null || input.Fields.Count is < 1 or > 100 ||
            input.RequestParameters is null || input.RequestParameters.Count > 100)
        // 第一阶段：根据 Validate 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            throw Invalid("外部参数源配置无效。");
        }
        _ = JToken.Parse(NormalizeRequestTemplate(input.RequestBodyTemplate));
        _ = JObject.Parse(NormalizeHeadersTemplate(input.HeadersTemplate));
        var requestParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in input.RequestParameters)
        {
            var parameterName = parameter.Name?.Trim() ?? string.Empty;
            var urlKey = parameter.UrlKey?.Trim() ?? string.Empty;
            var location = parameter.Location?.Trim() ?? string.Empty;
            var validName = location.Equals("Body", StringComparison.OrdinalIgnoreCase)
                ? JsonPathRegex.IsMatch(parameterName)
                : location.Equals("Header", StringComparison.OrdinalIgnoreCase)
                    ? HeaderNameRegex.IsMatch(parameterName)
                    : CodeRegex.IsMatch(parameterName);
            if (location is not ("Query" or "Body" or "Header") ||
                !validName ||
                !CodeRegex.IsMatch(urlKey) ||
                !requestParameterNames.Add($"{location}:{parameterName}"))
            // 第2阶段：根据 Validate 当前分支转换输入数据，并保存该阶段产生的中间结果。
            {
                throw Invalid($"URL 动态请求参数 '{parameter.Name}' 配置无效。");
            }
        }
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in input.Fields)
        {
            var fieldCode = field.Code?.Trim() ?? string.Empty;
            if (!CodeRegex.IsMatch(fieldCode) ||
                !codes.Add(fieldCode) ||
                string.IsNullOrWhiteSpace(field.DisplayName) || field.DisplayName.Length > 128 ||
                !DataTypes.Contains(field.DataType) ||
                string.IsNullOrWhiteSpace(field.ResponsePath) || field.ResponsePath.Length > 512)
            {
                throw Invalid($"外部字段 '{field.Code}' 配置无效。");
            }
        }
    }

    /// <summary>将 PocExternalParameterSourceService 中的 Normalize Base Url 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string NormalizeBaseUrl(string value) =>
        value.Trim().TrimEnd('/') + "/";

    /// <summary>将 PocExternalParameterSourceService 中的 Normalize Request Template 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string NormalizeRequestTemplate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "{}" : JToken.Parse(value).ToString(Formatting.None);

    /// <summary>将 PocExternalParameterSourceService 中的 Normalize Headers Template 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string NormalizeHeadersTemplate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "{}" : JObject.Parse(value).ToString(Formatting.None);

    /// <summary>查询或读取 PocExternalParameterSourceService 所需的 Read Context Keys 数据，并按调用约定返回结果。</summary>
    /// <param name="requestParameters">requestParameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="templates">templates 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IReadOnlyList<string> ReadContextKeys(
        IReadOnlyList<PocExternalRequestParameterDefinition> requestParameters,
        params string[] templates) =>
        requestParameters.Select(parameter => parameter.UrlKey)
            .Concat(templates
            .SelectMany(template => ContextPlaceholderRegex.Matches(template)
                .Select(match => match.Groups["key"].Value)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

    /// <summary>负责 PocExternalParameterSourceService 的 Invalid 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDatasetConfigurationException Invalid(string message) =>
        new("EXTERNAL_PARAMETER_SOURCE_INVALID", message);
}
