using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public static partial class PocTemplateValidator
{
    private const int MaximumSnapshotLength = 4 * 1024 * 1024;

    public static PocTemplateBinding Scan(string snapshotJson)
    {
        if (snapshotJson.Length > MaximumSnapshotLength)
        {
            throw new PocTemplateValidationException("Template snapshot cannot exceed 4 MiB.");
        }

        var snapshot = JObject.Parse(snapshotJson);
        var definition = PocReportCatalog.GetDefinition();
        var parameterCodes = definition.Parameters
            .Select(x => x.Code)
            .ToHashSet(StringComparer.Ordinal);
        var detailCodes = definition.Columns
            .Where(x => !string.Equals(x.DataType, "formula", StringComparison.Ordinal))
            .Select(x => x.Code)
            .ToHashSet(StringComparer.Ordinal);
        var parameterBindings = new List<PocParameterBinding>();
        var detailFields = new List<PocDetailFieldBinding>();
        string? detailSheetId = null;
        var detailRow = -1;

        if (snapshot["sheets"] is JObject sheets)
        {
            foreach (var sheetProperty in sheets.Properties())
            {
                if (sheetProperty.Value["cellData"] is not JObject cellData)
                {
                    continue;
                }

                foreach (var rowProperty in cellData.Properties())
                {
                    var row = int.Parse(rowProperty.Name, CultureInfo.InvariantCulture);
                    if (rowProperty.Value is not JObject rowData)
                    {
                        continue;
                    }

                    foreach (var columnProperty in rowData.Properties())
                    {
                        var column = int.Parse(columnProperty.Name, CultureInfo.InvariantCulture);
                        if (columnProperty.Value["v"]?.Type != JTokenType.String)
                        {
                            continue;
                        }

                        var value = columnProperty.Value.Value<string>("v")!;
                        foreach (Match match in TokenRegex().Matches(value))
                        {
                            var scope = match.Groups[1].Value;
                            var code = match.Groups[2].Value;
                            if (scope == "params")
                            {
                                EnsureKnownCode(parameterCodes, scope, code, sheetProperty.Name, row, column);
                                parameterBindings.Add(new PocParameterBinding(code, sheetProperty.Name, row, column));
                            }
                            else
                            {
                                if (!string.Equals(value.Trim(), match.Value, StringComparison.Ordinal))
                                {
                                    throw new PocTemplateValidationException(
                                        $"Detail token must occupy the entire cell at sheet '{sheetProperty.Name}', " +
                                        $"cell {GetCellAddress(row, column)}.");
                                }

                                EnsureKnownCode(detailCodes, scope, code, sheetProperty.Name, row, column);
                                if (detailSheetId is not null &&
                                    (!string.Equals(detailSheetId, sheetProperty.Name, StringComparison.Ordinal) ||
                                     detailRow != row))
                                {
                                    throw new PocTemplateValidationException(
                                        $"Detail tokens must use one template row; conflict at sheet '{sheetProperty.Name}', " +
                                        $"cell {GetCellAddress(row, column)}.");
                                }

                                detailSheetId ??= sheetProperty.Name;
                                if (detailRow < 0)
                                {
                                    detailRow = row;
                                }

                                detailFields.Add(new PocDetailFieldBinding(code, column));
                            }
                        }

                        var remainingValue = TokenRegex().Replace(value, string.Empty);
                        if (remainingValue.Contains("{{", StringComparison.Ordinal) ||
                            remainingValue.Contains("}}", StringComparison.Ordinal))
                        {
                            throw new PocTemplateValidationException(
                                $"Malformed template token at sheet '{sheetProperty.Name}', " +
                                $"cell {GetCellAddress(row, column)}.");
                        }
                    }
                }
            }
        }

        var orderedParameters = parameterBindings
            .OrderBy(x => x.SheetId, StringComparer.Ordinal)
            .ThenBy(x => x.Row)
            .ThenBy(x => x.Column)
            .ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToArray();
        if (detailFields.Count > 0)
        {
            EnsureDetailRowIsNotMerged(snapshot, detailSheetId!, detailRow);
        }

        var detailBinding = detailFields.Count == 0
            ? null
            : new PocDetailBinding(
                "detail",
                detailSheetId!,
                detailRow,
                detailFields.OrderBy(x => x.Column).ToArray());

        return new PocTemplateBinding(1, orderedParameters, detailBinding);
    }

    private static void EnsureDetailRowIsNotMerged(JObject snapshot, string sheetId, int detailRow)
    {
        if (snapshot["sheets"]?[sheetId]?["mergeData"] is not JArray mergeData)
        {
            return;
        }

        foreach (var merge in mergeData.OfType<JObject>())
        {
            var startRow = merge.Value<int?>("startRow");
            var endRow = merge.Value<int?>("endRow");
            if (startRow <= detailRow && detailRow <= endRow)
            {
                throw new PocTemplateValidationException(
                    $"Merged cells cannot touch the detail template row at sheet '{sheetId}', " +
                    $"cell {GetCellAddress(detailRow, 0)}.");
            }
        }
    }

    private static void EnsureKnownCode(
        HashSet<string> knownCodes,
        string scope,
        string code,
        string sheetId,
        int row,
        int column)
    {
        if (!knownCodes.Contains(code))
        {
            throw new PocTemplateValidationException(
                $"Unknown {scope} code '{code}' at sheet '{sheetId}', cell {GetCellAddress(row, column)}.");
        }
    }

    private static string GetCellAddress(int row, int column)
    {
        var letters = new StringBuilder();
        for (var value = column + 1; value > 0; value = (value - 1) / 26)
        {
            letters.Insert(0, (char)('A' + (value - 1) % 26));
        }

        return $"{letters}{row + 1}";
    }

    [GeneratedRegex(@"\{\{\s*(params|detail)\.([A-Za-z][A-Za-z0-9]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
