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

    /// <summary>
    /// 按 Excel 整行插入语义调整公式引用。插入点下方的引用后移，跨越或结束于插入点的显式范围扩展。
    /// </summary>
    public static string InsertRows(
        string formulaA1,
        string formulaSheet,
        int formulaRow,
        int formulaColumn,
        string insertedSheet,
        int insertAfterRow,
        int insertedRowCount)
    {
        if (string.IsNullOrWhiteSpace(formulaA1))
        {
            throw new ArgumentException("Excel公式不能为空。", nameof(formulaA1));
        }
        if (string.IsNullOrWhiteSpace(formulaSheet) || string.IsNullOrWhiteSpace(insertedSheet))
        {
            throw new ArgumentException("公式工作表和插入目标工作表不能为空。", nameof(formulaSheet));
        }
        if (formulaRow < 1 || formulaColumn < 1 || insertAfterRow < 1 || insertedRowCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(insertAfterRow),
                "公式坐标、插入行和插入数量必须是一基正整数。");
        }

        var expression = formulaA1[0] == '=' ? formulaA1[1..] : formulaA1;
        var visitor = new InsertRowsVisitor(insertedSheet, insertAfterRow, insertedRowCount);
        return $"={FormulaConverter.ModifyA1(
            expression,
            formulaSheet,
            formulaRow,
            formulaColumn,
            visitor)}";
    }

    /// <summary>
    /// ClosedXML.Parser 2.0 的 RefModVisitor 把引用钩子限制在程序集内部，
    /// 因此这里实现公开 IAstFactory，并把非引用节点完整委托给原生复制器。
    /// </summary>
    private sealed class InsertRowsVisitor :
        IAstFactory<TransformedSymbol, TransformedSymbol, ModContext>
    {
        private readonly RefModVisitor _copy = new();
        private readonly string _insertedSheet;
        private readonly int _insertAfterRow;
        private readonly int _insertedRowCount;

        public InsertRowsVisitor(
            string insertedSheet,
            int insertAfterRow,
            int insertedRowCount)
        {
            _insertedSheet = insertedSheet;
            _insertAfterRow = insertAfterRow;
            _insertedRowCount = insertedRowCount;
        }

        public TransformedSymbol LogicalValue(ModContext context, SymbolRange range, bool value) =>
            _copy.LogicalValue(context, range, value);

        public TransformedSymbol NumberValue(ModContext context, SymbolRange range, double value) =>
            _copy.NumberValue(context, range, value);

        public TransformedSymbol TextValue(ModContext context, SymbolRange range, string text) =>
            _copy.TextValue(context, range, text);

        public TransformedSymbol ErrorValue(
            ModContext context,
            SymbolRange range,
            ReadOnlySpan<char> error) =>
            _copy.ErrorValue(context, range, error);

        public TransformedSymbol ArrayNode(
            ModContext context,
            SymbolRange range,
            int rows,
            int columns,
            IReadOnlyList<TransformedSymbol> elements) =>
            _copy.ArrayNode(context, range, rows, columns, elements);

        public TransformedSymbol BlankNode(ModContext context, SymbolRange range) =>
            _copy.BlankNode(context, range);

        public TransformedSymbol LogicalNode(ModContext context, SymbolRange range, bool value) =>
            _copy.LogicalNode(context, range, value);

        public TransformedSymbol ErrorNode(
            ModContext context,
            SymbolRange range,
            ReadOnlySpan<char> error) =>
            _copy.ErrorNode(context, range, error);

        public TransformedSymbol NumberNode(ModContext context, SymbolRange range, double value) =>
            _copy.NumberNode(context, range, value);

        public TransformedSymbol TextNode(ModContext context, SymbolRange range, string text) =>
            _copy.TextNode(context, range, text);

        public TransformedSymbol Reference(
            ModContext context,
            SymbolRange range,
            ReferenceArea reference) =>
            _copy.Reference(
                context,
                range,
                IsInsertedSheet(context.Sheet) ? Transform(context, range, reference) : reference);

        public TransformedSymbol SheetReference(
            ModContext context,
            SymbolRange range,
            string sheet,
            ReferenceArea reference) =>
            _copy.SheetReference(
                context,
                range,
                sheet,
                IsInsertedSheet(sheet) ? Transform(context, range, reference) : reference);

        public TransformedSymbol BangReference(
            ModContext context,
            SymbolRange range,
            ReferenceArea reference) =>
            _copy.BangReference(
                context,
                range,
                IsInsertedSheet(context.Sheet) ? Transform(context, range, reference) : reference);

        public TransformedSymbol Reference3D(
            ModContext context,
            SymbolRange range,
            string firstSheet,
            string lastSheet,
            ReferenceArea reference) =>
            _copy.Reference3D(
                context,
                range,
                firstSheet,
                lastSheet,
                IsInsertedSheet(firstSheet) && IsInsertedSheet(lastSheet)
                    ? Transform(context, range, reference)
                    : reference);

        public TransformedSymbol ExternalSheetReference(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string sheet,
            ReferenceArea reference) =>
            _copy.ExternalSheetReference(context, range, workbookIndex, sheet, reference);

        public TransformedSymbol ExternalReference3D(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string firstSheet,
            string lastSheet,
            ReferenceArea reference) =>
            _copy.ExternalReference3D(
                context,
                range,
                workbookIndex,
                firstSheet,
                lastSheet,
                reference);

        public TransformedSymbol Function(
            ModContext context,
            SymbolRange range,
            ReadOnlySpan<char> functionName,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.Function(context, range, functionName, arguments);

        public TransformedSymbol Function(
            ModContext context,
            SymbolRange range,
            string sheetName,
            ReadOnlySpan<char> functionName,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.Function(context, range, sheetName, functionName, arguments);

        public TransformedSymbol ExternalFunction(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string sheetName,
            ReadOnlySpan<char> functionName,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.ExternalFunction(
                context,
                range,
                workbookIndex,
                sheetName,
                functionName,
                arguments);

        public TransformedSymbol ExternalFunction(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            ReadOnlySpan<char> functionName,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.ExternalFunction(
                context,
                range,
                workbookIndex,
                functionName,
                arguments);

        public TransformedSymbol CellFunction(
            ModContext context,
            SymbolRange range,
            RowCol cell,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.CellFunction(context, range, cell, arguments);

        public TransformedSymbol StructureReference(
            ModContext context,
            SymbolRange range,
            StructuredReferenceArea area,
            string? firstColumn,
            string? lastColumn) =>
            _copy.StructureReference(context, range, area, firstColumn, lastColumn);

        public TransformedSymbol StructureReference(
            ModContext context,
            SymbolRange range,
            string table,
            StructuredReferenceArea area,
            string? firstColumn,
            string? lastColumn) =>
            _copy.StructureReference(context, range, table, area, firstColumn, lastColumn);

        public TransformedSymbol ExternalStructureReference(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string table,
            StructuredReferenceArea area,
            string? firstColumn,
            string? lastColumn) =>
            _copy.ExternalStructureReference(
                context,
                range,
                workbookIndex,
                table,
                area,
                firstColumn,
                lastColumn);

        public TransformedSymbol Name(ModContext context, SymbolRange range, string name) =>
            _copy.Name(context, range, name);

        public TransformedSymbol SheetName(
            ModContext context,
            SymbolRange range,
            string sheet,
            string name) =>
            _copy.SheetName(context, range, sheet, name);

        public TransformedSymbol BangName(ModContext context, SymbolRange range, string name) =>
            _copy.BangName(context, range, name);

        public TransformedSymbol ExternalName(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string name) =>
            _copy.ExternalName(context, range, workbookIndex, name);

        public TransformedSymbol ExternalSheetName(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string sheet,
            string name) =>
            _copy.ExternalSheetName(context, range, workbookIndex, sheet, name);

        public TransformedSymbol BinaryNode(
            ModContext context,
            SymbolRange range,
            BinaryOperation operation,
            TransformedSymbol leftNode,
            TransformedSymbol rightNode) =>
            _copy.BinaryNode(context, range, operation, leftNode, rightNode);

        public TransformedSymbol Unary(
            ModContext context,
            SymbolRange range,
            UnaryOperation operation,
            TransformedSymbol node) =>
            _copy.Unary(context, range, operation, node);

        public TransformedSymbol Nested(
            ModContext context,
            SymbolRange range,
            TransformedSymbol node) =>
            _copy.Nested(context, range, node);

        private bool IsInsertedSheet(string? sheet) =>
            string.Equals(sheet, _insertedSheet, StringComparison.OrdinalIgnoreCase);

        private ReferenceArea Transform(
            ModContext context,
            SymbolRange range,
            ReferenceArea reference)
        {
            if (reference.Style != ReferenceStyle.A1 ||
                reference.First.RowType == ReferenceAxisType.None)
            {
                return reference;
            }

            var first = reference.First;
            var second = reference.Second;
            var firstRow = first.RowValue;
            var secondRow = second.RowValue;
            var explicitRange = context.Formula
                .AsSpan(range.Start, range.Length)
                .Contains(':');

            var adjustedFirstRow = ShiftRow(firstRow);
            var adjustedSecondRow = ShiftRow(secondRow);
            if (explicitRange &&
                firstRow <= _insertAfterRow &&
                secondRow <= _insertAfterRow &&
                Math.Max(firstRow, secondRow) == _insertAfterRow)
            {
                // 解析器会把 E6:E6 规范化成两个相同端点；把第二端点扩展即可保留显式范围语义。
                if (firstRow == secondRow || secondRow == _insertAfterRow)
                {
                    adjustedSecondRow = checked(secondRow + _insertedRowCount);
                }
                else
                {
                    adjustedFirstRow = checked(firstRow + _insertedRowCount);
                }
            }

            return new ReferenceArea(
                CopyWithRow(first, adjustedFirstRow),
                CopyWithRow(second, adjustedSecondRow));
        }

        private int ShiftRow(int row) =>
            row > _insertAfterRow ? checked(row + _insertedRowCount) : row;

        private static RowCol CopyWithRow(RowCol source, int row) =>
            new(
                source.RowType,
                row,
                source.ColumnType,
                source.ColumnValue,
                source.Style);
    }
}
