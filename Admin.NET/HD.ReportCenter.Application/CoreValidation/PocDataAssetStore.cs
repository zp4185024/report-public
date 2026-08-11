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
    public PocDataAssetRevisionConflictException(string assetType, string code, int expectedRevision)
        : base($"{assetType} '{code}' is no longer at revision {expectedRevision}.")
    {
    }
}

/// <summary>全局数据源和数据集的独立存储边界。</summary>
public sealed class PocDataAssetStore : ITransient
{
    private readonly ISqlSugarClient _database;

    public PocDataAssetStore(ISqlSugarClient database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<PocDataSource>> ListDataSourcesAsync() =>
        await _database.Queryable<PocDataSource>()
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    public async Task<PocDataSource?> GetDataSourceAsync(string code) =>
        await _database.Queryable<PocDataSource>()
            .Where(item => item.Code == code)
            .FirstAsync();

    public async Task<IReadOnlyList<PocReportDataset>> ListDatasetsAsync() =>
        await _database.Queryable<PocReportDataset>()
            .OrderByDescending(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    public async Task<PocReportDataset?> GetDatasetAsync(string code) =>
        await _database.Queryable<PocReportDataset>()
            .Where(item => item.Code == code)
            .FirstAsync();

    public async Task<IReadOnlyList<PocReport>> ListDatasetReferencesAsync(string datasetCode) =>
        await _database.Queryable<PocReport>()
            .Where(item => item.DatasetCode == datasetCode)
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    public async Task<int> SaveDataSourceAsync(PocDataSourceMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = DateTime.Now;
        var revision = mutation.ExpectedRevision + 1;
        var existing = await GetDataSourceAsync(mutation.Code);
        if (existing is null)
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

    public async Task<int> SaveDatasetAsync(PocDatasetMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = DateTime.Now;
        var revision = mutation.ExpectedRevision + 1;
        var existing = await GetDatasetAsync(mutation.Code);
        if (existing is null)
        {
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
