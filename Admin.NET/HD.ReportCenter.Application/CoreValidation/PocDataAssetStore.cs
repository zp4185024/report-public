namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocDataSourceMutation(
    string Code,
    string Name,
    string DbType,
    string ConnectionString,
    int DefaultTimeoutSeconds,
    int DefaultMaxRows,
    bool IsEnabled,
    int ExpectedRevision);

public sealed record PocDatasetMutation(
    string Code,
    string Name,
    string DataSourceCode,
    string QueryText,
    string FieldSchemaJson,
    int TimeoutSeconds,
    int MaxRows,
    bool IsEnabled,
    int ExpectedRevision);

public sealed class PocDataAssetRevisionConflictException : Exception
{
    /// <summary>初始化 PocDataAssetRevisionConflictException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="assetType">assetType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="expectedRevision">expectedRevision 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocDataAssetRevisionConflictException(string assetType, string code, int expectedRevision)
        : base($"{assetType} '{code}' is no longer at revision {expectedRevision}.")
    {
    }
}

/// <summary>
/// 目标资源仍被下游配置引用，删除请求被拒绝。API 层把它映射为 409 并回传引用清单，避免静默级联删除。
/// </summary>
public sealed class PocResourceInUseException : Exception
{
    /// <summary>初始化 PocResourceInUseException 异常实例，携带业务错误码和面向管理员的引用说明。</summary>
    /// <param name="code">供前端识别冲突类型的稳定业务错误码，如 DATASET_IN_USE。</param>
    /// <param name="message">说明谁在引用该资源以及下一步该怎么做的中文提示。</param>
    public PocResourceInUseException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>供前端识别冲突类型的稳定业务错误码。</summary>
    public string Code { get; }
}

/// <summary>删除校验共用的小工具，把引用名称列表压缩成适合错误提示展示的短句。</summary>
internal static class PocDeleteGuard
{
    /// <summary>
    /// 把引用方名称列表截断为最多 5 个的展示串；超出部分汇总为总数，避免长列表撑爆错误提示。
    /// </summary>
    /// <param name="names">引用方显示名称，顺序保持稳定以便管理员定位。</param>
    /// <returns>可直接拼进异常消息的引用摘要文本。</returns>
    public static string FormatReferenceSummary(IReadOnlyList<string> names)
    {
        const int displayLimit = 5;
        var display = string.Join("、", names.Take(displayLimit));
        return names.Count > displayLimit
            ? $"{display} 等共 {names.Count} 个"
            : display;
    }
}

/// <summary>全局数据源和数据集的独立存储边界。</summary>
public sealed class PocDataAssetStore : ITransient
{
    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocDataAssetStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="database">database 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocDataAssetStore(ISqlSugarClient database)
    {
        _database = database;
    }

    /// <summary>从 PocDataAssetStore 查询 List Data Sources Async 所需数据，处理空结果并按调用约定返回。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocDataSource>> ListDataSourcesAsync() =>
        await _database.Queryable<PocDataSource>()
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    /// <summary>查询或读取 PocDataAssetStore 所需的 Get Data Source Async 数据，并按调用约定返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocDataSource?> GetDataSourceAsync(string code) =>
        await _database.Queryable<PocDataSource>()
            .Where(item => item.Code == code)
            .FirstAsync();

    /// <summary>从 PocDataAssetStore 查询 List Datasets Async 所需数据，处理空结果并按调用约定返回。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocReportDataset>> ListDatasetsAsync() =>
        await _database.Queryable<PocReportDataset>()
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    /// <summary>按名称、编码或数据源筛选数据集，并仅读取资源列表当前页。</summary>
    /// <param name="page">从一开始的页码。</param>
    /// <param name="pageSize">当前页最多返回的记录数。</param>
    /// <param name="keyword">可选的模糊搜索关键字。</param>
    /// <returns>返回当前页数据集实体和筛选后的总记录数。</returns>
    public async Task<PocStoredPage<PocReportDataset>> ListDatasetsPageAsync(
        int page,
        int pageSize,
        string? keyword)
    {
        var query = _database.Queryable<PocReportDataset>();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(item =>
                item.Name.Contains(normalizedKeyword) ||
                item.Code.Contains(normalizedKeyword) ||
                item.DataSourceCode.Contains(normalizedKeyword));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PocStoredPage<PocReportDataset>(items, total);
    }

    /// <summary>查询或读取 PocDataAssetStore 所需的 Get Dataset Async 数据，并按调用约定返回结果。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<PocReportDataset?> GetDatasetAsync(string code) =>
        await _database.Queryable<PocReportDataset>()
            .Where(item => item.Code == code)
            .FirstAsync();

    /// <summary>从 PocDataAssetStore 查询 List Dataset References Async 所需数据，处理空结果并按调用约定返回。</summary>
    /// <param name="datasetCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocReport>> ListDatasetReferencesAsync(string datasetCode) =>
        await _database.Queryable<PocReport>()
            .Where(item => item.DatasetCode == datasetCode)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    /// <summary>列出仍挂在该数据源下的数据集，供删除数据源前的引用校验使用。</summary>
    /// <param name="dataSourceCode">待删除数据源的编码。</param>
    /// <returns>引用该数据源的数据集实体列表，空列表表示可以安全删除。</returns>
    public async Task<IReadOnlyList<PocReportDataset>> ListDatasetsByDataSourceAsync(
        string dataSourceCode) =>
        await _database.Queryable<PocReportDataset>()
            .Where(item => item.DataSourceCode == dataSourceCode)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    /// <summary>按编码物理删除数据源；调用方必须先完成引用校验，本方法不做级联清理。</summary>
    /// <param name="code">待删除数据源的编码。</param>
    /// <returns>是否恰好删除了一行；false 表示编码在删除瞬间已不存在。</returns>
    public async Task<bool> DeleteDataSourceAsync(string code) =>
        await _database.Deleteable<PocDataSource>()
            .Where(item => item.Code == code)
            .ExecuteCommandAsync() == 1;

