using HD.ReportCenter.ReportEngine.CoreValidation.Export;

namespace HD.ReportCenter.Tests;

public sealed class PocFormulaTranslatorTests
{
    /// <summary>验证 Move Formula Preserves Excel Reference Semantics 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="formula">formula 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="sourceRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="sourceColumn">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="targetRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="targetColumn">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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

    /// <summary>验证 Insert Rows Adjusts Only References To The Inserted Sheet 场景下的预期行为，并防止相关逻辑回归。</summary>
    /// <param name="formula">formula 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
    /// <param name="formulaSheet">当前读取或修改的工作表对象。</param>
    /// <param name="formulaRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="formulaColumn">参与映射、显示或数据写入的字段集合。</param>
    /// <param name="insertedSheet">当前读取或修改的工作表对象。</param>
    /// <param name="insertAfterRow">需要遍历、筛选或写入的业务数据集合。</param>
    /// <param name="insertedRowCount">控制数据范围或位置的数值。</param>
    /// <param name="expected">expected 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
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
