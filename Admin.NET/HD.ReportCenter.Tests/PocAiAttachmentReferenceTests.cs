using System.Text;
using ClosedXML.Excel;
using HD.ReportCenter.Application.CoreValidation;
using HD.ReportCenter.Application.CoreValidation.AiAssistant;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

/// <summary>
/// 阶段四附件参考测试：Excel/PDF 解析契约、附件校验拒绝、视觉降级与「参考材料」注入系统提示词。
/// Excel 用 ClosedXML 在内存构造；PDF 用手工拼装的最小单页文档（base14 字体仅支持 ASCII，
/// 解析器的列提取逻辑与语言无关，中文解析由 Excel 用例与真实 E2E S7 覆盖）。
/// 断言落在会话参考材料（领域态）而非线路字节——线路已是 ASCII 转义形态，中文在载荷中以 \uXXXX 出现。
/// </summary>
public sealed class PocAiAttachmentReferenceTests
{
    /// <summary>构造含标题合并行 + 前置空行的表头在第 3 行的 xlsx 字节，列序刻意与常规不同。</summary>
    /// <returns>xlsx 文件字节。</returns>
    private static byte[] BuildReferenceExcel()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("费用汇总");
        // 行 1：标题（合并 A1:D1，探测合并摘要与标题）；行 2：空行；行 3：表头（列序刻意乱序：类别→单据数→金额→科室）。
        worksheet.Cell(1, 1).Value = "门诊费用月度汇总（示例）";
        worksheet.Range("A1:D1").Merge();
        worksheet.Cell(3, 1).Value = "费用类别";
        worksheet.Cell(3, 2).Value = "单据数";
        worksheet.Cell(3, 3).Value = "金额合计";
        worksheet.Cell(3, 4).Value = "科室";
        worksheet.Cell(4, 1).Value = "西药费";
        worksheet.Cell(4, 2).Value = 12;
        worksheet.Cell(4, 3).Value = 356.5;
        worksheet.Cell(4, 4).Value = "内科";
        worksheet.Cell(5, 1).Value = "检查费";
        worksheet.Cell(5, 2).Value = 3;
        worksheet.Cell(5, 3).Value = 120.0;
        worksheet.Cell(5, 4).Value = "外科";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 构造带文本层的最小 PDF 字节：手工拼装单页 PDF（Helvetica + BT/Tj 文本对象），
    /// 文本用 ASCII（base14 字体不含中文字形）；解析器的列提取逻辑与语言无关，中文解析由 Excel 用例覆盖。
    /// </summary>
    /// <param name="lines">按行写入的文本（行内多列用多个空格分隔）。</param>
    /// <returns>PDF 文件字节。</returns>
    private static byte[] BuildTextPdf(params string[] lines)
    {
        var content = string.Join("\n", lines.Select((line, index) =>
            $"BT /F1 12 Tf 50 {760 - (index * 20)} Td ({line}) Tj ET"));
        var objects = new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            [4] = $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            [5] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write("%PDF-1.4\n");
            var offsets = new Dictionary<int, int>();
            for (var index = 1; index < objects.Count + 1; index++)
            {
                offsets[index] = (int)stream.Position;
                writer.Write($"{index} 0 obj\n{objects[index]}\nendobj\n");
            }
            var xrefStart = (int)stream.Position;
            writer.Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            for (var index = 1; index < objects.Count + 1; index++)
            {
                writer.Write($"{offsets[index]:D10} 00000 n \n");
            }
            writer.Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");
        }
        return stream.ToArray();
    }

    /// <summary>解析 Excel 参考：标题、表头行探测（第 3 行）、列序与合并摘要全部按解析器契约返回。</summary>
    [Fact]
    public void Parse_excel_detects_header_order_and_merged_summary()
    {
        var material = new PocAiExcelReferenceParser().Parse(BuildReferenceExcel(), "费用参考.xlsx");

        Assert.Equal("excel", material.SourceKind);
        Assert.Equal("门诊费用月度汇总（示例）", material.Title);
        // 列序跟随参考（类别→单据数→金额→科室），列名照抄表头。
        Assert.Equal(
            ["费用类别", "单据数", "金额合计", "科室"],
            material.Columns.Select(column => column.Name).ToArray());
        // 采样类型倾向：第 1 列文本、第 2 列数值。
        Assert.Equal("文本", material.Columns[0].Note);
        Assert.Equal("数值", material.Columns[1].Note);
        // 合并单元格摘要进入警告。
        Assert.Contains(material.Warnings, warning => warning.Contains("已合并"));
    }

    /// <summary>解析无表头 Excel（前 3 行均无 ≥2 个文本单元格）：返回空列清单与探测失败警告，不抛异常。</summary>
    [Fact]
    public void Parse_excel_without_header_returns_warning_material()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("空表");
        worksheet.Cell(1, 1).Value = 1;
        worksheet.Cell(2, 1).Value = 2;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var material = new PocAiExcelReferenceParser().Parse(stream.ToArray(), "无表头.xlsx");

        Assert.Equal("excel", material.SourceKind);
        Assert.Empty(material.Columns);
        Assert.Contains(material.Warnings, warning => warning.Contains("未探测到表头"));
    }

    /// <summary>解析带文本层 PDF：标题取最上方文本行，表头行启发切列得到三列且列序与 x 顺序一致。</summary>
    [Fact]
    public void Parse_pdf_extracts_text_columns()
    {
        var material = new PocAiPdfReferenceParser().Parse(BuildTextPdf(
            "Department Fee Summary Example",
            "dept        category        amount",
            "internalmedicine   drugfee   356.50"), "费用参考.pdf");

        Assert.Equal("pdf", material.SourceKind);
        // PDF y 轴向上：y 最大（页面最上方）的行是标题行。
        Assert.Equal("Department Fee Summary Example", material.Title);
        Assert.True(material.Columns.Count >= 3, $"应至少切出 3 列，实际 {material.Columns.Count} 列；警告=[{string.Join("|", material.Warnings)}]；标题={material.Title ?? "无"}。");
        Assert.Equal("dept", material.Columns[0].Name);
        Assert.Contains("category", material.Columns.Select(column => column.Name));
        Assert.Contains("amount", material.Columns.Select(column => column.Name));
    }

    /// <summary>解析无文本层 PDF（空白页）：不做 OCR，返回空列清单并提示转传图片。</summary>
    [Fact]
    public void Parse_pdf_without_text_layer_warns_image_hint()
    {
        var material = new PocAiPdfReferenceParser().Parse(BuildTextPdf(Array.Empty<string>()), "扫描件.pdf");

        Assert.Equal("pdf", material.SourceKind);
        Assert.Empty(material.Columns);
        Assert.Contains(material.Warnings, warning => warning.Contains("转传图片"));
    }

    /// <summary>附件校验：不支持的类型、图片超 5MB、文档超 10MB 分别抛出 AI_ATTACHMENT_INVALID（Code 承载稳定错误码）。</summary>
    [Fact]
    public void Validate_attachment_rejects_unsupported_and_oversized()
    {
        var unsupported = Assert.Throws<PocDatasetConfigurationException>(
            () => PocAiReferenceMaterial.Validate(new PocAiChatAttachment("报告.docx", "application/msword", 10, "aGk=")));
        Assert.Equal("AI_ATTACHMENT_INVALID", unsupported.Code);
        Assert.Contains("类型不受支持", unsupported.Message);

        var oversizedImage = Assert.Throws<PocDatasetConfigurationException>(
            () => PocAiReferenceMaterial.Validate(new PocAiChatAttachment("截图.png", "image/png", 5 * 1024 * 1024 + 1, "aGk=")));
        Assert.Equal("AI_ATTACHMENT_INVALID", oversizedImage.Code);
        Assert.Contains("5MB", oversizedImage.Message);

        var oversizedDoc = Assert.Throws<PocDatasetConfigurationException>(
            () => PocAiReferenceMaterial.Validate(new PocAiChatAttachment("大表.xlsx", "application/octet-stream", 10 * 1024 * 1024 + 1, "aGk=")));
        Assert.Equal("AI_ATTACHMENT_INVALID", oversizedDoc.Code);
        Assert.Contains("10MB", oversizedDoc.Message);
    }

    /// <summary>视觉分析：合法 JSON 回复映射为参考材料（列序、标题、合计提示）。</summary>
    [Fact]
    public async Task Vision_analyzer_maps_json_reply_to_material()
    {
        var replies = """
            {"choices":[{"finish_reason":"stop","index":0,"message":{"content":"```json\n{\"title\":\"科室费用\",\"columns\":[{\"name\":\"科室\"},{\"name\":\"费用类别\",\"note\":\"文本\"},{\"name\":\"金额\"}],\"totalsHint\":\"表尾含合计行\",\"warnings\":[\"金额列小字\"]}"}}]}
            """;
        var handler = new ScriptedLlmHandler();
        handler.EnqueueJson(replies);
        var analyzer = new PocAiVisionAnalyzer(new PocLlmClient(new HttpClient(handler)));

        var material = await analyzer.AnalyzeAsync(
            "aGk=", "image/png", "截图.png", new PocLlmEndpoint("http://llm.test/v1", "sk-x", "vision-model"));

        Assert.Equal("image", material.SourceKind);
        Assert.Equal("科室费用", material.Title);
        Assert.Equal(["科室", "费用类别", "金额"], material.Columns.Select(column => column.Name).ToArray());
        Assert.Equal("文本", material.Columns[1].Note);
        Assert.Equal("表尾含合计行", material.TotalsHint);
        // 视觉请求应包含 image_url content part（多模态 content 数组形态）。
        Assert.Contains("image_url", handler.RequestBodies[0]);
    }

    /// <summary>视觉降级：HTTP 500 与非法 JSON 两种失败均抛 PocAiVisionException，供对话服务按纯文本继续。</summary>
    [Theory]
    [InlineData(500, """{"error":"boom"}""")]
    [InlineData(200, "绝对不是 JSON 的一段话。")]
    public async Task Vision_analyzer_degrades_on_failure(int statusCode, string body)
    {
        var handler = new ScriptedLlmHandler();
        handler.EnqueueError(statusCode, body);
        var analyzer = new PocAiVisionAnalyzer(new PocLlmClient(new HttpClient(handler)));

        var exception = await Assert.ThrowsAsync<PocAiVisionException>(() => analyzer.AnalyzeAsync(
            "aGk=", "image/png", "截图.png", new PocLlmEndpoint("http://llm.test/v1", "sk-x", "vision-model")));

        Assert.Contains("已按纯文本继续", exception.Message);
    }

    /// <summary>对话注入：带 xlsx 附件的 chat 把「参考材料」写进会话参考材料并落 attachment_reference 元信息审计。</summary>
    [Fact]
    public async Task ChatAsync_with_excel_attachment_stores_reference_and_audits_metadata()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var excel = BuildReferenceExcel();
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("已按参考列设计模板草稿。"));

        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(
            null, "ai_main", "请按参考 Excel 的列名列序做一个科室费用汇总模板",
            [new PocAiChatAttachment(
                "费用参考.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                excel.Length,
                Convert.ToBase64String(excel))]));

        Assert.False(result.Expired);
        // 领域态断言：会话参考材料携带参考列清单（列序与 Excel 一致），供系统提示词注入。
        var session = host.Sessions.Get(result.SessionId);
        Assert.NotNull(session);
        var materials = session!.GetReferenceMaterials();
        Assert.Single(materials);
        Assert.Equal("费用参考.xlsx", materials[0].FileName);
        Assert.Equal(
            ["费用类别", "单据数", "金额合计", "科室"],
            materials[0].Columns.Select(column => column.Name).ToArray());
        // 元信息审计：本会话恰好一行 attachment_reference，行数位=命中列数；内容不含 base64。
        var auditRows = await host.CountAuditAsync(result.SessionId);
        Assert.Equal(1, auditRows);
    }

    /// <summary>图片附件视觉失败降级：会话参考材料带降级说明（随提示词注入），主循环继续产出回复且不抛异常。</summary>
    [Fact]
    public async Task ChatAsync_with_image_attachment_degrades_when_vision_fails()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        // 脚本：第一次 LLM 请求 = 视觉分析（返回 500），第二次 = 主模型工具循环（返回结论文本）。
        host.LlmHandler.EnqueueError(500, """{"error":"vision unavailable"}""");
        host.LlmHandler.EnqueueJson(PocAiLlmFixtures.ContentResponse("图片未能解析，请改传 Excel 或重新截图。"));

        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var result = await host.ChatService.ChatAsync(new PocAiChatRequest(
            null, "ai_main", "参考这张截图设计模板",
            [new PocAiChatAttachment("报表截图.png", "image/png", pngBytes.Length, Convert.ToBase64String(pngBytes))]));

        // 降级语义：会话继续（拿到最终回复），参考材料带降级说明供提示词注入。
        Assert.Contains("未能解析", result.Messages.Last().Content ?? string.Empty, StringComparison.Ordinal);
        var session = host.Sessions.Get(result.SessionId);
        Assert.NotNull(session);
        var materials = session!.GetReferenceMaterials();
        Assert.Single(materials);
        Assert.Equal("image", materials[0].SourceKind);
        Assert.Contains(materials[0].Warnings, warning => warning.Contains("视觉模型解析失败"));
        // 视觉请求确实发出（image_url 多模态形态），失败后主循环仍被调用。
        Assert.Equal(2, host.LlmHandler.RequestCount);
        Assert.Contains("image_url", host.LlmHandler.RequestBodies[0]);
    }

    /// <summary>超限拦截：超过 3 个附件抛 AI_ATTACHMENT_INVALID，且不建会话不调 LLM。</summary>
    [Fact]
    public async Task ChatAsync_with_more_than_three_attachments_rejects()
    {
        using var host = new PocAiTestHost();
        await host.EnableAiAsync();
        var attachments = Enumerable.Range(0, 4).Select(index => new PocAiChatAttachment(
            $"参考{index}.xlsx", "application/octet-stream", 10, "aGk=")).ToList();

        var exception = await Assert.ThrowsAsync<PocDatasetConfigurationException>(() =>
            host.ChatService.ChatAsync(new PocAiChatRequest(null, "ai_main", "参考这些附件", attachments)));

        Assert.Equal("AI_ATTACHMENT_INVALID", exception.Code);
        Assert.Equal(0, host.LlmHandler.RequestCount);
    }
}
