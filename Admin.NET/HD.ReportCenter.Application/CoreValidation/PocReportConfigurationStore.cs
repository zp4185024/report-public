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

public sealed record PocStoredPage<T>(
    IReadOnlyList<T> Items,
    int Total);

public sealed class PocReportConfigurationRevisionConflictException : Exception
{
    /// <summary>初始化 PocReportConfigurationRevisionConflictException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <param name="expectedRevision">expectedRevision 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocReportConfigurationRevisionConflictException(string reportCode, int expectedRevision)
        : base($"Report configuration '{reportCode}' is no longer at revision {expectedRevision}.")
    {
    }
}

/// <summary>报表自身定义和参数的事务存储层；数据资源由 PocDataAssetStore 独立维护。</summary>
public sealed class PocReportConfigurationStore : ITransient
{
    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocReportConfigurationStore 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="database">database 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocReportConfigurationStore(ISqlSugarClient database)
    {
        _database = database;
    }

    /// <summary>从 PocReportConfigurationStore 查询 List Reports Async 所需数据，处理空结果并按调用约定返回。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<IReadOnlyList<PocReport>> ListReportsAsync() =>
        await _database.Queryable<PocReport>()
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .ToListAsync();

    /// <summary>按名称、编码、工作表或数据集筛选报表，并仅读取管理列表当前页。</summary>
    /// <param name="page">从一开始的页码。</param>
    /// <param name="pageSize">当前页最多返回的记录数。</param>
    /// <param name="keyword">可选的模糊搜索关键字。</param>
    /// <returns>返回当前页报表实体和筛选后的总记录数。</returns>
    public async Task<PocStoredPage<PocReport>> ListReportsPageAsync(
        int page,
        int pageSize,
        string? keyword)
    {
        var query = _database.Queryable<PocReport>();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var normalizedKeyword = keyword.Trim();
            query = query.Where(item =>
                item.Name.Contains(normalizedKeyword) ||
                item.Code.Contains(normalizedKeyword) ||
                item.SheetName.Contains(normalizedKeyword) ||
                item.DatasetCode.Contains(normalizedKeyword));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(item => item.Name)
            .OrderBy(item => item.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PocStoredPage<PocReport>(items, total);
    }

    /// <summary>负责 PocReportConfigurationStore 的 Report Exists Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<bool> ReportExistsAsync(string reportCode) =>
        await _database.Queryable<PocReport>()
            .Where(item => item.Code == reportCode)
            .AnyAsync();

    /// <summary>查询或读取 PocReportConfigurationStore 所需的 Get Async 数据，并按调用约定返回结果。</summary>
    /// <param name="reportCode">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>更新 PocReportConfigurationStore 的 Save Async 状态，并同步相关业务数据。</summary>
    /// <param name="mutation">mutation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public async Task<int> SaveAsync(PocReportConfigurationMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var tenant = _database.AsTenant();
        await tenant.BeginTranAsync();
        try
        {
            var now = DateTime.Now;
            // 第一阶段：遍历 SaveAsync 当前数据集合，逐项转换并维护处理结果。
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
            // 第2阶段：遍历 SaveAsync 当前数据集合，逐项转换并维护处理结果。
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
            // 第3阶段：检查 SaveAsync 当前条件，并按匹配分支执行对应处理。
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

    /// <summary>
    /// 在单个事务里删除报表及其参数和模板快照，三张表要么一起消失要么保持原状；
    /// 报表行本身删除未命中时抛 KeyNotFoundException，表示编码在删除瞬间已不存在。
    /// </summary>
    /// <param name="reportCode">待删除报表的编码，调用前应已完成存在性检查。</param>
    /// <returns>异步任务，失败时事务回滚并向上抛出原始异常。</returns>
    public async Task DeleteAsync(string reportCode)
    {
        var tenant = _database.AsTenant();
        await tenant.BeginTranAsync();
        try
        {
            // 模板与参数是报表的从属数据，先删从属再删主记录，全程在同一事务内。
            await _database.Deleteable<PocReportTemplate>()
                .Where(item => item.ReportCode == reportCode)
                .ExecuteCommandAsync();
            await _database.Deleteable<PocReportParameter>()
                .Where(item => item.ReportCode == reportCode)
                .ExecuteCommandAsync();
            var affected = await _database.Deleteable<PocReport>()
                .Where(item => item.Code == reportCode)
                .ExecuteCommandAsync();
            if (affected != 1)
            {
                throw new KeyNotFoundException($"不存在报表 '{reportCode}'。");
            }

            await tenant.CommitTranAsync();
        }
        catch
        {
            await tenant.RollbackTranAsync();
            throw;
        }
    }
}
