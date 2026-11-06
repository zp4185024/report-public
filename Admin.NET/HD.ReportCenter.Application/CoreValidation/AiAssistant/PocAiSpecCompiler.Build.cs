using System.Text.RegularExpressions;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>
/// PocAiSpecCompiler 的包构造后半部：共享校验小工具 + 快照/绑定/包对象装配，
/// 与 scripts/report-design/New-ReportPackage.ps1 的 New-TemplateSnapshot/New-TemplateBinding/
/// ConvertTo-PackageJson 产物逐字段对齐。
/// </summary>
public sealed partial class PocAiSpecCompiler
{
    /// <summary>编译成功后的包构造：固定布局快照、规范绑定与四节结构，ExportedAt 使用 UTC 当前时间。</summary>
    /// <param name="spec">spec 根对象（读取可选 fieldPresentations 之外的根级字段不再使用）。</param>
    /// <param name="report">报表设计数据。</param>
    /// <param name="dataset">数据集设计数据。</param>
    /// <param name="template">模板设计数据。</param>
    /// <returns>可直接交给 PocReportPackageService.ValidateAsync 的完整报表包。</returns>
    private static PocReportPackage BuildPackage(
        JObject spec,
        PocAiSpecReportDesign report,
        PocAiSpecDatasetDesign dataset,
        PocAiSpecTemplateDesign template)
    {
        // 第一段：快照与绑定。绑定形态必须与后端 PocTemplateValidator.Scan 的重扫结果一致。
        var snapshot = BuildSnapshot(report, dataset, template);
        var binding = BuildBinding(report, template);

        // 第二段：参数列表补全包契约全部字段；sourceCode/sourceFieldCode 固定 null（外部源走配置页）。
        var parameters = report.Parameters.Select(item => new PocReportPackageParameter(
            item.Code,
            item.DisplayName,
            item.DataType,
            item.ControlType,
            item.SourceType,
            null,
            null,
            item.UrlKey,
            item.DefaultExpression,
            item.FixedValue,
            null,
            item.IsVisible,
            item.IsReadOnly,
            item.IsRequired,
            item.AllowUrlPrefill,
            item.DisplayOrder)).ToArray();

        // 第三段：组装根对象。isEnabled 固定启用（停用包无法导入）；数据源信息仅作导入方言比对提示。
        return new PocReportPackage(
            PocReportPackageFormat.PackageKind,
            PocReportPackageFormat.PackageVersion,
            DateTimeOffset.UtcNow,
            dataset.DataSourceCode,
            dataset.DataSourceType,
            new PocReportPackageReport(
                report.Code,
                report.Name,
                report.SheetName,
                dataset.Code,
                new PocPreviewSettings(
                    report.PreviewMode.Equals("Paged", StringComparison.OrdinalIgnoreCase)
                        ? PocPreviewMode.Paged
                        : PocPreviewMode.Full,
                    report.PageSize,
                    report.ShowExactTotal),
                new PocExportSettings(
                    report.LargeDefaultMode ? PocDataExportMode.Large : PocDataExportMode.Normal,
                    report.NormalMaxRows,
                    report.NormalMaxCells,
                    report.NormalTimeoutSeconds,
                    report.LargeExportEnabled,
                    report.RowsPerSheet,
                    report.FileRetentionHours),
                report.FieldPresentations,
                true),
            parameters,
            new PocReportPackageTemplate(snapshot, binding, 1),
            new PocReportPackageDataset(
                dataset.Code,
                dataset.Name,
                dataset.QueryText,
                new PocDatasetSchemaDocument(dataset.Variables, dataset.Fields),
                dataset.TimeoutSeconds,
                dataset.MaxRows,
                true));
    }

