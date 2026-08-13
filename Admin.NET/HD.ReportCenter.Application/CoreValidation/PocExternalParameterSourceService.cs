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

    public PocExternalParameterSourceService(
        PocExternalParameterSourceStore store,
        IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<PocExternalParameterSourceDocument>> ListAsync()
    {
        var sources = await _store.ListAsync();
        return sources.Select(ToDocument).ToArray();
    }

    public async Task<IReadOnlyList<PocExternalParameterSourceDefinition>> ListDefinitionsAsync()
    {
        var documents = await ListAsync();
        return documents.Select(ToDefinition).ToArray();
    }

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

    public async Task<IReadOnlyDictionary<string, string?>> ResolveAsync(
        IReadOnlyList<PocParameterDefinition> parameters,
        IReadOnlyDictionary<string, string?>? context,
        CancellationToken cancellationToken = default)
    {
        var external = parameters
            .Where(item => item.SourceType.Equals("External", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (external.Length == 0)
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

    private static string ReplaceEndpointTokens(
        string endpoint,
        IReadOnlyDictionary<string, string?> context) =>
        Regex.Replace(
            endpoint,
            "\\{\\{(?<key>[A-Za-z][A-Za-z0-9_]{0,63})\\}\\}",
            match => Uri.EscapeDataString(RequiredContext(context, match.Groups["key"].Value)),
            RegexOptions.CultureInvariant);

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

    private static string? ContextValue(
        IReadOnlyDictionary<string, string?> context,
        string key) => context.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static PocExternalParameterException MissingContext(string key) =>
        new(
            "EXTERNAL_PARAMETER_CONTEXT_REQUIRED",
            $"外部参数接口缺少必填 URL 参数 '{key}'。");

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

    internal static IReadOnlyList<PocExternalParameterFieldDefinition> ReadFields(
        PocExternalParameterSource source) =>
        JsonConvert.DeserializeObject<PocExternalParameterFieldDefinition[]>(source.FieldSchemaJson)
        ?? [];

    internal static IReadOnlyList<PocExternalRequestParameterDefinition> ReadRequestParameters(
        PocExternalParameterSource source) =>
        JsonConvert.DeserializeObject<PocExternalRequestParameterDefinition[]>(
            source.RequestParameterSchemaJson ?? "[]") ?? [];

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

    private static string NormalizeBaseUrl(string value) =>
        value.Trim().TrimEnd('/') + "/";

    private static string NormalizeRequestTemplate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "{}" : JToken.Parse(value).ToString(Formatting.None);

    private static string NormalizeHeadersTemplate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "{}" : JObject.Parse(value).ToString(Formatting.None);

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

    private static PocDatasetConfigurationException Invalid(string message) =>
        new("EXTERNAL_PARAMETER_SOURCE_INVALID", message);
}
