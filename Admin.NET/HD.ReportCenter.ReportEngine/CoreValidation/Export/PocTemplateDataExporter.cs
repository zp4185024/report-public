using ClosedXML.Excel;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.ReportEngine.CoreValidation.Export;

/// <summary>普通导出超过模板阈值或超时时抛出，调用方应提示用户改用大文件导出。</summary>
public sealed class PocNormalExportLimitException : Exception
{
    public PocNormalExportLimitException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// 使用已保存模板生成完整数据 XLSX。普通模式允许在阈值内收集数据，以换取 ClosedXML 的完整模板样式兼容。
/// </summary>
public sealed class PocTemplateDataExporter
{
    private readonly PocXlsxExchangeConverter _exchangeConverter;

    public PocTemplateDataExporter()
        : this(new PocXlsxExchangeConverter())
    {
    }

    public PocTemplateDataExporter(PocXlsxExchangeConverter exchangeConverter)
    {
        _exchangeConverter = exchangeConverter;
    }

    /// <summary>把所有分页数据写入一个工作簿；调用方继续拥有 destination。</summary>
    public async Task WriteNormalAsync(
        Stream destination,
        PocTemplateDataExportPlan plan,
        IAsyncEnumerable<IReadOnlyList<PocDataRow>> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pages);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("普通导出输出流必须可写。", nameof(destination));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(plan.Export.NormalTimeoutSeconds));
        var rows = await MaterializeRowsAsync(plan, pages, timeout.Token, cancellationToken);

        await using var templateStream = new MemoryStream();
        _exchangeConverter.Export(plan.Snapshot, plan.Mappings, templateStream);
        templateStream.Position = 0;
        using var workbook = new XLWorkbook(templateStream);

        ReplaceParameterBindings(workbook, plan);
        ExpandDetailRows(workbook, plan, rows, timeout.Token);

        if (destination.CanSeek)
        {
            destination.Position = 0;
            destination.SetLength(0);
        }
        try
        {
            workbook.SaveAs(destination, validate: true, evaluateFormulae: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimeoutExceeded();
        }
        if (destination.CanSeek)
        {
            destination.Position = 0;
        }
    }

