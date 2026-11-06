using ClosedXML.Excel;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>
/// Excel（xlsx）参考材料解析器（阶段四）：ClosedXML 纯后端解析，不依赖 LLM。
/// 取首个非空工作表，前 3 行探测表头行，输出列名+列序；合并单元格输出摘要；
/// 数据行采样 ≤5 行仅推断取值类型倾向，不外发数据内容。
/// </summary>
public sealed class PocAiExcelReferenceParser
{
    /// <summary>表头探测的候选行范围：只在首 3 行内找表头（spec §3）。</summary>
    private const int HeaderProbeRows = 3;

    /// <summary>数据行采样上限：仅用于推断列类型倾向，不进入参考材料内容。</summary>
    private const int SampleDataRows = 5;

    /// <summary>合并单元格摘要最多输出的条数，防止巨型合并区刷屏。</summary>
    private const int MaxMergedSummaries = 3;

    /// <summary>解析 xlsx 字节为统一参考材料契约；表结构与列序是主要产出，数据值不外发。</summary>
    /// <param name="content">xlsx 文件字节。</param>
    /// <param name="fileName">原始文件名（进审计与提示词展示）。</param>
    /// <returns>参考材料；工作表全空时返回带警告的空材料，不抛异常。</returns>
    public PocAiReferenceMaterial Parse(byte[] content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var stream = new MemoryStream(content);
        using var workbook = new XLWorkbook(stream);

        // 第一阶段：找首个非空工作表（有可见值或结构的工作表）。
        var worksheet = workbook.Worksheets
            .FirstOrDefault(sheet => sheet.Visibility == XLWorksheetVisibility.Visible && !IsEffectivelyEmpty(sheet));
        if (worksheet is null)
        {
            return new PocAiReferenceMaterial(
                "excel", fileName, null, [], ["工作簿中没有可解析的非空工作表。"]);
        }

        var warnings = new List<string>();
        var title = ReadTitle(worksheet, warnings);

        // 第二阶段：表头行探测——在前 3 行里选"非空单元格最多"的行；全部为空则视为无表头。
        var (headerRowIndex, headerCells) = DetectHeaderRow(worksheet);
        if (headerRowIndex < 0)
        {
            warnings.Add("前 3 行均未探测到表头（无文本单元格），无法提炼参考列。");
            return new PocAiReferenceMaterial(
                "excel", fileName, title, [], warnings, MergedSummary(worksheet, warnings));
        }

        // 第三阶段：列清单 = 表头行非空文本单元格，列序即单元格顺序；类型倾向由 ≤5 行采样推断。
        var columns = headerCells
            .Select(cell => new PocAiReferenceColumn(cell.GetString().Trim(), InferTypeNote(worksheet, cell.Address.ColumnNumber, headerRowIndex)))
            .ToList();
        var paramsHint = DetectParamsHint(worksheet, headerRowIndex);
        var totalsHint = DetectTotalsHint(worksheet, headerRowIndex);
        var material = new PocAiReferenceMaterial(
            "excel", fileName, title, columns, warnings, paramsHint, totalsHint);
        _ = MergedSummary(worksheet, warnings);
        return material;
    }

    /// <summary>判断工作表是否有效为空：所有单元格都没有值即视为空。</summary>
    /// <param name="worksheet">目标工作表。</param>
    /// <returns>为空返回 true。</returns>
    private static bool IsEffectivelyEmpty(IXLWorksheet worksheet)
    {
        return !worksheet.CellsUsed().Any();
    }

