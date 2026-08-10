using HD.ReportCenter.ReportEngine.CoreValidation;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace HD.ReportCenter.Application.CoreValidation;

public sealed class PocDatasetExecutionException : Exception
{
    public PocDatasetExecutionException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record PocRuntimeExecutionPlan(
    PocRuntimeReportConfiguration Configuration,
    PocExecuteRequest Query,
    PocResolvedParameterSet Parameters);

/// <summary>SQL Server 只读数据集执行器。配置读取与 SQL 执行分离，便于导出任务冻结运行输入。</summary>
public sealed class PocSqlDatasetExecutor : ITransient
{
    private readonly PocReportConfigurationService _configurationService;

    public PocSqlDatasetExecutor(PocReportConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public async Task<PocRuntimeExecutionPlan> PrepareAsync(
        string reportCode,
        PocExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = await _configurationService.GetRuntimeAsync(reportCode);
        var parameters = PocParameterResolver.Resolve(
            configuration.Definition.Parameters,
            request.Parameters);
        if (!configuration.UsesFallbackData)
        {
            _ = PocReadOnlySqlValidator.Validate(
                configuration.QueryText,
                configuration.Definition.Parameters.Select(item => item.Code),
                configuration.Definition.Preview.Mode);
        }
        return new PocRuntimeExecutionPlan(configuration, request, parameters);
    }

    public async Task<PocExecuteResult> ExecuteAsync(
        string reportCode,
        PocExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareAsync(reportCode, request, cancellationToken);
        return await ExecuteAsync(plan, cancellationToken);
    }

    public static async Task<PocExecuteResult> ExecuteAsync(
        PocRuntimeExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Configuration.UsesFallbackData)
        {
            return PocReportCatalog.Execute(plan.Query);
        }

        var definition = plan.Configuration.Definition;
        var preview = definition.Preview;
        var full = preview.Mode == PocPreviewMode.Full;
        var page = full ? 1 : Math.Max(1, plan.Query.Page);
        var take = full ? plan.Configuration.MaxRows + 1 : preview.PageSize + 1;
        var queryText = full
            ? plan.Configuration.QueryText
            : $"{plan.Configuration.QueryText.TrimEnd()} OFFSET @__offset ROWS FETCH NEXT @__take ROWS ONLY";

        try
        {
            await using var connection = new SqlConnection(plan.Configuration.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = queryText;
            command.CommandTimeout = Math.Clamp(plan.Configuration.TimeoutSeconds, 1, 600);

            var referenced = PocReadOnlySqlValidator.Validate(
                plan.Configuration.QueryText,
                definition.Parameters.Select(item => item.Code),
                preview.Mode);
            foreach (var code in referenced)
            {
                var parameterDefinition = definition.Parameters.Single(
                    item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                command.Parameters.Add(CreateParameter(
                    code,
                    parameterDefinition.DataType,
                    plan.Parameters.DatabaseValues[code]));
            }
            if (!full)
            {
                command.Parameters.Add(new SqlParameter("@__offset", SqlDbType.Int)
                {
                    Value = checked((page - 1) * preview.PageSize)
                });
                command.Parameters.Add(new SqlParameter("@__take", SqlDbType.Int)
                {
                    Value = take
                });
            }

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken);
            var fields = definition.Fields.ToArray();
            var ordinals = ResolveOrdinals(reader, fields);
            var rows = new List<PocDataRow>(Math.Min(take, 4096));
            while (rows.Count < take && await reader.ReadAsync(cancellationToken))
            {
                var row = new PocDataRow();
                foreach (var field in fields)
                {
                    row[field.Code] = NormalizeValue(reader.GetValue(ordinals[field.Code]));
                }
                rows.Add(row);
            }

            var hasMore = rows.Count > (full ? plan.Configuration.MaxRows : preview.PageSize);
            if (full && hasMore)
            {
                throw new PocDatasetExecutionException(
                    "PREVIEW_LIMIT_EXCEEDED",
                    $"完整预览超过 {plan.Configuration.MaxRows} 行限制。" );
            }
            if (!full && hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            return new PocExecuteResult(
                page,
                full ? rows.Count : preview.PageSize,
                !full && hasMore,
                plan.Parameters.DisplayValues,
                rows);
        }
        catch (PocDatasetExecutionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException exception)
        {
            throw new PocDatasetExecutionException(
                exception.Number == -2 ? "DATASET_TIMEOUT" : "DATASET_QUERY_FAILED",
                "数据集查询失败。",
                exception);
        }
    }

    public static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ReadExportPagesAsync(
        PocRuntimeExecutionPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var preview = plan.Configuration.Definition.Preview;
        if (preview.Mode == PocPreviewMode.Full)
        {
            var result = await ExecuteAsync(plan with
            {
                Query = plan.Query with { Page = 1 }
            }, cancellationToken);
            yield return result.Rows;
            yield break;
        }

        for (var page = 1; ; page++)
        {
            var result = await ExecuteAsync(plan with
            {
                Query = plan.Query with { Page = page }
            }, cancellationToken);
            if (result.Rows.Count > 0)
            {
                yield return result.Rows;
            }
            if (!result.HasMore)
            {
                yield break;
            }
        }
    }

    private static IReadOnlyDictionary<string, int> ResolveOrdinals(
        SqlDataReader reader,
        IReadOnlyList<PocDatasetFieldDefinition> fields)
    {
        var actual = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!actual.TryGetValue(field.Code, out var ordinal))
            {
                throw new PocDatasetExecutionException(
                    "DATASET_FIELD_MISSING",
                    $"查询结果缺少字段 '{field.Code}'。" );
            }
            ordinals[field.Code] = ordinal;
        }
        return ordinals;
    }

    internal static SqlParameter CreateParameter(string code, string dataType, object? value)
    {
        var parameter = new SqlParameter($"@{code}", dataType.Trim().ToLowerInvariant() switch
        {
            "long" or "int64" => SqlDbType.BigInt,
            "int" or "int32" or "number" => SqlDbType.Int,
            "decimal" => SqlDbType.Decimal,
            "date" => SqlDbType.Date,
            "datetime" => SqlDbType.DateTime2,
            "bool" or "boolean" => SqlDbType.Bit,
            _ => SqlDbType.NVarChar
        })
        {
            Value = value ?? DBNull.Value
        };
        if (parameter.SqlDbType == SqlDbType.NVarChar)
        {
            parameter.Size = Math.Max(1, Math.Min(4000, Convert.ToString(value, CultureInfo.InvariantCulture)?.Length ?? 1));
        }
        if (parameter.SqlDbType == SqlDbType.Decimal)
        {
            parameter.Precision = 28;
            parameter.Scale = 8;
        }
        return parameter;
    }

    private static object? NormalizeValue(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid guid => guid.ToString("D"),
        DateTimeOffset dateTimeOffset => dateTimeOffset,
        DateTime dateTime => dateTime,
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}
