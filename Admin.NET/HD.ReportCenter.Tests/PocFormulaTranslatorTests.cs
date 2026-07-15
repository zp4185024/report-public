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

    [Theory]
    [InlineData("=SUM(E6:E6)", "科室工作量", 7, 5, "科室工作量", 6, 136, "=SUM(E6:E142)")]
    [InlineData("=E6", "科室工作量", 7, 5, "科室工作量", 6, 136, "=E6")]
    [InlineData("=E7", "科室工作量", 10, 5, "科室工作量", 6, 136, "=E143")]
    [InlineData("='科室工作量'!$E$6:$E$6", "统计", 2, 1, "科室工作量", 6, 136, "=科室工作量!$E$6:$E$142")]
    [InlineData("='其它表'!E6:E6", "统计", 2, 1, "科室工作量", 6, 136, "=其它表!E6")]
    public void InsertRows_AdjustsOnlyReferencesToTheInsertedSheet(
        string formula,
        string formulaSheet,
        int formulaRow,
        int formulaColumn,
        string insertedSheet,
        int insertAfterRow,
        int insertedRowCount,
        string expected)
    {
        var adjusted = PocFormulaTranslator.InsertRows(
            formula,
            formulaSheet,
            formulaRow,
            formulaColumn,
            insertedSheet,
            insertAfterRow,
            insertedRowCount);

        Assert.Equal(expected, adjusted);
    }
}
