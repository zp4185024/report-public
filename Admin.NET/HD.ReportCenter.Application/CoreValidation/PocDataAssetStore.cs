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
