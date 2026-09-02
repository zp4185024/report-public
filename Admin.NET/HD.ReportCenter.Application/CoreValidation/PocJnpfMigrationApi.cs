using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Furion.FriendlyException;
using Microsoft.Extensions.Configuration;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed record PocJnpfMigrationInventoryRequest(
    string? JnpfDataSourceCode = null,
    string? ReportDataSourceCode = null,
    int MaxReports = 3);

public sealed record PocJnpfMigrationExecuteRequest(
    string? JnpfDataSourceCode = null,
    string? ReportDataSourceCode = null,
    string[]? SourceReportIds = null,
    int MaxReports = 3);

public sealed record PocJnpfMigrationInventoryItem(
    string? SourceReportId,
    string? SourceVersionId,
    string? ReportName,
    string? SourceReportCode,
    string? TargetReportCode,
    string? DataSourceNames,
    string? DbTypes,
    bool Migratable,
    string? Status,
    int FieldCount,
    int ParameterCount,
    int DetailFieldCount,
    string? Blockers,
    string? Markers);

public sealed record PocJnpfMigrationResultItem(
    string? ReportCode,
    string? ReportName,
    string? Status,
    string? Message);

public sealed record PocJnpfMigrationRunResult(
    IReadOnlyList<PocJnpfMigrationInventoryItem> Inventory,
    IReadOnlyList<PocJnpfMigrationResultItem> Results,
    string ReportDataSourceCode,
    string ReportDataSourceName,
    string ReportDataSourceDbType,
    string WriteTargetDatabase,
    string WriteTargetDbType);

/// <summary>
/// 盘点、转换和写入全部在当前 ASP.NET 进程内完成，不启动外部命令，
/// 也不通过环境变量传递数据库凭据。
/// </summary>
public sealed class PocJnpfMigrationRunner : ITransient
{
    private readonly PocDataAssetStore _dataAssets;
    private readonly ISqlSugarClient _database;

    /// <summary>初始化 PocJnpfMigrationRunner 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="dataAssets">dataAssets 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="database">database 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocJnpfMigrationRunner(PocDataAssetStore dataAssets, ISqlSugarClient database)
    {
        _dataAssets = dataAssets;
        _database = database;
    }

