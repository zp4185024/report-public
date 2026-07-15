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

    [Fact]
    public async Task Rules_are_sorted_by_sort_then_rule_code()
    {
        await InsertRuleAsync("rule-z", 3, true, 20);
        await InsertRuleAsync("rule-b", 3, true, 10);
        await InsertRuleAsync("rule-a", 3, true, 10);

        var current = await _store.GetCurrentAsync("0.25.1");

        Assert.Equal(["rule-a", "rule-b", "rule-z"], current.Rules.Select(rule => rule.RuleCode));
    }

    [Fact]
    public async Task Duplicate_rule_codes_are_rejected_by_the_database_index()
    {
        await InsertRuleAsync("duplicate", 4, true, 10);

        await Assert.ThrowsAsync<SqliteException>(
            () => InsertRuleAsync("duplicate", 4, true, 20));
    }

    public void Dispose()
    {
        _database.Dispose();
    }

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
