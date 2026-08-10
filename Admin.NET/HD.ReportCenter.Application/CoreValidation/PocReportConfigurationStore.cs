namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocStoredReportConfiguration(
    PocReport Report,
    PocDataSource DataSource,
    PocReportDataset Dataset,
    IReadOnlyList<PocReportParameter> Parameters);

public sealed record PocReportConfigurationMutation(
    string ReportCode,
    string ReportName,
    string SheetName,
    string PreviewSettingsJson,
    string ExportSettingsJson,
    string DataSourceCode,
    string DataSourceName,
    string DbType,
    string ConnectionString,
    int DefaultTimeoutSeconds,
    int DefaultMaxRows,
    string DatasetCode,
    string DatasetName,
    string QueryText,
    string FieldSchemaJson,
    int DatasetTimeoutSeconds,
    int DatasetMaxRows,
    IReadOnlyList<PocReportParameter> Parameters,
    int ExpectedRevision);

public sealed class PocReportConfigurationRevisionConflictException : Exception
{
    public PocReportConfigurationRevisionConflictException(string reportCode, int expectedRevision)
        : base($"Report configuration '{reportCode}' is no longer at revision {expectedRevision}.")
    {
    }
}

/// <summary>报表、主数据集、数据源和参数的事务存储层。</summary>
public sealed class PocReportConfigurationStore : ITransient
{
    private readonly ISqlSugarClient _database;

    public PocReportConfigurationStore(ISqlSugarClient database)
    {
        _database = database;
    }

    public async Task<PocStoredReportConfiguration?> GetAsync(string reportCode)
    {
        var report = await _database.Queryable<PocReport>()
            .Where(item => item.Code == reportCode)
            .FirstAsync();
        if (report is null)
        {
            return null;
        }

        var dataset = await _database.Queryable<PocReportDataset>()
            .Where(item => item.ReportCode == reportCode && item.IsEnabled)
            .OrderBy(item => item.Id)
            .FirstAsync()
            ?? throw new InvalidDataException($"Report '{reportCode}' has no enabled dataset.");
        var dataSource = await _database.Queryable<PocDataSource>()
            .Where(item => item.Code == dataset.DataSourceCode)
            .FirstAsync()
            ?? throw new InvalidDataException(
                $"Dataset '{dataset.Code}' references missing data source '{dataset.DataSourceCode}'.");
        var parameters = await _database.Queryable<PocReportParameter>()
            .Where(item => item.ReportCode == reportCode)
            .OrderBy(item => item.DisplayOrder)
            .OrderBy(item => item.Id)
            .ToListAsync();

        return new PocStoredReportConfiguration(report, dataSource, dataset, parameters);
    }

