using ClosedXML.Excel;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HD.ReportCenter.ReportEngine.CoreValidation.Exchange;

/// <summary>
/// Univer IWorkbookData 与 XLSX 的双向转换器。
/// 转换器只掌握通用算法；具体 JSON 路径、必填性和换算系数来自数据库规则集合。
/// </summary>
public sealed class PocXlsxExchangeConverter
{
    /// <summary>读取 XLSX，并构造当前 Univer 版本可直接加载的稀疏工作簿快照。</summary>
    public PocXlsxImportResult Import(
        Stream input,
        PocExchangeRuleSet rules,
        string workbookName,
        PocXlsxExchangeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(limits);
        if (!input.CanRead)
        {
            throw new ArgumentException("XLSX输入流必须可读。", nameof(input));
        }
        if (limits.MaxFileBytes <= 0 || limits.MaxWorksheets <= 0 || limits.MaxUsedCells <= 0)
        // 第一阶段：根据 Import 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "XLSX导入上限必须全部大于零。");
        }
        if (input.CanSeek && input.Length > limits.MaxFileBytes)
        {
            throw new PocExchangeConversionException(
                "TEMPLATE_XLSX_FILE_TOO_LARGE",
                $"XLSX文件超过 {limits.MaxFileBytes} 字节限制。");
        }

        var mapping = new RuleLookup(rules, PocExchangeDirections.ExcelToUniver);
        mapping.Require(
            "xlsx-to-univer-cell-value",
            "xlsx-to-univer-cell-type",
            "xlsx-to-univer-cell-formula",
            "xlsx-to-univer-cell-style");

        if (input.CanSeek)
        {
            input.Position = 0;
        }
        using var workbook = new XLWorkbook(input);
        if (workbook.Worksheets.Count > limits.MaxWorksheets)
        {
            throw new PocExchangeConversionException(
                "TEMPLATE_XLSX_TOO_MANY_SHEETS",
                $"XLSX包含 {workbook.Worksheets.Count} 个工作表，超过 {limits.MaxWorksheets} 个限制。");
        }

        var warnings = new List<PocExchangeDiagnostic>();
        var styles = new JObject();
        var sheetOrder = new JArray();
        var sheets = new JObject();
        var usedCellCount = 0;
        var sheetNumber = 0;

        // 第2阶段：遍历 Import 当前数据集合，逐项转换并维护处理结果。
        foreach (var sourceSheet in workbook.Worksheets)
        {
            var sheetId = $"xlsx-sheet-{++sheetNumber}";
            sheetOrder.Add(sheetId);
            sheets[sheetId] = ReadWorksheet(
                sourceSheet,
                sheetId,
                styles,
                mapping,
                warnings,
                limits,
                ref usedCellCount);
        }

        var snapshot = new JObject
        {
            ["id"] = $"xlsx-{Guid.NewGuid():N}",
            ["name"] = string.IsNullOrWhiteSpace(workbookName) ? "导入的Excel模板" : workbookName,
            ["appVersion"] = rules.UniverVersion,
            ["locale"] = "zhCN",
            ["styles"] = styles,
            ["sheetOrder"] = sheetOrder,
            ["sheets"] = sheets
        };

