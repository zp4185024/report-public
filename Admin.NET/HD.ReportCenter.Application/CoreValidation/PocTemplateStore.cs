namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocStoredTemplate(
    string ReportCode,
    string SnapshotJson,
    string BindingJson,
    int Revision);

public sealed class PocTemplateRevisionConflictException : Exception
{
    public PocTemplateRevisionConflictException(string reportCode, int expectedRevision)
        : base($"Template '{reportCode}' is no longer at revision {expectedRevision}.")
    {
    }

    public PocTemplateRevisionConflictException(
        string reportCode,
        int expectedRevision,
        Exception innerException)
        : base($"Template '{reportCode}' is no longer at revision {expectedRevision}.", innerException)
    {
    }
}

public sealed class PocTemplateStore : ITransient
{
    private readonly ISqlSugarClient _database;

    public PocTemplateStore(ISqlSugarClient database)
    {
        _database = database;
    }

    public async Task<PocStoredTemplate?> GetAsync(string reportCode)
    {
        var entity = await _database.Queryable<PocReportTemplate>()
            .Where(x => x.ReportCode == reportCode)
            .FirstAsync();

        return entity is null ? null : ToStoredTemplate(entity);
    }

    public async Task<PocStoredTemplate> SaveAsync(
        string reportCode,
        string snapshotJson,
        string bindingJson,
        int expectedRevision)
    {
        if (expectedRevision == 0)
        {
            var entity = new PocReportTemplate
            {
                Id = CreateId(),
                ReportCode = reportCode,
                SnapshotJson = snapshotJson,
                BindingJson = bindingJson,
                Revision = 1,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };

            try
            {
                await _database.Insertable(entity).ExecuteCommandAsync();
            }
            catch (Exception exception) when (IsReportCodeDuplicate(exception))
            {
                throw new PocTemplateRevisionConflictException(reportCode, expectedRevision, exception);
            }

            return ToStoredTemplate(entity);
        }

        var revision = expectedRevision + 1;
        var affected = await _database.Updateable<PocReportTemplate>()
            .SetColumns(x => new PocReportTemplate
            {
                SnapshotJson = snapshotJson,
                BindingJson = bindingJson,
                Revision = revision,
                UpdateTime = DateTime.Now
            })
            .Where(x => x.ReportCode == reportCode && x.Revision == expectedRevision)
            .ExecuteCommandAsync();

        if (affected != 1)
        {
            throw new PocTemplateRevisionConflictException(reportCode, expectedRevision);
        }

        return new PocStoredTemplate(reportCode, snapshotJson, bindingJson, revision);
    }

    private static PocStoredTemplate ToStoredTemplate(PocReportTemplate entity) =>
        new(entity.ReportCode, entity.SnapshotJson, entity.BindingJson, entity.Revision);

    private static long CreateId()
    {
        var id = BitConverter.ToInt64(Guid.NewGuid().ToByteArray()) & long.MaxValue;
        return id == 0 ? 1 : id;
    }

    private static bool IsReportCodeDuplicate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            var isDuplicate =
                message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate entry", StringComparison.OrdinalIgnoreCase);
            if (isDuplicate &&
                (message.Contains("ReportCode", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("uk_RcPocReportTemplate_ReportCode", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
