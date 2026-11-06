using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 设置服务单测：ApiKey 打码回显与空值保留、限额与启用条件校验、AI 启用守卫。
/// 存储层走 SQLite 内存库，不依赖真实数据库。
/// </summary>
public sealed class PocAiSettingsServiceTests
{
    /// <summary>验证打码规则：sk-*** + 末 4 位；过短密钥只显示前缀，绝不回传原文。</summary>
    [Theory]
    [InlineData("sk-abcdef123456", "sk-***3456")]
    [InlineData("abc123", "sk-***c123")]
    [InlineData("1234", "sk-***")]
    [InlineData("a", "sk-***")]
    public void MaskApiKey_masks_everything_but_last_four(string apiKey, string expected)
    {
        Assert.Equal(expected, PocAiSettingsService.MaskApiKey(apiKey));
    }

    /// <summary>验证保存后读取回显：ApiKey 只返回打码值与 configured 标记，原文不出现在任何字段。</summary>
    [Fact]
    public async Task GetDocument_masks_api_key()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync(apiKey: "sk-my-secret-9876");

        var document = await host.SettingsService.GetDocumentAsync();

        Assert.True(document.IsEnabled);
        Assert.Equal("sk-***9876", document.ApiKeyMasked);
        Assert.True(document.ApiKeyConfigured);
        Assert.DoesNotContain("sk-my-secret-9876", System.Text.Json.JsonSerializer.Serialize(document));
        Assert.Equal(20, document.SampleRowLimit);
        Assert.True(document.SqlProbeEnabled);
        Assert.Equal(100, document.SqlProbeRowLimit);
        Assert.Equal(8, document.MaxToolRounds);
        Assert.NotNull(document.UpdateTime);
    }

    /// <summary>验证空 ApiKey 保存语义：传空表示保留库中原值，打码回显不变；传新值则整体替换。</summary>
    [Fact]
    public async Task Save_empty_api_key_keeps_existing_value()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync(apiKey: "sk-original-key-1111");

        var saved = await host.SettingsService.SaveAsync(new PocAiSettingsInput(
            true, "http://llm.test/v1", "", "test-model", 20, true, 100, 8));
        Assert.Equal("sk-***1111", saved.ApiKeyMasked);
        Assert.Equal("sk-original-key-1111", (await host.SettingsStore.GetAsync()).ApiKey);

        var replaced = await host.SettingsService.SaveAsync(new PocAiSettingsInput(
            true, "http://llm.test/v1", "sk-new-key-2222", "test-model", 20, true, 100, 8));
        Assert.Equal("sk-***2222", replaced.ApiKeyMasked);
    }

    /// <summary>验证首次保存时行缺失则插入、再次保存整行覆盖（单行配置，后写覆盖先写）。</summary>
    [Fact]
    public async Task Save_upserts_single_row()
    {
        using var host = new PocAiTestHost();
        var first = await host.SettingsService.SaveAsync(new PocAiSettingsInput(
            false, "http://a/v1", null, "m1", 10, false, 50, 4));
        Assert.False(first.IsEnabled);
        Assert.Equal(10, first.SampleRowLimit);

        var second = await host.SettingsService.SaveAsync(new PocAiSettingsInput(
            false, "http://b/v2", null, "m2", 30, true, 200, 12));
        Assert.Equal("http://b/v2", second.BaseUrl);
        Assert.Equal(30, second.SampleRowLimit);
        Assert.Equal(200, second.SqlProbeRowLimit);
        Assert.Equal(12, second.MaxToolRounds);
    }

    /// <summary>验证限额校验：越界值整体拒绝（AI_SETTINGS_INVALID），非法边界一个都不放行。</summary>
    [Theory]
    [InlineData(-1, 100, 8)]
    [InlineData(101, 100, 8)]
    [InlineData(20, 0, 8)]
    [InlineData(20, 1001, 8)]
    [InlineData(20, 100, 0)]
    [InlineData(20, 100, 21)]
    public async Task Save_rejects_out_of_range_limits(int sampleRowLimit, int sqlProbeRowLimit, int maxToolRounds)
    {
        using var host = new PocAiTestHost();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            host.SettingsService.SaveAsync(new PocAiSettingsInput(
                false, "http://llm.test/v1", null, "m", sampleRowLimit, true, sqlProbeRowLimit, maxToolRounds)));

        Assert.Equal("AI_SETTINGS_INVALID", exception.Code);
    }

    /// <summary>验证启用条件：启用时 BaseUrl 必须是合法 http(s) 绝对地址且 Model 必填，否则拒绝保存。</summary>
    [Theory]
    [InlineData("", "m")]
    [InlineData("not-a-url", "m")]
    [InlineData("ftp://llm.test", "m")]
    [InlineData("http://llm.test/v1", "")]
    public async Task Save_enabled_requires_valid_base_url_and_model(string baseUrl, string model)
    {
        using var host = new PocAiTestHost();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            host.SettingsService.SaveAsync(new PocAiSettingsInput(
                true, baseUrl, null, model, 20, true, 100, 8)));

        Assert.Equal("AI_SETTINGS_INVALID", exception.Code);
        Assert.False((await host.SettingsStore.GetAsync()).IsEnabled);
    }

    /// <summary>验证 AI 启用守卫：未启用 / 缺地址 / 缺模型三种情况全部抛 AI_DISABLED。</summary>
    [Theory]
    [InlineData(false, "http://llm.test/v1", "m")]
    [InlineData(true, "", "m")]
    [InlineData(true, "http://llm.test/v1", "")]
    public async Task RequireEnabled_throws_ai_disabled(bool isEnabled, string baseUrl, string model)
    {
        using var host = new PocAiTestHost();
        // 直接种子存储层（绕过保存校验），模拟手工改库造成的“启用但配置不完整”状态。
        await host.SettingsStore.SaveAsync(new PocAiSettings
        {
            IsEnabled = isEnabled,
            BaseUrl = baseUrl,
            ApiKey = null,
            Model = model,
            SampleRowLimit = 20,
            SqlProbeEnabled = true,
            SqlProbeRowLimit = 100,
            MaxToolRounds = 8
        });

        var exception = await Assert.ThrowsAsync<PocAiDisabledException>(
            () => host.SettingsService.RequireEnabledAsync());

        Assert.StartsWith(PocAiDisabledException.ErrorCode, exception.Message);
    }

    /// <summary>验证守卫放行：启用且配置完整时返回配置实体供对话与测试连通使用。</summary>
    [Fact]
    public async Task RequireEnabled_returns_settings_when_usable()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync(baseUrl: "http://llm.test/v1", model: "test-model", apiKey: "sk-ok-9999");

        var settings = await host.SettingsService.RequireEnabledAsync();

        Assert.True(settings.IsEnabled);
        Assert.Equal("test-model", settings.Model);
        Assert.Equal("sk-ok-9999", settings.ApiKey);
    }
}
