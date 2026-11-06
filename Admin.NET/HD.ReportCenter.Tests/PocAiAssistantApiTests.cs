using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using Xunit;

namespace HD.ReportCenter.Tests;

/// <summary>
/// AI 助手 API 层回归测试：钉住"设置保存是开关载体，不受 AI_DISABLED 拦截"这一契约——
/// 历史上保存接口曾被统一守卫误包，导致 AI 关闭态（默认态）下永远无法启用，且既有单测
/// 只覆盖服务层未能暴露该缺陷，故在 API 层显式钉住。调用型接口（chat 等）的关闭守卫
/// 由服务层用例（RequireEnabled/ChatAsync_when_disabled）钉住；其 Furion 异常翻译胶水
/// 依赖应用运行时静态初始化，无法在单测宿主内执行，不在本用例覆盖范围。
/// </summary>
public sealed class PocAiAssistantApiTests
{
    /// <summary>验证 AI 关闭态下保存设置仍然成功：保存是开启 AI 的唯一入口，必须永远可走通。</summary>
    /// <returns>保存返回的文档应携带所写开关与打码后的 ApiKey。</returns>
    [Fact]
    public async Task SaveSettings_when_ai_disabled_succeeds_and_returns_masked_document()
    {
        using var host = new PocAiTestHost();
        var api = new PocAiAssistantApi(host.SettingsService, host.ChatService, host.SyncMatch);

        var saved = await api.SaveSettings(new PocAiSettingsInput(
            IsEnabled: false,
            BaseUrl: "https://llm.test/v1",
            ApiKey: "sk-test-1234",
            Model: "test-model",
            SampleRowLimit: 20,
            SqlProbeEnabled: true,
            SqlProbeRowLimit: 100,
            MaxToolRounds: 8));

        Assert.False(saved.IsEnabled);
        Assert.Equal("test-model", saved.Model);
        Assert.Contains("***", saved.ApiKeyMasked);
    }
}
