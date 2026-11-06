using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 同步 AI 智能字段匹配单测（spec §8）：确定性同名基线、LLM 语义匹配与降级、
/// 白名单外转换器拒绝、人工映射不被覆盖、确定性层先行的零 LLM 请求钉子。
/// LLM 走可脚本化伪 HTTP 通道，存储走 SQLite 内存库。
/// </summary>
public sealed class PocAiSyncMatchTests
{
    /// <summary>构造直接载荷请求。</summary>
    /// <param name="sourceFields">源字段清单。</param>
    /// <param name="targetColumns">目标列元数据。</param>
    /// <param name="mappings">当前映射。</param>
    /// <returns>匹配请求。</returns>
    private static PocAiSyncMatchRequest BuildRequest(
        IReadOnlyList<PocAiSyncMatchSourceField> sourceFields,
        IReadOnlyList<PocSyncColumnMetadataDocument> targetColumns,
        IReadOnlyList<PocSyncColumnMapping>? mappings = null) => new(
        null, "SqlServer", "SqlServer", sourceFields, targetColumns, mappings ?? []);

    /// <summary>
    /// 构造源字段清单：用例按 (名称, 类型代码, 中文注释) 三元组声明，
    /// 注释参与 LLM 提示词装配，故与数据集字段契约保持同一形状。
    /// </summary>
    /// <param name="definitions">(名称, 类型, 注释) 序列。</param>
    /// <returns>源字段清单。</returns>
    private static IReadOnlyList<PocAiSyncMatchSourceField> Sources(
        params (string Name, string DataType, string? Description)[] definitions) =>
        definitions.Select(item => new PocAiSyncMatchSourceField(item.Name, item.DataType, item.Description)).ToArray();

    /// <summary>构造目标列元数据。</summary>
    /// <param name="definitions">(名称, 数据库类型, 可空, 长度, 注释) 序列。</param>
    /// <returns>目标列清单。</returns>
    private static IReadOnlyList<PocSyncColumnMetadataDocument> Targets(
        params (string Name, string DbType, bool Nullable, int? Length, string? Description)[] definitions) =>
        definitions.Select(item => new PocSyncColumnMetadataDocument(
            item.Name, item.DbType, item.Nullable, item.Length, item.Description)).ToArray();

