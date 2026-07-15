using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>逐条映射规则的数据库访问层，负责首次种子和当前映射版本选择。</summary>
public sealed class PocExchangeMappingStore : ITransient
{
    private readonly ISqlSugarClient _database;

    public PocExchangeMappingStore(ISqlSugarClient database)
    {
        _database = database;
    }

    public async Task<PocExchangeRuleSet> GetCurrentAsync(string univerVersion)
    {
        if (string.IsNullOrWhiteSpace(univerVersion))
        {
            throw new ArgumentException("Univer版本不能为空。", nameof(univerVersion));
        }

        var mappingVersion = await FindLatestVersionAsync(univerVersion);
        if (mappingVersion == 0 &&
            string.Equals(univerVersion, PocExchangeMappingDefaults.CurrentUniverVersion, StringComparison.Ordinal))
        {
            await SeedDefaultsAsync();
            mappingVersion = await FindLatestVersionAsync(univerVersion);
        }

        if (mappingVersion == 0)
        {
            throw new InvalidOperationException($"没有找到Univer {univerVersion}的Excel转换映射。");
        }

        var entities = await _database.Queryable<PocExchangeMapping>()
            .Where(rule =>
                rule.UniverVersion == univerVersion &&
                rule.MappingVersion == mappingVersion &&
                rule.IsEnabled)
            .OrderBy(rule => rule.Sort)
            .OrderBy(rule => rule.RuleCode)
            .ToListAsync();

        Validate(entities, univerVersion, mappingVersion);
        return new PocExchangeRuleSet(
            univerVersion,
            mappingVersion,
            entities.Select(ToRule).ToArray());
    }

    private async Task<int> FindLatestVersionAsync(string univerVersion)
    {
        var entity = await _database.Queryable<PocExchangeMapping>()
            .Where(rule => rule.UniverVersion == univerVersion)
            .OrderBy(rule => rule.MappingVersion, OrderByType.Desc)
            .FirstAsync();
        return entity?.MappingVersion ?? 0;
    }

    private async Task SeedDefaultsAsync()
    {
        // 先检查再插入可避免正常重复读取产生写操作；唯一索引仍负责阻止并发重复种子。
        var exists = await _database.Queryable<PocExchangeMapping>()
            .AnyAsync(rule => rule.UniverVersion == PocExchangeMappingDefaults.CurrentUniverVersion);
        if (!exists)
        {
            await _database.Insertable(PocExchangeMappingDefaults.Create().ToList()).ExecuteCommandAsync();
        }
    }

    private static void Validate(
        IReadOnlyCollection<PocExchangeMapping> rules,
        string univerVersion,
        int mappingVersion)
    {
        if (rules.Count == 0)
        {
            throw new InvalidOperationException(
                $"Univer {univerVersion} 映射版本 {mappingVersion} 没有启用规则。");
        }

        var duplicate = rules
            .GroupBy(rule => rule.RuleCode, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Excel转换映射规则编码重复：{duplicate.Key}。");
        }

        var invalid = rules.FirstOrDefault(rule =>
            string.IsNullOrWhiteSpace(rule.RuleCode) ||
            string.IsNullOrWhiteSpace(rule.Direction) ||
            string.IsNullOrWhiteSpace(rule.SourcePath) ||
            string.IsNullOrWhiteSpace(rule.TargetPath) ||
            string.IsNullOrWhiteSpace(rule.TransformType));
        if (invalid is not null)
        {
            throw new InvalidOperationException($"Excel转换映射包含无效规则：{invalid.RuleCode}。");
        }
    }

    private static PocExchangeRule ToRule(PocExchangeMapping rule) => new(
        rule.RuleCode,
        rule.Direction,
        rule.Category,
        rule.SourcePath,
        rule.TargetPath,
        rule.TransformType,
        rule.SourceValue,
        rule.TargetValue,
        rule.Factor,
        rule.IsRequired,
        rule.Sort);
}
