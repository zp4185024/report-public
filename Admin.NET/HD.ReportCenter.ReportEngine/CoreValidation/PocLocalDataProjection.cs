using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>不修改 SQL，在完整结果或导出流上执行报表级本地筛选和字段显示映射。</summary>
public static class PocLocalDataProjection
{
    /// <summary>执行 PocLocalDataProjection 的 Apply Rows 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="rows">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static IReadOnlyList<PocDataRow> ApplyRows(
        PocReportDefinition definition,
        PocExecuteRequest request,
        IReadOnlyList<PocDataRow> rows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);

        var fieldCodes = definition.Fields
            .Select(field => field.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filters = definition.Parameters
            .Where(parameter =>
                parameter.SourceType.Equals("Form", StringComparison.OrdinalIgnoreCase) &&
                fieldCodes.Contains(parameter.Code))
            .Select(parameter => new
            {
                parameter.Code,
                Values = RequestValues(parameter, request)
            })
            .Where(filter => filter.Values.Count > 0)
            .ToArray();
        var presentations = definition.FieldPresentations ?? [];

        return rows
            .Where(row => filters.All(filter =>
                row.TryGetValue(filter.Code, out var value) &&
                filter.Values.Any(filterValue => Comparable(value) == Comparable(filterValue))))
            .Select(row => ApplyPresentations(row, presentations))
            .ToArray();
    }

    /// <summary>执行 PocLocalDataProjection 的 Apply Parameter Labels 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static PocResolvedParameters ApplyParameterLabels(
        PocReportDefinition definition,
        PocResolvedParameters parameters)
    {
        var result = new PocResolvedParameters(parameters);
        foreach (var parameter in definition.Parameters)
        {
            if (!result.TryGetValue(parameter.Code, out var rawValue) ||
                parameter.Options is null ||
                parameter.Options.Count == 0)
            {
                continue;
            }
            var values = parameter.ControlType.Equals("multiSelect", StringComparison.OrdinalIgnoreCase)
                ? ParseMultiple(rawValue)
                : [rawValue];
            result[parameter.Code] = string.Join("、", values.Select(value =>
                parameter.Options.FirstOrDefault(option => Comparable(option.Value) == Comparable(value))?.Label
                ?? value));
        }
        return result;
    }

    /// <summary>调用 PocLocalDataProjection 的异步依赖完成 Project Pages 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="pages">pages 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static async IAsyncEnumerable<IReadOnlyList<PocDataRow>> ProjectPages(
        IAsyncEnumerable<IReadOnlyList<PocDataRow>> pages,
        PocReportDefinition definition,
        PocExecuteRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var page in pages.WithCancellation(cancellationToken))
        {
            yield return ApplyRows(definition, request, page);
        }
    }

    /// <summary>根据 PocLocalDataProjection 当前输入计算 Request Values 结果，并按方法声明的类型返回。</summary>
    /// <param name="parameter">parameter 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="request">本次操作使用的输入数据。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IReadOnlyList<string> RequestValues(
        PocParameterDefinition parameter,
        PocExecuteRequest request)
    {
        if (!request.Parameters.TryGetValue(parameter.Code, out var rawValue) ||
            string.IsNullOrWhiteSpace(rawValue))
        {
            return [];
        }
        return parameter.ControlType.Equals("multiSelect", StringComparison.OrdinalIgnoreCase)
            ? ParseMultiple(rawValue)
            : [rawValue.Trim()];
    }

    /// <summary>将 PocLocalDataProjection 中的 Parse Multiple 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static IReadOnlyList<string> ParseMultiple(string value)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(value);
            if (parsed is not null)
            {
                return parsed.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray();
            }
        }
        catch (JsonException)
        {
            // 兼容旧的逗号分隔 URL 参数。
        }
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>执行 PocLocalDataProjection 的 Apply Presentations 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="presentations">presentations 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static PocDataRow ApplyPresentations(
        PocDataRow row,
        IReadOnlyList<PocFieldPresentationDefinition> presentations)
    {
        PocDataRow? result = null;
        foreach (var presentation in presentations)
        {
            if (!row.TryGetValue(presentation.Code, out var rawValue)) continue;
            var mapping = presentation.ValueMappings.FirstOrDefault(
                item => Comparable(item.Value) == Comparable(rawValue));
            if (mapping is null) continue;
            result ??= new PocDataRow(row);
            result[presentation.Code] = mapping.Label;
        }
        return result ?? row;
    }

    /// <summary>把 PocLocalDataProjection 的字段值转换为可比较文本，供排序和筛选逻辑复用。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string Comparable(object? value) =>
        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant()
        ?? string.Empty;
}