    /// <summary>读标题：取 A1 或首行首个非空文本（前 3 行范围内），作为参考物料标题；没有则空。</summary>
    /// <param name="worksheet">目标工作表。</param>
    /// <param name="warnings">警告收集器。</param>
    /// <returns>标题文本或 null。</returns>
    private static string? ReadTitle(IXLWorksheet worksheet, List<string> warnings)
    {
        foreach (var row in worksheet.Rows(1, HeaderProbeRows))
        {
            var text = row.Cells().FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell.GetString()))?.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            // 表头探测若选中了含标题的首行，标题与表头同源属于正常现象，无需额外警告。
            return text.Trim();
        }
        return null;
    }

    /// <summary>表头行探测：前 3 行内取非空文本单元格最多的行；最多行数不足 2 个文本单元格视为无表头。</summary>
    /// <param name="worksheet">目标工作表。</param>
    /// <returns>表头行号（1 基；未找到为 -1）与该行非空文本单元格列表。</returns>
    private static (int HeaderRowIndex, List<IXLCell> HeaderCells) DetectHeaderRow(IXLWorksheet worksheet)
    {
        var bestRow = -1;
        List<IXLCell> bestCells = [];
        foreach (var row in worksheet.Rows(1, HeaderProbeRows))
        {
            var cells = row.Cells()
                .Where(cell => cell.Address.ColumnNumber <= 64 && !string.IsNullOrWhiteSpace(cell.GetString()))
                .ToList();
            if (cells.Count > bestCells.Count)
            {
                bestCells = cells;
                bestRow = row.RowNumber();
            }
        }

        return bestCells.Count >= 2 ? (bestRow, bestCells) : (-1, []);
    }

    /// <summary>推断列的取值类型倾向：采样表头行之后 ≤5 行非空值，归纳为 数值/日期/布尔/文本；无样本返回 null。</summary>
    /// <param name="worksheet">目标工作表。</param>
    /// <param name="columnNumber">列号（1 基）。</param>
    /// <param name="headerRowIndex">表头行号（1 基），采样从下一行开始。</param>
    /// <returns>类型倾向文本或 null。</returns>
    private static string? InferTypeNote(IXLWorksheet worksheet, int columnNumber, int headerRowIndex)
    {
        var hasNumber = false;
        var hasDate = false;
        var hasBool = false;
        var hasText = false;
        var sampled = 0;
        foreach (var row in worksheet.Rows(headerRowIndex + 1, headerRowIndex + SampleDataRows))
        {
            var cell = row.Cell(columnNumber);
            if (cell.IsEmpty())
            {
                continue;
            }
            sampled++;
            switch (cell.DataType)
            {
                case XLDataType.Number:
                    hasNumber = true;
                    break;
                case XLDataType.DateTime:
                case XLDataType.TimeSpan:
                    hasDate = true;
                    break;
                case XLDataType.Boolean:
                    hasBool = true;
                    break;
                default:
                    hasText = true;
                    break;
            }
        }

        // 第一段：采样表头行之后 ≤5 行非空值，按 ClosedXML 单元格类型归类累计。
        if (sampled == 0)
        {
            return null;
        }
        // 第二段：按累计结果归纳类型倾向；混合类型一律按文本处理。
        if (hasNumber && !hasText && !hasDate)
        {
            return "数值";
        }
        if (hasDate && !hasText && !hasNumber)
        {
            return "日期";
        }
        if (hasBool && !hasText && !hasNumber && !hasDate)
        {
            return "布尔";
        }
        return "文本";
    }

    /// <summary>参数提示：表头行之前或首行文本中出现的"日期/期间/筛选"关键词行。</summary>
    /// <param name="worksheet">目标工作表。</param>
    /// <param name="headerRowIndex">表头行号。</param>
    /// <returns>参数提示文本或 null。</returns>
    private static string? DetectParamsHint(IXLWorksheet worksheet, int headerRowIndex)
    {
        foreach (var row in worksheet.Rows(1, Math.Max(1, headerRowIndex - 1)))
        {
            var text = string.Join(' ', row.Cells()
                .Where(cell => !string.IsNullOrWhiteSpace(cell.GetString()))
                .Select(cell => cell.GetString().Trim()));
            if (text.Contains("日") || text.Contains("期间") || text.Contains("月") || text.Contains("年"))
            {
                return text.Length <= 120 ? text : text[..120];
            }
        }
        return null;
    }

    /// <summary>合计提示：表头行之后扫描 ≤10 行中出现的"合计/小计/总计"文本。</summary>
    /// <param name="worksheet">目标工作表。</param>
    /// <param name="headerRowIndex">表头行号。</param>
    /// <returns>合计提示文本或 null。</returns>
    private static string? DetectTotalsHint(IXLWorksheet worksheet, int headerRowIndex)
    {
        foreach (var row in worksheet.Rows(headerRowIndex + 1, headerRowIndex + 10))
        {
            var text = string.Join(' ', row.Cells()
                .Where(cell => !string.IsNullOrWhiteSpace(cell.GetString()))
                .Select(cell => cell.GetString().Trim()));
            if (text.Contains("合计") || text.Contains("小计") || text.Contains("总计"))
            {
                return text.Length <= 120 ? text : text[..120];
            }
        }
        return null;
    }

    /// <summary>合并单元格摘要：最多输出 3 条形如"A1:C1 已合并"的提示，追加进警告列表。</summary>
    /// <param name="worksheet">目标工作表。</param>
    /// <param name="warnings">警告收集器。</param>
    /// <returns>固定返回 null（保留调用点语义：仅追加警告副作用）。</returns>
    private static string? MergedSummary(IXLWorksheet worksheet, List<string> warnings)
    {
        var merged = worksheet.MergedRanges.Take(MaxMergedSummaries).ToList();
        foreach (var range in merged)
        {
            warnings.Add($"{range.RangeAddress} 已合并。");
        }
        if (worksheet.MergedRanges.Count > MaxMergedSummaries)
        {
            warnings.Add($"另有 {worksheet.MergedRanges.Count - MaxMergedSummaries} 处合并单元格未逐一列出。");
        }
        return null;
    }
}
