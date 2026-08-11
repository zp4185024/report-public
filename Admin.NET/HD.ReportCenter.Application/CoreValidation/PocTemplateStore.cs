using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 存储层返回的模板数据，不携带 ORM 实体的审计字段，避免上层依赖数据库模型。
/// </summary>
public sealed record PocStoredTemplate(
    string ReportCode,
    string SnapshotJson,
    string BindingJson,
    int Revision);

public sealed record PocStoredTemplateSummary(
    string ReportCode,
    int Revision,
    DateTime? UpdatedAt);

/// <summary>
/// 保存时的期望修订号与数据库实际状态不一致。
/// 这是正常的并发冲突，不应重试为无条件覆盖。
/// </summary>
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

/// <summary>
/// 模板数据库访问层。使用 SqlSugar 承接现有 Admin.NET 数据库配置，同时通过条件更新实现乐观并发。
/// </summary>
public sealed class PocTemplateStore : ITransient
{
    private readonly ISqlSugarClient _database;

    public PocTemplateStore(ISqlSugarClient database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<PocStoredTemplateSummary>> ListAsync()
    {
        var templates = await _database.Queryable<PocReportTemplate>()
            .OrderBy(item => item.ReportCode)
            .ToListAsync();
        return templates.Select(item => new PocStoredTemplateSummary(
            item.ReportCode,
            item.Revision,
            item.UpdateTime ?? item.CreateTime)).ToArray();
    }

    /// <summary>按报表编码读取唯一模板；尚未保存过时返回 null，由服务层提供种子模板。</summary>
    public async Task<PocStoredTemplate?> GetAsync(string reportCode)
    {
        var entity = await _database.Queryable<PocReportTemplate>()
            .Where(x => x.ReportCode == reportCode)
            .FirstAsync();

        return entity is null ? null : ToStoredTemplate(entity);
    }

    /// <summary>
    /// 保存模板并返回新的修订号。expectedRevision 为 0 表示首次落库，否则只更新匹配旧修订号的记录。
    /// </summary>
    public async Task<PocStoredTemplate> SaveAsync(
        string reportCode,
        string snapshotJson,
        string bindingJson,
        int expectedRevision)
    {
        if (expectedRevision == 0)
        {
            // 首次保存使用插入。ReportCode 唯一索引负责处理两个 revision 0 页面同时保存的竞争。
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

        // 修订号放进 WHERE 条件，使“检查版本 + 更新内容”成为一个原子数据库操作。
        // affected 不为 1 时，记录已被别人更新或删除，当前页面必须刷新，不能静默覆盖。
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
        // SQL Server 与 SQLite 的唯一键异常编码不同；POC 同时支持生产型数据库和本地验证数据库。
        // 优先识别提供程序的结构化错误码，避免把其它约束或普通 SQL 错误误判为并发冲突。
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

        // 未暴露具体提供程序异常的驱动只能退回消息匹配，并且必须同时命中约束名与重复键关键词。
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
        message.Contains("uk_RcReportTemplate_ReportCode", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("RcReportTemplate.ReportCode", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("ReportCode", StringComparison.OrdinalIgnoreCase);
}