    /// <summary>构造 Univer 工作簿快照：行0标题合并、行1参数行、行2表头、行3明细模板行、行4合计行。</summary>
    /// <param name="report">报表设计数据。</param>
    /// <param name="dataset">数据集设计数据（列样式判断用字段类型）。</param>
    /// <param name="template">模板设计数据。</param>
    /// <returns>与 ps1 New-TemplateSnapshot 同构的快照对象。</returns>
    private static JObject BuildSnapshot(
        PocAiSpecReportDesign report,
        PocAiSpecDatasetDesign dataset,
        PocAiSpecTemplateDesign template)
    {
        _ = dataset;
        var usedColumnCount = Math.Max(6, template.Columns.Count);

        // 第一段：cellData 稀疏结构。参数行允许整格混排文字与 params token。
        var row0 = new JObject { ["0"] = Cell(template.Title, "title", null) };
        var cellData = new JObject { ["0"] = row0 };
        if (!string.IsNullOrEmpty(template.ParameterLine))
        {
            cellData["1"] = new JObject { ["0"] = Cell(template.ParameterLine, "meta", null) };
        }

        var headerRow = new JObject();
        var detailRow = new JObject();
        foreach (var column in template.Columns)
        {
            headerRow[column.ColumnIndex.ToString()] = Cell(column.Header, "header", null);
            var detailStyle = column.FieldDataType.Equals("number", StringComparison.OrdinalIgnoreCase)
                ? "number"
                : "center";
            detailRow[column.ColumnIndex.ToString()] = Cell($"{{{{detail.{column.Field}}}}}", detailStyle, null);
        }
        cellData["2"] = headerRow;
        cellData["3"] = detailRow;
        if (template.Totals.Count > 0)
        {
            // 合计行：SUM 显式范围两端点都落在明细行（一基第 4 行），导出展开时自动扩展到实际行区间。
            var totalsRow = new JObject
            {
                ["0"] = Cell(template.Totals[0].Label, "totalsLabel", null)
            };
            foreach (var total in template.Totals)
            {
                var letter = ColumnIndexToLetter(total.ColumnIndex + 1);
                totalsRow[total.ColumnIndex.ToString()] = Cell(
                    null,
                    "totals",
                    $"=SUM({letter}4:{letter}4)");
            }
            cellData["4"] = totalsRow;
        }

        // 第二段：行高/列宽与合并区。合并区只覆盖标题行，不与明细模板行相交。
        var rowData = new JObject
        {
            ["0"] = new JObject { ["h"] = 42 },
            ["1"] = new JObject { ["h"] = 30 },
            ["2"] = new JObject { ["h"] = 34 }
        };
        if (template.Totals.Count > 0)
        {
            rowData["4"] = new JObject { ["h"] = 30 };
        }
        var columnData = new JObject();
        for (var index = 0; index < usedColumnCount; index++)
        {
            columnData[index.ToString()] = new JObject { ["w"] = index == 0 ? 150 : 120 };
        }

        // 第三段：样式表与工作簿外壳。样式键自由命名，cells 通过 s 引用。
        return new JObject
        {
            ["id"] = $"report-{report.Code}",
            ["rev"] = 1,
            ["name"] = report.Name,
            ["appVersion"] = "1.0.0",
            ["locale"] = "zhCN",
            ["styles"] = new JObject
            {
                ["title"] = new JObject { ["ff"] = "Microsoft YaHei", ["fs"] = 16, ["bl"] = 1, ["vt"] = 2 },
                ["meta"] = new JObject
                {
                    ["ff"] = "Microsoft YaHei", ["fs"] = 10,
                    ["cl"] = new JObject { ["rgb"] = "#344054" }, ["vt"] = 2
                },
                ["header"] = new JObject
                {
                    ["ff"] = "Microsoft YaHei", ["fs"] = 10, ["bl"] = 1,
                    ["bg"] = new JObject { ["rgb"] = "#e9eef5" },
                    ["cl"] = new JObject { ["rgb"] = "#18212b" },
                    ["ht"] = 2, ["vt"] = 2
                },
                ["center"] = new JObject { ["ht"] = 2, ["vt"] = 2 },
                ["number"] = new JObject
                {
                    ["ht"] = 3, ["vt"] = 2, ["n"] = new JObject { ["pattern"] = "#,##0.00" }
                },
                ["totalsLabel"] = new JObject
                {
                    ["ff"] = "Microsoft YaHei", ["fs"] = 10, ["bl"] = 1, ["ht"] = 2, ["vt"] = 2
                },
                ["totals"] = new JObject
                {
                    ["ff"] = "Microsoft YaHei", ["fs"] = 10, ["bl"] = 1, ["ht"] = 3, ["vt"] = 2,
                    ["n"] = new JObject { ["pattern"] = "#,##0.00" }
                }
            },
            ["sheetOrder"] = new JArray("report-sheet"),
            ["sheets"] = new JObject
            {
                ["report-sheet"] = new JObject
                {
                    ["id"] = "report-sheet",
                    ["name"] = report.SheetName,
                    ["hidden"] = 0,
                    ["rowCount"] = 50,
                    ["columnCount"] = Math.Max(12, usedColumnCount),
                    ["zoomRatio"] = 1,
                    ["defaultColumnWidth"] = 100,
                    ["defaultRowHeight"] = 26,
                    ["mergeData"] = new JArray
                    {
                        new JObject
                        {
                            ["startRow"] = 0, ["endRow"] = 0, ["startColumn"] = 0, ["endColumn"] = usedColumnCount - 1
                        }
                    },
                    ["cellData"] = cellData,
                    ["rowData"] = rowData,
                    ["columnData"] = columnData,
                    ["rowHeader"] = new JObject { ["width"] = 46, ["hidden"] = 0 },
                    ["columnHeader"] = new JObject { ["height"] = 24, ["hidden"] = 0 },
                    ["showGridlines"] = 1,
                    ["rightToLeft"] = 0
                }
            }
        };
    }