    /// <summary>按编码物理删除数据集；调用方必须先确认没有报表引用，避免报表运行时找不到数据。</summary>
    /// <param name="code">待删除数据集的编码。</param>
    /// <returns>是否恰好删除了一行；false 表示编码在删除瞬间已不存在。</returns>
    public async Task<bool> DeleteDatasetAsync(string code) =>
        await _database.Deleteable<PocReportDataset>()
            .Where(item => item.Code == code)
            .ExecuteCommandAsync() == 1;

    /// <summary>更新 PocDataAssetStore 的 Save Data Source Async 状态，并同步相关业务数据。</summary>
    /// <param name="mutation">mutation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<int> SaveDataSourceAsync(PocDataSourceMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = DateTime.Now;
        var revision = mutation.ExpectedRevision + 1;
        var existing = await GetDataSourceAsync(mutation.Code);
        if (existing is null)
        // 第一阶段：调用 SaveDataSourceAsync 依赖的异步能力，等待并校验返回数据。
        {
            if (mutation.ExpectedRevision != 0)
            {
                throw new PocDataAssetRevisionConflictException(
                    "Data source",
                    mutation.Code,
                    mutation.ExpectedRevision);
            }
            await _database.Insertable(new PocDataSource
            {
                Code = mutation.Code,
                Name = mutation.Name,
                DbType = mutation.DbType,
                ConnectionString = mutation.ConnectionString,
                DefaultTimeoutSeconds = mutation.DefaultTimeoutSeconds,
                DefaultMaxRows = mutation.DefaultMaxRows,
                Revision = revision,
                IsEnabled = mutation.IsEnabled,
                CreateTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();
            return revision;
        }

        // 第2阶段：遍历 SaveDataSourceAsync 当前数据集合，逐项转换并维护处理结果。
        var affected = await _database.Updateable<PocDataSource>()
            .SetColumns(item => new PocDataSource
            {
                Name = mutation.Name,
                DbType = mutation.DbType,
                ConnectionString = mutation.ConnectionString,
                DefaultTimeoutSeconds = mutation.DefaultTimeoutSeconds,
                DefaultMaxRows = mutation.DefaultMaxRows,
                Revision = revision,
                IsEnabled = mutation.IsEnabled,
                UpdateTime = now
            })
            .Where(item => item.Code == mutation.Code && item.Revision == mutation.ExpectedRevision)
            .ExecuteCommandAsync();
        if (affected != 1)
        {
            throw new PocDataAssetRevisionConflictException(
                "Data source",
                mutation.Code,
                mutation.ExpectedRevision);
        }
        return revision;
    }

    /// <summary>更新 PocDataAssetStore 的 Save Dataset Async 状态，并同步相关业务数据。</summary>
    /// <param name="mutation">mutation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<int> SaveDatasetAsync(PocDatasetMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = DateTime.Now;
        var revision = mutation.ExpectedRevision + 1;
        var existing = await GetDatasetAsync(mutation.Code);
        if (existing is null)
        {
            // 第一阶段：检查 SaveDatasetAsync 当前条件，并按匹配分支执行对应处理。
            if (mutation.ExpectedRevision != 0)
            {
                throw new PocDataAssetRevisionConflictException(
                    "Dataset",
                    mutation.Code,
                    mutation.ExpectedRevision);
            }
            await _database.Insertable(new PocReportDataset
            {
                Code = mutation.Code,
                Name = mutation.Name,
                DataSourceCode = mutation.DataSourceCode,
                QueryText = mutation.QueryText,
                FieldSchemaJson = mutation.FieldSchemaJson,
                TimeoutSeconds = mutation.TimeoutSeconds,
                MaxRows = mutation.MaxRows,
                Revision = revision,
                IsEnabled = mutation.IsEnabled,
                CreateTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();
            return revision;
        }

        // 第2阶段：遍历 SaveDatasetAsync 当前数据集合，逐项转换并维护处理结果。
        var affected = await _database.Updateable<PocReportDataset>()
            .SetColumns(item => new PocReportDataset
            {
                Name = mutation.Name,
                DataSourceCode = mutation.DataSourceCode,
                QueryText = mutation.QueryText,
                FieldSchemaJson = mutation.FieldSchemaJson,
                TimeoutSeconds = mutation.TimeoutSeconds,
                MaxRows = mutation.MaxRows,
                Revision = revision,
                IsEnabled = mutation.IsEnabled,
                UpdateTime = now
            })
            .Where(item => item.Code == mutation.Code && item.Revision == mutation.ExpectedRevision)
            .ExecuteCommandAsync();
        if (affected != 1)
        {
            throw new PocDataAssetRevisionConflictException(
                "Dataset",
                mutation.Code,
                mutation.ExpectedRevision);
        }
        return revision;
    }
}
