using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

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

    private static bool IsReportCodeDuplicate(Exception exception)
    {
        var hasKnownProviderException = false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                hasKnownProviderException = true;
                if (sqlException.Number is 2601 or 2627 &&
                    IsReportCodeConstraint(sqlException.Message))
                {
                    return true;
                }

                continue;
            }

            if (current is SqliteException sqliteException)
            {
                hasKnownProviderException = true;
                if (sqliteException.SqliteErrorCode == 19 &&
                    sqliteException.SqliteExtendedErrorCode is 19 or 1555 or 2067 &&
                    IsReportCodeConstraint(sqliteException.Message))
                {
                    return true;
                }
            }
        }

        if (hasKnownProviderException)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsReportCodeConstraint(current.Message) &&
                (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("duplicate entry", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReportCodeConstraint(string message) =>
        message.Contains("uk_RcPocReportTemplate_ReportCode", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("RcPocReportTemplate.ReportCode", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("ReportCode", StringComparison.OrdinalIgnoreCase);
}
