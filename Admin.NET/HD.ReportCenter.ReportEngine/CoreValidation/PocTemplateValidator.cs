using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>
/// Univer 工作簿模板的后台可信校验器。
/// 它只读取 token 和绑定所需的 JSON 子树，不实例化前端运行时；保存和读取模板时都必须重新扫描。
/// </summary>
public static partial class PocTemplateValidator
{
    // 防止用户提交异常大的 JSON 让解析和多次扫描消耗不可控内存；正式值应结合数据库和网关限制配置化。
    private const int MaximumSnapshotLength = 4 * 1024 * 1024;

    /// <summary>
    /// 扫描工作簿所有字符串单元格，验证 token 并返回按坐标稳定排序的规范绑定。
    /// Snapshot 是事实来源，调用方不能用客户端提交的 Binding 绕过本方法。
    /// </summary>
    public static PocTemplateBinding Scan(string snapshotJson)
    {
        if (snapshotJson.Length > MaximumSnapshotLength)
        {
            throw new PocTemplateValidationException("Template snapshot cannot exceed 4 MiB.");
        }

        var snapshot = JObject.Parse(snapshotJson);
        // 允许字段完全来自报表定义；公式列不是后台数据字段，禁止被 detail token 直接绑定。
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
        string? detailSheetName = null;
        var detailRow = -1;

        // Univer 的 cellData 使用“工作表 -> 行字符串键 -> 列字符串键 -> 单元格对象”的稀疏结构。
        // 只遍历实际存在的单元格，避免按 rowCount * columnCount 扫描大量空白区域。
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
                var sheetName = GetSheetDisplayName(sheet, sheetProperty.Name);

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
                            // 正则可能命中三重花括号中间的合法片段，因此还要检查 token 两侧是否多出花括号。
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
                                // 明细值必须保持原始数字/文本类型，不能与固定文字拼接，所以 token 必须独占单元格。
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
                                // 当前执行器通过复制一条模板行展开数据，因此所有 detail token 必须共享工作表和行。
                                if (detailSheetId is not null &&
                                    (!string.Equals(detailSheetId, sheetProperty.Name, StringComparison.Ordinal) ||
                                     detailRow != row))
                                {
                                    throw new PocTemplateValidationException(
                                        $"已有明细区域：工作表 {FormatSheetLabel(detailSheetName!, detailSheetId)}" +
                                        $"第 {detailRow + 1} 行。" +
                                        $"当前冲突工作表：{FormatSheetLabel(sheetName, sheetProperty.Name)}。" +
                                        "所有明细 token 必须位于同一个工作表行；如需重新建立明细区域，" +
                                        "请先删除旧区域中的明细 token。" +
                                        $"冲突位置：工作表 '{sheetProperty.Name}'，单元格 {GetCellAddress(row, column)}。");
                                }

                                detailSheetId ??= sheetProperty.Name;
                                detailSheetName ??= sheetName;
                                if (detailRow < 0)
                                {
                                    detailRow = row;
                                }

                                detailFields.Add(new PocDetailFieldBinding(code, column));
                            }
                        }

                        // 删除全部已识别 token 后仍有 {{ 或 }}，说明存在拼写错误或不受支持的 token。
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

        // 规范排序让前后端生成的 Binding JSON 可以稳定比较，也避免字典遍历顺序影响修订内容。
        var orderedParameters = parameterBindings
            .OrderBy(x => x.SheetId, StringComparer.Ordinal)
            .ThenBy(x => x.Row)
            .ThenBy(x => x.Column)
            .ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToArray();
        if (detailFields.Count > 0)
        {
            // 复制整行时，跨越模板行的合并区域会改变目标坐标或导致 Univer 拒绝插行，必须提前阻止。
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
        // 后台只接受真正的 JSON 整数。浏览器在保存前会归一化旧版 Univer 返回的数字字符串。
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

    private static string GetSheetDisplayName(JObject sheet, string sheetId)
    {
        // name 对应用户看到的页签；缺失或空白时退回稳定的内部 ID。
        var name = sheet.Value<string>("name");
        return string.IsNullOrWhiteSpace(name) ? sheetId : name.Trim();
    }

    // 同时展示名称和 ID，既方便设计人员定位页签，也保留服务端排查所需的快照标识。
    private static string FormatSheetLabel(string sheetName, string sheetId) =>
        string.Equals(sheetName, sheetId, StringComparison.Ordinal)
            ? $"'{sheetId}'"
            : $"'{sheetName}'（ID: {sheetId}）";

    private static string GetCellAddress(int row, int column)
    {
        // 错误消息使用用户熟悉的 A1 地址，而内部绑定继续使用便于计算的零基行列坐标。
        var letters = new StringBuilder();
        for (var value = column + 1; value > 0; value = (value - 1) / 26)
        {
            letters.Insert(0, (char)('A' + (value - 1) % 26));
        }

        return $"{letters}{row + 1}";
    }

    // GeneratedRegex 在编译期生成匹配器，避免运行时构造正则；仅允许 params/detail 两种作用域。
    [GeneratedRegex(@"\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9]*)[ \t\r\n\f\v]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
