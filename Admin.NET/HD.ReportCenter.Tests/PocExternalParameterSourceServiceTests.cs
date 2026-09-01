using System.Net;
using System.Reflection;
using System.Text;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace HD.ReportCenter.Tests;

public sealed class PocExternalParameterSourceServiceTests : IDisposable
{
    private long _nextId = 80_000;
    private readonly SqlSugarClient _database;

    /// <summary>初始化 PocExternalParameterSourceServiceTests 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocExternalParameterSourceServiceTests()
    {
        _database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (property, column) =>
                {
                    if (new NullabilityInfoContext().Create(property).WriteState is NullabilityState.Nullable)
                    {
                        column.IsNullable = true;
                    }
                }
            }
        });
        _database.Aop.DataExecuting = (_, entityInfo) =>
        {
            if (entityInfo.OperationType != DataFilterType.InsertByObject ||
                !entityInfo.EntityColumnInfo.IsPrimarykey ||
                entityInfo.EntityColumnInfo.PropertyInfo.PropertyType != typeof(long))
            {
                return;
            }
            var id = (long)entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue)!;
            if (id == 0)
            {
                entityInfo.SetValue(Interlocked.Increment(ref _nextId));
            }
        };
        _database.Ado.Open();
        _database.CodeFirst.InitTables<PocExternalParameterSource>();
    }

    /// <summary>验证 List Definitions Seeds Safe His Definition 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ListDefinitions_SeedsSafeHisDefinition()
    {
        var service = CreateService(_ => JsonResponse("{}"));

        var sources = await service.ListAsync();
        var definitions = await service.ListDefinitionsAsync();

        var source = Assert.Single(sources);
        Assert.Equal("http://192.168.2.5:8889/hospital/", source.BaseUrl);
        Assert.Equal("1", JObject.Parse(source.HeadersTemplate)["token"]!.Value<string>());
        var his = Assert.Single(definitions);
        Assert.Equal("his", his.Code);
        Assert.Equal(["token"], his.ContextKeys);
        Assert.Contains(his.Fields, field =>
            field.Code == "hisOrganizeName" && field.ResponsePath == "content.organName");
    }

    /// <summary>验证 Resolve Async Sends Optional His Token In Body And Maps Requested Fields 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveAsync_SendsOptionalHisTokenInBodyAndMapsRequestedFields()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var service = CreateService(async request =>
        {
            captured = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {"resultCode":"0","content":{"userName":"张医生","organId":"organ1001","organName":"锦州市凌河区医院"}}
                """);
        });
        var parameters = new[]
        {
            ExternalParameter("hisUserName", "hisUserName"),
            ExternalParameter("hisOrganizeId", "hisOrganizeId"),
            ExternalParameter("hisOrganizeName", "hisOrganizeName")
        };

        var values = await service.ResolveAsync(
            parameters,
            new Dictionary<string, string?> { ["token"] = "his-token-123" });

        Assert.Equal("张医生", values["hisUserName"]);
        Assert.Equal("organ1001", values["hisOrganizeId"]);
        Assert.Equal("锦州市凌河区医院", values["hisOrganizeName"]);
        Assert.Equal(
            "/hospital/SystemUserService/GetUserInfoByToken",
            captured!.RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, captured.RequestUri.Query);
        Assert.Equal("1", Assert.Single(captured.Headers.GetValues("token")));
        Assert.Equal(
            "his-token-123",
            JObject.Parse(capturedBody!)["content"]!["token"]!.Value<string>());
    }

    /// <summary>验证 Resolve Async Allows Missing Optional His Token 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task ResolveAsync_AllowsMissingOptionalHisToken()
    {
        string? capturedBody = null;
        var service = CreateService(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"content\":{\"userName\":\"匿名用户\"}}");
        });

        var values = await service.ResolveAsync(
            [ExternalParameter("hisUserName", "hisUserName")],
            new Dictionary<string, string?>());

        Assert.Equal("匿名用户", values["hisUserName"]);
        Assert.Empty(JObject.Parse(capturedBody!).Properties());
    }

    /// <summary>验证 Test Async Supports Embedded Header And Body Placeholders 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task TestAsync_SupportsEmbeddedHeaderAndBodyPlaceholders()
    {
        string? authorization = null;
        string? body = null;
        var service = CreateService(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"payload\":{\"displayName\":\"李医生\"}}");
        });
        await service.SaveAsync("other_system", new PocExternalParameterSourceInput(
            "other_system",
            "其它系统",
            "https://other.example/api/",
            "profile",
            "POST",
            "{\"operator\":\"user-{{userId}}\"}",
            "{\"Authorization\":\"Bearer {{token}}\"}",
            [],
            10,
            true,
            0,
            [new PocExternalParameterFieldDefinition(
                "displayName", "人员姓名", "string", "payload.displayName")]));

        var result = await service.TestAsync(
            "other_system",
            new PocExternalParameterSourceTestRequest(new Dictionary<string, string?>
            {
                ["token"] = "external-token",
                ["userId"] = "119"
            }));

        Assert.Equal("Bearer external-token", authorization);
        Assert.Equal("user-119", JObject.Parse(body!)["operator"]!.Value<string>());
        Assert.Equal("李医生", result.Values["displayName"]);
    }

    /// <summary>验证 Test Async Merges Generic Parameters With Configured Body And Headers 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task TestAsync_MergesGenericParametersWithConfiguredBodyAndHeaders()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var service = CreateService(async request =>
        {
            captured = request;
            // 第一阶段：调用 TestAsync_MergesGenericParametersWithConfiguredBodyAndHeaders 依赖的异步能力，等待并校验返回数据。
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"payload\":{\"result\":\"ok\"}}");
        });
        await service.SaveAsync("generic_request", new PocExternalParameterSourceInput(
            "generic_request",
            "通用参数接口",
            "https://other.example/api/",
            "lookup?fixedQuery=1",
            "POST",
            "{\"fixedBody\":\"body-fixed\"}",
            "{\"X-Fixed\":\"header-fixed\"}",
            [
                new PocExternalRequestParameterDefinition(
                    "payload.department", "bodyInput", "Body", false),
                new PocExternalRequestParameterDefinition(
                    "X-Operator", "headerInput", "Header", false),
                new PocExternalRequestParameterDefinition(
                    "search", "queryInput", "Query", false)
            ],
            10,
            true,
            0,
            [new PocExternalParameterFieldDefinition(
                "result", "结果", "string", "payload.result")]));

        // 第2阶段：调用 TestAsync_MergesGenericParametersWithConfiguredBodyAndHeaders 依赖的异步能力，等待并校验返回数据。
        var result = await service.TestAsync(
            "generic_request",
            new PocExternalParameterSourceTestRequest(new Dictionary<string, string?>
            {
                ["bodyInput"] = "财务科",
                ["headerInput"] = "operator-1",
                ["queryInput"] = "门诊"
            }));

        Assert.Equal("ok", result.Values["result"]);
        var query = Uri.UnescapeDataString(captured!.RequestUri!.Query);
        Assert.Contains("fixedQuery=1", query);
        Assert.Contains("search=门诊", query);
        Assert.Equal("header-fixed", Assert.Single(captured.Headers.GetValues("X-Fixed")));
        Assert.Equal("operator-1", Assert.Single(captured.Headers.GetValues("X-Operator")));
        var requestBody = JObject.Parse(body!);
        Assert.Equal("body-fixed", requestBody["fixedBody"]!.Value<string>());
        Assert.Equal("财务科", requestBody["payload"]!["department"]!.Value<string>());
    }

    /// <summary>验证 Test Async Rejects Missing Required Dynamic Url Parameter Before Sending 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task TestAsync_RejectsMissingRequiredDynamicUrlParameterBeforeSending()
    {
        var sent = false;
        var service = CreateService(request =>
        {
            sent = true;
            return JsonResponse("{}");
        });
        await service.SaveAsync("required_context", new PocExternalParameterSourceInput(
            "required_context",
            "必填上下文接口",
            "https://other.example/api/",
            "profile",
            "POST",
            "{}",
            "{}",
            [new PocExternalRequestParameterDefinition("content.organId", "organId", "Body", true)],
            10,
            true,
            0,
            [new PocExternalParameterFieldDefinition(
                "displayName", "人员姓名", "string", "payload.displayName")]));

        var exception = await Assert.ThrowsAsync<PocExternalParameterException>(() =>
            service.TestAsync(
                "required_context",
                new PocExternalParameterSourceTestRequest(new Dictionary<string, string?>())));

        Assert.Equal("EXTERNAL_PARAMETER_CONTEXT_REQUIRED", exception.Code);
        Assert.False(sent);
    }

    /// <summary>验证 Test Async Reports External Business Message When Mapped Content Is Empty 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task TestAsync_ReportsExternalBusinessMessageWhenMappedContentIsEmpty()
    {
        var service = CreateService(_ => JsonResponse("""
            {"content":null,"resultMsg":"token无效或用户不存在","resultCode":"1"}
            """));

        var exception = await Assert.ThrowsAsync<PocExternalParameterException>(() =>
            service.TestAsync(
                "his",
                new PocExternalParameterSourceTestRequest(new Dictionary<string, string?>
                {
                    ["token"] = "1"
                })));

        Assert.Equal("EXTERNAL_PARAMETER_RESPONSE_INVALID", exception.Code);
        Assert.Contains("token无效或用户不存在", exception.Message);
    }

    /// <summary>释放 PocExternalParameterSourceServiceTests 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    /// <summary>构建 PocExternalParameterSourceServiceTests 所需的 Create Service 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="response">上一步或远程调用返回的处理结果。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private PocExternalParameterSourceService CreateService(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) =>
        new(
            new PocExternalParameterSourceStore(_database),
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(response))));

    /// <summary>构建 PocExternalParameterSourceServiceTests 所需的 Create Service 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="response">上一步或远程调用返回的处理结果。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private PocExternalParameterSourceService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> response) =>
        CreateService(request => Task.FromResult(response(request)));

    /// <summary>负责 PocExternalParameterSourceServiceTests 的 External Parameter 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="fieldCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocParameterDefinition ExternalParameter(string code, string fieldCode) =>
        new(
            code,
            code,
            "text",
            "External",
            null,
            false,
            "string",
            Required: true,
            SourceCode: "his",
            SourceFieldCode: fieldCode);

    /// <summary>负责 PocExternalParameterSourceServiceTests 的 Json Response 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="json">json 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        /// <summary>构建 PocExternalParameterSourceServiceTests 所需的 Create Client 内容，并保证生成结果符合调用约定。</summary>
        /// <param name="name">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        /// <summary>执行 PocExternalParameterSourceServiceTests 的 Send Async 核心流程，并保持错误和结果语义稳定。</summary>
        /// <param name="request">本次操作使用的输入数据。</param>
        /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request);
    }
}
