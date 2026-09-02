using HD.ReportCenter.Application;
using Microsoft.Extensions.Configuration;

namespace HD.ReportCenter.Tests;

/// <summary>验证报表中心可选功能开关的默认值和配置文件读取行为。</summary>
public sealed class ReportCenterFeaturesTests
{
    /// <summary>验证未配置 JNPF 迁移开关时保持关闭，避免可选菜单意外暴露。</summary>
    [Fact]
    public void JnpfMigrationMenuIsDisabledByDefault()
    {
        var previous = Environment.GetEnvironmentVariable("JNPF_MIGRATION_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("JNPF_MIGRATION_ENABLED", null);
            var configuration = new ConfigurationBuilder().Build();

            Assert.False(ReportCenterFeatureOptions.IsJnpfMigrationMenuEnabled(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JNPF_MIGRATION_ENABLED", previous);
        }
    }

    /// <summary>验证配置文件显式开启 JNPF 迁移开关时返回开启状态。</summary>
    [Fact]
    public void JnpfMigrationMenuReadsConfigurationValue()
    {
        var previous = Environment.GetEnvironmentVariable("JNPF_MIGRATION_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("JNPF_MIGRATION_ENABLED", null);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ReportCenterFeatureOptions.JnpfMigrationMenuEnabledKey] = "true",
                })
                .Build();

            Assert.True(ReportCenterFeatureOptions.IsJnpfMigrationMenuEnabled(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JNPF_MIGRATION_ENABLED", previous);
        }
    }
}