    public async Task<int> SaveAsync(PocReportConfigurationMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var tenant = _database.AsTenant();
        await tenant.BeginTranAsync();
        try
        {
            var now = DateTime.Now;
            var existingReport = await _database.Queryable<PocReport>()
                .Where(item => item.Code == mutation.ReportCode)
                .FirstAsync();
            var revision = mutation.ExpectedRevision + 1;
            if (existingReport is null)
            {
                if (mutation.ExpectedRevision != 0)
                {
                    throw new PocReportConfigurationRevisionConflictException(
                        mutation.ReportCode,
                        mutation.ExpectedRevision);
                }

                await _database.Insertable(new PocReport
                {
                    Code = mutation.ReportCode,
                    Name = mutation.ReportName,
                    SheetName = mutation.SheetName,
                    PreviewSettingsJson = mutation.PreviewSettingsJson,
                    ExportSettingsJson = mutation.ExportSettingsJson,
                    Revision = revision,
                    IsEnabled = true,
                    CreateTime = now,
                    UpdateTime = now
                }).ExecuteCommandAsync();
            }
            else
            {
                var affected = await _database.Updateable<PocReport>()
                    .SetColumns(item => new PocReport
                    {
                        Name = mutation.ReportName,
                        SheetName = mutation.SheetName,
                        PreviewSettingsJson = mutation.PreviewSettingsJson,
                        ExportSettingsJson = mutation.ExportSettingsJson,
                        Revision = revision,
                        IsEnabled = true,
                        UpdateTime = now
                    })
                    .Where(item =>
                        item.Code == mutation.ReportCode &&
                        item.Revision == mutation.ExpectedRevision)
                    .ExecuteCommandAsync();
                if (affected != 1)
                {
                    throw new PocReportConfigurationRevisionConflictException(
                        mutation.ReportCode,
                        mutation.ExpectedRevision);
                }
            }

            var existingDataSource = await _database.Queryable<PocDataSource>()
                .Where(item => item.Code == mutation.DataSourceCode)
                .FirstAsync();
            if (existingDataSource is null)
            {
                await _database.Insertable(new PocDataSource
                {
                    Code = mutation.DataSourceCode,
                    Name = mutation.DataSourceName,
                    DbType = mutation.DbType,
                    ConnectionString = mutation.ConnectionString,
                    DefaultTimeoutSeconds = mutation.DefaultTimeoutSeconds,
                    DefaultMaxRows = mutation.DefaultMaxRows,
                    IsEnabled = true,
                    CreateTime = now,
                    UpdateTime = now
                }).ExecuteCommandAsync();
            }
            else
            {
                await _database.Updateable<PocDataSource>()
                    .SetColumns(item => new PocDataSource
                    {
                        Name = mutation.DataSourceName,
                        DbType = mutation.DbType,
                        ConnectionString = mutation.ConnectionString,
                        DefaultTimeoutSeconds = mutation.DefaultTimeoutSeconds,
                        DefaultMaxRows = mutation.DefaultMaxRows,
                        IsEnabled = true,
                        UpdateTime = now
                    })
                    .Where(item => item.Code == mutation.DataSourceCode)
                    .ExecuteCommandAsync();
            }

            var existingDataset = await _database.Queryable<PocReportDataset>()
                .Where(item => item.ReportCode == mutation.ReportCode)
                .OrderBy(item => item.Id)
                .FirstAsync();
            if (existingDataset is null)
            {
                await _database.Insertable(new PocReportDataset
                {
                    ReportCode = mutation.ReportCode,
                    DataSourceCode = mutation.DataSourceCode,
                    Code = mutation.DatasetCode,
                    Name = mutation.DatasetName,
                    QueryText = mutation.QueryText,
                    FieldSchemaJson = mutation.FieldSchemaJson,
                    TimeoutSeconds = mutation.DatasetTimeoutSeconds,
                    MaxRows = mutation.DatasetMaxRows,
                    IsEnabled = true,
                    CreateTime = now,
                    UpdateTime = now
                }).ExecuteCommandAsync();
            }
            else
            {
                await _database.Updateable<PocReportDataset>()
                    .SetColumns(item => new PocReportDataset
                    {
                        DataSourceCode = mutation.DataSourceCode,
                        Code = mutation.DatasetCode,
                        Name = mutation.DatasetName,
                        QueryText = mutation.QueryText,
                        FieldSchemaJson = mutation.FieldSchemaJson,
                        TimeoutSeconds = mutation.DatasetTimeoutSeconds,
                        MaxRows = mutation.DatasetMaxRows,
                        IsEnabled = true,
                        UpdateTime = now
                    })
                    .Where(item => item.Id == existingDataset.Id)
                    .ExecuteCommandAsync();
            }

            await _database.Deleteable<PocReportParameter>()
                .Where(item => item.ReportCode == mutation.ReportCode)
                .ExecuteCommandAsync();
            if (mutation.Parameters.Count > 0)
            {
                foreach (var parameter in mutation.Parameters)
                {
                    parameter.Id = 0;
                    parameter.ReportCode = mutation.ReportCode;
                    parameter.CreateTime = now;
                    parameter.UpdateTime = now;
                }
                await _database.Insertable(mutation.Parameters.ToList()).ExecuteCommandAsync();
            }

            await tenant.CommitTranAsync();
            return revision;
        }
        catch
        {
            await tenant.RollbackTranAsync();
            throw;
        }
    }
}
