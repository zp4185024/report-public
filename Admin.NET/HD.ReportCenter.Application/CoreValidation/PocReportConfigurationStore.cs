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
    string DatasetCode,
    IReadOnlyList<PocReportParameter> Parameters,
    int ExpectedRevision,
    string? FieldPresentationsJson = null);

public sealed class PocReportConfigurationRevisionConflictException : Exception
{
    public PocReportConfigurationRevisionConflictException(string reportCode, int expectedRevision)
        : base($"Report configuration '{reportCode}' is no longer at revision {expectedRevision}.")
    {
    }
}

/// <summary>报表自身定义和参数的事务存储层；数据资源由 PocDataAssetStore 独立维护。</summary>
public sealed class PocReportConfigurationStore : ITransient
{
    private readonly ISqlSugarClient _database;

    public PocReportConfigurationStore(ISqlSugarClient database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<PocReport>> ListReportsAsync() =>
        await _database.Queryable<PocReport>()
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    public async Task<bool> ReportExistsAsync(string reportCode) =>
        await _database.Queryable<PocReport>()
            .Where(item => item.Code == reportCode)
            .AnyAsync();

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
            .Where(item => item.Code == report.DatasetCode)
            .FirstAsync()
            ?? throw new InvalidDataException(
                $"Report '{reportCode}' references missing dataset '{report.DatasetCode}'.");
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
                    DatasetCode = mutation.DatasetCode,
                    PreviewSettingsJson = mutation.PreviewSettingsJson,
                    ExportSettingsJson = mutation.ExportSettingsJson,
                    FieldPresentationsJson = mutation.FieldPresentationsJson,
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
                        DatasetCode = mutation.DatasetCode,
                        PreviewSettingsJson = mutation.PreviewSettingsJson,
                        ExportSettingsJson = mutation.ExportSettingsJson,
                        FieldPresentationsJson = mutation.FieldPresentationsJson,
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
