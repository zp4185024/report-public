using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>当前 Univer 0.25.1 的初始双向映射；只在对应版本尚无数据库记录时写入。</summary>
public static class PocExchangeMappingDefaults
{
    public const string CurrentUniverVersion = "0.25.1";
    public const int CurrentMappingVersion = 1;

    public static IReadOnlyList<PocExchangeMapping> Create()
    {
        var now = DateTime.Now;
        var rules = new List<PocExchangeMapping>();
        var sort = 0;

        void Add(
            string code,
            string direction,
            string category,
            string source,
            string target,
            string transform,
            bool required = false,
            decimal? factor = null,
            string? sourceValue = null,
            string? targetValue = null,
            string? remark = null)
        {
            rules.Add(new PocExchangeMapping
            {
                UniverVersion = CurrentUniverVersion,
                MappingVersion = CurrentMappingVersion,
                RuleCode = code,
                Direction = direction,
                Category = category,
                SourcePath = source,
                TargetPath = target,
                TransformType = transform,
                SourceValue = sourceValue,
                TargetValue = targetValue,
                Factor = factor,
                IsRequired = required,
                IsEnabled = true,
                Sort = sort += 10,
                Remark = remark,
                CreateTime = now,
                UpdateTime = now
            });
        }

        const string import = PocExchangeDirections.ExcelToUniver;
        Add("xlsx-to-univer-cell-value", import, "Cell", "Excel.Cell.Value", "v", PocExchangeTransformTypes.Direct, true);
        Add("xlsx-to-univer-cell-type", import, "Cell", "Excel.Cell.Type", "t", PocExchangeTransformTypes.CellType, true);
        Add("xlsx-to-univer-cell-formula", import, "Formula", "Excel.Cell.FormulaA1", "f", PocExchangeTransformTypes.AddPrefix, true);
        Add("xlsx-to-univer-cell-style", import, "Style", "Excel.Cell.Style", "s", PocExchangeTransformTypes.StyleReference, true);
        Add("xlsx-to-univer-style-font-name", import, "Style", "Excel.Style.Font.Name", "ff", PocExchangeTransformTypes.Direct);
        Add("xlsx-to-univer-style-font-size", import, "Style", "Excel.Style.Font.Size", "fs", PocExchangeTransformTypes.Direct);
        Add("xlsx-to-univer-style-bold", import, "Style", "Excel.Style.Font.Bold", "bl", PocExchangeTransformTypes.Boolean01);
        Add("xlsx-to-univer-style-italic", import, "Style", "Excel.Style.Font.Italic", "it", PocExchangeTransformTypes.Boolean01);
        Add("xlsx-to-univer-style-underline", import, "Style", "Excel.Style.Font.Underline", "ul.s", PocExchangeTransformTypes.Boolean01);
        Add("xlsx-to-univer-style-strike", import, "Style", "Excel.Style.Font.Strike", "st.s", PocExchangeTransformTypes.Boolean01);
        Add("xlsx-to-univer-style-font-color", import, "Style", "Excel.Style.Font.Color", "cl.rgb", PocExchangeTransformTypes.Color);
        Add("xlsx-to-univer-style-background", import, "Style", "Excel.Style.Fill.Color", "bg.rgb", PocExchangeTransformTypes.Color);
        Add("xlsx-to-univer-style-number-format", import, "Style", "Excel.Style.NumberFormat", "n.pattern", PocExchangeTransformTypes.Direct);
        Add("xlsx-to-univer-style-horizontal", import, "Style", "Excel.Style.Alignment.Horizontal", "ht", PocExchangeTransformTypes.Enum);
        Add("xlsx-to-univer-style-vertical", import, "Style", "Excel.Style.Alignment.Vertical", "vt", PocExchangeTransformTypes.Enum);
        Add("xlsx-to-univer-style-wrap", import, "Style", "Excel.Style.Alignment.Wrap", "tb", PocExchangeTransformTypes.Enum);
        Add("xlsx-to-univer-style-rotation", import, "Style", "Excel.Style.Alignment.Rotation", "tr.a", PocExchangeTransformTypes.Direct);
        Add("xlsx-to-univer-border-top", import, "Style", "Excel.Style.Border.Top", "bd.t", PocExchangeTransformTypes.Border);
        Add("xlsx-to-univer-border-right", import, "Style", "Excel.Style.Border.Right", "bd.r", PocExchangeTransformTypes.Border);
        Add("xlsx-to-univer-border-bottom", import, "Style", "Excel.Style.Border.Bottom", "bd.b", PocExchangeTransformTypes.Border);
        Add("xlsx-to-univer-border-left", import, "Style", "Excel.Style.Border.Left", "bd.l", PocExchangeTransformTypes.Border);
        Add("xlsx-to-univer-row-height", import, "Row", "Excel.Row.HeightPoints", "rowData.h", PocExchangeTransformTypes.Scale, factor: 1.33333333m);
        Add("xlsx-to-univer-column-width", import, "Column", "Excel.Column.Width", "columnData.w", PocExchangeTransformTypes.ColumnWidth);
        Add("xlsx-to-univer-merge", import, "Sheet", "Excel.Sheet.Merges", "mergeData", PocExchangeTransformTypes.Direct);
        Add("xlsx-to-univer-freeze", import, "Sheet", "Excel.Sheet.Freeze", "freeze", PocExchangeTransformTypes.Direct);

        const string export = PocExchangeDirections.UniverToExcel;
        Add("univer-to-xlsx-cell-value", export, "Cell", "v", "Excel.Cell.Value", PocExchangeTransformTypes.Direct, true);
        Add("univer-to-xlsx-cell-type", export, "Cell", "t", "Excel.Cell.Type", PocExchangeTransformTypes.CellType, true);
        Add("univer-to-xlsx-cell-formula", export, "Formula", "f", "Excel.Cell.FormulaA1", PocExchangeTransformTypes.RemovePrefix, true);
        Add("univer-to-xlsx-cell-style", export, "Style", "s", "Excel.Cell.Style", PocExchangeTransformTypes.StyleReference, true);
        Add("univer-to-xlsx-style-font-name", export, "Style", "ff", "Excel.Style.Font.Name", PocExchangeTransformTypes.Direct);
        Add("univer-to-xlsx-style-font-size", export, "Style", "fs", "Excel.Style.Font.Size", PocExchangeTransformTypes.Direct);
        Add("univer-to-xlsx-style-bold", export, "Style", "bl", "Excel.Style.Font.Bold", PocExchangeTransformTypes.Boolean01);
        Add("univer-to-xlsx-style-italic", export, "Style", "it", "Excel.Style.Font.Italic", PocExchangeTransformTypes.Boolean01);
        Add("univer-to-xlsx-style-underline", export, "Style", "ul.s", "Excel.Style.Font.Underline", PocExchangeTransformTypes.Boolean01);
        Add("univer-to-xlsx-style-strike", export, "Style", "st.s", "Excel.Style.Font.Strike", PocExchangeTransformTypes.Boolean01);
        Add("univer-to-xlsx-style-font-color", export, "Style", "cl.rgb", "Excel.Style.Font.Color", PocExchangeTransformTypes.Color);
        Add("univer-to-xlsx-style-background", export, "Style", "bg.rgb", "Excel.Style.Fill.Color", PocExchangeTransformTypes.Color);
        Add("univer-to-xlsx-style-number-format", export, "Style", "n.pattern", "Excel.Style.NumberFormat", PocExchangeTransformTypes.Direct);
        Add("univer-to-xlsx-style-horizontal", export, "Style", "ht", "Excel.Style.Alignment.Horizontal", PocExchangeTransformTypes.Enum);
        Add("univer-to-xlsx-style-vertical", export, "Style", "vt", "Excel.Style.Alignment.Vertical", PocExchangeTransformTypes.Enum);
        Add("univer-to-xlsx-style-wrap", export, "Style", "tb", "Excel.Style.Alignment.Wrap", PocExchangeTransformTypes.Enum);
        Add("univer-to-xlsx-style-rotation", export, "Style", "tr.a", "Excel.Style.Alignment.Rotation", PocExchangeTransformTypes.Direct);
        Add("univer-to-xlsx-border-top", export, "Style", "bd.t", "Excel.Style.Border.Top", PocExchangeTransformTypes.Border);
        Add("univer-to-xlsx-border-right", export, "Style", "bd.r", "Excel.Style.Border.Right", PocExchangeTransformTypes.Border);
        Add("univer-to-xlsx-border-bottom", export, "Style", "bd.b", "Excel.Style.Border.Bottom", PocExchangeTransformTypes.Border);
        Add("univer-to-xlsx-border-left", export, "Style", "bd.l", "Excel.Style.Border.Left", PocExchangeTransformTypes.Border);
        Add("univer-to-xlsx-row-height", export, "Row", "rowData.h", "Excel.Row.HeightPoints", PocExchangeTransformTypes.Scale, factor: 0.75m);
        Add("univer-to-xlsx-column-width", export, "Column", "columnData.w", "Excel.Column.Width", PocExchangeTransformTypes.ColumnWidth);
        Add("univer-to-xlsx-merge", export, "Sheet", "mergeData", "Excel.Sheet.Merges", PocExchangeTransformTypes.Direct);
        Add("univer-to-xlsx-freeze", export, "Sheet", "freeze", "Excel.Sheet.Freeze", PocExchangeTransformTypes.Direct);

        return rules;
    }
}
