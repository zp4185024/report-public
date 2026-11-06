using UglyToad.PdfPig;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>
/// PDF 参考材料解析器（阶段四）：PdfPig 提取文本层 + 字符坐标，按行聚类、x 间隔启发切列。
/// 无文本层的扫描版 PDF 不做 OCR，直接提示"请转传图片"（spec §3）。
/// </summary>
public sealed class PocAiPdfReferenceParser
{
    /// <summary>参与解析的最大页数：参考物料通常 1~2 页，超出部分忽略并提示。</summary>
    private const int MaxPages = 5;

    /// <summary>同一文本行聚类的 Y 容差（PDF 单位）；字形高度通常 6~12，取中值的一半即可稳定聚类。</summary>
    private const double LineTolerance = 3.0;

    /// <summary>行内切列的 x 间隔阈值（相对字符平均宽度的倍数）。</summary>
    private const double ColumnGapFactor = 1.8;

    /// <summary>表头候选行的扫描范围：文本聚类后的前 3 行。</summary>
    private const int HeaderProbeLines = 3;

    /// <summary>解析 PDF 字节为统一参考材料契约；无文本层/无表头都返回带警告的材料而非异常。</summary>
    /// <param name="content">PDF 文件字节。</param>
    /// <param name="fileName">原始文件名（进审计与提示词展示）。</param>
    /// <returns>参考材料；无文本层时 Columns 为空并带"请转传图片"警告。</returns>
    public PocAiReferenceMaterial Parse(byte[] content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        var warnings = new List<string>();
        string? title = null;
        var lines = new List<(double Y, List<(double X, double Width, string Text)> Cells)>();

        using (var document = PdfDocument.Open(content))
        {
            var pageCount = Math.Min(document.NumberOfPages, MaxPages);
            if (document.NumberOfPages > MaxPages)
            {
                warnings.Add($"PDF 共 {document.NumberOfPages} 页，仅解析前 {MaxPages} 页。");
            }

            foreach (var pageIndex in Enumerable.Range(1, pageCount))
            {
                var page = document.GetPage(pageIndex);
                var letters = page.Letters;
                if (letters.Count == 0)
                {
                    continue;
                }

                // 第一阶段：按 Y 坐标聚类成文本行（容差内视为同一行），行内按 X 升序。
                foreach (var lineGroup in letters
                    .GroupBy(letter => Math.Round(letter.Location.Y / LineTolerance))
                    .OrderByDescending(group => group.Max(letter => letter.Location.Y)))
                {
                    var ordered = lineGroup.OrderBy(letter => letter.Location.X).ToList();
                    var text = string.Concat(ordered.Select(letter => letter.Value));
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    // 记录页序：越靠前的页越可能是表头所在页；行内先暂存整行文本，切列在探测到表头行后进行。
                    var y = ordered.Average(letter => letter.Location.Y);
                    if (lines.Count == 0 && title is null)
                    {
                        title = text.Trim();
                    }
                    lines.Add((y, SplitIntoCells(ordered)));
                }
            }
        }

        if (lines.Count == 0)
        {
            warnings.Add("PDF 没有文本层（扫描版/图片型 PDF），不做 OCR；请把关键页截图后转传图片重试。");
            return new PocAiReferenceMaterial("pdf", fileName, null, [], warnings);
        }

        // 第二阶段：表头行探测——前 3 行里取切出的单元格最多的行（≥2 个单元格才算表）。
        var header = lines.Take(HeaderProbeLines)
            .Select(line => line.Cells)
            .OrderByDescending(cells => cells.Count)
            .FirstOrDefault(cells => cells.Count >= 2);
        if (header is null || header.Count < 2)
        {
            warnings.Add("前 3 行未能启发式切出 ≥2 列，无法提炼参考列；建议改传 Excel 或图片。");
            return new PocAiReferenceMaterial("pdf", fileName, title, [], warnings);
        }

        // 第三段：列清单 = 表头行切列结果，列序即 x 顺序；备注留空（PDF 无类型信息）。
        var columns = header
            .Select(cell => new PocAiReferenceColumn(cell.Text.Trim(), null))
            .Where(column => column.Name.Length > 0)
            .ToList();
        var paramsHint = lines.Take(Math.Min(3, lines.Count))
            .Select(line => string.Join(' ', line.Cells.Select(cell => cell.Text)))
            .FirstOrDefault(text => text.Contains("日") || text.Contains("期间") || text.Contains("年"));
        var totalsHint = lines
            .Select(line => string.Join(' ', line.Cells.Select(cell => cell.Text)))
            .FirstOrDefault(text => text.Contains("合计") || text.Contains("小计") || text.Contains("总计"));
        return new PocAiReferenceMaterial("pdf", fileName, title, columns, warnings, paramsHint, totalsHint);
    }

    /// <summary>
    /// 行内切列：以空格字形为分隔——连续 ≥ColumnGapFactor（默认 2）个空格视为列边界，
    /// 单个空格保留在单元格内部；段内字符拼接为单元格文本并去首尾空白。
    /// </summary>
    /// <param name="orderedLetters">已按 X 升序排列的整行字符。</param>
    /// <returns>切列后的单元格列表（x 起点、宽度、文本），保持 x 顺序。</returns>
    private static List<(double X, double Width, string Text)> SplitIntoCells(
        IReadOnlyList<UglyToad.PdfPig.Content.Letter> orderedLetters)
    {
        var cells = new List<(double X, double Width, string Text)>();
        var segmentStart = -1;
        var spaceRun = 0;
        for (var index = 0; index <= orderedLetters.Count; index++)
        {
            var isSpace = index < orderedLetters.Count && orderedLetters[index].Value == " ";
            var isEnd = index == orderedLetters.Count;
            if (!isEnd && !isSpace)
            {
                // 非空格字符：段未开始则开新段；空格连击计数清零。
                if (segmentStart < 0)
                {
                    segmentStart = index;
                }
                spaceRun = 0;
                continue;
            }

            if (isSpace)
            {
                spaceRun++;
            }

            // 列边界：行结束，或连续空格数达到阈值且有未收尾的段。
            // 段尾 = 本轮空格连击的起始位置（index - spaceRun + 1），保证段内不含分隔空格。
            if (segmentStart >= 0 && (isEnd || spaceRun >= ColumnGapFactor))
            {
                var segmentEnd = isEnd ? index : index - spaceRun + 1;
                var segment = orderedLetters.Skip(segmentStart).Take(segmentEnd - segmentStart).ToList();
                var text = string.Concat(segment.Select(letter => letter.Value)).Trim();
                if (text.Length > 0)
                {
                    cells.Add((segment[0].Location.X, segment.Max(letter => letter.Width), text));
                }
                segmentStart = -1;
                spaceRun = 0;
            }
        }
        return cells;
    }
}
