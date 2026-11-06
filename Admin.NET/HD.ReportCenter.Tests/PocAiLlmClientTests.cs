using System.Net;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

/// <summary>
/// PocLlmClient 单测：用伪造 HttpMessageHandler 覆盖 OpenAI 兼容协议的请求形状与响应解析，
/// 不引入任何 LLM SDK，也不触达真实端点。
/// </summary>
public sealed class PocAiLlmClientTests
{
    /// <summary>验证纯文本回复解析：content 原样返回且无 tool_calls。</summary>
    [Fact]
    public async Task ChatAsync_parses_plain_content_response()
    {
        var handler = new ScriptedLlmHandler();
        handler.EnqueueJson(PocAiLlmFixtures.ContentResponse("结论：表结构正常。"));
        var client = new PocLlmClient(new HttpClient(handler));

        var response = await client.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1", "sk-key", "test-model"),
            new PocLlmRequest([new PocLlmChatMessage("user", "你好")]),
            PocLlmClient.ChatTimeoutSeconds);

        Assert.Equal("结论：表结构正常。", response.Content);
        Assert.Empty(response.ToolCalls);
        Assert.Equal("stop", response.FinishReason);
    }

    /// <summary>验证 tool_calls 解析：编号、工具名与 arguments JSON 字符串逐字段对齐 OpenAI 协议。</summary>
    [Fact]
    public async Task ChatAsync_parses_tool_calls()
    {
        var handler = new ScriptedLlmHandler();
        handler.EnqueueJson(PocAiLlmFixtures.ToolCallResponse(
            "call_1",
            "run_readonly_sql",
            """{"dataSourceCode":"ai_main","sql":"SELECT 1"}""",
            content: "我先查一下"));
        var client = new PocLlmClient(new HttpClient(handler));

        var response = await client.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1", null, "m"),
            new PocLlmRequest([new PocLlmChatMessage("user", "看看数据")]),
            PocLlmClient.ChatTimeoutSeconds);

        Assert.Equal("我先查一下", response.Content);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("run_readonly_sql", call.Name);
        Assert.Contains("ai_main", call.ArgumentsJson);
    }

    /// <summary>验证请求形状：POST 到 baseUrl+/chat/completions，携带 Bearer、模型名、消息与 tools。</summary>
    [Fact]
    public async Task ChatAsync_sends_model_messages_tools_and_bearer()
    {
        var handler = new ScriptedLlmHandler();
        handler.EnqueueJson(PocAiLlmFixtures.ContentResponse("ok"));
        var client = new PocLlmClient(new HttpClient(handler));
        var tools = PocAiToolRegistry.ListTools();

        await client.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1/", "sk-secret", "test-model"),
            new PocLlmRequest(
            [
                new PocLlmChatMessage("system", "系统提示"),
                new PocLlmChatMessage("assistant", null, [new PocLlmToolCall("c1", "list_tables", "{}")]),
                new PocLlmChatMessage("tool", "{\"count\":1}", ToolCallId: "c1", ToolName: "list_tables"),
                new PocLlmChatMessage("user", "继续"),
            ], tools),
            PocLlmClient.ChatTimeoutSeconds);

        Assert.Single(handler.RequestBodies);
        var payload = JObject.Parse(handler.RequestBodies[0]);
        Assert.Equal("test-model", payload.Value<string>("model"));
        Assert.Equal(4, payload["messages"]!.Count());
        Assert.NotNull(payload["tools"]);
        Assert.Equal(PocAiToolRegistry.ListDataSources,
            payload["tools"]![0]!["function"]!.Value<string>("name"));
        // tool 结果消息必须带 tool_call_id；assistant 的 tool_calls 必须是 id/type/function 三段结构。
        Assert.Equal("c1", payload["messages"]![2]!.Value<string>("tool_call_id"));
        Assert.Equal("list_tables", payload["messages"]![1]!["tool_calls"]![0]!["function"]!.Value<string>("name"));
    }

    /// <summary>验证非法 baseUrl 直接拒绝，请求不外发。</summary>
    [Fact]
    public async Task ChatAsync_rejects_invalid_base_url_without_sending()
    {
        var handler = new ScriptedLlmHandler();
        var client = new PocLlmClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<PocAiLlmException>(() => client.ChatAsync(
            new PocLlmEndpoint("not-a-url", null, "m"),
            new PocLlmRequest([new PocLlmChatMessage("user", "hi")]),
            PocLlmClient.ChatTimeoutSeconds));

        Assert.Equal("LLM_BASE_URL_INVALID", exception.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>验证非 200 响应转业务异常；错误文本只保留摘要，不携带请求体或密钥。</summary>
    [Fact]
    public async Task ChatAsync_maps_http_errors_to_stable_code()
    {
        var handler = new ScriptedLlmHandler();
        handler.EnqueueError((int)HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}");
        var client = new PocLlmClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<PocAiLlmException>(() => client.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1", "sk-secret", "m"),
            new PocLlmRequest([new PocLlmChatMessage("user", "hi")]),
            PocLlmClient.ChatTimeoutSeconds));

        Assert.Equal("LLM_REQUEST_FAILED", exception.Code);
        Assert.Contains("401", exception.Message);
    }

    /// <summary>验证响应不是合法 JSON 或缺少 choices.message 时返回 LLM_RESPONSE_INVALID。</summary>
    [Fact]
    public async Task ChatAsync_rejects_invalid_response_structure()
    {
        var handler = new ScriptedLlmHandler();
        handler.EnqueueJson("<html>not json</html>");
        var client = new PocLlmClient(new HttpClient(handler));
        var missingChoice = await Assert.ThrowsAsync<PocAiLlmException>(() => client.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1", null, "m"),
            new PocLlmRequest([new PocLlmChatMessage("user", "hi")]),
            PocLlmClient.ChatTimeoutSeconds));
        Assert.Equal("LLM_RESPONSE_INVALID", missingChoice.Code);

        var emptyHandler = new ScriptedLlmHandler();
        emptyHandler.EnqueueJson(JsonConvert.SerializeObject(new { choices = new[] { new { index = 0 } } }));
        var emptyClient = new PocLlmClient(new HttpClient(emptyHandler));
        var empty = await Assert.ThrowsAsync<PocAiLlmException>(() => emptyClient.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1", null, "m"),
            new PocLlmRequest([new PocLlmChatMessage("user", "hi")]),
            PocLlmClient.ChatTimeoutSeconds));
        Assert.Equal("LLM_RESPONSE_INVALID", empty.Code);
    }

    /// <summary>验证超时转换为 LLM_REQUEST_TIMEOUT；区分调用方主动取消（OperationCanceledException 原样外抛）。</summary>
    [Fact]
    public async Task ChatAsync_maps_timeout_and_preserves_caller_cancellation()
    {
        var handler = new ScriptedLlmHandler();
        handler.EnqueueDelayedJson(PocAiLlmFixtures.ContentResponse("late"), TimeSpan.FromSeconds(3));
        var client = new PocLlmClient(new HttpClient(handler));
        var timeout = await Assert.ThrowsAsync<PocAiLlmException>(() => client.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1", null, "m"),
            new PocLlmRequest([new PocLlmChatMessage("user", "hi")]),
            timeoutSeconds: 1));
        Assert.Equal("LLM_REQUEST_TIMEOUT", timeout.Code);

        var cancelHandler = new ScriptedLlmHandler();
        cancelHandler.EnqueueDelayedJson(PocAiLlmFixtures.ContentResponse("late"), TimeSpan.FromSeconds(5));
        var cancelClient = new PocLlmClient(new HttpClient(cancelHandler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelClient.ChatAsync(
            new PocLlmEndpoint("http://llm.test/v1", null, "m"),
            new PocLlmRequest([new PocLlmChatMessage("user", "hi")]),
            PocLlmClient.ChatTimeoutSeconds,
            cts.Token));
    }

    /// <summary>验证连通性测试走真实 chat 路径并返回成功结果；AI 服务层用它给管理员明确反馈。</summary>
    [Fact]
    public async Task Settings_service_test_connection_reports_success_and_failure()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("正常"));
        var success = await host.SettingsService.TestConnectionAsync();
        Assert.True(success.Success);
        Assert.Contains("连接成功", success.Message);

        var failureHost = new PocAiTestHost();
        await failureHost.EnableAiAsync();
        failureHost.LlmHandler.EnqueueError((int)HttpStatusCode.BadGateway, "upstream down");
        var failure = await failureHost.SettingsService.TestConnectionAsync();
        Assert.False(failure.Success);
        Assert.Contains("502", failure.Message);
    }
}
