using ClosedXML.Excel;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace HD.ReportCenter.ReportEngine.CoreValidation.Exchange;

/// <summary>
/// Univer IWorkbookData 与 XLSX 的双向转换器。
/// 转换器只掌握通用算法；具体 JSON 路径、必填性和换算系数来自数据库规则集合。
/// </summary>
public sealed class PocXlsxExchangeConverter
{
    /// <summary>把一个经过模板校验的 Univer 快照写成可由 Excel/WPS 打开的 XLSX。</summary>
    public void Export(JObject snapshot, PocExchangeRuleSet rules, Stream output)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("XLSX输出流必须可写。", nameof(output));
        }

        var mapping = new RuleLookup(rules, PocExchangeDirections.UniverToExcel);
        mapping.Require(
            "univer-to-xlsx-cell-value",
            "univer-to-xlsx-cell-formula",
            "univer-to-xlsx-cell-style");

        if (output.CanSeek)
        {
            output.Position = 0;
            output.SetLength(0);
        }

        using var workbook = new XLWorkbook();
        workbook.Properties.Title = snapshot.Value<string>("name") ?? "Univer模板";
        workbook.CalculateMode = XLCalculateMode.Auto;
        workbook.FullCalculationOnLoad = true;
        workbook.ForceFullCalculation = true;

        var styles = snapshot["styles"] as JObject ?? new JObject();
        var sheets = snapshot["sheets"] as JObject
            ?? throw InvalidSnapshot("工作簿缺少 sheets 对象。");
        var sheetOrder = snapshot["sheetOrder"] as JArray
            ?? throw InvalidSnapshot("工作簿缺少 sheetOrder 数组。");

        foreach (var sheetIdToken in sheetOrder)
        {
            var sheetId = sheetIdToken.Value<string>();
            if (string.IsNullOrWhiteSpace(sheetId) || sheets[sheetId] is not JObject sheetData)
            {
                throw InvalidSnapshot($"工作簿顺序包含不存在的工作表 '{sheetId}'。");
            }

            WriteWorksheet(workbook, sheetData, styles, mapping);
        }

        if (workbook.Worksheets.Count == 0)
        {
            throw InvalidSnapshot("工作簿至少需要一个工作表。");
        }

        workbook.SaveAs(output, validate: true, evaluateFormulae: false);
        if (output.CanSeek)
        {
            output.Position = 0;
        }
    }

    private static void WriteWorksheet(
        XLWorkbook workbook,
        JObject sheetData,
        JObject styles,
        RuleLookup mapping)
    {
        var requestedName = sheetData.Value<string>("name") ?? "工作表";
        var sheetName = CreateUniqueSheetName(workbook, requestedName);
        var sheet = workbook.Worksheets.Add(sheetName);

        var tabColor = sheetData.Value<string>("tabColor");
        if (TryColor(tabColor, out var parsedTabColor))
        {
            sheet.TabColor = parsedTabColor;
        }
        if (sheetData.Value<int?>("hidden") == 1)
        {
            sheet.Visibility = XLWorksheetVisibility.Hidden;
        }

        // cellData 是稀疏矩阵，只遍历实际存在的行列键，避免模板 rowCount 很大时生成海量空单元格。
        if (sheetData["cellData"] is JObject cellData)
        {
            foreach (var rowProperty in cellData.Properties())
            {
                var rowIndex = ParseCoordinate(rowProperty.Name, "单元格行");
                if (rowProperty.Value is not JObject rowData)
                {
                    throw InvalidSnapshot($"工作表 '{sheetName}' 第 {rowIndex + 1} 行不是对象。");
                }

                foreach (var columnProperty in rowData.Properties())
                {
                    var columnIndex = ParseCoordinate(columnProperty.Name, "单元格列");
                    if (columnProperty.Value is not JObject cellDataObject)
                    {
                        throw InvalidSnapshot(
                            $"工作表 '{sheetName}' 第 {rowIndex + 1} 行第 {columnIndex + 1} 列不是对象。");
                    }

                    WriteCell(
                        sheet.Cell(rowIndex + 1, columnIndex + 1),
                        cellDataObject,
                        styles,
                        mapping);
                }
            }
        }

        ApplyMerges(sheet, sheetData["mergeData"] as JArray, sheetName);
        ApplyRows(sheet, sheetData["rowData"] as JObject, mapping);
        ApplyColumns(sheet, sheetData["columnData"] as JObject, mapping);
        ApplyFreeze(sheet, sheetData["freeze"] as JObject);
    }

    private static void WriteCell(
        IXLCell cell,
        JObject cellData,
        JObject styles,
        RuleLookup mapping)
    {
        var formulaRule = mapping["univer-to-xlsx-cell-formula"];
        var formula = SelectToken(cellData, formulaRule.SourcePath)?.Value<string>();
        if (!string.IsNullOrWhiteSpace(formula))
        {
            // Univer 按公开契约保存带 '=' 的 f；ClosedXML 0.105 的 FormulaA1 使用不带 '=' 的表达式。
            cell.FormulaA1 = formula.StartsWith('=') ? formula[1..] : formula;
        }
        else
        {
            var valueRule = mapping["univer-to-xlsx-cell-value"];
            WriteCellValue(cell, SelectToken(cellData, valueRule.SourcePath));
        }

        var styleRule = mapping["univer-to-xlsx-cell-style"];
        var styleReference = SelectToken(cellData, styleRule.SourcePath);
        JObject? style = styleReference switch
        {
            JValue { Type: JTokenType.String } reference => styles[reference.Value<string>()!] as JObject,
            JObject inlineStyle => inlineStyle,
            _ => null
        };
        if (style is not null)
        {
            ApplyStyle(cell.Style, style, mapping);
        }
    }

    private static void WriteCellValue(IXLCell cell, JToken? token)
    {
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return;
        }

        switch (token.Type)
        {
            case JTokenType.Integer:
                cell.Value = token.Value<long>();
                break;
            case JTokenType.Float:
                cell.Value = token.Value<double>();
                break;
            case JTokenType.Boolean:
                cell.Value = token.Value<bool>();
                break;
            case JTokenType.String:
                cell.Value = token.Value<string>() ?? string.Empty;
                break;
            default:
                throw InvalidSnapshot($"单元格值类型 '{token.Type}' 不能导出到 XLSX。");
        }
    }

    private static void ApplyStyle(IXLStyle target, JObject source, RuleLookup mapping)
    {
        ApplyString(source, mapping, "univer-to-xlsx-style-font-name", value => target.Font.FontName = value);
        ApplyNumber(source, mapping, "univer-to-xlsx-style-font-size", value => target.Font.FontSize = value);
        ApplyBoolean(source, mapping, "univer-to-xlsx-style-bold", value => target.Font.Bold = value);
        ApplyBoolean(source, mapping, "univer-to-xlsx-style-italic", value => target.Font.Italic = value);
        ApplyBoolean(source, mapping, "univer-to-xlsx-style-underline", value =>
            target.Font.Underline = value ? XLFontUnderlineValues.Single : XLFontUnderlineValues.None);
        ApplyBoolean(source, mapping, "univer-to-xlsx-style-strike", value => target.Font.Strikethrough = value);
        ApplyColor(source, mapping, "univer-to-xlsx-style-font-color", value => target.Font.FontColor = value);
        ApplyColor(source, mapping, "univer-to-xlsx-style-background", value => target.Fill.BackgroundColor = value);
        ApplyString(source, mapping, "univer-to-xlsx-style-number-format", value => target.NumberFormat.Format = value);

        ApplyNumber(source, mapping, "univer-to-xlsx-style-horizontal", value =>
            target.Alignment.Horizontal = ToHorizontalAlignment((int)value));
        ApplyNumber(source, mapping, "univer-to-xlsx-style-vertical", value =>
            target.Alignment.Vertical = ToVerticalAlignment((int)value));
        ApplyNumber(source, mapping, "univer-to-xlsx-style-wrap", value =>
            target.Alignment.WrapText = (int)value == 3);
        ApplyNumber(source, mapping, "univer-to-xlsx-style-rotation", value =>
            target.Alignment.TextRotation = (int)Math.Clamp(value, -90, 90));

        ApplyBorder(source, mapping, "univer-to-xlsx-border-top", target.Border, BorderSide.Top);
        ApplyBorder(source, mapping, "univer-to-xlsx-border-right", target.Border, BorderSide.Right);
        ApplyBorder(source, mapping, "univer-to-xlsx-border-bottom", target.Border, BorderSide.Bottom);
        ApplyBorder(source, mapping, "univer-to-xlsx-border-left", target.Border, BorderSide.Left);
    }

    private static void ApplyMerges(IXLWorksheet sheet, JArray? mergeData, string sheetName)
    {
        if (mergeData is null)
        {
            return;
        }

        foreach (var item in mergeData.OfType<JObject>())
        {
            var startRow = RequiredCoordinate(item, "startRow", sheetName);
            var endRow = RequiredCoordinate(item, "endRow", sheetName);
            var startColumn = RequiredCoordinate(item, "startColumn", sheetName);
            var endColumn = RequiredCoordinate(item, "endColumn", sheetName);
            if (endRow < startRow || endColumn < startColumn)
            {
                throw InvalidSnapshot($"工作表 '{sheetName}' 包含反向合并范围。");
            }
            sheet.Range(startRow + 1, startColumn + 1, endRow + 1, endColumn + 1).Merge();
        }
    }

    private static void ApplyRows(IXLWorksheet sheet, JObject? rowData, RuleLookup mapping)
    {
        if (rowData is null)
        {
            return;
        }

        var rule = mapping.GetOptional("univer-to-xlsx-row-height");
        var factor = (double)(rule?.Factor ?? 0.75m);
        foreach (var property in rowData.Properties())
        {
            var rowIndex = ParseCoordinate(property.Name, "行样式");
            if (property.Value is not JObject data)
            {
                continue;
            }

            if (data.Value<double?>("h") is { } height && height > 0)
            {
                sheet.Row(rowIndex + 1).Height = height * factor;
            }
            if (data.Value<int?>("hd") == 1)
            {
                sheet.Row(rowIndex + 1).Hide();
            }
        }
    }

    private static void ApplyColumns(IXLWorksheet sheet, JObject? columnData, RuleLookup mapping)
    {
        if (columnData is null)
        {
            return;
        }

        mapping.GetOptional("univer-to-xlsx-column-width");
        foreach (var property in columnData.Properties())
        {
            var columnIndex = ParseCoordinate(property.Name, "列样式");
            if (property.Value is not JObject data)
            {
                continue;
            }

            if (data.Value<double?>("w") is { } width && width > 0)
            {
                // Excel 列宽使用字符单位，Univer 使用像素；该经验公式与 Excel 常见默认字体的换算一致。
                sheet.Column(columnIndex + 1).Width = PixelToExcelColumnWidth(width);
            }
            if (data.Value<int?>("hd") == 1)
            {
                sheet.Column(columnIndex + 1).Hide();
            }
        }
    }

    private static void ApplyFreeze(IXLWorksheet sheet, JObject? freeze)
    {
        if (freeze is null)
        {
            return;
        }

        var rows = Math.Max(0, freeze.Value<int?>("ySplit") ?? 0);
        var columns = Math.Max(0, freeze.Value<int?>("xSplit") ?? 0);
        if (rows > 0)
        {
            sheet.SheetView.FreezeRows(rows);
        }
        if (columns > 0)
        {
            sheet.SheetView.FreezeColumns(columns);
        }
    }

    private static void ApplyString(
        JObject source,
        RuleLookup mapping,
        string ruleCode,
        Action<string> apply)
    {
        var rule = mapping.GetOptional(ruleCode);
        var value = rule is null ? null : SelectToken(source, rule.SourcePath)?.Value<string>();
        if (!string.IsNullOrEmpty(value))
        {
            apply(value);
        }
    }

    private static void ApplyNumber(
        JObject source,
        RuleLookup mapping,
        string ruleCode,
        Action<double> apply)
    {
        var rule = mapping.GetOptional(ruleCode);
        var token = rule is null ? null : SelectToken(source, rule.SourcePath);
        if (token?.Type is JTokenType.Integer or JTokenType.Float)
        {
            apply(token.Value<double>());
        }
    }

    private static void ApplyBoolean(
        JObject source,
        RuleLookup mapping,
        string ruleCode,
        Action<bool> apply)
    {
        var rule = mapping.GetOptional(ruleCode);
        var token = rule is null ? null : SelectToken(source, rule.SourcePath);
        if (token?.Type is JTokenType.Integer or JTokenType.Boolean)
        {
            apply(token.Type == JTokenType.Boolean ? token.Value<bool>() : token.Value<int>() != 0);
        }
    }

    private static void ApplyColor(
        JObject source,
        RuleLookup mapping,
        string ruleCode,
        Action<XLColor> apply)
    {
        var rule = mapping.GetOptional(ruleCode);
        var value = rule is null ? null : SelectToken(source, rule.SourcePath)?.Value<string>();
        if (TryColor(value, out var color))
        {
            apply(color);
        }
    }

    private static void ApplyBorder(
        JObject source,
        RuleLookup mapping,
        string ruleCode,
        IXLBorder target,
        BorderSide side)
    {
        var rule = mapping.GetOptional(ruleCode);
        if (rule is null || SelectToken(source, rule.SourcePath) is not JObject border)
        {
            return;
        }

        var style = ToBorderStyle(border.Value<int?>("s") ?? 0);
        var colorValue = border["cl"]?["rgb"]?.Value<string>();
        var hasColor = TryColor(colorValue, out var color);
        switch (side)
        {
            case BorderSide.Top:
                target.TopBorder = style;
                if (hasColor) target.TopBorderColor = color;
                break;
            case BorderSide.Right:
                target.RightBorder = style;
                if (hasColor) target.RightBorderColor = color;
                break;
            case BorderSide.Bottom:
                target.BottomBorder = style;
                if (hasColor) target.BottomBorderColor = color;
                break;
            case BorderSide.Left:
                target.LeftBorder = style;
                if (hasColor) target.LeftBorderColor = color;
                break;
        }
    }

    private static JToken? SelectToken(JToken source, string relativePath)
    {
        JToken? current = source;
        foreach (var segment in relativePath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current?[segment];
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }

    private static bool TryColor(string? value, out XLColor color)
    {
        color = XLColor.NoColor;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            color = XLColor.FromHtml(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static XLAlignmentHorizontalValues ToHorizontalAlignment(int value) => value switch
    {
        1 => XLAlignmentHorizontalValues.Left,
        2 => XLAlignmentHorizontalValues.Center,
        3 => XLAlignmentHorizontalValues.Right,
        4 or 5 => XLAlignmentHorizontalValues.Justify,
        6 => XLAlignmentHorizontalValues.Distributed,
        _ => XLAlignmentHorizontalValues.General
    };

    private static XLAlignmentVerticalValues ToVerticalAlignment(int value) => value switch
    {
        1 => XLAlignmentVerticalValues.Top,
        2 => XLAlignmentVerticalValues.Center,
        3 => XLAlignmentVerticalValues.Bottom,
        _ => XLAlignmentVerticalValues.Bottom
    };

    private static XLBorderStyleValues ToBorderStyle(int value) => value switch
    {
        1 => XLBorderStyleValues.Thin,
        2 => XLBorderStyleValues.Hair,
        3 => XLBorderStyleValues.Dotted,
        4 => XLBorderStyleValues.Dashed,
        5 => XLBorderStyleValues.DashDot,
        6 => XLBorderStyleValues.DashDotDot,
        7 => XLBorderStyleValues.Double,
        8 => XLBorderStyleValues.Medium,
        9 => XLBorderStyleValues.MediumDashed,
        10 => XLBorderStyleValues.MediumDashDot,
        11 => XLBorderStyleValues.MediumDashDotDot,
        12 => XLBorderStyleValues.SlantDashDot,
        13 => XLBorderStyleValues.Thick,
        _ => XLBorderStyleValues.None
    };

    private static double PixelToExcelColumnWidth(double pixels) =>
        Math.Max(0.1d, pixels <= 12d ? pixels / 12d : (pixels - 5d) / 7d);

    private static int ParseCoordinate(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var coordinate) || coordinate < 0)
        {
            throw InvalidSnapshot($"{label}坐标 '{value}' 无效。");
        }
        return coordinate;
    }

    private static int RequiredCoordinate(JObject item, string name, string sheetName)
    {
        var token = item[name];
        if (token?.Type != JTokenType.Integer || token.Value<long>() is < 0 or > int.MaxValue)
        {
            throw InvalidSnapshot($"工作表 '{sheetName}' 合并范围字段 {name} 无效。");
        }
        return token.Value<int>();
    }

    private static string CreateUniqueSheetName(XLWorkbook workbook, string requestedName)
    {
        var safeName = string.Concat(requestedName.Where(character => !"[]:*?/\\".Contains(character)));
        safeName = string.IsNullOrWhiteSpace(safeName) ? "工作表" : safeName[..Math.Min(31, safeName.Length)];
        var candidate = safeName;
        var suffix = 2;
        while (workbook.Worksheets.Any(sheet => string.Equals(sheet.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            var suffixText = $"_{suffix++}";
            candidate = safeName[..Math.Min(safeName.Length, 31 - suffixText.Length)] + suffixText;
        }
        return candidate;
    }

    private static PocExchangeConversionException InvalidSnapshot(string message) =>
        new("TEMPLATE_XLSX_SNAPSHOT_INVALID", message);

    private enum BorderSide
    {
        Top,
        Right,
        Bottom,
        Left
    }

    /// <summary>将数据库记录收敛为方向内唯一规则，并提供带规则编码的缺失错误。</summary>
    private sealed class RuleLookup
    {
        private readonly IReadOnlyDictionary<string, PocExchangeRule> _rules;

        public RuleLookup(PocExchangeRuleSet rules, string direction)
        {
            _rules = rules.Rules
                .Where(rule => string.Equals(rule.Direction, direction, StringComparison.Ordinal))
                .ToDictionary(rule => rule.RuleCode, StringComparer.Ordinal);
        }

        public PocExchangeRule this[string ruleCode] => GetOptional(ruleCode)
            ?? throw new PocExchangeConversionException(
                "TEMPLATE_XLSX_MAPPING_MISSING",
                $"缺少Excel转换映射规则 '{ruleCode}'。",
                ruleCode);

        public PocExchangeRule? GetOptional(string ruleCode) =>
            _rules.GetValueOrDefault(ruleCode);

        public void Require(params string[] ruleCodes)
        {
            foreach (var ruleCode in ruleCodes)
            {
                _ = this[ruleCode];
            }
        }
    }
}