    private static async Task<IReadOnlyList<PocDataRow>> MaterializeRowsAsync(
        PocTemplateDataExportPlan plan,
        IAsyncEnumerable<IReadOnlyList<PocDataRow>> pages,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        var rows = new List<PocDataRow>();
        var columnsPerRow = Math.Max(1, PocReportCatalog.GetDefinition().Columns.Count);
        try
        {
            await foreach (var page in pages.WithCancellation(timeoutToken))
            {
                foreach (var row in page)
                {
                    timeoutToken.ThrowIfCancellationRequested();
                    if (rows.Count >= plan.Export.NormalMaxRows)
                    {
                        throw new PocNormalExportLimitException(
                            "NORMAL_EXPORT_ROW_LIMIT_EXCEEDED",
                            $"数据超过普通导出 {plan.Export.NormalMaxRows} 行限制，请使用大文件导出。");
                    }
                    if ((long)(rows.Count + 1) * columnsPerRow > plan.Export.NormalMaxCells)
                    {
                        throw new PocNormalExportLimitException(
                            "NORMAL_EXPORT_CELL_LIMIT_EXCEEDED",
                            $"数据超过普通导出 {plan.Export.NormalMaxCells} 个单元格限制，请使用大文件导出。");
                    }
                    rows.Add(row);
                }
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw TimeoutExceeded();
        }

        return rows;
    }

    private static void ReplaceParameterBindings(
        XLWorkbook workbook,
        PocTemplateDataExportPlan plan)
    {
        var parameters = PocReportCatalog.Execute(plan.Query with { Page = 1 }).Parameters;
        foreach (var binding in plan.Binding.ParameterBindings)
        {
            var worksheet = ResolveWorksheet(workbook, plan, binding.SheetId);
            var cell = worksheet.Cell(binding.Row + 1, binding.Column + 1);
            var token = $"{{{{params.{binding.Code}}}}}";
            var current = cell.GetString();
            cell.Value = current.Replace(
                token,
                ResolveParameter(parameters, binding.Code),
                StringComparison.Ordinal);
        }
    }

    private static void ExpandDetailRows(
        XLWorkbook workbook,
        PocTemplateDataExportPlan plan,
        IReadOnlyList<PocDataRow> rows,
        CancellationToken cancellationToken)
    {
        var detail = plan.Binding.DetailBinding;
        if (detail is null)
        {
            if (rows.Count > 0)
            {
                throw new InvalidOperationException("模板没有明细绑定，不能写入导出数据。");
            }
            return;
        }

        var worksheet = ResolveWorksheet(workbook, plan, detail.SheetId);
        var templateRow = detail.TemplateRow + 1;
        var lastColumn = Math.Max(
            worksheet.LastColumnUsed(XLCellsUsedOptions.All)?.ColumnNumber() ?? 1,
            detail.Fields.Max(field => field.Column + 1));
        var sourceHeight = worksheet.Row(templateRow).Height;
        var sourceFormulas = Enumerable.Range(1, lastColumn)
            .Select(column => worksheet.Cell(templateRow, column))
            .Where(cell => cell.HasFormula)
            .ToDictionary(cell => cell.Address.ColumnNumber, cell => cell.FormulaA1);
        var sourceMerges = worksheet.MergedRanges
            .Where(range =>
                range.RangeAddress.FirstAddress.RowNumber == templateRow &&
                range.RangeAddress.LastAddress.RowNumber == templateRow)
            .Select(range => (
                StartColumn: range.RangeAddress.FirstAddress.ColumnNumber,
                EndColumn: range.RangeAddress.LastAddress.ColumnNumber))
            .ToArray();
        var workbookFormulas = workbook.Worksheets
            .SelectMany(sheet => sheet.CellsUsed(XLCellsUsedOptions.Contents)
                .Where(cell => cell.HasFormula)
                .Select(cell => new WorkbookFormula(
                    sheet.Name,
                    cell.Address.RowNumber,
                    cell.Address.ColumnNumber,
                    cell.FormulaA1)))
            .ToArray();

        if (rows.Count == 0)
        {
            // 保留一条有完整样式和公式的空模板行，清除数据 token，避免导出文件残留模板占位符。
            foreach (var field in detail.Fields)
            {
                worksheet.Cell(templateRow, field.Column + 1).Clear(XLClearOptions.Contents);
            }
            return;
        }

        if (rows.Count > 1)
        {
            var insertedRowCount = rows.Count - 1;
            worksheet.Row(templateRow).InsertRowsBelow(insertedRowCount);
            AdjustFormulasAfterRowInsertion(
                workbook,
                workbookFormulas,
                worksheet.Name,
                templateRow,
                insertedRowCount);
        }

        var sourceRange = worksheet.Range(templateRow, 1, templateRow, lastColumn);
        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetRow = templateRow + index;
            if (index > 0)
            {
                sourceRange.CopyTo(worksheet.Cell(targetRow, 1));
                worksheet.Row(targetRow).Height = sourceHeight;
                foreach (var merge in sourceMerges)
                {
                    worksheet.Range(
                        targetRow,
                        merge.StartColumn,
                        targetRow,
                        merge.EndColumn).Merge();
                }
            }

            // CopyTo 对常见相对引用可以自动调整，但这里仍以解析器结果覆盖，统一普通和流式导出的公式语义。
            foreach (var formula in sourceFormulas)
            {
                var moved = PocFormulaTranslator.MoveFormula(
                    $"={formula.Value}",
                    templateRow,
                    formula.Key,
                    targetRow,
                    formula.Key);
                worksheet.Cell(targetRow, formula.Key).FormulaA1 = moved[1..];
            }

            foreach (var field in detail.Fields)
            {
                WriteDetailValue(
                    worksheet.Cell(targetRow, field.Column + 1),
                    field.Code,
                    rows[index]);
            }
        }
    }

