using System.Reflection;
using HD.ReportCenter.Application.CoreValidation;
using Microsoft.Data.Sqlite;
using SqlSugar;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 数据同步任务存储层测试：重点回归“最近运行时间按本地偏移落库”的时区口径。
/// UpdateTaskLastRunAsync 与运行日志使用同一口径（本地偏移），若改回存 UTC，
/// 任务页“上次运行”墙上时间会差一个时区，Daily 调度的日期比较也会随之偏移。
/// </summary>
public sealed class PocDataSyncStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _database;
    private readonly PocDataSyncStore _store;

    /// <summary>初始化测试实例：SQLite 内存库建任务表并绑定被测存储。</summary>
    public PocDataSyncStoreTests()
    {
        _connection = new NonClosingSqliteConnection("Data Source=:memory:");
        _connection.Open();
        _database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
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
        // SqlSugar 允许注入既有连接：主存储客户端直接绑定共享连接且整个测试期间不释放。
        _database.Ado.Connection = _connection;
        _database.Ado.Open();
        _database.CodeFirst.InitTables(typeof(PocSyncTask));
        _store = new PocDataSyncStore(_database);
    }

    /// <summary>验证最近运行时间写入后读回偏移与墙上时间一致（本地偏移口径，不再存 UTC）。</summary>
    [Fact]
    public async Task UpdateTaskLastRunAsync_keeps_local_offset_and_wall_time()
    {
        // 第一阶段：播种任务行并写入一个非 UTC 偏移的完成时间，模拟 +08:00 服务器时钟。
        var when = new DateTimeOffset(2026, 11, 4, 10, 30, 0, TimeSpan.FromHours(8));
        await _database.Insertable(new PocSyncTask
        {
            Code = "tz_task",
            Name = "时区回归任务",
            SourceDatasetCode = "ds",
            TargetDataSourceCode = "tgt",
            TargetTable = "TZ_TGT",
            DefinitionJson = "{}",
            ConflictPolicy = "Append",
            ScheduleType = "Manual",
            IntervalMinutes = 0,
            Revision = 1,
            IsEnabled = true
        }).ExecuteCommandAsync();

        // 第二阶段：读回断言。偏移与墙上时间都必须与写入值一致——改存 UTC 会让
        // LastRunAt.Offset 变成 +00:00 且 LocalDateTime 偏移 8 小时，两者任一漂移都判失败。
        await _store.UpdateTaskLastRunAsync("tz_task", when);
        var task = await _store.GetTaskAsync("tz_task");
        Assert.NotNull(task?.LastRunAt);
        Assert.Equal(when.Offset, task!.LastRunAt!.Value.Offset);
        Assert.Equal(when.LocalDateTime, task.LastRunAt.Value.LocalDateTime);
    }

    /// <summary>释放内存库连接，销毁测试数据库。</summary>
    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