        return new PocXlsxImportResult(snapshot, usedCellCount, warnings);
    }

    /// <summary>把一个经过模板校验的 Univer 快照写成可由 Excel/WPS 打开的 XLSX。</summary>
    public void Export(JObject snapshot, PocExchangeRuleSet rules, Stream output)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            // 第一阶段：根据 Export 当前分支转换输入数据，并保存该阶段产生的中间结果。
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
        // 第2阶段：根据 Export 当前分支转换输入数据，并保存该阶段产生的中间结果。
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

    /// <summary>更新 PocXlsxExchangeConverter 的 Write Worksheet 状态，并同步相关业务数据。</summary>
    /// <param name="workbook">包含工作簿结构、单元格和样式信息的快照数据。</param>
    /// <param name="sheetData">本次操作使用的输入数据。</param>
    /// <param name="styles">styles 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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
        // 第一阶段：检查 WriteWorksheet 当前条件，并按匹配分支执行对应处理。
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
                    // 第2阶段：根据 WriteWorksheet 当前分支转换输入数据，并保存该阶段产生的中间结果。
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

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Worksheet 数据，并按调用约定返回结果。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="sheetId">需要定位的业务记录标识。</param>
    /// <param name="workbookStyles">workbookStyles 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="warnings">warnings 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="limits">limits 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="usedCellCount">控制数据范围或位置的数值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject ReadWorksheet(
        IXLWorksheet source,
        string sheetId,
        JObject workbookStyles,
        RuleLookup mapping,
        List<PocExchangeDiagnostic> warnings,
        PocXlsxExchangeLimits limits,
        ref int usedCellCount)
    {
        var cellData = new JObject();
        var options = XLCellsUsedOptions.All;
        var lastRow = source.LastRowUsed(options)?.RowNumber() ?? 0;
        var lastColumn = source.LastColumnUsed(options)?.ColumnNumber() ?? 0;

        foreach (var cell in source.CellsUsed(options))
        // 第一阶段：根据 ReadWorksheet 当前分支转换输入数据，并保存该阶段产生的中间结果。
        {
            usedCellCount++;
            if (usedCellCount > limits.MaxUsedCells)
            {
                throw new PocExchangeConversionException(
                    "TEMPLATE_XLSX_TOO_MANY_CELLS",
                    $"XLSX有效单元格超过 {limits.MaxUsedCells} 个限制。");
            }

            var rowIndex = cell.Address.RowNumber - 1;
            var columnIndex = cell.Address.ColumnNumber - 1;
            var row = cellData[rowIndex.ToString(CultureInfo.InvariantCulture)] as JObject;
            if (row is null)
            {
                row = new JObject();
                cellData[rowIndex.ToString(CultureInfo.InvariantCulture)] = row;
            }

            var importedCell = ReadCell(cell, workbookStyles, mapping, warnings);
            if (importedCell.HasValues)
            {
                row[columnIndex.ToString(CultureInfo.InvariantCulture)] = importedCell;
            }
        }

        // 合并范围中的非左上单元格可能只有格式；删除转换后仍为空的稀疏行，保持快照紧凑。
        // 第2阶段：遍历 ReadWorksheet 当前数据集合，逐项转换并维护处理结果。
        foreach (var emptyRow in cellData.Properties().Where(property =>
                     property.Value is JObject row && !row.HasValues).ToArray())
        {
            emptyRow.Remove();
        }

        var mergeData = new JArray(source.MergedRanges.Select(range => new JObject
        {
            ["startRow"] = range.RangeAddress.FirstAddress.RowNumber - 1,
            ["endRow"] = range.RangeAddress.LastAddress.RowNumber - 1,
            ["startColumn"] = range.RangeAddress.FirstAddress.ColumnNumber - 1,
            ["endColumn"] = range.RangeAddress.LastAddress.ColumnNumber - 1
        }));
        var rowData = ReadRows(source, lastRow, mapping);
        var columnData = ReadColumns(source, lastColumn, mapping);

        var splitRows = Math.Max(0, source.SheetView.SplitRow);
        var splitColumns = Math.Max(0, source.SheetView.SplitColumn);
        // 第3阶段：整理 ReadWorksheet 已计算的数据，并按方法契约返回最终结果。
        return new JObject
        {
            ["id"] = sheetId,
            ["name"] = source.Name,
            ["tabColor"] = ToRgb(source.TabColor) ?? string.Empty,
            ["hidden"] = source.Visibility == XLWorksheetVisibility.Visible ? 0 : 1,
            ["freeze"] = new JObject
            {
                ["xSplit"] = splitColumns,
                ["ySplit"] = splitRows,
                ["startRow"] = splitRows,
                ["startColumn"] = splitColumns
            },
            ["rowCount"] = Math.Max(50, lastRow + 20),
            ["columnCount"] = Math.Max(12, lastColumn + 5),
            ["zoomRatio"] = 1,
            ["scrollTop"] = 0,
            ["scrollLeft"] = 0,
            ["defaultColumnWidth"] = ExcelColumnWidthToPixels(source.ColumnWidth),
            ["defaultRowHeight"] = PointsToPixels(source.RowHeight, mapping),
            ["mergeData"] = mergeData,
            ["cellData"] = cellData,
            ["rowData"] = rowData,
            ["columnData"] = columnData,
            ["rowHeader"] = new JObject { ["width"] = 46, ["hidden"] = 0 },
            ["columnHeader"] = new JObject { ["height"] = 24, ["hidden"] = 0 },
            ["showGridlines"] = source.ShowGridLines ? 1 : 0,
            ["rightToLeft"] = source.RightToLeft ? 1 : 0
        };
    }

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Cell 数据，并按调用约定返回结果。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="workbookStyles">workbookStyles 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="warnings">warnings 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject ReadCell(
        IXLCell source,
        JObject workbookStyles,
        RuleLookup mapping,
        List<PocExchangeDiagnostic> warnings)
    {
        var result = new JObject();
        if (source.HasFormula)
        {
            var formulaRule = mapping["xlsx-to-univer-cell-formula"];
            var valueRule = mapping["xlsx-to-univer-cell-value"];
            var typeRule = mapping["xlsx-to-univer-cell-type"];
            var formula = source.FormulaA1;
            var (cachedValue, cachedType) = ReadFormulaCachedValue(source.CachedValue);
            SetToken(result, formulaRule.TargetPath, formula.StartsWith('=') ? formula : $"={formula}");
            // Univer 0.25.1 默认会计算没有 v 的公式。模板设计态的引用单元格仍是
            // {{detail.*}} token，因此必须同时写入 XLSX 的缓存结果；没有缓存时用数值 0
            // 占位，真正进入数据预览后再由 Univer 对已填充的数据显式重算。
            SetToken(result, valueRule.TargetPath, cachedValue);
            SetToken(result, typeRule.TargetPath, cachedType);
        }
        else if (!source.IsEmpty(XLCellsUsedOptions.Contents))
        {
            var valueRule = mapping["xlsx-to-univer-cell-value"];
            var typeRule = mapping["xlsx-to-univer-cell-type"];
            var (value, type) = ReadCellValue(source, warnings);
            if (value is not null)
            {
                SetToken(result, valueRule.TargetPath, value);
                SetToken(result, typeRule.TargetPath, type);
            }
        }

        // ClosedXML 的 IXLCell 不暴露 HasStyle；所有已使用单元格都读取样式并通过哈希去重，
        // 因此默认样式最多只会在工作簿 styles 中保存一次。
        var style = ReadStyle(source.Style, mapping);
        if (style.HasValues)
        {
            var styleId = StoreStyle(workbookStyles, style);
            var styleRule = mapping["xlsx-to-univer-cell-style"];
            SetToken(result, styleRule.TargetPath, styleId);
        }

        return result;
    }

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Formula Cached Value 数据，并按调用约定返回结果。</summary>
    /// <param name="cachedValue">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static (JToken Value, int Type) ReadFormulaCachedValue(XLCellValue cachedValue)
    {
        return cachedValue.Type switch
        {
            XLDataType.Text when !string.IsNullOrEmpty(cachedValue.GetText()) =>
                (new JValue(cachedValue.GetText()), 1),
            XLDataType.Number => (new JValue(cachedValue.GetNumber()), 2),
            XLDataType.Boolean => (new JValue(cachedValue.GetBoolean()), 3),
            XLDataType.DateTime => (new JValue(cachedValue.GetDateTime().ToOADate()), 2),
            XLDataType.TimeSpan => (new JValue(cachedValue.GetTimeSpan().TotalDays), 2),
            _ => (new JValue(0d), 2)
        };
    }

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Cell Value 数据，并按调用约定返回结果。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="warnings">warnings 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static (JToken? Value, int Type) ReadCellValue(
        IXLCell cell,
        List<PocExchangeDiagnostic> warnings)
    {
        return cell.DataType switch
        {
            XLDataType.Text => (new JValue(cell.GetString()), 1),
            XLDataType.Number => (new JValue(cell.GetDouble()), 2),
            XLDataType.Boolean => (new JValue(cell.GetBoolean()), 3),
            XLDataType.DateTime => (new JValue(cell.GetDateTime().ToOADate()), 2),
            XLDataType.TimeSpan => (new JValue(cell.GetTimeSpan().TotalDays), 2),
            XLDataType.Blank => (null, 1),
            XLDataType.Error => ReadErrorCell(cell, warnings),
            _ => (new JValue(cell.GetString()), 1)
        };
    }

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Error Cell 数据，并按调用约定返回结果。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="warnings">warnings 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static (JToken Value, int Type) ReadErrorCell(
        IXLCell cell,
        List<PocExchangeDiagnostic> warnings)
    {
        warnings.Add(new PocExchangeDiagnostic(
            "xlsx-to-univer-cell-value",
            "Excel错误值按文本导入。",
            cell.Worksheet.Name,
            cell.Address.ToString()));
        return (new JValue(cell.GetString()), 1);
    }

    /// <summary>从 ClosedXML 样式构造 Univer 样式；未启用换行时保留历史模板允许文字延伸的显示语义。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject ReadStyle(IXLStyle source, RuleLookup mapping)
    {
        var style = new JObject();
        SetMapped(style, mapping, "xlsx-to-univer-style-font-name", source.Font.FontName);
        SetMapped(style, mapping, "xlsx-to-univer-style-font-size", source.Font.FontSize);
        if (source.Font.Bold) SetMapped(style, mapping, "xlsx-to-univer-style-bold", 1);
        if (source.Font.Italic) SetMapped(style, mapping, "xlsx-to-univer-style-italic", 1);
        if (source.Font.Underline != XLFontUnderlineValues.None)
            SetMapped(style, mapping, "xlsx-to-univer-style-underline", 1);
        if (source.Font.Strikethrough) SetMapped(style, mapping, "xlsx-to-univer-style-strike", 1);

        SetMapped(style, mapping, "xlsx-to-univer-style-font-color", ToRgb(source.Font.FontColor));
        SetMapped(style, mapping, "xlsx-to-univer-style-background", ToRgb(source.Fill.BackgroundColor));
        var numberFormat = source.NumberFormat.Format;
        if (!string.IsNullOrWhiteSpace(numberFormat))
            SetMapped(style, mapping, "xlsx-to-univer-style-number-format", numberFormat);

        SetMapped(style, mapping, "xlsx-to-univer-style-horizontal", FromHorizontalAlignment(source.Alignment.Horizontal));
        SetMapped(style, mapping, "xlsx-to-univer-style-vertical", FromVerticalAlignment(source.Alignment.Vertical));
        // Excel 未启用 WrapText 对应 Univer 的 OVERFLOW；显式换行仍使用 WRAP 策略。
        SetMapped(style, mapping, "xlsx-to-univer-style-wrap", source.Alignment.WrapText ? 3 : 1);
        if (source.Alignment.TextRotation != 0)
            SetMapped(style, mapping, "xlsx-to-univer-style-rotation", source.Alignment.TextRotation);

        SetMapped(style, mapping, "xlsx-to-univer-border-top", ReadBorder(source.Border.TopBorder, source.Border.TopBorderColor));
        SetMapped(style, mapping, "xlsx-to-univer-border-right", ReadBorder(source.Border.RightBorder, source.Border.RightBorderColor));
        SetMapped(style, mapping, "xlsx-to-univer-border-bottom", ReadBorder(source.Border.BottomBorder, source.Border.BottomBorderColor));
        SetMapped(style, mapping, "xlsx-to-univer-border-left", ReadBorder(source.Border.LeftBorder, source.Border.LeftBorderColor));
        return style;
    }

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Rows 数据，并按调用约定返回结果。</summary>
    /// <param name="sheet">当前读取或修改的工作表对象。</param>
    /// <param name="lastRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject ReadRows(IXLWorksheet sheet, int lastRow, RuleLookup mapping)
    {
        var result = new JObject();
        var defaultHeight = sheet.RowHeight;
        for (var rowNumber = 1; rowNumber <= lastRow; rowNumber++)
        {
            var row = sheet.Row(rowNumber);
            if (!row.IsHidden && Math.Abs(row.Height - defaultHeight) < 0.01d)
            {
                continue;
            }

            var data = new JObject();
            if (Math.Abs(row.Height - defaultHeight) >= 0.01d)
                data["h"] = PointsToPixels(row.Height, mapping);
            if (row.IsHidden) data["hd"] = 1;
            result[(rowNumber - 1).ToString(CultureInfo.InvariantCulture)] = data;
        }
        return result;
    }

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Columns 数据，并按调用约定返回结果。</summary>
    /// <param name="sheet">当前读取或修改的工作表对象。</param>
    /// <param name="lastColumn">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject ReadColumns(IXLWorksheet sheet, int lastColumn, RuleLookup mapping)
    {
        var result = new JObject();
        var defaultWidth = sheet.ColumnWidth;
        for (var columnNumber = 1; columnNumber <= lastColumn; columnNumber++)
        {
            var column = sheet.Column(columnNumber);
            if (!column.IsHidden && Math.Abs(column.Width - defaultWidth) < 0.01d)
            {
                continue;
            }

            var data = new JObject();
            if (Math.Abs(column.Width - defaultWidth) >= 0.01d)
                data["w"] = ExcelColumnWidthToPixels(column.Width);
            if (column.IsHidden) data["hd"] = 1;
            result[(columnNumber - 1).ToString(CultureInfo.InvariantCulture)] = data;
        }
        return result;
    }

    /// <summary>更新 PocXlsxExchangeConverter 的 Set Mapped 状态，并同步相关业务数据。</summary>
    /// <param name="target">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void SetMapped(
        JObject target,
        RuleLookup mapping,
        string ruleCode,
        object? value)
    {
        var rule = mapping.GetOptional(ruleCode);
        if (rule is null || value is null)
        {
            return;
        }
        SetToken(target, rule.TargetPath, JToken.FromObject(value));
    }

    /// <summary>更新 PocXlsxExchangeConverter 的 Set Token 状态，并同步相关业务数据。</summary>
    /// <param name="target">本次转换或写入使用的数据对象。</param>
    /// <param name="relativePath">relativePath 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void SetToken(JObject target, string relativePath, object value)
    {
        var segments = relativePath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw InvalidSnapshot("映射目标路径不能为空。");
        }

        var current = target;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (current[segments[index]] is not JObject child)
            {
                child = new JObject();
                current[segments[index]] = child;
            }
            current = child;
        }
        current[segments[^1]] = value is JToken token ? token : JToken.FromObject(value);
    }

    /// <summary>根据 PocXlsxExchangeConverter 当前输入计算 Store Style 结果，并按方法声明的类型返回。</summary>
    /// <param name="workbookStyles">workbookStyles 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="style">style 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string StoreStyle(JObject workbookStyles, JObject style)
    {
        var normalized = style.ToString(Newtonsoft.Json.Formatting.None);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16]
            .ToLowerInvariant();
        var styleId = $"xlsx-{hash}";
        workbookStyles[styleId] ??= style;
        return styleId;
    }

    /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Read Border 数据，并按调用约定返回结果。</summary>
    /// <param name="style">style 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="color">color 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static JObject? ReadBorder(XLBorderStyleValues style, XLColor color)
    {
        var univerStyle = FromBorderStyle(style);
        if (univerStyle == 0)
        {
            return null;
        }

        return new JObject
        {
            ["s"] = univerStyle,
            ["cl"] = new JObject { ["rgb"] = ToRgb(color) ?? "#000000" }
        };
    }

    /// <summary>根据 PocXlsxExchangeConverter 当前输入计算 To Rgb 结果，并按方法声明的类型返回。</summary>
    /// <param name="color">color 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? ToRgb(XLColor? color)
    {
        if (color is null || color.Equals(XLColor.NoColor))
        {
            return null;
        }

        try
        {
            var value = color.Color;
            return $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        }
        catch (InvalidOperationException)
        {
            // 主题色和索引色没有稳定 RGB 时跳过；后续可通过数据库规则选择专用颜色算法。
            return null;
        }
    }

    /// <summary>负责 PocXlsxExchangeConverter 的 From Horizontal Alignment 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int FromHorizontalAlignment(XLAlignmentHorizontalValues value) => value switch
    {
        XLAlignmentHorizontalValues.Left => 1,
        XLAlignmentHorizontalValues.Center => 2,
        XLAlignmentHorizontalValues.Right => 3,
        XLAlignmentHorizontalValues.Justify => 4,
        XLAlignmentHorizontalValues.Distributed => 6,
        _ => 0
    };

    /// <summary>负责 PocXlsxExchangeConverter 的 From Vertical Alignment 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int FromVerticalAlignment(XLAlignmentVerticalValues value) => value switch
    {
        XLAlignmentVerticalValues.Top => 1,
        XLAlignmentVerticalValues.Center => 2,
        XLAlignmentVerticalValues.Bottom => 3,
        _ => 0
    };

    /// <summary>负责 PocXlsxExchangeConverter 的 From Border Style 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int FromBorderStyle(XLBorderStyleValues value) => value switch
    {
        XLBorderStyleValues.Thin => 1,
        XLBorderStyleValues.Hair => 2,
        XLBorderStyleValues.Dotted => 3,
        XLBorderStyleValues.Dashed => 4,
        XLBorderStyleValues.DashDot => 5,
        XLBorderStyleValues.DashDotDot => 6,
        XLBorderStyleValues.Double => 7,
        XLBorderStyleValues.Medium => 8,
        XLBorderStyleValues.MediumDashed => 9,
        XLBorderStyleValues.MediumDashDot => 10,
        XLBorderStyleValues.MediumDashDotDot => 11,
        XLBorderStyleValues.SlantDashDot => 12,
        XLBorderStyleValues.Thick => 13,
        _ => 0
    };

    /// <summary>根据 PocXlsxExchangeConverter 当前输入计算 Points To Pixels 结果，并按方法声明的类型返回。</summary>
    /// <param name="points">points 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static double PointsToPixels(double points, RuleLookup mapping)
    {
        var factor = (double)(mapping.GetOptional("xlsx-to-univer-row-height")?.Factor ?? 1.33333333m);
        return Math.Round(points * factor, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>负责 PocXlsxExchangeConverter 的 Excel Column Width To Pixels 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="width">width 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static double ExcelColumnWidthToPixels(double width) =>
        Math.Round(width <= 1d ? width * 12d : width * 7d + 5d, 4, MidpointRounding.AwayFromZero);

    /// <summary>更新 PocXlsxExchangeConverter 的 Write Cell 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cellData">本次操作使用的输入数据。</param>
    /// <param name="styles">styles 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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
            var value = SelectToken(cellData, valueRule.SourcePath);
            if (value is not null && value.Type is not (JTokenType.Null or JTokenType.Undefined))
            {
                WriteCellValue(cell, value);
            }
            else if (TryReadRichText(cellData["p"], out var richText))
            {
                cell.Value = richText;
            }
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

    /// <summary>更新 PocXlsxExchangeConverter 的 Write Cell Value 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
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

    /// <summary>尝试或判断 PocXlsxExchangeConverter 的 Try Read Rich Text 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="paragraph">paragraph 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="text">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool TryReadRichText(JToken? paragraph, out string text)
    {
        text = string.Empty;
        var dataStream = paragraph?["body"]?["dataStream"]?.Value<string>();
        if (string.IsNullOrEmpty(dataStream))
        {
            return false;
        }

        // 只移除文档末尾的段落换行；其余字符全部属于模板内容，不能按具体标题猜测。
        dataStream = dataStream.TrimEnd('\r', '\n');
        text = dataStream.Replace('\r', '\n');
        return text.Length > 0;
    }

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Style 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="target">本次转换或写入使用的数据对象。</param>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Merges 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="sheet">当前读取或修改的工作表对象。</param>
    /// <param name="mergeData">本次操作使用的输入数据。</param>
    /// <param name="sheetName">需要读取、校验或转换的业务值。</param>
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

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Rows 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="sheet">当前读取或修改的工作表对象。</param>
    /// <param name="rowData">本次操作使用的输入数据。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Columns 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="sheet">当前读取或修改的工作表对象。</param>
    /// <param name="columnData">本次操作使用的输入数据。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Freeze 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="sheet">当前读取或修改的工作表对象。</param>
    /// <param name="freeze">freeze 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply String 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    /// <param name="apply">apply 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Number 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    /// <param name="apply">apply 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void ApplyNumber(
        JObject source,
        RuleLookup mapping,
        string ruleCode,
        Action<double> apply)
    {
        var rule = mapping.GetOptional(ruleCode);
        var token = rule is null ? null : SelectToken(source, rule.SourcePath);
        if (TryReadNumber(token, out var value))
        {
            apply(value);
        }
    }

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Boolean 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    /// <param name="apply">apply 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void ApplyBoolean(
        JObject source,
        RuleLookup mapping,
        string ruleCode,
        Action<bool> apply)
    {
        var rule = mapping.GetOptional(ruleCode);
        var token = rule is null ? null : SelectToken(source, rule.SourcePath);
        if (token?.Type == JTokenType.Boolean)
        {
            apply(token.Value<bool>());
        }
        else if (TryReadNumber(token, out var numericValue))
        {
            apply(numericValue != 0);
        }
        else if (token?.Type == JTokenType.String &&
                 bool.TryParse(token.Value<string>(), out var booleanValue))
        {
            apply(booleanValue);
        }
    }

    /// <summary>尝试或判断 PocXlsxExchangeConverter 的 Try Read Number 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="token">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool TryReadNumber(JToken? token, out double value)
    {
        value = 0;
        if (token?.Type is JTokenType.Integer or JTokenType.Float)
        {
            value = token.Value<double>();
            return true;
        }

        return token?.Type == JTokenType.String &&
               double.TryParse(
                   token.Value<string>(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Color 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    /// <param name="apply">apply 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>执行 PocXlsxExchangeConverter 的 Apply Border 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="mapping">mapping 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
    /// <param name="target">本次转换或写入使用的数据对象。</param>
    /// <param name="side">side 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>遍历 PocXlsxExchangeConverter 当前数据并执行 Select Token 转换，生成调用方需要的结果集合。</summary>
    /// <param name="source">本次转换或写入使用的数据对象。</param>
    /// <param name="relativePath">relativePath 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>尝试或判断 PocXlsxExchangeConverter 的 Try Color 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="color">color 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool TryColor(string? value, out XLColor color)
    {
        color = XLColor.NoColor;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = NormalizeCssColor(value);
        if (value is null)
        {
            return false;
        }

        try
        {
            color = XLColor.FromHtml(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>将 PocXlsxExchangeConverter 中的 Normalize Css Color 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string? NormalizeCssColor(string value)
    {
        var trimmed = value.Trim();
        var openParenthesis = trimmed.IndexOf('(');
        if (openParenthesis <= 0 || !trimmed.EndsWith(')'))
        {
            return trimmed;
        }

        var functionName = trimmed[..openParenthesis].Trim();
        if (!functionName.Equals("rgb", StringComparison.OrdinalIgnoreCase) &&
            !functionName.Equals("rgba", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var components = trimmed[(openParenthesis + 1)..^1]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var expectedComponentCount = functionName.Equals("rgba", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
        if (components.Length != expectedComponentCount ||
            !TryCssColorComponent(components[0], out var red) ||
            !TryCssColorComponent(components[1], out var green) ||
            !TryCssColorComponent(components[2], out var blue))
        {
            return null;
        }

        if (components.Length == 4 && !TryCssAlpha(components[3]))
        {
            return null;
        }

        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    /// <summary>尝试或判断 PocXlsxExchangeConverter 的 Try Css Color Component 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="component">component 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool TryCssColorComponent(string value, out byte component)
    {
        component = 0;
        if (value.EndsWith('%') &&
            decimal.TryParse(value[..^1], NumberStyles.Number, CultureInfo.InvariantCulture, out var percentage))
        {
            if (percentage is < 0 or > 100)
            {
                return false;
            }

            component = (byte)Math.Round(percentage * 255m / 100m, MidpointRounding.AwayFromZero);
            return true;
        }

        return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out component);
    }

    /// <summary>尝试或判断 PocXlsxExchangeConverter 的 Try Css Alpha 条件，并以明确结果表示是否匹配。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static bool TryCssAlpha(string value) =>
        value.EndsWith('%')
            ? decimal.TryParse(value[..^1], NumberStyles.Number, CultureInfo.InvariantCulture, out var percentage) &&
              percentage is >= 0 and <= 100
            : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var alpha) &&
              alpha is >= 0 and <= 1;

    /// <summary>将 PocXlsxExchangeConverter 的 To Horizontal Alignment 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static XLAlignmentHorizontalValues ToHorizontalAlignment(int value) => value switch
    {
        1 => XLAlignmentHorizontalValues.Left,
        2 => XLAlignmentHorizontalValues.Center,
        3 => XLAlignmentHorizontalValues.Right,
        4 or 5 => XLAlignmentHorizontalValues.Justify,
        6 => XLAlignmentHorizontalValues.Distributed,
        _ => XLAlignmentHorizontalValues.General
    };

    /// <summary>将 PocXlsxExchangeConverter 的 To Vertical Alignment 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static XLAlignmentVerticalValues ToVerticalAlignment(int value) => value switch
    {
        1 => XLAlignmentVerticalValues.Top,
        2 => XLAlignmentVerticalValues.Center,
        3 => XLAlignmentVerticalValues.Bottom,
        _ => XLAlignmentVerticalValues.Bottom
    };

    /// <summary>将 PocXlsxExchangeConverter 的 To Border Style 输入转换为稳定格式，保留后续计算或展示需要的信息。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>负责 PocXlsxExchangeConverter 的 Pixel To Excel Column Width 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="pixels">pixels 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static double PixelToExcelColumnWidth(double pixels) =>
        Math.Max(0.1d, pixels <= 12d ? pixels / 12d : (pixels - 5d) / 7d);

    /// <summary>将 PocXlsxExchangeConverter 中的 Parse Coordinate 输入转换为调用方需要的稳定格式。</summary>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    /// <param name="label">label 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int ParseCoordinate(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var coordinate) || coordinate < 0)
        {
            throw InvalidSnapshot($"{label}坐标 '{value}' 无效。");
        }
        return coordinate;
    }

    /// <summary>执行 PocXlsxExchangeConverter 的 Required Coordinate 核心流程，并保持错误和结果语义稳定。</summary>
    /// <param name="item">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="name">需要读取、校验或转换的业务值。</param>
    /// <param name="sheetName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static int RequiredCoordinate(JObject item, string name, string sheetName)
    {
        var token = item[name];
        if (token?.Type != JTokenType.Integer || token.Value<long>() is < 0 or > int.MaxValue)
        {
            throw InvalidSnapshot($"工作表 '{sheetName}' 合并范围字段 {name} 无效。");
        }
        return token.Value<int>();
    }

    /// <summary>构建 PocXlsxExchangeConverter 所需的 Create Unique Sheet Name 内容，并保证生成结果符合调用约定。</summary>
    /// <param name="workbook">包含工作簿结构、单元格和样式信息的快照数据。</param>
    /// <param name="requestedName">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>负责 PocXlsxExchangeConverter 的 Invalid Snapshot 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>初始化 RuleLookup 实例，保存后续处理所需的依赖并建立初始状态。</summary>
        /// <param name="rules">rules 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="direction">direction 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

        /// <summary>查询或读取 PocXlsxExchangeConverter 所需的 Get Optional 数据，并按调用约定返回结果。</summary>
        /// <param name="ruleCode">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public PocExchangeRule? GetOptional(string ruleCode) =>
            _rules.GetValueOrDefault(ruleCode);

        /// <summary>执行 PocXlsxExchangeConverter 的 Require 核心流程，并保持错误和结果语义稳定。</summary>
        /// <param name="ruleCodes">ruleCodes 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        public void Require(params string[] ruleCodes)
        {
            foreach (var ruleCode in ruleCodes)
            {
                _ = this[ruleCode];
            }
        }
    }
}
