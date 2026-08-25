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
