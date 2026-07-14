using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using HD.ReportCenter.ReportEngine.CoreValidation;

namespace HD.ReportCenter.Tests;

public sealed class PocExcelExporterTests
{
    [Fact]
    public void Export_WritesFormulaAndForcesCalculationOnOpen()
    {
        using var stream = new MemoryStream();

        PocExcelExporter.Write(stream, PocReportCatalog.EnumerateRows(3, "D02"));

        Assert.True(stream.CanRead);
        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart!.Workbook;
        var sheetData = document.WorkbookPart.WorksheetParts.Single()
            .Worksheet.GetFirstChild<SheetData>()!;
        var firstDataRow = sheetData.Elements<Row>().Single(x => x.RowIndex?.Value == 6U);

        Assert.Equal(
            "C6+D6",
            firstDataRow.Elements<Cell>().Single(x => x.CellReference == "E6").CellFormula!.Text);
        Assert.Equal(CalculateModeValues.Auto, workbook.CalculationProperties!.CalculationMode!.Value);
        Assert.True(workbook.CalculationProperties.FullCalculationOnLoad!.Value);
        Assert.True(workbook.CalculationProperties.ForceFullCalculation!.Value);
    }

    [Fact]
    public void Export_ReportsProgressWithoutMaterializingRows()
    {
        using var stream = new MemoryStream();
        var progress = new List<long>();

        PocExcelExporter.Write(
            stream,
            PocReportCatalog.EnumerateRows(2_001),
            progress.Add);

        Assert.Equal([1_000L, 2_000L, 2_001L], progress);
    }
}
