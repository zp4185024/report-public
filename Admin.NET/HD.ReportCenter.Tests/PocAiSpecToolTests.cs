using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 产物工具（submit_report_spec / dry_run_report_spec）单测：编译+全链校验、
/// 字段路径错误回传、3 次失败熔断、草稿状态流转与试跑失败结构化回传。
/// 全链校验走 SQLite 内存库，不触达真实业务库。
/// </summary>
public sealed class PocAiSpecToolTests
{
    /// <summary>构造 submit_report_spec 工具调用脚本项（spec 取仓库样例，可覆写字段）。</summary>
    /// <param name="callId">工具调用编号。</param>
    /// <param name="specJson">spec 原文；null 表示直接使用仓库样例。</param>
    /// <returns>OpenAI 工具调用响应 JSON。</returns>
    private static string SubmitCall(string callId, string? specJson = null) =>
        PocAiLlmFixtures.ToolCallResponse(
            callId,
            PocAiToolRegistry.SubmitReportSpec,
            BuildArgumentsJson(specJson),
            content: "我提交一个 spec");

    /// <summary>构造 dry_run_report_spec 工具调用脚本项。</summary>
    /// <param name="callId">工具调用编号。</param>
    /// <param name="specJson">可选 spec 原文；不传则回落会话草稿。</param>
    /// <param name="sampleParameters">可选样例参数。</param>
    /// <returns>OpenAI 工具调用响应 JSON。</returns>
    private static string DryRunCall(string callId, string? specJson = null, JObject? sampleParameters = null)
    {
        var arguments = new JObject();
        if (specJson is not null)
        {
            arguments["spec"] = JObject.Parse(specJson);
        }
        if (sampleParameters is not null)
        {
            arguments["sampleParameters"] = sampleParameters;
        }
        return PocAiLlmFixtures.ToolCallResponse(
            callId,
            PocAiToolRegistry.DryRunReportSpec,
            arguments.ToString(Newtonsoft.Json.Formatting.None),
            content: "我试跑一下");
    }

    /// <summary>
    /// 读取会话中指定角色的工具消息原文（即回传给 LLM 的结构化 JSON）；
    /// 展示视图会把 tool 消息正文置空，错误内容必须从会话历史断言。
    /// </summary>
    /// <param name="host">测试宿主。</param>
    /// <param name="sessionId">会话编号。</param>
    /// <returns>工具消息原文。</returns>
    private static string ToolMessageContent(PocAiTestHost host, string sessionId)
    {
        var session = host.Sessions.Get(sessionId);
        Assert.NotNull(session);
        var toolMessage = session!.Messages.Where(item => item.Role == PocAiChatRole.Tool).Last();
        return toolMessage.Content ?? string.Empty;
    }

    /// <summary>组装工具参数 JSON 文本：{"spec": 对象}。</summary>
    /// <param name="specJson">spec 原文。</param>
    /// <returns>参数 JSON 文本。</returns>
    private static string BuildArgumentsJson(string? specJson)
    {
        var spec = specJson is null
            ? JObject.Parse(PocAiTestHost.LoadSampleSpec())
            : JObject.Parse(specJson);
        return new JObject { ["spec"] = spec }.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>把样例 spec 的数据源类型改为指定方言（试跑方言不匹配用例使用）。</summary>
    /// <param name="dataSourceType">目标方言。</param>
    /// <returns>修改后的 spec JSON 文本。</returns>
    private static string SampleSpecWithSourceType(string dataSourceType)
    {
        var spec = JObject.Parse(PocAiTestHost.LoadSampleSpec());
        spec["dataset"]!["dataSourceType"] = dataSourceType;
        return spec.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>验证提交通过：编译 + 全链校验成功后草稿置 validated 并随 /chat 响应下发。</summary>
    [Fact]
    public async Task Submit_validated_spec_returns_draft_and_records_audit()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(SubmitCall("call_ok"));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("已提交，草稿卡片可见。"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "帮我做一张报表"));

        var draft = result.Draft;
        Assert.NotNull(draft);
        Assert.Equal("validated", draft!.Status);
        Assert.NotNull(draft.Package);
        var packageJson = Assert.IsType<JObject>(draft.Package);
        Assert.Equal("report_asset_inventory", packageJson["report"]!.Value<string>("code"));
        Assert.NotNull(draft.ValidateResult);
        Assert.True(draft.ValidateResult!.Ok);
        Assert.NotNull(draft.Spec);
        // 工具消息回传成功标记 + 汇总，模型据此告知用户。
        var toolView = result.Messages.Single(item => item.Role == "tool");
        Assert.True(toolView.Success);
        Assert.Equal(PocAiToolRegistry.SubmitReportSpec, toolView.ToolName);
        Assert.Equal(1, await host.CountAuditAsync(result.SessionId));
        var audit = (await host.Database.Queryable<PocAiAuditLog>()
            .Where(item => item.SessionId == result.SessionId)
            .ToListAsync()).Single();
        Assert.True(audit.Success);
        Assert.Null(audit.SqlText);
    }

