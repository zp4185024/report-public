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
        return Scan(snapshotJson, PocReportCatalog.GetDefinition());
    }

    /// <summary>扫描 PocTemplateValidator 接收的结构化输入，校验业务标记并返回稳定排序的结果。</summary>
    /// <param name="snapshotJson">snapshotJson 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="definition">描述字段、参数或业务约束的定义对象。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    public static PocTemplateBinding Scan(
        string snapshotJson,
        PocReportDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (snapshotJson.Length > MaximumSnapshotLength)
        {
            throw new PocTemplateValidationException("Template snapshot cannot exceed 4 MiB.");
        }

        var snapshot = JObject.Parse(snapshotJson);
        // 允许字段完全来自数据集返回契约；Excel 公式不属于数据集字段。
        var parameterCodes = definition.Parameters
            .Select(x => x.Code)
            .ToHashSet(StringComparer.Ordinal);
        var detailCodes = definition.Fields
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

    /// <summary>
    /// 检查明细模板行没有被合并区域覆盖：扫描 mergeData 的行坐标，命中明细行时直接拒绝保存。
    /// 坐标读取通过 TryReadCoordinate 完成，因此既接受 JSON 整数也接受数字字符串（见该方法说明）。
    /// </summary>
    /// <param name="snapshot">已解析的 Univer 工作簿快照 JSON 对象。</param>
    /// <param name="sheetId">明细 token 所在工作表的内部 ID，用于定位 mergeData。</param>
    /// <param name="detailRow">明细模板行的零基行号。</param>
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

    /// <summary>
    /// 构建 mergeData 坐标无法解析时的校验异常：错误消息携带工作表与明细行 A1 地址，
    /// 导入向导按 TEMPLATE_VALIDATION_FAILED 错误码展示，提示用户该包的合并区域数据不可读。
    /// </summary>
    /// <param name="sheetId">mergeData 所在工作表的内部 ID，用于错误定位。</param>
    /// <param name="detailRow">明细模板行的零基行号，用于在错误消息中给出 A1 地址。</param>
    /// <returns>携带定位信息的模板校验异常。</returns>
    private static PocTemplateValidationException CreateMergeDataException(string sheetId, int detailRow) =>
        new(
            $"mergeData at sheet '{sheetId}' must contain valid row coordinates near " +
            $"cell {GetCellAddress(detailRow, 0)}.");

    /// <summary>
    /// 读取合并区域的行坐标：接受 JSON 整数，也接受内容可被 int 解析的数字字符串（如 "0"、"5"）。
    /// 必须容忍字符串的原因：JNPF 迁移写入的快照与旧版 Univer 输出的 mergeData 坐标就是数字字符串
    /// （还包括 "rangeType":"0" 这类枚举字段），是真实存量数据；浏览器保存前的归一化只覆盖编辑器路径，
    /// 不能假设库内快照都已完成归一化。null、非数字文本与浮点值仍拒绝，避免掩盖损坏数据。
    /// </summary>
    /// <param name="token">mergeData 条目中的坐标 JSON 节点，允许为 null。</param>
    /// <param name="coordinate">解析成功时输出零基坐标；失败时输出 0。</param>
    /// <returns>坐标可解析为 32 位整数时返回 true，其余返回 false。</returns>
    private static bool TryReadCoordinate(JToken? token, out int coordinate)
    {
        coordinate = 0;
        var text = token?.Type is JTokenType.Integer or JTokenType.String
            ? token.ToString()
            : null;
        return text is not null &&
               int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out coordinate);
    }

    /// <summary>将 PocTemplateValidator 中的 Parse Coordinate 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="key">需要读取、校验或转换的业务值。</param>
    /// <param name="coordinateName">需要读取、校验或转换的业务值。</param>
    /// <param name="sheetId">需要定位的业务记录标识。</param>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>校验 PocTemplateValidator 的 Ensure Known Code 条件，并返回可供后续流程判断的结果。</summary>
    /// <param name="knownCodes">knownCodes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="scope">scope 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="sheetId">需要定位的业务记录标识。</param>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="column">参与映射、显示或数据写入的字段集合。</param>
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

    /// <summary>查询或读取 PocTemplateValidator 所需的 Get Sheet Display Name 数据，并按调用约定返回结果。</summary>
    /// <param name="sheet">当前读取或修改的工作表对象。</param>
    /// <param name="sheetId">需要定位的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string GetSheetDisplayName(JObject sheet, string sheetId)
    {
        // name 对应用户看到的页签；缺失或空白时退回稳定的内部 ID。
        var name = sheet.Value<string>("name");
        return string.IsNullOrWhiteSpace(name) ? sheetId : name.Trim();
    }

    // 同时展示名称和 ID，既方便设计人员定位页签，也保留服务端排查所需的快照标识。
    /// <summary>将 PocTemplateValidator 中的 Format Sheet Label 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="sheetName">需要读取、校验或转换的业务值。</param>
    /// <param name="sheetId">需要定位的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string FormatSheetLabel(string sheetName, string sheetId) =>
        string.Equals(sheetName, sheetId, StringComparison.Ordinal)
            ? $"'{sheetId}'"
            : $"'{sheetName}'（ID: {sheetId}）";

    /// <summary>查询或读取 PocTemplateValidator 所需的 Get Cell Address 数据，并按调用约定返回结果。</summary>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="column">参与映射、显示或数据写入的字段集合。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
    // 字段编码与数据集定义保持一致，允许首字母后的下划线，例如 VISIT_ID。
    /// <summary>负责 PocTemplateValidator 的 Token Regex 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [GeneratedRegex(@"\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9_]*)[ \t\r\n\f\v]*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
