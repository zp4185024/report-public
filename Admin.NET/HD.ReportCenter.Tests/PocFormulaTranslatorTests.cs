using HD.ReportCenter.ReportEngine.CoreValidation.Export;

namespace HD.ReportCenter.Tests;

public sealed class PocFormulaTranslatorTests
{
    [Theory]
    [InlineData("=C6+D6", 6, 5, 7, 5, "=C7+D7")]
    [InlineData("=$C6+D$6", 6, 5, 8, 5, "=$C8+D$6")]
    [InlineData("=AVERAGE(C6:D6)", 6, 6, 9, 6, "=AVERAGE(C9:D9)")]
    [InlineData("='数据 表'!C6+D6", 6, 5, 7, 5, "='数据 表'!C7+D7")]
    public void MoveFormula_PreservesExcelReferenceSemantics(
        string formula,
        int sourceRow,
        int sourceColumn,
        int targetRow,
        int targetColumn,
        string expected)
    {
        var moved = PocFormulaTranslator.MoveFormula(
            formula,
            sourceRow,
            sourceColumn,
            targetRow,
            targetColumn);

        Assert.Equal(expected, moved);
    }
}