    /// <summary>验证编译失败回传带字段路径的错误列表并把草稿置 failed、计入熔断计数。</summary>
    [Fact]
    public async Task Submit_invalid_spec_returns_issues_with_paths_and_marks_failed()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var badSpec = JObject.Parse(PocAiTestHost.LoadSampleSpec());
        badSpec["futureExtension"] = new JObject { ["x"] = 1 };
        badSpec["report"]!["code"] = "9bad";
        host.LlmHandler.EnqueueJson(SubmitCall("call_bad", badSpec.ToString(Newtonsoft.Json.Formatting.None)));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("收到错误，我会修正。"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "提交一个 spec"));

        var draft = result.Draft;
        Assert.NotNull(draft);
        Assert.Equal("failed", draft!.Status);
        Assert.Null(draft.Package);
        var toolView = result.Messages.Single(item => item.Role == "tool");
        Assert.False(toolView.Success);
        var content = ToolMessageContent(host, result.SessionId);
        Assert.Contains("SPEC_FIELD_UNKNOWN", content, StringComparison.Ordinal);
        Assert.Contains("futureExtension", content, StringComparison.Ordinal);
        Assert.Contains("SPEC_CODE_INVALID", content, StringComparison.Ordinal);
        Assert.Contains("report.code", content, StringComparison.Ordinal);
        var session = host.Sessions.Get(result.SessionId);
        Assert.NotNull(session);
        Assert.Equal(1, session!.FailedSubmitCount);
        Assert.False((await host.Database.Queryable<PocAiAuditLog>()
            .Where(item => item.SessionId == result.SessionId)
            .ToListAsync()).Single().Success);
    }

    /// <summary>
    /// 验证校验失败（而非编译失败）同样计入熔断并把 validate errors 回传模型。
    /// 编译器刻意镜像了后端全部规则，可构造的“编译通过但校验失败”路径只剩
    /// 模板快照体积（后端限 4 MiB，编译器不限）：用 256 列超长表头把快照撑过上限。
    /// </summary>
    [Fact]
    public async Task Submit_validation_failure_returns_errors_and_marks_failed()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        // 样例已有 6 个字段/列；补到字段上限 256 个，每列表头 20000 字符 → 快照约 5 MiB 越限，
        // 编译器不限快照体积可过，后端校验按 4 MiB 上限拒绝（TEMPLATE_SNAPSHOT_TOO_LARGE）。
        var spec = JObject.Parse(PocAiTestHost.LoadSampleSpec());
        var hugeHeader = new string('表', 20000);
        var variables = (JArray)spec["dataset"]!["variables"]!;
        var fields = (JArray)spec["dataset"]!["fields"]!;
        var parameters = (JArray)spec["parameters"]!;
        var columns = (JArray)spec["template"]!["columns"]!;
        for (var index = 1; index <= 250; index++)
        {
            var fieldCode = $"field_{index:D3}";
            // 只补字段/参数/列，不补变量：数据集变量上限 100，SQL 未引用就不需要变量。
            parameters.Add(new JObject
            {
                ["code"] = fieldCode,
                ["displayName"] = $"字段{index}",
                ["dataType"] = "string",
                ["controlType"] = "text",
                ["sourceType"] = "Form",
                ["isRequired"] = false,
                ["isReadOnly"] = false,
                ["isVisible"] = false,
                ["allowUrlPrefill"] = false,
                ["urlKey"] = null,
                ["defaultExpression"] = null,
                ["fixedValue"] = null,
                ["options"] = null,
                ["displayOrder"] = index + 1,
            });
            fields.Add(new JObject
            {
                ["code"] = fieldCode,
                ["displayName"] = $"字段{index}",
                ["dataType"] = "string",
            });
            columns.Add(new JObject
            {
                ["field"] = fieldCode,
                ["header"] = hugeHeader,
            });
        }
        host.LlmHandler.EnqueueJson(SubmitCall("call_vbad", spec.ToString(Newtonsoft.Json.Formatting.None)));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("了解。"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "提交"));

        var toolView = result.Messages.Single(item => item.Role == "tool");
        Assert.False(toolView.Success);
        var content = ToolMessageContent(host, result.SessionId);
        System.IO.File.WriteAllText("D:/zp/git/hd-report-center/output/tmp-pin/issues_debug.txt", content);
        Assert.Contains("PACKAGE_VALIDATION_FAILED", content, StringComparison.Ordinal);
        Assert.Contains("TEMPLATE_SNAPSHOT_TOO_LARGE", content, StringComparison.Ordinal);
        Assert.Equal("failed", result.Draft!.Status);
        Assert.NotNull(result.Draft.ValidateResult);
        Assert.False(result.Draft.ValidateResult!.Ok);
    }

    /// <summary>验证 3 次失败熔断：第 4 次提交不再执行编译/校验，直接提示离线流水线人工介入。</summary>
    [Fact]
    public async Task Submit_circuit_breaks_after_three_failures()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var badSpec = JObject.Parse(PocAiTestHost.LoadSampleSpec());
        badSpec["report"]!["code"] = "9bad";
        var badSpecText = badSpec.ToString(Newtonsoft.Json.Formatting.None);
        // 三次失败必须发生在同一会话（熔断计数挂在会话上）；先建立会话再连续提交。
        host.LlmHandler.EnqueueJson(SubmitCall("call_bad_0", badSpecText));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("继续修正。"));
        var first = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "第1次提交"));
        Assert.Equal("failed", first.Draft!.Status);
        for (var round = 1; round < 3; round++)
        {
            host.LlmHandler.EnqueueJson(SubmitCall($"call_bad_{round}", badSpecText));
            host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("继续修正。"));
            var result = await host.ChatService.ChatAsync(new PocAiChatRequest(first.SessionId, "ai_main", $"第{round + 1}次提交"));
            Assert.Equal("failed", result.Draft!.Status);
        }
        // 第 4 次：熔断生效，工具消息明确指向离线流水线与人工介入。
        host.LlmHandler.EnqueueJson(SubmitCall("call_after_break"));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("明白，我会提示用户人工介入。"));
        var final = await host.ChatService.ChatAsync(new PocAiChatRequest(first.SessionId, "ai_main", "再试一次"));
        var toolView = final.Messages.Single(item => item.Role == "tool");
        Assert.False(toolView.Success);
        var content = ToolMessageContent(host, final.SessionId);
        Assert.Contains("SUBMIT_CIRCUIT_OPEN", content, StringComparison.Ordinal);
        Assert.Contains("离线流水线", content, StringComparison.Ordinal);
        Assert.Contains("人工介入", content, StringComparison.Ordinal);
        // 熔断期间不再触碰编译/校验链路：草稿保持最后一次失败的 failed 态。
        Assert.Equal("failed", final.Draft!.Status);
        // 同一会话累计 4 条审计：3 次失败提交 + 1 次熔断拒绝，每次工具调用都留痕。
        Assert.Equal(4, await host.CountAuditAsync(final.SessionId));
    }

    /// <summary>验证提交成功会清零熔断计数：失败→成功→失败 的序列不会被历史失败误熔断。</summary>
    [Fact]
    public async Task Submit_success_resets_failure_counter()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var badSpec = JObject.Parse(PocAiTestHost.LoadSampleSpec());
        badSpec["report"]!["code"] = "9bad";
        host.LlmHandler.EnqueueJson(SubmitCall("bad1", badSpec.ToString(Newtonsoft.Json.Formatting.None)));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("修正中。"));
        var failed = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "第一次提交"));
        host.LlmHandler.EnqueueJson(SubmitCall("good1"));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("这次通过了。"));
        var good = await host.ChatService.ChatAsync(new PocAiChatRequest(failed.SessionId, "ai_main", "第二次提交"));
        Assert.Equal("validated", good.Draft!.Status);
        var session = host.Sessions.Get(good.SessionId);
        Assert.NotNull(session);
        Assert.Equal(0, session!.FailedSubmitCount);
    }

    /// <summary>验证无草稿时 dry_run 直接拒绝（SPEC_DRAFT_REQUIRED），不触碰试跑链路。</summary>
    [Fact]
    public async Task DryRun_without_draft_is_refused()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(DryRunCall("call_dry"));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("好的。"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "试跑一下"));

        var toolView = result.Messages.Single(item => item.Role == "tool");
        Assert.False(toolView.Success);
        Assert.Contains("SPEC_DRAFT_REQUIRED", ToolMessageContent(host, result.SessionId), StringComparison.Ordinal);
        Assert.Null(result.Draft);
    }

    /// <summary>验证 dry_run 携带方言不一致的 spec 时回传 PACKAGE_DATA_SOURCE_TYPE_MISMATCH。</summary>
    [Fact]
    public async Task DryRun_with_type_mismatch_returns_structured_error()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        // 会话数据源 ai_main 标注为 PostgreSql；spec 声明 SqlServer → 试跑前方言校验拒绝。
        var specJson = SampleSpecWithSourceType("SqlServer");
        host.LlmHandler.EnqueueJson(SubmitCall("call_submit", specJson));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("已提交。"));
        var submitted = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "提交"));
        Assert.Equal("validated", submitted.Draft!.Status);

        host.LlmHandler.EnqueueJson(DryRunCall("call_dry"));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("看到了错误。"));
        var dryRun = await host.ChatService.ChatAsync(new PocAiChatRequest(submitted.SessionId, "ai_main", "试跑"));

        var toolView = dryRun.Messages.Single(item => item.Role == "tool");
        Assert.False(toolView.Success);
        Assert.Contains("PACKAGE_DATA_SOURCE_TYPE_MISMATCH", ToolMessageContent(host, dryRun.SessionId), StringComparison.Ordinal);
        Assert.Equal("validated", dryRun.Draft!.Status);
    }

    /// <summary>验证 dry_run 缺少必填参数样例值时回传 PARAM_REQUIRED，参数编码一并透出。</summary>
    [Fact]
    public async Task DryRun_missing_required_parameter_returns_structured_error()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var spec = JObject.Parse(PocAiTestHost.LoadSampleSpec());
        spec["dataset"]!["dataSourceType"] = "PostgreSql";
        spec["dataset"]!["variables"]![0]!["required"] = true;
        spec["parameters"]![0]!["isRequired"] = true;
        var specJson = spec.ToString(Newtonsoft.Json.Formatting.None);
        host.LlmHandler.EnqueueJson(SubmitCall("call_submit", specJson));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("已提交必填版。"));
        var submitted = await host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "提交"));
        Assert.Equal("validated", submitted.Draft!.Status);

        host.LlmHandler.EnqueueJson(DryRunCall("call_dry"));
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("收到。"));
        var dryRun = await host.ChatService.ChatAsync(new PocAiChatRequest(submitted.SessionId, "ai_main", "试跑"));

        var toolView = dryRun.Messages.Single(item => item.Role == "tool");
        Assert.False(toolView.Success);
        var content = ToolMessageContent(host, dryRun.SessionId);
        Assert.Contains("PARAM_REQUIRED", content, StringComparison.Ordinal);
        Assert.Contains("startDate", content, StringComparison.Ordinal);
    }

    /// <summary>验证 AI 关闭降级覆盖新工具：submit 在 IsEnabled=false 时同样 fail-fast，不产生任何草稿或审计。</summary>
    [Fact]
    public async Task Submit_when_disabled_fails_fast()
    {
        using var host = new PocAiTestHost();
        // 保持默认 IsEnabled=false。
        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(() => host.ChatService.ChatAsync(
            new PocAiChatRequest(null, "ai_main", "提交 spec")));

        Assert.StartsWith(PocAiDisabledException.ErrorCode, exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
        Assert.Equal(0, host.Sessions.CountActive());
        Assert.Equal(0, await host.CountAuditAsync("any"));
    }
}