    /// <summary>验证同名基线：忽略大小写命中、类型按 targetDbTypeToType 推断、置信 high。</summary>
    [Fact]
    public async Task Match_same_name_baseline_is_high_confidence()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(("FEE_TYPE", "string", "费用类别"), ("VISIT_DATE", "datetime", "就诊日期")),
            Targets(
                ("fee_type", "varchar", true, 32, "费用类别"),
                ("visit_date", "datetime2", true, null, "就诊日期"))));

        Assert.False(result.Degraded);
        Assert.Equal(2, result.Suggestions.Count);
        var feeType = result.Suggestions.Single(item => item.TargetField == "fee_type");
        Assert.Equal("FEE_TYPE", feeType.SourceField);
        Assert.Equal("high", feeType.Confidence);
        Assert.Equal("string", feeType.TargetType);
        Assert.Equal(32, feeType.Length);
        Assert.Empty(feeType.Converters);
        var visitDate = result.Suggestions.Single(item => item.TargetField == "visit_date");
        Assert.Equal("datetime", visitDate.TargetType);
        Assert.Null(visitDate.Length);
    }

    /// <summary>验证确定性层先行：全部歧义已被同名/人工覆盖时不发任何 LLM 请求。</summary>
    [Fact]
    public async Task Match_skips_llm_when_nothing_ambiguous()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(("PATIENT_ID", "number", "患者编号"), ("PATIENT_NAME", "string", "姓名")),
            Targets(
                ("PATIENT_ID", "bigint", false, null, "患者编号"),
                ("PATIENT_NAME", "varchar", true, 64, "姓名")),
            [new PocSyncColumnMapping("PATIENT_ID", "PATIENT_ID", "number", null, false, [])]));

        Assert.False(result.Degraded);
        // 源/目标一对一：人工 1 条 + 同名 1 条，歧义清单为空 → 零 LLM 请求。
        Assert.Equal(0, host.LlmHandler.RequestCount);
        var suggestion = result.Suggestions.Single(item => item.TargetField == "PATIENT_NAME");
        Assert.Equal("high", suggestion.Confidence);
        Assert.True(result.Suggestions.All(item => !item.NeedsManual));
    }

    /// <summary>验证 LLM 语义匹配：改名/缩写字段由模型给出建议并回填置信与依据。</summary>
    [Fact]
    public async Task Match_llm_suggests_renamed_fields()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(JsonConvert.SerializeObject(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    finish_reason = "stop",
                    message = new
                    {
                        role = "assistant",
                        content = JsonConvert.SerializeObject(new
                        {
                            matches = new object[]
                            {
                                new
                                {
                                    sourceField = "FEE_TYPE",
                                    targetField = "FEE_CATEGORY",
                                    confidence = "high",
                                    reason = "费用类别语义一致",
                                    converters = new[] { "trim" },
                                },
                                new
                                {
                                    sourceField = "IS_REFUND",
                                    targetField = "REFUND_FLAG",
                                    confidence = "mid",
                                    reason = "退费标志，方向一致",
                                },
                            }
                        })
                    }
                }
            }
        }));

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(
                ("FEE_TYPE", "string", "费用类别"),
                ("IS_REFUND", "string", "是否退费"),
                ("PATIENT_ID", "number", "患者编号")),
            Targets(
                ("FEE_CATEGORY", "varchar", true, 32, "费用类别"),
                ("REFUND_FLAG", "varchar", true, 2, "退费标志"),
                ("patient_id", "bigint", false, null, "患者编号"))));

        Assert.False(result.Degraded);
        Assert.Equal(1, host.LlmHandler.RequestCount);
        // 第一段：命中数与请求次数核对——同名 1 列直通，歧义 2 列走了一次 LLM 语义匹配。
        // patient_id 走同名 high；FEE_CATEGORY 走 LLM high（含白名单内 trim）；REFUND_FLAG 走 LLM mid。
        var feeCategory = result.Suggestions.Single(item => item.TargetField == "FEE_CATEGORY");
        Assert.Equal("high", feeCategory.Confidence);
        Assert.Equal("FEE_TYPE", feeCategory.SourceField);
        Assert.Contains("费用类别语义一致", feeCategory.Reason, StringComparison.Ordinal);
        Assert.Single(feeCategory.Converters);
        Assert.Equal("trim", feeCategory.Converters[0].Type);
        var refund = result.Suggestions.Single(item => item.TargetField == "REFUND_FLAG");
        Assert.Equal("mid", refund.Confidence);
        var patientId = result.Suggestions.Single(item => item.TargetField == "patient_id");
        Assert.Equal("high", patientId.Confidence);
        Assert.Equal("PATIENT_ID", patientId.SourceField);
    }

    /// <summary>验证 LLM 返回非法 JSON 时降级为同名结果并在响应中提示。</summary>
    [Fact]
    public async Task Match_degrades_to_baseline_when_llm_returns_garbage()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("这不是 JSON，是一段自由发挥的文字"));

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(
                ("FEE_TYPE", "string", "费用类别"),
                ("VISIT_DATE", "datetime", "就诊日期")),
            Targets(
                ("FEE_CATEGORY", "varchar", true, 32, "费用类别"),
                ("visit_date", "datetime2", true, null, "就诊日期"))));

        Assert.True(result.Degraded);
        Assert.NotNull(result.Message);
        Assert.Contains("降级", result.Message, StringComparison.Ordinal);
        // 同名基线保留：visit_date 仍命中；FEE_CATEGORY 无同名且 LLM 不可用 → 标记需人工处理。
        var visitDate = result.Suggestions.Single(item => item.TargetField == "visit_date");
        Assert.Equal("VISIT_DATE", visitDate.SourceField);
        Assert.Equal("high", visitDate.Confidence);
        var feeCategory = result.Suggestions.Single(item => item.TargetField == "FEE_CATEGORY");
        Assert.True(feeCategory.NeedsManual);
        Assert.Contains("需人工处理", feeCategory.Reason, StringComparison.Ordinal);
        Assert.Empty(feeCategory.SourceField);
    }

    /// <summary>验证 LLM 请求失败（网关错误）同样降级为同名结果。</summary>
    [Fact]
    public async Task Match_degrades_when_llm_request_fails()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueError(500, "boom");

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(
                ("VISIT_DATE", "datetime", "就诊日期"),
                ("FEE_TYPE", "string", "费用类别")),
            Targets(
                ("visit_date", "datetime2", true, null, "就诊日期"),
                ("FEE_CATEGORY", "varchar", true, 32, "费用类别"))));

        Assert.True(result.Degraded);
        var visitDate = result.Suggestions.Single(item => item.TargetField == "visit_date");
        Assert.Equal("VISIT_DATE", visitDate.SourceField);
        Assert.Equal("FEE_CATEGORY", result.Suggestions.Single(item => item.TargetField == "FEE_CATEGORY").TargetField);
    }

    /// <summary>验证白名单外转换器逐个拒绝：非法名被剔除并写进依据，合法名保留。</summary>
    [Fact]
    public void ResolveConverterChain_rejects_non_whitelisted_converters()
    {
        var (steps, rejected) = PocAiSyncMatchService.ResolveConverterChain(
            ["toDecimal", "dropTable", "trim", "evalScript"]);

        Assert.Equal(2, steps.Count);
        Assert.Equal("toDecimal", steps[0].Type);
        Assert.Equal("trim", steps[1].Type);
        Assert.Equal(new[] { "dropTable", "evalScript" }, rejected.ToArray());
    }

    /// <summary>验证 LLM 建议中的白名单外转换器在语义匹配内被拒绝且依据注明。</summary>
    [Fact]
    public async Task Match_rejects_non_whitelisted_llm_converters()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var matchJson = JsonConvert.SerializeObject(new
        {
            matches = new[]
            {
                new
                {
                    sourceField = "FEE_TYPE",
                    targetField = "FEE_CATEGORY",
                    confidence = "high",
                    reason = "费用类别",
                    converters = new[] { "toDecimal", "execShell" },
                },
            }
        });
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse(matchJson));

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(("FEE_TYPE", "number", "费用类别")),
            Targets(("FEE_CATEGORY", "varchar", true, 32, "费用类别"))));

        var suggestion = Assert.Single(result.Suggestions.Where(item => !item.NeedsManual));
        Assert.Single(suggestion.Converters);
        Assert.Equal("toDecimal", suggestion.Converters[0].Type);
        Assert.Contains("execShell", suggestion.Reason, StringComparison.Ordinal);
        Assert.Contains("白名单外", suggestion.Reason, StringComparison.Ordinal);
    }

    /// <summary>验证人工映射不被覆盖：LLM 建议与人工映射冲突时以人工为准，建议被丢弃。</summary>
    [Fact]
    public async Task Match_never_overrides_manual_mappings()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        // 人工已把 PATIENT_NAME 映射到 patient_name；LLM 却建议把 OTHER_SRC 映射到 patient_name（冲突）。
        var matchJson = JsonConvert.SerializeObject(new
        {
            matches = new[]
            {
                new
                {
                    sourceField = "OTHER_SRC",
                    targetField = "patient_name",
                    confidence = "high",
                    reason = "名字语义撞车",
                },
            }
        });
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse(matchJson));

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(
                ("PATIENT_NAME", "string", "患者姓名"),
                ("OTHER_SRC", "string", "其他来源")),
            Targets(
                ("patient_name", "varchar", true, 64, "患者姓名"),
                ("backup_name", "varchar", true, 64, "备用姓名")),
            [new PocSyncColumnMapping("PATIENT_NAME", "patient_name", "string", 64, true, [])]));

        // 人工映射的 patient_name 不生成任何建议；LLM 冲突建议被丢弃；backup_name 落入需人工处理。
        Assert.DoesNotContain(result.Suggestions, item => item.TargetField == "patient_name");
        var backup = result.Suggestions.Single(item => item.TargetField == "backup_name");
        Assert.True(backup.NeedsManual);
        Assert.Equal(1, host.LlmHandler.RequestCount);
    }

    /// <summary>验证确定性转换矩阵：字符串→数值/时间补解析转换，其他→字符串补 toString，同类型直通。</summary>
    [Theory]
    [InlineData("string", "number", "toNumber")]
    [InlineData("string", "decimal", "toDecimal")]
    [InlineData("string", "datetime", "toDate")]
    [InlineData("number", "string", "toString")]
    [InlineData("datetime", "string", "toString")]
    [InlineData("number", "decimal", "")]
    [InlineData("string", "string", "")]
    public void SuggestConverters_follows_type_matrix(string source, string target, string expected)
    {
        var steps = PocAiSyncMatchService.SuggestConverters(source, target);

        if (expected.Length == 0)
        {
            Assert.Empty(steps);
        }
        else
        {
            var step = Assert.Single(steps);
            Assert.Equal(expected, step.Type);
        }
    }

    /// <summary>验证目标类型推断与前端 targetDbTypeToType 同词表：时间优先、未知退化 string。</summary>
    [Theory]
    [InlineData("datetime2", "datetime")]
    [InlineData("TIMESTAMP(6)", "datetime")]
    [InlineData("bit", "bool")]
    [InlineData("bigint", "number")]
    [InlineData("numeric(12,2)", "decimal")]
    [InlineData("CLOB", "string")]
    [InlineData("varchar", "string")]
    public void InferTargetType_mirrors_frontend_rules(string dbType, string expected)
    {
        Assert.Equal(expected, PocAiSyncMatchService.InferTargetType(dbType));
    }

    /// <summary>验证审计：匹配请求记一行 RcAiAuditLog（工具名 sync_match，含命中数与耗时）。</summary>
    [Fact]
    public async Task Match_writes_audit_row_per_request()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("{\"matches\":[]}"));

        var result = await host.SyncMatch.MatchAsync(BuildRequest(
            Sources(("VISIT_DATE", "datetime", "就诊日期")),
            Targets(
                ("visit_date", "datetime2", true, null, "就诊日期"),
                ("FEE_CATEGORY", "varchar", true, 32, "费用类别"))));

        var audit = (await host.Database.Queryable<PocAiAuditLog>()
            .Where(item => item.ToolName == PocAiSyncMatchService.AuditToolName)
            .ToListAsync()).Single();
        Assert.True(audit.Success);
        Assert.Equal(1, audit.RowCount);
        Assert.True(audit.DurationMs >= 0);
        Assert.Null(audit.SqlText);
        Assert.False(result.Degraded);
    }

    /// <summary>验证 AI 关闭降级覆盖 sync-match：IsEnabled=false 时 fail-fast，零 LLM 请求、零审计。</summary>
    [Fact]
    public async Task Match_when_disabled_fails_fast()
    {
        using var host = new PocAiTestHost();
        // 保持默认 IsEnabled=false。

        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(() => host.SyncMatch.MatchAsync(
            BuildRequest(
                Sources(("FEE_TYPE", "string", "费用类别")),
                Targets(("FEE_CATEGORY", "varchar", true, 32, "费用类别")))));

        Assert.StartsWith(PocAiDisabledException.ErrorCode, exception.Message);
        Assert.Equal(0, host.LlmHandler.RequestCount);
        Assert.Equal(0, await host.Database.Queryable<PocAiAuditLog>().CountAsync());
    }
}