    /// <summary>构造规范绑定：params token 逐次出现各记一条并按 (sheet,row,column,code) 排序，明细绑定固定 'detail'。</summary>
    /// <param name="report">报表设计数据。</param>
    /// <param name="template">模板设计数据。</param>
    /// <returns>与后端 PocTemplateValidator.Scan 输出一致的绑定。</returns>
    private static PocTemplateBinding BuildBinding(PocAiSpecReportDesign report, PocAiSpecTemplateDesign template)
    {
        _ = report;
        var parameterBindings = new List<PocParameterBinding>();
        foreach (var (text, row) in new[] { (template.Title, 0), (template.ParameterLine, 1) })
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            foreach (var (kind, tokenCode) in MatchTokens(text))
            {
                if (kind.Equals("detail", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                parameterBindings.Add(new PocParameterBinding(tokenCode, "report-sheet", row, 0));
            }
        }

        var ordered = parameterBindings
            .OrderBy(item => item.SheetId, StringComparer.Ordinal)
            .ThenBy(item => item.Row)
            .ThenBy(item => item.Column)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        var fields = template.Columns
            .Select(item => new PocDetailFieldBinding(item.Field, item.ColumnIndex))
            .ToArray();
        return new PocTemplateBinding(
            1,
            ordered,
            new PocDetailBinding("detail", "report-sheet", 3, fields));
    }

    /// <summary>构造快照单元格：公式在前、值次之、样式引用最后，与 ps1 New-Cell 的键序一致。</summary>
    /// <param name="value">显示文本；公式单元格传 null。</param>
    /// <param name="style">命名样式键。</param>
    /// <param name="formula">公式（含前导 =）；无公式传 null。</param>
    /// <returns>Univer cellData 单元格对象。</returns>
    private static JObject Cell(string? value, string style, string? formula)
    {
        var cell = new JObject();
        if (!string.IsNullOrEmpty(formula))
        {
            cell["f"] = formula;
        }
        if (value is not null)
        {
            cell["v"] = value;
        }
        if (!string.IsNullOrEmpty(style))
        {
            cell["s"] = style;
        }
        return cell;
    }

    /// <summary>一基列序号转 Excel A1 列字母（1→A，27→AA），供合计行 SUM 公式使用。</summary>
    /// <param name="index">一基列序号。</param>
    /// <returns>列字母文本。</returns>
    private static string ColumnIndexToLetter(int index)
    {
        var letters = string.Empty;
        var value = index;
        while (value > 0)
        {
            var remainder = (value - 1) % 26;
            letters = (char)(65 + remainder) + letters;
            value = (value - 1) / 26;
        }
        return letters;
    }

    /// <summary>枚举节点下的对象数组项及原始下标；属性缺失、null 或非数组时返回空序列。</summary>
    /// <param name="node">宿主节点。</param>
    /// <param name="key">数组属性名。</param>
    /// <returns>(JObject 项, 原始下标) 序列。</returns>
    private static IEnumerable<(JObject Item, int Index)> Items(JObject node, string key)
    {
        if (node[key] is not JArray array)
        {
            yield break;
        }
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is JObject item)
            {
                yield return (item, index);
            }
        }
    }

    /// <summary>空安全读取字符串属性；缺失或 null 返回 null，其余按文本返回。</summary>
    /// <param name="node">JSON 对象。</param>
    /// <param name="key">属性名。</param>
    /// <returns>属性文本。</returns>
    private static string? Text(JObject node, string key)
    {
        var token = node[key];
        return token is null || token.Type == JTokenType.Null
            ? null
            : token.ToString();
    }

    /// <summary>读取整数属性；缺失/null 返回默认值，非法形态记错误并返回默认值。</summary>
    /// <param name="node">JSON 对象。</param>
    /// <param name="key">属性名。</param>
    /// <param name="fallback">默认值。</param>
    /// <param name="issues">错误收集器。</param>
    /// <returns>整数值。</returns>
    private static int IntegerOr(JObject node, string key, int fallback, List<PocAiSpecIssue> issues)
    {
        var token = node[key];
        if (token is null || token.Type == JTokenType.Null)
        {
            return fallback;
        }
        if (token.Type == JTokenType.Integer)
        {
            return token.Value<int>();
        }
        if (token.Type == JTokenType.String &&
            int.TryParse(token.Value<string>(), out var parsed))
        {
            return parsed;
        }
        issues.Add(new PocAiSpecIssue(key, "SPEC_VALUE_INVALID", $"字段 '{key}' 必须是整数，当前为 '{token.ToString(Newtonsoft.Json.Formatting.None)}'。"));
        return fallback;
    }

    /// <summary>读取布尔属性；缺失/null 返回默认值，非法形态按 false 处理并交由白名单校验兜底。</summary>
    /// <param name="node">JSON 对象。</param>
    /// <param name="key">属性名。</param>
    /// <param name="fallback">默认值。</param>
    /// <returns>布尔值。</returns>
    private static bool BooleanOr(JObject node, string key, bool fallback)
    {
        var token = node[key];
        if (token is null || token.Type == JTokenType.Null)
        {
            return fallback;
        }
        return token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.Integer => token.Value<long>() != 0,
            _ => fallback
        };
    }

    /// <summary>按忽略大小写匹配选项清单并回填规范大小写；未命中返回 null。</summary>
    /// <param name="value">原始输入。</param>
    /// <param name="choices">规范选项。</param>
    /// <param name="raw">原样回传的输入文本。</param>
    /// <returns>规范选项或 null。</returns>
    private static string? NormalizeChoice(string? value, IReadOnlyList<string> choices, out string? raw)
    {
        raw = value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return choices.FirstOrDefault(choice => choice.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析模板文本中的 (params|detail) token 序列，正则与后端校验器一致。</summary>
    /// <param name="text">模板文本。</param>
    /// <returns>(token 类型, 编码) 序列。</returns>
    private static IEnumerable<(string Kind, string Code)> MatchTokens(string text)
    {
        foreach (Match match in TemplateTokenRegex().Matches(text))
        {
            yield return (match.Groups[1].Value, match.Groups[2].Value);
        }
    }

    /// <summary>条件断言：条件不成立时按字段路径记录一条结构化错误。</summary>
    /// <param name="issues">错误收集器。</param>
    /// <param name="path">spec 字段路径。</param>
    /// <param name="condition">校验条件。</param>
    /// <param name="code">稳定错误码。</param>
    /// <param name="message">中文失败原因。</param>
    private static void AssertDesign(
        List<PocAiSpecIssue> issues,
        string path,
        bool condition,
        string code,
        string message)
    {
        if (!condition)
        {
            issues.Add(new PocAiSpecIssue(path, code, message));
        }
    }

    /// <summary>编码断言：字母开头、仅字母数字下划线、最长 64。</summary>
    /// <param name="code">编码文本。</param>
    /// <param name="path">spec 字段路径。</param>
    /// <param name="label">定位用中文名。</param>
    /// <param name="issues">错误收集器。</param>
    private static void AssertSpecCode(string? code, string path, string label, List<PocAiSpecIssue> issues)
    {
        AssertDesign(
            issues,
            path,
            !string.IsNullOrWhiteSpace(code) &&
                System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Za-z][A-Za-z0-9_]{0,63}$"),
            "SPEC_CODE_INVALID",
            $"{label} '{code}' 不符合编码规则：字母开头，仅允许字母/数字/下划线，最长 64 字符。");
    }

    /// <summary>
    /// 数值范围断言：把运行参数（超时/行数/保留小时等）钳制在后端保存链路同一边界内，
    /// 越界时按字段路径记录结构化错误，避免非法数值流入报表包再被校验器退回。
    /// </summary>
    /// <param name="issues">错误收集器。</param>
    /// <param name="path">spec 字段路径。</param>
    /// <param name="value">数值。</param>
    /// <param name="min">下限（含）。</param>
    /// <param name="max">上限（含）。</param>
    /// <param name="message">中文失败原因。</param>
    private static void AssertRange(
        List<PocAiSpecIssue> issues,
        string path,
        int value,
        int min,
        int max,
        string message)
    {
        AssertDesign(issues, path, value >= min && value <= max, "SPEC_VALUE_INVALID", message);
    }

    /// <summary>把节点类型键翻译为面向模型的中文描述，用于未知字段错误消息。</summary>
    /// <param name="nodeKind">节点类型键。</param>
    /// <returns>中文描述。</returns>
    private static string DescribeNode(string nodeKind) => nodeKind switch
    {
        "$" => "spec 根节点",
        "report" => "report 节",
        "preview" => "report.preview 节",
        "export" => "report.export 节",
        "dataset" => "dataset 节",
        "variable" => "dataset.variables 项",
        "field" => "dataset.fields 项",
        "parameter" => "parameters 项",
        "template" => "template 节",
        "column" => "template.columns 项",
        "total" => "template.totals 项",
        "presentation" => "report.fieldPresentations 项",
        "mapping" => "valueMappings 项",
        _ => nodeKind
    };
}
