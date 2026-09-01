using Admin.NET.Core;
using Microsoft.Extensions.Configuration;
using SqlSugar;

namespace HD.ReportCenter.Tests;

[CollectionDefinition(nameof(DatabaseEnvironmentCollection), DisableParallelization = true)]
public sealed class DatabaseEnvironmentCollection;

[Collection(nameof(DatabaseEnvironmentCollection))]
public sealed class DbConnectionOptionsTests
{
    private const string InitializationVariable = "HD_REPORT_CENTER_DB_INITIALIZE";

    /// <summary>验证 One time initialization environment switch enables tables and seeds only 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void One_time_initialization_environment_switch_enables_tables_and_seeds_only()
    {
        var previousValue = Environment.GetEnvironmentVariable(InitializationVariable);
        Environment.SetEnvironmentVariable(InitializationVariable, "true");
        try
        {
            var options = CreateOptions();

            options.PostConfigure(options, new ConfigurationBuilder().Build());

            var mainConfig = Assert.Single(options.ConnectionConfigs);
            Assert.False(mainConfig.DbSettings.EnableInitDb);
            Assert.True(mainConfig.TableSettings.EnableInitTable);
            Assert.True(mainConfig.SeedSettings.EnableInitSeed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(InitializationVariable, previousValue);
        }
    }

    /// <summary>验证 Normal runtime keeps all initialization switches disabled 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Normal_runtime_keeps_all_initialization_switches_disabled()
    {
        var previousValue = Environment.GetEnvironmentVariable(InitializationVariable);
        Environment.SetEnvironmentVariable(InitializationVariable, null);
        try
        {
            var options = CreateOptions();

            options.PostConfigure(options, new ConfigurationBuilder().Build());

            var mainConfig = Assert.Single(options.ConnectionConfigs);
            Assert.False(mainConfig.DbSettings.EnableInitDb);
            Assert.False(mainConfig.TableSettings.EnableInitTable);
            Assert.False(mainConfig.SeedSettings.EnableInitSeed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(InitializationVariable, previousValue);
        }
    }

    /// <summary>验证 Runtime rejects sqlite main database 场景下的预期行为，并防止相关逻辑回归。</summary>
    [Fact]
    public void Runtime_rejects_sqlite_main_database()
    {
        var options = CreateOptions();
        options.ConnectionConfigs[0].DbType = DbType.Sqlite;
        options.ConnectionConfigs[0].ConnectionString = "Data Source=./Admin.NET.db";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            options.PostConfigure(options, new ConfigurationBuilder().Build()));

        Assert.Contains("必须使用 SQL Server", exception.Message);
        Assert.Contains("禁止使用 SQLite", exception.Message);
    }

    /// <summary>构建 DbConnectionOptionsTests 所需的 Create Options 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static DbConnectionOptions CreateOptions() =>
        new()
        {
            ConnectionConfigs =
            [
                new DbConnectionConfig
                {
                    DbType = DbType.SqlServer,
                    ConnectionString = "Server=127.0.0.1;Database=hd_report_center;User Id=test;Password=CHANGE_ME;",
                    DbSettings = new DbSettings { EnableInitDb = false },
                    TableSettings = new TableSettings { EnableInitTable = false },
                    SeedSettings = new SeedSettings { EnableInitSeed = false }
                }
            ]
        };
}
