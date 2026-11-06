using HD.ReportCenter.Application.CoreValidation;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>AI 单行配置的独立存储边界；表不存在行时按默认关闭配置返回，保证首次使用零迁移成本。</summary>
public sealed class PocAiSettingsStore : ITransient
{
    /// <summary>单行配置固定使用的主键值；脚本播种与应用保存共用同一行，避免多行配置歧义。</summary>
    public const long SingleRowId = 1;

    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocAiSettingsStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="database">报表中心主库的 SqlSugar 客户端。</param>
    public PocAiSettingsStore(ISqlSugarClient database)
    {
        _database = database;
    }

    /// <summary>读取单行 AI 配置；行缺失时返回 IsEnabled=false 的默认配置，不隐式建行。</summary>
    /// <returns>当前配置实体或默认关闭配置。</returns>
    public async Task<PocAiSettings> GetAsync()
    {
        var settings = await _database.Queryable<PocAiSettings>()
            .Where(item => item.Id == SingleRowId)
            .FirstAsync();
        return settings ?? new PocAiSettings
        {
            Id = SingleRowId,
            IsEnabled = false,
            BaseUrl = string.Empty,
            ApiKey = null,
            Model = string.Empty,
            VisionModel = string.Empty,
            SampleRowLimit = 20,
            SqlProbeEnabled = true,
            SqlProbeRowLimit = 100,
            MaxToolRounds = 8
        };
    }

    /// <summary>原子保存单行配置；行缺失时按修订号 0 首插，存在时整行覆盖。单行配置不设乐观并发，后写覆盖先写。</summary>
    /// <param name="settings">已通过服务层校验的配置实体。</param>
    /// <returns>保存完成后的配置实体。</returns>
    public async Task<PocAiSettings> SaveAsync(PocAiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Id = SingleRowId;
        var now = DateTime.Now;
        settings.UpdateTime = now;
        var existing = await _database.Queryable<PocAiSettings>()
            .Where(item => item.Id == SingleRowId)
            .AnyAsync();
        if (!existing)
        {
            settings.CreateTime = now;
            await _database.Insertable(settings).ExecuteCommandAsync();
            return settings;
        }

        await _database.Updateable<PocAiSettings>()
            .SetColumns(item => new PocAiSettings
            {
                IsEnabled = settings.IsEnabled,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                Model = settings.Model,
                VisionModel = settings.VisionModel,
                SampleRowLimit = settings.SampleRowLimit,
                SqlProbeEnabled = settings.SqlProbeEnabled,
                SqlProbeRowLimit = settings.SqlProbeRowLimit,
                MaxToolRounds = settings.MaxToolRounds,
                UpdateTime = now
            })
            .Where(item => item.Id == SingleRowId)
            .ExecuteCommandAsync();
        return await GetAsync();
    }
}
