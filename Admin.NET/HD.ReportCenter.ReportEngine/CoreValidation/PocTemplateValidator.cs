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

        var sheetsToken = snapshot["sheets"];
        if (sheetsToken is not null && sheetsToken.Type != JTokenType.Null)
        {
            if (sheetsToken is not JObject sheets)
            {
                throw new PocTemplateValidationException("Template 'sheets' must be an object.");
            }

            foreach (var sheetProperty in sheets.Properties())
            {
                if (sheetProperty.Value is not JObject sheet)
                {
                    throw new PocTemplateValidationException(
                        $"Sheet '{sheetProperty.Name}' must be an object.");
                }

                var cellDataToken = sheet["cellData"];
                if (cellDataToken is null || cellDataToken.Type == JTokenType.Null)
                {
                    continue;
                }

                if (cellDataToken is not JObject cellData)
                {
                    throw new PocTemplateValidationException(
                        $"cellData at sheet '{sheetProperty.Name}' must be an object.");
                }

                foreach (var rowProperty in cellData.Properties())
                {
                    var row = ParseCoordinate(rowProperty.Name, "row", sheetProperty.Name);
                    if (rowProperty.Value is not JObject rowData)
                    {
                        throw new PocTemplateValidationException(
                            $"Row {row + 1} at sheet '{sheetProperty.Name}' must be an object.");
                    }

                    foreach (var columnProperty in rowData.Properties())
                    {
                        var column = ParseCoordinate(columnProperty.Name, "column", sheetProperty.Name, row);
                        if (columnProperty.Value is not JObject cell)
                        {
                            throw new PocTemplateValidationException(
                                $"Cell {GetCellAddress(row, column)} at sheet '{sheetProperty.Name}' must be an object.");
                        }

                        if (cell["v"]?.Type != JTokenType.String)
                        {
                            continue;
                        }

                        var value = cell.Value<string>("v")!;
                        foreach (Match match in TokenRegex().Matches(value))
                        {
                            if ((match.Index > 0 && value[match.Index - 1] == '{') ||
                                (match.Index + match.Length < value.Length && value[match.Index + match.Length] == '}'))
                            {
                                throw new PocTemplateValidationException(
                                    $"Malformed template token at sheet '{sheetProperty.Name}', " +
                                    $"cell {GetCellAddress(row, column)}.");
                            }

                            var scope = match.Groups[1].Value;
                            var code = match.Groups[2].Value;
                            if (scope == "params")
                            {
                                EnsureKnownCode(parameterCodes, scope, code, sheetProperty.Name, row, column);
                                parameterBindings.Add(new PocParameterBinding(code, sheetProperty.Name, row, column));
                            }
                            else
                            {
                                if (!string.Equals(
                                        value.Trim(' ', '\t', '\r', '\n', '\f', '\v'),
                                        match.Value,
                                        StringComparison.Ordinal))
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
        var mergeDataToken = snapshot["sheets"]?[sheetId]?["mergeData"];
        if (mergeDataToken is null || mergeDataToken.Type == JTokenType.Null)
        {
            return;
        }

        if (mergeDataToken is not JArray mergeData)
        {
            throw CreateMergeDataException(sheetId, detailRow);
        }

        foreach (var mergeToken in mergeData)
        {
            if (mergeToken is not JObject merge ||
                !TryReadCoordinate(merge["startRow"], out var startRow) ||
                !TryReadCoordinate(merge["endRow"], out var endRow) ||
                endRow < startRow)
            {
                throw CreateMergeDataException(sheetId, detailRow);
            }

            if (startRow <= detailRow && detailRow <= endRow)
            {
                throw new PocTemplateValidationException(
                    $"Merged cells cannot touch the detail template row at sheet '{sheetId}', " +
                    $"cell {GetCellAddress(detailRow, 0)}.");
            }
        }
    }

    private static PocTemplateValidationException CreateMergeDataException(string sheetId, int detailRow) =>
        new(
            $"mergeData at sheet '{sheetId}' must contain valid row coordinates near " +
            $"cell {GetCellAddress(detailRow, 0)}.");

    private static bool TryReadCoordinate(JToken? token, out int coordinate)
    {
        coordinate = 0;
        return token?.Type == JTokenType.Integer &&
               int.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out coordinate);
    }

    private static int ParseCoordinate(
        string key,
        string coordinateName,
        string sheetId,
        int? row = null)
    {
        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var coordinate))
        {
            return coordinate;
        }

        var rowContext = row.HasValue ? $", row {row.Value + 1}" : string.Empty;
        throw new PocTemplateValidationException(
            $"Invalid {coordinateName} key '{key}' at sheet '{sheetId}'{rowContext}.");
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

    [GeneratedRegex(@"\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9]*)[ \t\r\n\f\v]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
