using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

/// <summary>
/// 低内存 Excel 导出器。直接用 ZipArchive + XmlWriter 写最小 Open XML 包，避免 DOM/工作簿对象缓存全部行。
/// 该实现服务于技术验证，样式、图片和复杂模板将在后续统一模板导出阶段补齐。
/// </summary>
public static class PocExcelExporter
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly string[] Headers =
    [
        "序号",
        "科室",
        "门诊人次",
        "出院人次",
        "合计",
        "平均值"
    ];

    /// <summary>
    /// 将明细序列流式写入 xlsx。rows 只枚举一次；progress 按批报告；取消在每行开始前检查。
    /// </summary>
    /// <param name="output">可写输出流。可定位流会先清空，调用方仍拥有该流。</param>
    /// <param name="rows">惰性明细序列，允许十万行以上数据按需产生。</param>
    /// <param name="progress">已完成数据行数回调，不包含标题和表头。</param>
    /// <param name="cancellationToken">协作式取消标记。</param>
    public static void Write(
        Stream output,
        IEnumerable<PocWorkloadRow> rows,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rows);

        if (!output.CanWrite)
        {
            throw new ArgumentException("输出流必须可写。", nameof(output));
        }

        if (output.CanSeek)
        {
            output.Position = 0;
            output.SetLength(0);
        }

        // xlsx 本质是具有固定部件关系的 ZIP 包。这里只创建 Excel 打开文件所需的最小部件集合。
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8);
        WriteEntry(archive, "[Content_Types].xml", WriteContentTypes);
        WriteEntry(archive, "_rels/.rels", WritePackageRelationships);
        WriteEntry(archive, "xl/workbook.xml", WriteWorkbook);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WriteWorkbookRelationships);
        WriteEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            writer => WriteWorksheet(writer, rows, progress, cancellationToken));
    }

    /// <summary>更新 PocExcelExporter 的 Write Worksheet 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="rows">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="progress">progress 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    private static void WriteWorksheet(
        XmlWriter writer,
        IEnumerable<PocWorkloadRow> rows,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        WriteColumns(writer);
        writer.WriteStartElement("sheetData", SpreadsheetNamespace);

        StartRow(writer, 1);
        WriteTextCell(writer, "A1", "科室工作量统计");
        writer.WriteEndElement();
        StartRow(writer, 2);
        WriteTextCell(writer, "A2", "公式由 Excel/WPS 打开文件时自动计算");
        writer.WriteEndElement();
        StartRow(writer, 3);
        WriteTextCell(writer, "A3", "核心功能验证导出");
        writer.WriteEndElement();
        StartRow(writer, 4);
        writer.WriteEndElement();
        StartRow(writer, 5);
        for (var index = 0; index < Headers.Length; index++)
        {
            WriteTextCell(writer, $"{ColumnName(index + 1)}5", Headers[index]);
        }

        writer.WriteEndElement();

        long written = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheetRow = checked((uint)(written + 6));
            StartRow(writer, sheetRow);
            WriteNumberCell(writer, $"A{sheetRow}", row.Sequence);
            WriteTextCell(writer, $"B{sheetRow}", row.DepartmentName);
            WriteNumberCell(writer, $"C{sheetRow}", row.OutpatientCount);
            WriteNumberCell(writer, $"D{sheetRow}", row.DischargeCount);
            // 只写公式表达式，不在服务器计算或写缓存结果；Excel/WPS 打开时根据 workbook calcPr 完成计算。
            WriteFormulaCell(writer, $"E{sheetRow}", $"C{sheetRow}+D{sheetRow}");
            WriteFormulaCell(writer, $"F{sheetRow}", $"AVERAGE(C{sheetRow}:D{sheetRow})");
            writer.WriteEndElement();

            written++;
            // 每千行报告一次，避免高频回调和锁竞争影响纯写入吞吐。
            if (written % 1_000 == 0)
            {
                progress?.Invoke(written);
            }
        }

        if (written > 0 && written % 1_000 != 0)
        {
            progress?.Invoke(written);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>更新 PocExcelExporter 的 Write Content Types 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WriteContentTypes(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Types", ContentTypesNamespace);
        WriteContentType(writer, "Default", "Extension", "rels", "application/vnd.openxmlformats-package.relationships+xml");
        WriteContentType(writer, "Default", "Extension", "xml", "application/xml");
        WriteContentType(writer, "Override", "PartName", "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        WriteContentType(writer, "Override", "PartName", "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>更新 PocExcelExporter 的 Write Content Type 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="elementName">需要读取、校验或转换的业务值。</param>
    /// <param name="keyName">需要读取、校验或转换的业务值。</param>
    /// <param name="keyValue">需要读取、校验或转换的业务值。</param>
    /// <param name="contentType">contentType 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WriteContentType(
        XmlWriter writer,
        string elementName,
        string keyName,
        string keyValue,
        string contentType)
    {
        writer.WriteStartElement(elementName, ContentTypesNamespace);
        writer.WriteAttributeString(keyName, keyValue);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    /// <summary>更新 PocExcelExporter 的 Write Package Relationships 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WritePackageRelationships(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(
            writer,
            "rId1",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
            "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>更新 PocExcelExporter 的 Write Workbook Relationships 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WriteWorkbookRelationships(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipsNamespace);
        WriteRelationship(
            writer,
            "rId1",
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
            "worksheets/sheet1.xml");
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>更新 PocExcelExporter 的 Write Relationship 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="id">需要定位的业务记录标识。</param>
    /// <param name="type">type 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="target">本次转换或写入使用的数据对象。</param>
    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipsNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    /// <summary>更新 PocExcelExporter 的 Write Workbook 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WriteWorkbook(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipsNamespace);
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        writer.WriteStartElement("sheet", SpreadsheetNamespace);
        writer.WriteAttributeString("name", "科室工作量");
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", OfficeRelationshipsNamespace, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        // 强制桌面表格软件打开文件时完整重算，因为流式导出刻意没有在服务端生成公式缓存值。
        writer.WriteStartElement("calcPr", SpreadsheetNamespace);
        writer.WriteAttributeString("calcMode", "auto");
        writer.WriteAttributeString("fullCalcOnLoad", "1");
        writer.WriteAttributeString("forceFullCalc", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>更新 PocExcelExporter 的 Write Columns 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WriteColumns(XmlWriter writer)
    {
        double[] widths = [10, 26, 16, 16, 16, 16];
        writer.WriteStartElement("cols", SpreadsheetNamespace);
        for (var index = 0; index < widths.Length; index++)
        {
            writer.WriteStartElement("col", SpreadsheetNamespace);
            writer.WriteAttributeString("min", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", widths[index].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>启动 PocExcelExporter 的 Start Row 流程，并建立运行期间所需状态。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="rowIndex">控制数据范围或位置的数值。</param>
    private static void StartRow(XmlWriter writer, uint rowIndex)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", rowIndex.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>更新 PocExcelExporter 的 Write Text Cell 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void WriteTextCell(XmlWriter writer, string reference, string value)
    {
        // 使用 inlineStr 可省去 sharedStrings.xml 及其全量去重缓存，更符合大文件流式目标。
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", SpreadsheetNamespace);
        writer.WriteElementString("t", SpreadsheetNamespace, value);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>更新 PocExcelExporter 的 Write Number Cell 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="value">需要读取、校验或转换的业务值。</param>
    private static void WriteNumberCell(XmlWriter writer, string reference, int value)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteElementString("v", SpreadsheetNamespace, value.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    /// <summary>更新 PocExcelExporter 的 Write Formula Cell 状态，并同步相关业务数据。</summary>
    /// <param name="writer">writer 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="formula">formula 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WriteFormulaCell(XmlWriter writer, string reference, string formula)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteElementString("f", SpreadsheetNamespace, formula);
        writer.WriteEndElement();
    }

    /// <summary>更新 PocExcelExporter 的 Write Entry 状态，并同步相关业务数据。</summary>
    /// <param name="archive">archive 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="path">path 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="write">write 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    private static void WriteEntry(ZipArchive archive, string path, Action<XmlWriter> write)
    {
        // Fastest 用较低 CPU 换取足够的压缩率；XmlWriter 禁止缩进，减少临时字节和最终文件体积。
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(
            stream,
            new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                CloseOutput = false,
                Indent = false,
                OmitXmlDeclaration = false
            });
        write(writer);
    }

    /// <summary>把零基列序号转换为 Excel 列名，供工作表单元格引用使用。</summary>
    /// <param name="number">number 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    private static string ColumnName(int number)
    {
        // Open XML 单元格地址使用 A..Z、AA..ZZ 的 26 进制字母列名；stackalloc 避免逐行小数组分配。
        Span<char> buffer = stackalloc char[3];
        var index = buffer.Length;
        while (number > 0)
        {
            number--;
            buffer[--index] = (char)('A' + number % 26);
            number /= 26;
        }

        return new string(buffer[index..]);
    }

}
