using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>逐条映射规则的数据库访问层，负责首次种子和当前映射版本选择。</summary>
public sealed class PocExchangeMappingStore : ITransient
{
    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocExchangeMappingStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="database">database 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocExchangeMappingStore(ISqlSugarClient database)
    {
        _database = database;
    }

    /// <summary>查询或读取 PocExchangeMappingStore 所需的 Get Current Async 数据，并按调用约定返回结果。</summary>
    /// <param name="univerVersion">univerVersion 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>查询或读取 PocExchangeMappingStore 所需的 Find Latest Version Async 数据，并按调用约定返回结果。</summary>
    /// <param name="univerVersion">univerVersion 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<int> FindLatestVersionAsync(string univerVersion)
    {
        var entity = await _database.Queryable<PocExchangeMapping>()
            .Where(rule => rule.UniverVersion == univerVersion)
            .OrderBy(rule => rule.MappingVersion, OrderByType.Desc)
            .FirstAsync();
        return entity?.MappingVersion ?? 0;
    }

    /// <summary>调用 PocExchangeMappingStore 的异步依赖完成 Seed Defaults Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>校验 PocExchangeMappingStore 的 Validate 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="rules">rules 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="univerVersion">univerVersion 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="mappingVersion">mappingVersion 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>将 PocExchangeMappingStore 的 To Rule 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="rule">rule 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
