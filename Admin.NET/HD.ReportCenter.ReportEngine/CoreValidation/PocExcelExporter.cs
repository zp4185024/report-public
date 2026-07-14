using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

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
            WriteFormulaCell(writer, $"E{sheetRow}", $"C{sheetRow}+D{sheetRow}");
            WriteFormulaCell(writer, $"F{sheetRow}", $"AVERAGE(C{sheetRow}:D{sheetRow})");
            writer.WriteEndElement();

            written++;
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

    private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationshipsNamespace);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

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
        writer.WriteStartElement("calcPr", SpreadsheetNamespace);
        writer.WriteAttributeString("calcMode", "auto");
        writer.WriteAttributeString("fullCalcOnLoad", "1");
        writer.WriteAttributeString("forceFullCalc", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

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

    private static void StartRow(XmlWriter writer, uint rowIndex)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", rowIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteTextCell(XmlWriter writer, string reference, string value)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", SpreadsheetNamespace);
        writer.WriteElementString("t", SpreadsheetNamespace, value);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteNumberCell(XmlWriter writer, string reference, int value)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteElementString("v", SpreadsheetNamespace, value.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static void WriteFormulaCell(XmlWriter writer, string reference, string formula)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);
        writer.WriteElementString("f", SpreadsheetNamespace, formula);
        writer.WriteEndElement();
    }

    private static void WriteEntry(ZipArchive archive, string path, Action<XmlWriter> write)
    {
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

    private static string ColumnName(int number)
    {
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
