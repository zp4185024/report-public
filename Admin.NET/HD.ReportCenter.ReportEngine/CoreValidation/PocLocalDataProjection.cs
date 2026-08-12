using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>不修改 SQL，在完整结果或导出流上执行报表级本地筛选和字段显示映射。</summary>
public static class PocLocalDataProjection
{
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

    private static string Comparable(object? value) =>
        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant()
        ?? string.Empty;
}