    private static IXLWorksheet ResolveWorksheet(
        XLWorkbook workbook,
        PocTemplateDataExportPlan plan,
        string sheetId)
    {
        var sheetName = plan.Snapshot["sheets"]?[sheetId]?["name"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(sheetName) || !workbook.TryGetWorksheet(sheetName, out var worksheet))
        {
            throw new InvalidOperationException($"导出模板找不到绑定工作表 '{sheetId}'。");
        }
        return worksheet;
    }

    private static void AdjustFormulasAfterRowInsertion(
        XLWorkbook workbook,
        IReadOnlyList<WorkbookFormula> formulas,
        string insertedSheet,
        int insertAfterRow,
        int insertedRowCount)
    {
        foreach (var formula in formulas)
        {
            var worksheet = workbook.Worksheet(formula.SheetName);
            // 公式单元格自身位于插入点下方时已经随整行下移；其它工作表中的公式坐标保持不变。
            var targetRow = string.Equals(
                    formula.SheetName,
                    insertedSheet,
                    StringComparison.OrdinalIgnoreCase) &&
                formula.Row > insertAfterRow
                ? checked(formula.Row + insertedRowCount)
                : formula.Row;
            var adjusted = PocFormulaTranslator.InsertRows(
                $"={formula.FormulaA1}",
                formula.SheetName,
                formula.Row,
                formula.Column,
                insertedSheet,
                insertAfterRow,
                insertedRowCount);
            worksheet.Cell(targetRow, formula.Column).FormulaA1 = adjusted[1..];
        }
    }

    private static string ResolveParameter(PocResolvedParameters parameters, string code) => code switch
    {
        "hospitalId" => parameters.HospitalId,
        "deptId" => parameters.DeptId,
        "personId" => parameters.PersonId,
        "startDate" => parameters.StartDate,
        "endDate" => parameters.EndDate,
        _ => throw new InvalidOperationException($"导出模板包含未知参数绑定 '{code}'。")
    };

    private static void WriteDetailValue(IXLCell cell, string code, PocDataRow row)
    {
        if (!row.TryGetValue(code, out var value))
        {
            throw new InvalidOperationException($"导出数据缺少明细字段 '{code}'。");
        }

        switch (value)
        {
            case null:
            case DBNull:
                cell.Clear(XLClearOptions.Contents);
                break;
            case string text:
                cell.Value = text;
                break;
            case bool boolean:
                cell.Value = boolean;
                break;
            case DateTime dateTime:
                cell.Value = dateTime;
                break;
            case DateTimeOffset dateTimeOffset:
                cell.Value = dateTimeOffset.DateTime;
                break;
            case byte number:
                cell.Value = number;
                break;
            case sbyte number:
                cell.Value = number;
                break;
            case short number:
                cell.Value = number;
                break;
            case ushort number:
                cell.Value = number;
                break;
            case int number:
                cell.Value = number;
                break;
            case uint number:
                cell.Value = number;
                break;
            case long number:
                cell.Value = number;
                break;
            case ulong number when number <= long.MaxValue:
                cell.Value = (long)number;
                break;
            case float number:
                cell.Value = number;
                break;
            case double number:
                cell.Value = number;
                break;
            case decimal number:
                cell.Value = number;
                break;
            default:
                throw new InvalidOperationException(
                    $"明细字段 '{code}' 的类型 '{value.GetType().Name}' 不能写入 Excel。");
        }
    }

    private static PocNormalExportLimitException TimeoutExceeded() =>
        new(
            "NORMAL_EXPORT_TIMEOUT",
            "普通导出处理超时，请使用大文件导出。");

    private sealed record WorkbookFormula(
        string SheetName,
        int Row,
        int Column,
        string FormulaA1);
}
