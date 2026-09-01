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

        /// <summary>初始化 InsertRowsVisitor 实例，保存后续处理所需的依赖并建立初始状态。</summary>
        /// <param name="insertedSheet">当前读取或修改的工作表对象。</param>
        /// <param name="insertAfterRow">需要遍历、筛选或写入的业务数据集合。</param>
        /// <param name="insertedRowCount">控制数据范围或位置的数值。</param>
        public InsertRowsVisitor(
            string insertedSheet,
            int insertAfterRow,
            int insertedRowCount)
        {
            _insertedSheet = insertedSheet;
            _insertAfterRow = insertAfterRow;
            _insertedRowCount = insertedRowCount;
        }

        /// <summary>负责 InsertRowsVisitor 的 Logical Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="value">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol LogicalValue(ModContext context, SymbolRange range, bool value) =>
            _copy.LogicalValue(context, range, value);

        /// <summary>负责 InsertRowsVisitor 的 Number Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="value">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol NumberValue(ModContext context, SymbolRange range, double value) =>
            _copy.NumberValue(context, range, value);

        /// <summary>负责 InsertRowsVisitor 的 Text Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="text">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol TextValue(ModContext context, SymbolRange range, string text) =>
            _copy.TextValue(context, range, text);

        /// <summary>负责 InsertRowsVisitor 的 Error Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="error">error 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol ErrorValue(
            ModContext context,
            SymbolRange range,
            ReadOnlySpan<char> error) =>
            _copy.ErrorValue(context, range, error);

        /// <summary>负责 InsertRowsVisitor 的 Array Node 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="rows">需要遍历、筛选或写入的业务数据集合。</param>
        /// <param name="columns">参与映射、显示或数据写入的字段集合。</param>
        /// <param name="elements">elements 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol ArrayNode(
            ModContext context,
            SymbolRange range,
            int rows,
            int columns,
            IReadOnlyList<TransformedSymbol> elements) =>
            _copy.ArrayNode(context, range, rows, columns, elements);

        /// <summary>负责 InsertRowsVisitor 的 Blank Node 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol BlankNode(ModContext context, SymbolRange range) =>
            _copy.BlankNode(context, range);

        /// <summary>负责 InsertRowsVisitor 的 Logical Node 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="value">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol LogicalNode(ModContext context, SymbolRange range, bool value) =>
            _copy.LogicalNode(context, range, value);

        /// <summary>负责 InsertRowsVisitor 的 Error Node 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="error">error 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol ErrorNode(
            ModContext context,
            SymbolRange range,
            ReadOnlySpan<char> error) =>
            _copy.ErrorNode(context, range, error);

        /// <summary>负责 InsertRowsVisitor 的 Number Node 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="value">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol NumberNode(ModContext context, SymbolRange range, double value) =>
            _copy.NumberNode(context, range, value);

        /// <summary>负责 InsertRowsVisitor 的 Text Node 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="text">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol TextNode(ModContext context, SymbolRange range, string text) =>
            _copy.TextNode(context, range, text);

        /// <summary>负责 InsertRowsVisitor 的 Reference 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol Reference(
            ModContext context,
            SymbolRange range,
            ReferenceArea reference) =>
            _copy.Reference(
                context,
                range,
                IsInsertedSheet(context.Sheet) ? Transform(context, range, reference) : reference);

        /// <summary>负责 InsertRowsVisitor 的 Sheet Reference 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="sheet">当前读取或修改的工作表对象。</param>
        /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>负责 InsertRowsVisitor 的 Bang Reference 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol BangReference(
            ModContext context,
            SymbolRange range,
            ReferenceArea reference) =>
            _copy.BangReference(
                context,
                range,
                IsInsertedSheet(context.Sheet) ? Transform(context, range, reference) : reference);

        /// <summary>负责 InsertRowsVisitor 的 Reference3 D 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="firstSheet">当前读取或修改的工作表对象。</param>
        /// <param name="lastSheet">当前读取或修改的工作表对象。</param>
        /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>负责 InsertRowsVisitor 的 External Sheet Reference 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="workbookIndex">控制数据范围或位置的数值。</param>
        /// <param name="sheet">当前读取或修改的工作表对象。</param>
        /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol ExternalSheetReference(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string sheet,
            ReferenceArea reference) =>
            _copy.ExternalSheetReference(context, range, workbookIndex, sheet, reference);

        /// <summary>负责 InsertRowsVisitor 的 External Reference3 D 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="workbookIndex">控制数据范围或位置的数值。</param>
        /// <param name="firstSheet">当前读取或修改的工作表对象。</param>
        /// <param name="lastSheet">当前读取或修改的工作表对象。</param>
        /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>负责 InsertRowsVisitor 的 Function 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="functionName">需要读取、校验或转换的业务值。</param>
        /// <param name="arguments">arguments 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol Function(
            ModContext context,
            SymbolRange range,
            ReadOnlySpan<char> functionName,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.Function(context, range, functionName, arguments);

        /// <summary>负责 InsertRowsVisitor 的 Function 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="sheetName">需要读取、校验或转换的业务值。</param>
        /// <param name="functionName">需要读取、校验或转换的业务值。</param>
        /// <param name="arguments">arguments 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol Function(
            ModContext context,
            SymbolRange range,
            string sheetName,
            ReadOnlySpan<char> functionName,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.Function(context, range, sheetName, functionName, arguments);

        /// <summary>负责 InsertRowsVisitor 的 External Function 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="workbookIndex">控制数据范围或位置的数值。</param>
        /// <param name="sheetName">需要读取、校验或转换的业务值。</param>
        /// <param name="functionName">需要读取、校验或转换的业务值。</param>
        /// <param name="arguments">arguments 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>负责 InsertRowsVisitor 的 External Function 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="workbookIndex">控制数据范围或位置的数值。</param>
        /// <param name="functionName">需要读取、校验或转换的业务值。</param>
        /// <param name="arguments">arguments 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>负责 InsertRowsVisitor 的 Cell Function 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="cell">cell 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="arguments">arguments 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol CellFunction(
            ModContext context,
            SymbolRange range,
            RowCol cell,
            IReadOnlyList<TransformedSymbol> arguments) =>
            _copy.CellFunction(context, range, cell, arguments);

        /// <summary>负责 InsertRowsVisitor 的 Structure Reference 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="area">area 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="firstColumn">参与映射、显示或数据写入的字段集合。</param>
        /// <param name="lastColumn">参与映射、显示或数据写入的字段集合。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol StructureReference(
            ModContext context,
            SymbolRange range,
            StructuredReferenceArea area,
            string? firstColumn,
            string? lastColumn) =>
            _copy.StructureReference(context, range, area, firstColumn, lastColumn);

        /// <summary>负责 InsertRowsVisitor 的 Structure Reference 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="table">table 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="area">area 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="firstColumn">参与映射、显示或数据写入的字段集合。</param>
        /// <param name="lastColumn">参与映射、显示或数据写入的字段集合。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol StructureReference(
            ModContext context,
            SymbolRange range,
            string table,
            StructuredReferenceArea area,
            string? firstColumn,
            string? lastColumn) =>
            _copy.StructureReference(context, range, table, area, firstColumn, lastColumn);

        /// <summary>负责 InsertRowsVisitor 的 External Structure Reference 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="workbookIndex">控制数据范围或位置的数值。</param>
        /// <param name="table">table 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="area">area 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="firstColumn">参与映射、显示或数据写入的字段集合。</param>
        /// <param name="lastColumn">参与映射、显示或数据写入的字段集合。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>负责 InsertRowsVisitor 的 Name 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="name">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol Name(ModContext context, SymbolRange range, string name) =>
            _copy.Name(context, range, name);

        /// <summary>负责 InsertRowsVisitor 的 Sheet Name 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="sheet">当前读取或修改的工作表对象。</param>
        /// <param name="name">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol SheetName(
            ModContext context,
            SymbolRange range,
            string sheet,
            string name) =>
            _copy.SheetName(context, range, sheet, name);

        /// <summary>负责 InsertRowsVisitor 的 Bang Name 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="name">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol BangName(ModContext context, SymbolRange range, string name) =>
            _copy.BangName(context, range, name);

        /// <summary>负责 InsertRowsVisitor 的 External Name 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="workbookIndex">控制数据范围或位置的数值。</param>
        /// <param name="name">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol ExternalName(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string name) =>
            _copy.ExternalName(context, range, workbookIndex, name);

        /// <summary>负责 InsertRowsVisitor 的 External Sheet Name 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="workbookIndex">控制数据范围或位置的数值。</param>
        /// <param name="sheet">当前读取或修改的工作表对象。</param>
        /// <param name="name">需要读取、校验或转换的业务值。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol ExternalSheetName(
            ModContext context,
            SymbolRange range,
            int workbookIndex,
            string sheet,
            string name) =>
            _copy.ExternalSheetName(context, range, workbookIndex, sheet, name);

        /// <summary>负责 InsertRowsVisitor 的 Binary Node 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="operation">operation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="leftNode">leftNode 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="rightNode">rightNode 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol BinaryNode(
            ModContext context,
            SymbolRange range,
            BinaryOperation operation,
            TransformedSymbol leftNode,
            TransformedSymbol rightNode) =>
            _copy.BinaryNode(context, range, operation, leftNode, rightNode);

        /// <summary>负责 InsertRowsVisitor 的 Unary 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="operation">operation 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <param name="node">node 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol Unary(
            ModContext context,
            SymbolRange range,
            UnaryOperation operation,
            TransformedSymbol node) =>
            _copy.Unary(context, range, operation, node);

        /// <summary>负责 InsertRowsVisitor 的 Nested 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="node">node 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        public TransformedSymbol Nested(
            ModContext context,
            SymbolRange range,
            TransformedSymbol node) =>
            _copy.Nested(context, range, node);

        /// <summary>校验 PocFormulaTranslator 的 Is Inserted Sheet 条件，并返回可供后续流程判断的结果。</summary>
        /// <param name="sheet">当前读取或修改的工作表对象。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        private bool IsInsertedSheet(string? sheet) =>
            string.Equals(sheet, _insertedSheet, StringComparison.OrdinalIgnoreCase);

        /// <summary>将 PocFormulaTranslator 中的 Transform 输入转换为调用方需要的稳定格式。</summary>
        /// <param name="context">本次操作共享的运行上下文。</param>
        /// <param name="range">需要读取、修改或校验的单元格范围。</param>
        /// <param name="reference">reference 对应的业务输入；具体类型和空值范围以方法签名为准。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
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

        /// <summary>负责 InsertRowsVisitor 的 Shift Row 处理：读取该步骤所需数据，完成对应状态变更并返回结果。</summary>
        /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        private int ShiftRow(int row) =>
            row > _insertAfterRow ? checked(row + _insertedRowCount) : row;

        /// <summary>按照 PocFormulaTranslator 的业务约束完成 Copy With Row 数据合并或复制。</summary>
        /// <param name="source">本次转换或写入使用的数据对象。</param>
        /// <param name="row">需要遍历、筛选或写入的业务数据集合。</param>
        /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
        private static RowCol CopyWithRow(RowCol source, int row) =>
            new(
                source.RowType,
                row,
                source.ColumnType,
                source.ColumnValue,
                source.Style);
    }
}
