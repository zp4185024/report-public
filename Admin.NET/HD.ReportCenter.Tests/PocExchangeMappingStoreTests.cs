using HD.ReportCenter.Application.CoreValidation;
using Microsoft.Data.Sqlite;
using System.Reflection;
using SqlSugar;

namespace HD.ReportCenter.Tests;

public sealed class PocExchangeMappingStoreTests : IDisposable
{
    private long _nextId = 2000;
    private readonly SqlSugarClient _database;
    private readonly PocExchangeMappingStore _store;

    /// <summary>初始化 PocExchangeMappingStoreTests 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocExchangeMappingStoreTests()
    {
        _database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:",
            DbType = DbType.Sqlite,
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
        _database.Aop.DataExecuting = (_, entityInfo) =>
        {
            if (entityInfo.OperationType != DataFilterType.InsertByObject ||
                !entityInfo.EntityColumnInfo.IsPrimarykey ||
                entityInfo.EntityColumnInfo.PropertyInfo.PropertyType != typeof(long))
            {
                return;
            }

            var id = (long)entityInfo.EntityColumnInfo.PropertyInfo.GetValue(entityInfo.EntityValue)!;
            if (id == 0)
            {
                entityInfo.SetValue(Interlocked.Increment(ref _nextId));
            }
        };
        _database.Ado.Open();
        _database.CodeFirst.InitTables<PocExchangeMapping>();
        _store = new PocExchangeMappingStore(_database);
    }

    /// <summary>验证 Empty table is seeded once and returns current rules 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Empty_table_is_seeded_once_and_returns_current_rules()
    {
        var first = await _store.GetCurrentAsync("0.25.1");
        var countAfterFirstRead = await _database.Queryable<PocExchangeMapping>().CountAsync();
        var second = await _store.GetCurrentAsync("0.25.1");

        Assert.NotEmpty(first.Rules);
        Assert.Equal(1, first.MappingVersion);
        Assert.Equal(first.MappingVersion, second.MappingVersion);
        Assert.Equal(first.Rules.Count, countAfterFirstRead);
        Assert.Equal(countAfterFirstRead, await _database.Queryable<PocExchangeMapping>().CountAsync());
        Assert.Contains(first.Rules, rule => rule.RuleCode == "xlsx-to-univer-cell-formula");
        Assert.Contains(first.Rules, rule => rule.RuleCode == "univer-to-xlsx-cell-formula");
    }

    /// <summary>验证 Highest mapping version is selected and disabled rules are ignored 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Highest_mapping_version_is_selected_and_disabled_rules_are_ignored()
    {
        await InsertRuleAsync("old-rule", 1, true, 10);
        await InsertRuleAsync("new-enabled", 2, true, 20);
        await InsertRuleAsync("new-disabled", 2, false, 10);

        var current = await _store.GetCurrentAsync("0.25.1");

        Assert.Equal(2, current.MappingVersion);
        var rule = Assert.Single(current.Rules);
        Assert.Equal("new-enabled", rule.RuleCode);
    }

    /// <summary>验证 Rules are sorted by sort then rule code 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Rules_are_sorted_by_sort_then_rule_code()
    {
        await InsertRuleAsync("rule-z", 3, true, 20);
        await InsertRuleAsync("rule-b", 3, true, 10);
        await InsertRuleAsync("rule-a", 3, true, 10);

        var current = await _store.GetCurrentAsync("0.25.1");

        Assert.Equal(["rule-a", "rule-b", "rule-z"], current.Rules.Select(rule => rule.RuleCode));
    }

    /// <summary>验证 Duplicate rule codes are rejected by the database index 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Duplicate_rule_codes_are_rejected_by_the_database_index()
    {
        await InsertRuleAsync("duplicate", 4, true, 10);

        await Assert.ThrowsAsync<SqliteException>(
            () => InsertRuleAsync("duplicate", 4, true, 20));
    }

    /// <summary>释放 PocExchangeMappingStoreTests 持有的连接、订阅或其他运行资源，避免资源继续占用。</summary>
    public void Dispose()
    {
        _database.Dispose();
    }

    /// <summary>负责 PocExchangeMappingStoreTests 的 Insert Rule Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    /// <param name="mappingVersion">mappingVersion 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="enabled">enabled 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sort">sort 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private Task<int> InsertRuleAsync(string ruleCode, int mappingVersion, bool enabled, int sort) =>
        _database.Insertable(new PocExchangeMapping
        {
            UniverVersion = "0.25.1",
            MappingVersion = mappingVersion,
            RuleCode = ruleCode,
            Direction = "ExcelToUniver",
            Category = "Cell",
            SourcePath = "Excel.Cell.Value",
            TargetPath = "v",
            TransformType = "Direct",
            IsRequired = true,
            IsEnabled = enabled,
            Sort = sort,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        }).ExecuteCommandAsync();
}