    /// <summary>负责 PocJnpfMigrationRunner 的 Inventory Async 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<PocJnpfMigrationRunResult> InventoryAsync(
        PocJnpfMigrationInventoryRequest request,
        CancellationToken cancellationToken) => RunAsync(
            request?.JnpfDataSourceCode,
            request?.ReportDataSourceCode,
            [],
            request?.MaxReports ?? 3,
            inventoryOnly: true,
            cancellationToken);

    /// <summary>执行 PocJnpfMigrationApi 的 Execute Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public Task<PocJnpfMigrationRunResult> ExecuteAsync(
        PocJnpfMigrationExecuteRequest request,
        CancellationToken cancellationToken) => RunAsync(
            request?.JnpfDataSourceCode,
            request?.ReportDataSourceCode,
            request?.SourceReportIds,
            request?.MaxReports ?? 3,
            inventoryOnly: false,
            cancellationToken);

    /// <summary>执行 PocJnpfMigrationApi 的 Run Async 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="jnpfDataSourceCode">需要读取、校验或转换的业务值。</param>
    /// <param name="reportDataSourceCode">需要读取、校验或转换的业务值。</param>
    /// <param name="reportIds">reportIds 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="maxReports">maxReports 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="inventoryOnly">inventoryOnly 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private async Task<PocJnpfMigrationRunResult> RunAsync(
        string? jnpfDataSourceCode,
        string? reportDataSourceCode,
        string[]? reportIds,
        int maxReports,
        bool inventoryOnly,
        CancellationToken cancellationToken)
    {
        var normalizedReportIds = ValidateValues(reportIds);
        var normalizedJnpfDataSourceCode = ValidateDataSourceCode(jnpfDataSourceCode, "请选择 JNPF 来源数据源。");
        var normalizedReportDataSourceCode = ValidateDataSourceCode(reportDataSourceCode, "请选择报表运行数据源。");
        if (maxReports is < 1 or > 50) throw Oops.Oh("批量数量必须在 1 到 50 之间。").StatusCode(400);

        var sourceEndpoint = await ResolveDataSourceAsync(
            normalizedJnpfDataSourceCode,
            PocDatabaseProvider.MySql,
            "JNPF 来源");
        // 第一阶段：调用 RunAsync 依赖的异步能力，等待并校验返回数据。
        var reportDataSource = await _dataAssets.GetDataSourceAsync(normalizedReportDataSourceCode)
            ?? throw Oops.Oh($"报表运行数据源 '{normalizedReportDataSourceCode}' 不存在。").StatusCode(400);
        _ = await ResolveDataSourceAsync(normalizedReportDataSourceCode, expectedType: null, "报表运行");
        var writeTarget = ResolveCurrentTargetEndpoint();

        IReadOnlyList<JnpfSourcePackage> packages;
        try
        {
            packages = await PocJnpfMigrationSourceReader.ReadAsync(sourceEndpoint, cancellationToken);
        }
        catch (Exception exception) when (PocDatabaseProvider.IsDatabaseException(exception))
        {
            throw new InvalidOperationException(
                $"读取 JNPF 报表配置失败：{PocDatabaseProvider.GetSafeConnectionError(exception)}",
                exception);
        }

        var reportDbType = PocDatabaseProvider.NormalizeType(reportDataSource.DbType);
        var candidates = packages
            .Select(package => PocJnpfMigrationConverter.CreateCandidate(
                package,
                reportDataSource.Code,
                reportDbType))
            .ToArray();
        var inventory = candidates.Select(PocJnpfMigrationConverter.ToInventory).ToArray();
        var results = new List<PocJnpfMigrationResultItem>();

        if (!inventoryOnly)
        {
            EnsureTargetSchema();
            var selectedIds = normalizedReportIds.ToHashSet(StringComparer.Ordinal);
            var selected = candidates
                .Where(candidate => candidate.Migratable && candidate.Migration is not null)
                .Where(candidate => selectedIds.Count == 0 || selectedIds.Contains(candidate.Package.ReportId))
                .OrderBy(candidate => candidate.Migration!.FieldCount)
                .ThenBy(candidate => candidate.Migration!.ParameterCount)
                .ThenBy(candidate => candidate.Package.ReportName, StringComparer.Ordinal)
                .Take(maxReports)
                .ToArray();
            // 第2阶段：遍历 RunAsync 当前数据集合，逐项转换并维护处理结果。
            foreach (var candidate in selected)
            {
                try
                {
                    results.Add(await WriteMigrationAsync(candidate.Migration!, cancellationToken));
                }
                catch (Exception exception)
                {
                    results.Add(new PocJnpfMigrationResultItem(
                        candidate.Migration!.ReportCode,
                        candidate.Migration.ReportName,
                        "Failed",
                        SafeMigrationMessage(exception)));
                }
            }
        }

        return new PocJnpfMigrationRunResult(
            inventory,
            results,
            reportDataSource.Code,
            reportDataSource.Name,
            reportDbType,
            writeTarget.Database,
            PocDatabaseProvider.SqlServer);
    }

    /// <summary>更新 PocJnpfMigrationApi 的 Write Migration Async 状态，并同步相关业务数据。</summary>
    /// <param name="migration">migration 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal async Task<PocJnpfMigrationResultItem> WriteMigrationAsync(
        JnpfMigration migration,
        CancellationToken cancellationToken = default)
    {
        var tenant = _database.AsTenant();
        await tenant.BeginTranAsync();
        try
        {
            if (await _database.Queryable<PocReport>().Where(item => item.Code == migration.ReportCode).AnyAsync())
            {
                await tenant.RollbackTranAsync();
                // 第一阶段：整理 WriteMigrationAsync 已计算的数据，并按方法契约返回最终结果。
                return new PocJnpfMigrationResultItem(
                    migration.ReportCode,
                    migration.ReportName,
                    "Skipped",
                    "目标报表已存在，未覆盖。");
            }
            var datasetExists = await _database.Queryable<PocReportDataset>()
                .Where(item => item.Code == migration.DatasetCode)
                .AnyAsync();
            var templateExists = await _database.Queryable<PocReportTemplate>()
                .Where(item => item.ReportCode == migration.ReportCode)
                .AnyAsync();
            if (datasetExists || templateExists)
            {
                throw new InvalidOperationException("目标存在孤立的同编码数据集或模板，拒绝自动处理。");
            }

            var dataSource = await _database.Queryable<PocDataSource>()
                .Where(item => item.Code == migration.DataSourceCode)
                .FirstAsync();
            if (dataSource is null)
            {
                throw new InvalidOperationException($"指定复用的目标数据源 '{migration.DataSourceCode}' 不存在。");
            }
            if (!PocDatabaseProvider.NormalizeType(dataSource.DbType).Equals(
                    migration.DataSourceDbType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"目标数据源 '{migration.DataSourceCode}' 类型不是 {migration.DataSourceDbType}。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTime.Now;
            // 第2阶段：调用 WriteMigrationAsync 依赖的异步能力，等待并校验返回数据。
            await _database.Insertable(new PocReportDataset
            {
                Id = StableId("dataset", migration.SourceVersionId),
                DataSourceCode = migration.DataSourceCode,
                Code = migration.DatasetCode,
                Name = migration.DatasetName,
                QueryText = migration.QueryText,
                FieldSchemaJson = migration.FieldSchemaJson,
                TimeoutSeconds = 60,
                MaxRows = 20_000,
                Revision = 1,
                IsEnabled = true,
                CreateTime = now,
                UpdateTime = now,
                CreateUserId = 0,
                CreateUserName = string.Empty,
                UpdateUserId = 0,
                UpdateUserName = string.Empty
            }).ExecuteCommandAsync();
            await _database.Insertable(new PocReport
            {
                Id = StableId("report", migration.SourceReportId),
                Code = migration.ReportCode,
                Name = migration.ReportName,
                SheetName = migration.SheetName,
                DatasetCode = migration.DatasetCode,
                PreviewSettingsJson = migration.PreviewSettingsJson,
                ExportSettingsJson = migration.ExportSettingsJson,
                FieldPresentationsJson = migration.FieldPresentationsJson,
                Revision = 1,
                IsEnabled = true,
                CreateTime = now,
                UpdateTime = now,
                CreateUserId = 0,
                CreateUserName = string.Empty,
                UpdateUserId = 0,
                UpdateUserName = string.Empty
            }).ExecuteCommandAsync();
            if (migration.Parameters.Count > 0)
            {
                // 第3阶段：遍历 WriteMigrationAsync 当前数据集合，逐项转换并维护处理结果。
                var parameters = migration.Parameters.Select(parameter => new PocReportParameter
                {
                    Id = StableId("parameter", $"{migration.SourceReportId}:{parameter.Code}"),
                    ReportCode = migration.ReportCode,
                    Code = parameter.Code,
                    DisplayName = parameter.DisplayName,
                    DataType = parameter.DataType,
                    ControlType = parameter.ControlType,
                    SourceType = parameter.SourceType,
                    SourceCode = parameter.SourceCode,
                    SourceFieldCode = parameter.SourceFieldCode,
                    UrlKey = parameter.UrlKey,
                    DefaultExpression = parameter.DefaultExpression,
                    FixedValue = parameter.FixedValue,
                    OptionsJson = parameter.OptionsJson,
                    IsVisible = parameter.IsVisible,
                    IsReadOnly = parameter.IsReadOnly,
                    IsRequired = parameter.IsRequired,
                    AllowUrlPrefill = parameter.AllowUrlPrefill,
                    DisplayOrder = parameter.DisplayOrder,
                    CreateTime = now,
                    UpdateTime = now,
                    CreateUserId = 0,
                    CreateUserName = string.Empty,
                    UpdateUserId = 0,
                    UpdateUserName = string.Empty
                }).ToArray();
                await _database.Insertable(parameters).ExecuteCommandAsync();
            }
            await _database.Insertable(new PocReportTemplate
            {
                Id = StableId("template", migration.SourceVersionId),
                ReportCode = migration.ReportCode,
                SnapshotJson = migration.SnapshotJson,
                BindingJson = migration.BindingJson,
                Revision = migration.TemplateRevision,
                CreateTime = now,
                UpdateTime = now,
                CreateUserId = 0,
                CreateUserName = string.Empty,
                UpdateUserId = 0,
                UpdateUserName = string.Empty
            }).ExecuteCommandAsync();

            // 第4阶段：遍历 WriteMigrationAsync 当前数据集合，逐项转换并维护处理结果。
            var datasetCount = await _database.Queryable<PocReportDataset>()
                .Where(item => item.Code == migration.DatasetCode).CountAsync();
            var reportCount = await _database.Queryable<PocReport>()
                .Where(item => item.Code == migration.ReportCode).CountAsync();
            var parameterCount = await _database.Queryable<PocReportParameter>()
                .Where(item => item.ReportCode == migration.ReportCode).CountAsync();
            var templateCount = await _database.Queryable<PocReportTemplate>()
                .Where(item => item.ReportCode == migration.ReportCode).CountAsync();
            if (datasetCount != 1 || reportCount != 1 || parameterCount != migration.Parameters.Count || templateCount != 1)
            {
                throw new InvalidOperationException(
                    $"目标写后校验失败，期望 1,1,{migration.Parameters.Count},1，实际 {datasetCount},{reportCount},{parameterCount},{templateCount}。");
            }

            await tenant.CommitTranAsync();
            return new PocJnpfMigrationResultItem(
                migration.ReportCode,
                migration.ReportName,
                "Inserted",
                $"已写入 {migration.FieldCount} 个字段、{migration.ParameterCount} 个参数。");
        }
        catch
        {
            await tenant.RollbackTranAsync();
            throw;
        }
    }

    /// <summary>解析并计算 PocJnpfMigrationApi 的 Resolve Data Source Async 结果，处理必要的边界条件。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="expectedType">expectedType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="description">description 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal async Task<PocDataSourceEndpoint> ResolveDataSourceAsync(
        string code,
        string? expectedType,
        string description)
    {
        var dataSource = await _dataAssets.GetDataSourceAsync(code);
        if (dataSource is null) throw Oops.Oh($"{description}数据源 '{code}' 不存在。").StatusCode(400);
        if (!dataSource.IsEnabled) throw Oops.Oh($"{description}数据源 '{code}' 未启用。").StatusCode(400);
        if (!string.IsNullOrWhiteSpace(expectedType) &&
            !PocDatabaseProvider.NormalizeType(dataSource.DbType).Equals(expectedType, StringComparison.OrdinalIgnoreCase))
        {
            throw Oops.Oh($"{description}数据源必须是 {expectedType}，实际为 {dataSource.DbType}。").StatusCode(400);
        }
        var endpoint = PocDatabaseProvider.ParseEndpoint(dataSource.DbType, dataSource.ConnectionString);
        if (string.IsNullOrWhiteSpace(endpoint.Password)) throw Oops.Oh($"{description}数据源 '{code}' 未配置密码。").StatusCode(400);
        return endpoint;
    }

    /// <summary>解析并计算 PocJnpfMigrationApi 的 Resolve Current Target Endpoint 结果，处理必要的边界条件。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal PocDataSourceEndpoint ResolveCurrentTargetEndpoint()
    {
        var configuration = _database.CurrentConnectionConfig;
        if (configuration.DbType != SqlSugar.DbType.SqlServer)
        {
            throw Oops.Oh("当前报表中心连接不是 SQL Server，不能执行报表迁移写入。").StatusCode(503);
        }
        var endpoint = PocDatabaseProvider.ParseEndpoint(PocDatabaseProvider.SqlServer, configuration.ConnectionString);
        if (!endpoint.Database.Equals("hd_report_center", StringComparison.OrdinalIgnoreCase))
        {
            throw Oops.Oh("当前报表中心连接未指向 hd_report_center，不能执行报表迁移写入。").StatusCode(503);
        }
        return endpoint;
    }

    /// <summary>解析并计算 PocJnpfMigrationApi 的 Resolve Target Connection String Async 结果，处理必要的边界条件。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    internal Task<string> ResolveTargetConnectionStringAsync()
    {
        _ = ResolveCurrentTargetEndpoint();
        return Task.FromResult(_database.CurrentConnectionConfig.ConnectionString);
    }

    /// <summary>校验 PocJnpfMigrationApi 的 Ensure Target Schema 条件，并返回可供后续流程判断的结果。</summary>
    private void EnsureTargetSchema()
    {
        foreach (var table in new[] { "RcDataSource", "RcDataset", "RcReport", "RcReportParameter", "RcReportTemplate" })
        {
            if (!_database.DbMaintenance.IsAnyTable(table, false))
            {
                throw new InvalidOperationException($"目标库缺少报表中心配置表 '{table}'。");
            }
        }
    }

    /// <summary>校验 PocJnpfMigrationApi 的 Validate Values 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="values">values 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string[] ValidateValues(string[]? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length <= 128)
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToArray();

    /// <summary>校验 PocJnpfMigrationApi 的 Validate Data Source Code 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="missingMessage">missingMessage 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ValidateDataSourceCode(string? value, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Oops.Oh(missingMessage).StatusCode(400);
        var normalized = value.Trim();
        if (!Regex.IsMatch(normalized, "^[A-Za-z0-9_-]{1,64}$"))
        {
            throw Oops.Oh("目标数据源编码格式不正确。").StatusCode(400);
        }
        return normalized;
    }

    /// <summary>根据 PocJnpfMigrationRunner 当前输入计算 Stable Id 结果，并按方法声明的类型返回。</summary>
    /// <param name="idNamespace">idNamespace 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static long StableId(string idNamespace, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"hd-report-center:{idNamespace}:{value}"));
        var id = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
        return id == 0 ? 1 : id;
    }

    /// <summary>根据 PocJnpfMigrationRunner 当前输入计算 Safe Migration Message 结果，并按方法声明的类型返回。</summary>
    /// <param name="exception">exception 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string SafeMigrationMessage(Exception exception)
    {
        if (PocDatabaseProvider.IsDatabaseException(exception))
        {
            return PocDatabaseProvider.GetSafeConnectionError(exception);
        }
        return exception.Message.Length <= 2000 ? exception.Message : exception.Message[^2000..];
    }
}

