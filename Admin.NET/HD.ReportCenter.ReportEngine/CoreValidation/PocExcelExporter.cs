using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace HD.ReportCenter.ReportEngine.CoreValidation;

public static class PocExcelExporter
{
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

        using var packageStream = new NonClosingStream(output);
        using var document = SpreadsheetDocument.Create(
            packageStream,
            SpreadsheetDocumentType.Workbook,
            true);

        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var relationshipId = workbookPart.GetIdOfPart(worksheetPart);

        WriteWorksheet(worksheetPart, rows, progress, cancellationToken);

        workbookPart.Workbook = new Workbook(
            new Sheets(
                new Sheet
                {
                    Id = relationshipId,
                    SheetId = 1U,
                    Name = "科室工作量"
                }),
            new CalculationProperties
            {
                CalculationMode = CalculateModeValues.Auto,
                ForceFullCalculation = true,
                FullCalculationOnLoad = true
            });
        workbookPart.Workbook.Save();
    }

    private static void WriteWorksheet(
        WorksheetPart worksheetPart,
        IEnumerable<PocWorkloadRow> rows,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());

        WriteRow(writer, 1, TextCell("A1", "科室工作量统计"));
        WriteRow(writer, 2, TextCell("A2", "公式由 Excel/WPS 打开文件时自动计算"));
        WriteRow(writer, 3, TextCell("A3", "核心功能验证导出"));
        WriteRow(writer, 4);
        WriteRow(
            writer,
            5,
            Headers.Select((header, index) => TextCell($"{ColumnName(index + 1)}5", header)).ToArray());

        long written = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheetRow = checked((uint)(written + 6));
            WriteRow(
                writer,
                sheetRow,
                NumberCell($"A{sheetRow}", row.Sequence),
                TextCell($"B{sheetRow}", row.DepartmentName),
                NumberCell($"C{sheetRow}", row.OutpatientCount),
                NumberCell($"D{sheetRow}", row.DischargeCount),
                FormulaCell($"E{sheetRow}", $"C{sheetRow}+D{sheetRow}"),
                FormulaCell($"F{sheetRow}", $"AVERAGE(C{sheetRow}:D{sheetRow})"));

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
    }

    private static void WriteRow(OpenXmlWriter writer, uint rowIndex, params Cell[] cells)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var cell in cells)
        {
            writer.WriteElement(cell);
        }

        writer.WriteEndElement();
    }

    private static Cell TextCell(string reference, string value) =>
        new()
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value))
        };

    private static Cell NumberCell(string reference, int value) =>
        new()
        {
            CellReference = reference,
            CellValue = new CellValue(value)
        };

    private static Cell FormulaCell(string reference, string formula) =>
        new()
        {
            CellReference = reference,
            CellFormula = new CellFormula(formula)
        };

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

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Flush();
            }

            base.Dispose(disposing);
        }
    }
}
