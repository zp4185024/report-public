using ClosedXML.Parser;

namespace HD.ReportCenter.ReportEngine.CoreValidation.Export;

/// <summary>
/// 按 Excel 复制单元格的语义平移 A1 公式。解析器负责区分相对/绝对引用、范围和工作表名，
/// 避免使用正则表达式误改字符串常量、函数名或带引号的工作表引用。
/// </summary>
public static class PocFormulaTranslator
{
    /// <summary>把源单元格中的公式复制到目标单元格，行列坐标均为 Excel 的一基坐标。</summary>
    public static string MoveFormula(
        string formulaA1,
        int sourceRow,
        int sourceColumn,
        int targetRow,
        int targetColumn)
    {
        if (string.IsNullOrWhiteSpace(formulaA1))
        {
            throw new ArgumentException("Excel公式不能为空。", nameof(formulaA1));
        }
        if (sourceRow < 1 || sourceColumn < 1 || targetRow < 1 || targetColumn < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceRow),
                "Excel公式源坐标和目标坐标必须是一基正整数。");
        }

        var expression = formulaA1[0] == '=' ? formulaA1[1..] : formulaA1;
        var relativeFormula = FormulaConverter.ToR1C1(expression, sourceRow, sourceColumn);
        return $"={FormulaConverter.ToA1(relativeFormula, targetRow, targetColumn)}";
    }
}