[AllowAnonymous]
[ApiDescriptionSettings("报表中心验证", Name = "JnpfReportMigrationPoc", Order = 902)]
public sealed class PocJnpfMigrationApi : IDynamicApiController
{
    private readonly PocJnpfMigrationRunner _runner;
    private readonly IConfiguration _configuration;

    /// <summary>初始化 PocJnpfMigrationApi 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="runner">runner 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="configuration">configuration 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    public PocJnpfMigrationApi(
        PocJnpfMigrationRunner runner,
        IConfiguration configuration)
    {
        _runner = runner;
        _configuration = configuration;
    }

    /// <summary>根据 PocJnpfMigrationApi 当前输入计算 Inventory 结果，并按方法声明的类型返回。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/jnpf-report-migration/inventory")]
    public Task<PocJnpfMigrationRunResult> Inventory(
        [FromBody] PocJnpfMigrationInventoryRequest request,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return _runner.InventoryAsync(request, cancellationToken);
    }

    /// <summary>根据 PocJnpfMigrationApi 当前输入计算 Migrate 结果，并按方法声明的类型返回。</summary>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [SuppressMonitor]
    [HttpPost("/api/report-center/poc/jnpf-report-migration/migrate")]
    public Task<PocJnpfMigrationRunResult> Migrate(
        [FromBody] PocJnpfMigrationExecuteRequest request,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return _runner.ExecuteAsync(request, cancellationToken);
    }

    /// <summary>校验 PocJnpfMigrationApi 的 Ensure Enabled 条件，并返回可供后续流程判断的结果。</summary>
    private void EnsureEnabled()
    {
        if (!ReportCenterFeatureOptions.IsJnpfMigrationMenuEnabled(_configuration))
        {
            throw Oops.Oh("JNPF 报表迁移菜单未在当前环境启用。").StatusCode(403);
        }
    }
}
