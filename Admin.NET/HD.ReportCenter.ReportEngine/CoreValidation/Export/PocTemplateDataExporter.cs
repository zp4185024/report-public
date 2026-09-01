using ClosedXML.Excel;
using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.ReportEngine.CoreValidation.Export;

/// <summary>普通导出超过模板阈值或超时时抛出，调用方应提示用户改用大文件导出。</summary>
public sealed class PocNormalExportLimitException : Exception
{
    /// <summary>初始化 PocNormalExportLimitException 异常实例，保存错误上下文并把消息传递给基础异常类型。</summary>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="message">message 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>初始化 PocTemplateDataExporter 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public PocTemplateDataExporter()
        : this(new PocXlsxExchangeConverter())
    {
    }

    /// <summary>初始化 PocTemplateDataExporter 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    /// <param name="exchangeConverter">exchangeConverter 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>调用 PocTemplateDataExporter 的异步依赖完成 Materialize Rows Async 操作，并按当前返回类型传递处理结果。</summary>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="pages">pages 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="timeoutToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <param name="callerToken">调用受保护接口或校验请求身份所需的访问令牌。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static async Task<IReadOnlyList<PocDataRow>> MaterializeRowsAsync(
        PocTemplateDataExportPlan plan,
        IAsyncEnumerable<IReadOnlyList<PocDataRow>> pages,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        var rows = new List<PocDataRow>();
        var columnsPerRow = Math.Max(
            1,
            (plan.Definition ?? PocReportCatalog.GetDefinition()).Fields.Count);
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

    /// <summary>遍历 PocTemplateDataExporter 当前数据并执行 Replace Parameter Bindings 转换，生成调用方需要的结果集合。</summary>
    /// <param name="workbook">包含工作簿结构、单元格和样式信息的快照数据。</param>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void ReplaceParameterBindings(
        XLWorkbook workbook,
        PocTemplateDataExportPlan plan)
    {
        var parameters = plan.Parameters
            ?? PocReportCatalog.Execute(plan.Query with { Page = 1 }).Parameters;
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

    /// <summary>遍历 PocTemplateDataExporter 当前数据并执行 Expand Detail Rows 转换，生成调用方需要的结果集合。</summary>
    /// <param name="workbook">包含工作簿结构、单元格和样式信息的快照数据。</param>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="rows">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
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
            // 第一阶段：整理 ExpandDetailRows 已计算的数据，并按方法契约返回最终结果。
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
            // 第2阶段：遍历 ExpandDetailRows 当前数据集合，逐项转换并维护处理结果。
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
                    // 第3阶段：根据 ExpandDetailRows 当前分支转换输入数据，并保存该阶段产生的中间结果。
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

    /// <summary>解析并计算 PocTemplateDataExporter 的 Resolve Worksheet 结果，处理必要的边界条件。</summary>
    /// <param name="workbook">包含工作簿结构、单元格和样式信息的快照数据。</param>
    /// <param name="plan">plan 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sheetId">需要定位的业务记录标识。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

    /// <summary>遍历 PocTemplateDataExporter 当前数据并执行 Adjust Formulas After Row Insertion 转换，生成调用方需要的结果集合。</summary>
    /// <param name="workbook">包含工作簿结构、单元格和样式信息的快照数据。</param>
    /// <param name="formulas">formulas 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="insertedSheet">当前读取或修改的工作表对象。</param>
    /// <param name="insertAfterRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="insertedRowCount">控制数据范围或位置的数值。</param>
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

    /// <summary>解析并计算 PocTemplateDataExporter 的 Resolve Parameter 结果，处理必要的边界条件。</summary>
    /// <param name="parameters">parameters 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ResolveParameter(PocResolvedParameters parameters, string code) =>
        parameters.TryGetValue(code, out var value)
            ? value
            : throw new InvalidOperationException($"导出模板包含未知参数绑定 '{code}'。");

    /// <summary>更新 PocTemplateDataExporter 的 Write Detail Value 状态，并同步相关业务数据。</summary>
    /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="code">需要读取、校验或转换的业务值。</param>
    /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
    private static void WriteDetailValue(IXLCell cell, string code, PocDataRow row)
    {
        if (!row.TryGetValue(code, out var value))
        {
            throw new InvalidOperationException($"导出数据缺少明细字段 '{code}'。");
        }

        // 第一阶段：检查 WriteDetailValue 当前条件，并按匹配分支执行对应处理。
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
                // 第2阶段：根据 WriteDetailValue 当前分支转换输入数据，并保存该阶段产生的中间结果。
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

    /// <summary>负责 PocTemplateDataExporter 的 Timeout Exceeded 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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
