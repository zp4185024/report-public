namespace HD.ReportCenter.Application;

using Microsoft.Extensions.Configuration;

/// <summary>报表中心可选功能的公开配置键和值读取规则。</summary>
internal static class ReportCenterFeatureOptions
{
    /// <summary>控制 JNPF 迁移菜单和对应接口是否启用的配置键。</summary>
    public const string JnpfMigrationMenuEnabledKey =
        "AppSettings:ReportCenter:JnpfMigrationMenuEnabled";

    /// <summary>
    /// 读取 JNPF 迁移功能开关；环境变量优先于配置文件，未明确配置时默认关闭。
    /// </summary>
    /// <param name="configuration">应用配置，用于读取功能开关。</param>
    /// <returns>配置为 true 时返回 true，否则返回 false。</returns>
    public static bool IsJnpfMigrationMenuEnabled(IConfiguration configuration)
    {
        var environmentOverride = Environment.GetEnvironmentVariable("JNPF_MIGRATION_ENABLED");
        if (bool.TryParse(environmentOverride, out var enabledByEnvironment))
        {
            return enabledByEnvironment;
        }

        return bool.TryParse(configuration[JnpfMigrationMenuEnabledKey], out var enabledByConfiguration)
            && enabledByConfiguration;
    }
}

/// <summary>报表中心前端启动所需的可选功能开关。</summary>
public sealed record ReportCenterFeaturesResult(bool JnpfMigrationMenuEnabled);

[AllowAnonymous]
[ApiDescriptionSettings("报表中心", Name = "ReportCenterFeatures", Order = 900)]
public sealed class ReportCenterFeaturesApi : IDynamicApiController
{
    private readonly IConfiguration _configuration;

    /// <summary>初始化功能配置接口，保存读取报表中心开关所需的应用配置。</summary>
    /// <param name="configuration">应用配置，用于读取功能开关。</param>
    public ReportCenterFeaturesApi(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>返回前端菜单和路由需要的报表中心可选功能开关。</summary>
    /// <returns>包含 JNPF 迁移菜单是否启用的配置结果。</returns>
    [HttpGet("/api/report-center/features")]
    public ReportCenterFeaturesResult Get()
    {
        return new ReportCenterFeaturesResult(
            ReportCenterFeatureOptions.IsJnpfMigrationMenuEnabled(_configuration));
    }
}
