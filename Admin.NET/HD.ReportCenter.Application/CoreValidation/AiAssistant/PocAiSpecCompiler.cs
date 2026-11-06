using System.Text.RegularExpressions;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>spec 编译错误/警告单条记录；Path 是 spec JSON 字段路径，直接回传 LLM 供自修。</summary>
public sealed record PocAiSpecIssue(string Path, string Code, string Message);

/// <summary>spec 编译结果；Ok=false 时 Issues 非空且 Package 为空，Ok=true 时 Issues 可含警告。</summary>
public sealed record PocAiSpecCompileResult(bool Ok, IReadOnlyList<PocAiSpecIssue> Issues, PocReportPackage? Package);

/// <summary>
/// C# 版 spec→报表包编译器：与 scripts/report-design/New-ReportPackage.ps1 输出同构
/// （固定布局快照 + 规范绑定 + 四节包结构），供 AI submit/dry_run 工具在服务端内存态编译。
/// 有意分歧：ps1 对未知字段宽松忽略，本编译器对 specVersion=1 未知字段严格拒绝，
/// 防止 AI 输出的未支持配置被静默丢弃；ps1 编译器本身保留不动，离线流水线继续使用。
/// </summary>
public sealed partial class PocAiSpecCompiler
{
    /// <summary>与后端 PocDatabaseProvider.SupportedTypes 对齐的数据源类型白名单（规范大小写）。</summary>
    private static readonly string[] SupportedDataSourceTypes = ["SqlServer", "Oracle", "MySql", "PostgreSQL"];

    /// <summary>与后端 PocDataAssetService 校验白名单对齐的数据类型清单。</summary>
    private static readonly string[] VariableDataTypes = ["string", "int", "long", "decimal", "date", "datetime", "bool"];
    private static readonly string[] FieldDataTypes = ["string", "number", "date", "datetime", "bool"];
    private static readonly string[] ParameterDataTypes = ["string", "text", "int", "long", "decimal", "date", "datetime", "bool", "boolean"];
    private static readonly string[] ParameterSourceTypes = ["Form", "Url", "System", "Fixed", "External"];
    private static readonly string[] ParameterControlTypes =
        ["text", "number", "date", "datetime", "month", "year", "checkbox", "select", "multiSelect", "hidden"];

    /// <summary>与后端 PocTemplateValidator.TokenRegex 一致的模板 token 正则（允许 token 两侧空白）。</summary>
    [GeneratedRegex(@"\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9_]*)[ \t\r\n\f\v]*\}\}")]
    private static partial Regex TemplateTokenRegex();

    /// <summary>specVersion=1 各节点允许的字段集合；未知字段严格拒绝是 C# 编译器的有意加强项。</summary>
    private static readonly Dictionary<string, IReadOnlySet<string>> AllowedKeys = new()
    {
        ["$"] = new HashSet<string>(["specVersion", "report", "dataset", "parameters", "template"]),
        ["report"] = new HashSet<string>(["code", "name", "sheetName", "preview", "export", "fieldPresentations"]),
        ["preview"] = new HashSet<string>(["mode", "pageSize", "showExactTotal"]),
        ["export"] = new HashSet<string>([
            "defaultMode", "normalMaxRows", "normalMaxCells", "normalTimeoutSeconds",
            "largeExportEnabled", "rowsPerSheet", "fileRetentionHours"]),
        ["dataset"] = new HashSet<string>([
            "code", "name", "dataSourceCode", "dataSourceType", "timeoutSeconds",
            "maxRows", "queryText", "variables", "fields"]),
        ["variable"] = new HashSet<string>(["code", "displayName", "dataType", "required"]),
        ["field"] = new HashSet<string>(["code", "displayName", "dataType"]),
        ["parameter"] = new HashSet<string>([
            "code", "displayName", "dataType", "controlType", "sourceType", "isRequired",
            "isReadOnly", "isVisible", "allowUrlPrefill", "urlKey", "defaultExpression",
            "fixedValue", "options", "displayOrder"]),
        ["template"] = new HashSet<string>(["title", "parameterLine", "columns", "totals"]),
        ["column"] = new HashSet<string>(["field", "header"]),
        ["total"] = new HashSet<string>(["field", "label"]),
        ["presentation"] = new HashSet<string>(["code", "valueMappings"]),
        ["mapping"] = new HashSet<string>(["value", "label"])
    };

    /// <summary>编译 spec：逐节校验（结构、命名、白名单、变量绑定、SQL 引用），全部通过后构造与 ps1 同构的报表包。</summary>
    /// <param name="specJson">LLM 提交的 spec JSON 文本。</param>
    /// <returns>编译结果；失败时 Issues 携带 spec 字段路径供回传模型自修。</returns>
    public PocAiSpecCompileResult Compile(string specJson)
    {
        var issues = new List<PocAiSpecIssue>();
        JObject spec;
        try
        {
            spec = JObject.Parse(specJson ?? string.Empty);
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            issues.Add(new PocAiSpecIssue("$", "SPEC_JSON_INVALID", $"spec 不是合法 JSON：{exception.Message}"));
            return new PocAiSpecCompileResult(false, issues, null);
        }

        // 第一阶段：specVersion 与未知字段严格校验（有意分歧项；ps1 宽松忽略，AI 通道必须拒绝）。
        // specVersion 错误是结构性问题，立即返回；根级未知字段只记账，后续各节错误继续收集，
        // 让模型一次拿到完整错误列表而不是逐轮试错。
        AssertSpecVersion(spec, issues);
        AssertAllowedKeys("$", "$", spec, issues);
        if (issues.Any(item => item.Code == "SPEC_VERSION_INVALID"))
        {
            return new PocAiSpecCompileResult(false, issues, null);
        }

        // 第二阶段：三大节依次校验；数据集是模板/绑定对齐的基础，其字段缺失时跳过模板节以免噪声报错。
        var datasetDesign = ReadDatasetDesign(spec, issues);
        var reportDesign = ReadReportAndParameterDesign(spec, datasetDesign, issues);
        var templateDesign = ReadTemplateDesign(spec, datasetDesign, reportDesign, issues);
        AssertSqlReferences(datasetDesign, reportDesign?.PreviewMode, issues);
        if (issues.Count > 0 || datasetDesign is null || reportDesign is null || templateDesign is null)
        {
            return new PocAiSpecCompileResult(false, issues, null);
        }

        // 第三阶段：构造包对象（快照 + 绑定 + 四节结构，与 ps1 逐字段对齐）。
        var package = BuildPackage(spec, reportDesign, datasetDesign, templateDesign);
        return new PocAiSpecCompileResult(true, issues, package);
    }

    /// <summary>校验 specVersion 必须存在且为 1；其余版本直接拒绝，不猜测兼容。</summary>
    /// <param name="spec">spec 根对象。</param>
    /// <param name="issues">错误收集器。</param>
    private static void AssertSpecVersion(JObject spec, List<PocAiSpecIssue> issues)
    {
        var version = spec["specVersion"];
        if (version is null || version.Type != JTokenType.Integer || version.Value<int>() != 1)
        {
            issues.Add(new PocAiSpecIssue(
                "specVersion",
                "SPEC_VERSION_INVALID",
                $"specVersion 必须为 1，当前为 '{version?.ToString(Newtonsoft.Json.Formatting.None) ?? "（缺失）"}'。"));
        }
    }

    /// <summary>按节点类型校验未知字段；未知字段意味着 AI 输出了未支持配置，必须显式拒绝而不是静默丢弃。</summary>
    /// <param name="nodeKind">节点类型键（对应 AllowedKeys）。</param>
    /// <param name="path">该节点在 spec 中的字段路径。</param>
    /// <param name="node">待检 JSON 对象。</param>
    /// <param name="issues">错误收集器。</param>
    private static void AssertAllowedKeys(
        string nodeKind,
        string path,
        JObject node,
        List<PocAiSpecIssue> issues)
    {
        if (!AllowedKeys.TryGetValue(nodeKind, out var allowed))
        {
            return;
        }

        var unknown = node.Properties()
            .Where(property => !allowed.Contains(property.Name))
            .Select(property => property.Name)
            .ToArray();
        if (unknown.Length > 0)
        {
            issues.Add(new PocAiSpecIssue(
                path,
                "SPEC_FIELD_UNKNOWN",
                $"{DescribeNode(nodeKind)}包含不支持的配置字段：{string.Join("、", unknown)}。" +
                "specVersion=1 只允许声明范围内的字段；未支持的能力会被静默丢弃，因此这里直接拒绝。"));
        }
    }

    /// <summary>读取并校验 spec 的 dataset 节：编码、白名单、运行参数与变量/字段契约。</summary>
    /// <param name="spec">spec 根对象。</param>
    /// <param name="issues">错误收集器；可累积多条。</param>
    /// <returns>规范化数据集设计；结构缺失时为 null。</returns>
    private PocAiSpecDatasetDesign? ReadDatasetDesign(JObject spec, List<PocAiSpecIssue> issues)
    {
        var dataset = spec["dataset"] as JObject;
        if (dataset is null)
        {
            issues.Add(new PocAiSpecIssue("dataset", "SPEC_SECTION_MISSING", "缺少 dataset 节。"));
            return null;
        }
        AssertAllowedKeys("dataset", "dataset", dataset, issues);

        var code = Text(dataset, "code");
        var name = Text(dataset, "name");
        AssertSpecCode(code, "dataset.code", "数据集编码", issues);
        AssertDesign(
            issues,
            "dataset.name",
            !string.IsNullOrWhiteSpace(name) && name.Length <= 128,
            "SPEC_VALUE_INVALID",
            "数据集名称必填且不超过 128 字符。");
        var dataSourceCode = Text(dataset, "dataSourceCode");
        AssertSpecCode(dataSourceCode, "dataset.dataSourceCode", "数据源编码", issues);
        var normalizedType = NormalizeChoice(
            Text(dataset, "dataSourceType"),
            SupportedDataSourceTypes,
            out var rawType);
        if (normalizedType is null)
        {
            issues.Add(new PocAiSpecIssue(
                "dataset.dataSourceType",
                "SPEC_VALUE_INVALID",
                $"数据源类型 '{rawType}' 不受支持，仅允许 {string.Join(" / ", SupportedDataSourceTypes)}。"));
        }

        // 第一段：SQL 与运行参数校验；超时/行数上限与后端保存链路一致。
        var queryText = Text(dataset, "queryText");
        AssertDesign(issues, "dataset.queryText", !string.IsNullOrWhiteSpace(queryText), "SPEC_VALUE_INVALID", "数据集 queryText 必填。");
        AssertDesign(issues, "dataset.queryText", queryText.Length <= 200_000, "SPEC_VALUE_INVALID", "数据集 queryText 超过 200000 字符上限。");
        var timeoutSeconds = IntegerOr(dataset, "timeoutSeconds", 60, issues);
        var maxRows = IntegerOr(dataset, "maxRows", 20_000, issues);
        AssertRange(issues, "dataset.timeoutSeconds", timeoutSeconds, 1, 600, "数据集 timeoutSeconds 必须在 1-600 之间。");
        AssertRange(issues, "dataset.maxRows", maxRows, 1, 1_000_000, "数据集 maxRows 必须在 1-1000000 之间。");

        // 第二段：变量契约——编码唯一、类型白名单、required 默认 false，数量上限 100。
        var variables = new List<PocDatasetVariableDefinition>();
        var variableCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (variable, index) in Items(dataset, "variables"))
        {
            AssertAllowedKeys("variable", $"dataset.variables[{index}]", variable, issues);
            var path = $"dataset.variables[{index}]";
            var variableCode = Text(variable, "code");
            AssertSpecCode(variableCode, path, "变量编码", issues);
            var dataType = Text(variable, "dataType");
            var matchedType = NormalizeChoice(dataType, VariableDataTypes, out var rawDataType);
            if (matchedType is null)
            {
                issues.Add(new PocAiSpecIssue(
                    $"{path}.dataType",
                    "SPEC_VALUE_INVALID",
                    $"变量 '{variableCode}' 的 dataType '{rawDataType}' 不受支持，仅允许 {string.Join(" / ", VariableDataTypes)}。"));
            }
            if (!variableCodes.Add(variableCode))
            {
                issues.Add(new PocAiSpecIssue(path, "SPEC_VALUE_INVALID", $"数据集变量编码 '{variableCode}' 重复。"));
                continue;
            }
            variables.Add(new PocDatasetVariableDefinition(
                variableCode,
                Text(variable, "displayName"),
                matchedType ?? (dataType ?? string.Empty).Trim(),
                BooleanOr(variable, "required", false)));
        }
        AssertDesign(issues, "dataset.variables", variables.Count <= 100, "SPEC_VALUE_INVALID", "数据集变量数量不能超过 100。");

        // 第三段：字段契约——编码唯一（忽略大小写）、类型白名单、1-256 个。
        var fields = new List<PocDatasetFieldDefinition>();
        var fieldCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, index) in Items(dataset, "fields"))
        {
            AssertAllowedKeys("field", $"dataset.fields[{index}]", field, issues);
            var path = $"dataset.fields[{index}]";
            var fieldCode = Text(field, "code");
            AssertSpecCode(fieldCode, path, "字段编码", issues);
            var dataType = Text(field, "dataType");
            var matchedType = NormalizeChoice(dataType, FieldDataTypes, out var rawFieldType);
            if (matchedType is null)
            {
                issues.Add(new PocAiSpecIssue(
                    $"{path}.dataType",
                    "SPEC_VALUE_INVALID",
                    $"字段 '{fieldCode}' 的 dataType '{rawFieldType}' 不受支持，仅允许 {string.Join(" / ", FieldDataTypes)}。"));
            }
            if (!fieldCodes.Add(fieldCode))
            {
                issues.Add(new PocAiSpecIssue(path, "SPEC_VALUE_INVALID", $"数据集字段编码 '{fieldCode}' 重复。"));
                continue;
            }
            fields.Add(new PocDatasetFieldDefinition(
                fieldCode,
                Text(field, "displayName"),
                matchedType ?? (dataType ?? string.Empty).Trim()));
        }
        AssertDesign(issues, "dataset.fields", fields.Count > 0 && fields.Count <= 256, "SPEC_VALUE_INVALID", "数据集 fields 必填且不超过 256 个。");

        if (issues.Count > 0)
        {
            return null;
        }
        return new PocAiSpecDatasetDesign(
            code, name!, dataSourceCode, normalizedType!, queryText, timeoutSeconds, maxRows, variables, fields);
    }

    /// <summary>读取并校验 spec 的 report/parameters 节：标识、预览与导出设置、参数定义与变量绑定对齐。</summary>
    /// <param name="spec">spec 根对象。</param>
    /// <param name="datasetDesign">已规范化的数据集设计（变量绑定对齐需要）。</param>
    /// <param name="issues">错误收集器。</param>
    /// <returns>规范化报表设计；结构缺失时为 null。</returns>
    private PocAiSpecReportDesign? ReadReportAndParameterDesign(
        JObject spec,
        PocAiSpecDatasetDesign? datasetDesign,
        List<PocAiSpecIssue> issues)
    {
        var report = spec["report"] as JObject;
        if (report is null)
        {
            issues.Add(new PocAiSpecIssue("report", "SPEC_SECTION_MISSING", "缺少 report 节。"));
            return null;
        }
        AssertAllowedKeys("report", "report", report, issues);

        var code = Text(report, "code");
        var name = Text(report, "name");
        var sheetName = Text(report, "sheetName");
        AssertSpecCode(code, "report.code", "报表编码", issues);
        AssertDesign(issues, "report.name", !string.IsNullOrWhiteSpace(name) && name.Length <= 128, "SPEC_VALUE_INVALID", "报表名称必填且不超过 128 字符。");
        AssertDesign(issues, "report.sheetName", !string.IsNullOrWhiteSpace(sheetName) && sheetName.Length <= 31, "SPEC_VALUE_INVALID", "工作表名 sheetName 必填且不超过 31 字符。");
        // sheetName 缺失时上一条断言已记 SPEC_VALUE_INVALID，此处必须短路跳过字符白名单检查，避免空引用。
        AssertDesign(
            issues,
            "report.sheetName",
            string.IsNullOrEmpty(sheetName) || sheetName.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) < 0,
            "SPEC_VALUE_INVALID",
            "工作表名 sheetName 不能包含 []:*?/\\ 字符。");

        // 第一段：预览与导出设置；缺省值与 ps1/后端建议一致，Paged 的 ORDER BY 在 SQL 阶段判定。
        var preview = report["preview"] as JObject ?? [];
        AssertAllowedKeys("preview", "report.preview", preview, issues);
        var previewModeRaw = Text(preview, "mode") ?? "Full";
        var previewMode = previewModeRaw.Equals("Paged", StringComparison.OrdinalIgnoreCase) ? "Paged" : "Full";
        AssertDesign(
            issues,
            "report.preview.mode",
            previewModeRaw.Equals("Full", StringComparison.OrdinalIgnoreCase) ||
                previewModeRaw.Equals("Paged", StringComparison.OrdinalIgnoreCase),
            "SPEC_VALUE_INVALID",
            "preview.mode 只允许 Full 或 Paged。");
        var pageSize = IntegerOr(preview, "pageSize", 20, issues);
        AssertRange(issues, "report.preview.pageSize", pageSize, 1, 10_000, "preview.pageSize 必须在 1-10000 之间。");

        // 预览与导出缺省值对齐 ps1 与后端建议；defaultMode=Large 必须 largeExportEnabled 同步开启。
        var export = report["export"] as JObject ?? [];
        AssertAllowedKeys("export", "report.export", export, issues);
        var defaultModeRaw = Text(export, "defaultMode") ?? "Normal";
        AssertDesign(
            issues,
            "report.export.defaultMode",
            defaultModeRaw.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
                defaultModeRaw.Equals("Large", StringComparison.OrdinalIgnoreCase),
            "SPEC_VALUE_INVALID",
            "export.defaultMode 只允许 Normal 或 Large。");
        var defaultMode = defaultModeRaw.Equals("Large", StringComparison.OrdinalIgnoreCase);
        var normalMaxRows = IntegerOr(export, "normalMaxRows", 20_000, issues);
        var normalMaxCells = IntegerOr(export, "normalMaxCells", 500_000, issues);
        var normalTimeoutSeconds = IntegerOr(export, "normalTimeoutSeconds", 60, issues);
        var largeExportEnabled = BooleanOr(export, "largeExportEnabled", true);
        var rowsPerSheet = IntegerOr(export, "rowsPerSheet", 1_000_000, issues);
        var fileRetentionHours = IntegerOr(export, "fileRetentionHours", 24, issues);
        AssertRange(issues, "report.export.normalMaxRows", normalMaxRows, 1, 1_000_000, "export.normalMaxRows 必须在 1-1000000 之间。");
        AssertRange(issues, "report.export.normalMaxCells", normalMaxCells, 1, 20_000_000, "export.normalMaxCells 必须在 1-20000000 之间。");
        AssertRange(issues, "report.export.normalTimeoutSeconds", normalTimeoutSeconds, 1, 3600, "export.normalTimeoutSeconds 必须在 1-3600 之间。");
        AssertRange(issues, "report.export.rowsPerSheet", rowsPerSheet, 1, 1_048_576, "export.rowsPerSheet 必须在 1-1048576 之间。");
        AssertRange(issues, "report.export.fileRetentionHours", fileRetentionHours, 1, 720, "export.fileRetentionHours 必须在 1-720 之间。");
        AssertDesign(
            issues,
            "report.export.defaultMode",
            !defaultMode || largeExportEnabled,
            "SPEC_VALUE_INVALID",
            "export.defaultMode 为 Large 时必须开启 largeExportEnabled。");

        // 第二段：参数定义——编码/白名单/唯一性；options 仅允许显式 null（外部源依赖在报表中心配置）。
        // 参数循环逐项独立校验并累积错误，让模型一次看到全部参数问题而不是首错即停。
        var parameters = new List<PocAiSpecParameterDesign>();
        var parameterCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawParameters = Items(spec, "parameters").ToList();
        for (var index = 0; index < rawParameters.Count; index++)
        {
            var (parameter, listedIndex) = rawParameters[index];
            AssertAllowedKeys("parameter", $"parameters[{listedIndex}]", parameter, issues);
            var path = $"parameters[{listedIndex}]";
            var parameterCode = Text(parameter, "code");
            AssertSpecCode(parameterCode, path, "参数编码", issues);
            if (parameter["options"] is not null && parameter["options"]!.Type != JTokenType.Null)
            {
                issues.Add(new PocAiSpecIssue(
                    $"{path}.options",
                    "SPEC_VALUE_INVALID",
                    $"参数 '{parameterCode}' 的 options 需在报表中心配置页另行设置，spec 流水线只允许 null。"));
            }
            var dataType = Text(parameter, "dataType");
            var matchedDataType = NormalizeChoice(dataType, ParameterDataTypes, out var rawDataType);
            if (matchedDataType is null)
            {
                issues.Add(new PocAiSpecIssue(
                    $"{path}.dataType",
                    "SPEC_VALUE_INVALID",
                    $"参数 '{parameterCode}' 的 dataType '{rawDataType}' 不受支持，仅允许 {string.Join(" / ", ParameterDataTypes)}。"));
            }
            var controlType = Text(parameter, "controlType") ?? "text";
            var matchedControlType = NormalizeChoice(controlType, ParameterControlTypes, out var rawControlType);
            if (matchedControlType is null)
            {
                issues.Add(new PocAiSpecIssue(
                    $"{path}.controlType",
                    "SPEC_VALUE_INVALID",
                    $"参数 '{parameterCode}' 的 controlType '{rawControlType}' 不受支持，仅允许 {string.Join(" / ", ParameterControlTypes)}。"));
            }
            var sourceType = Text(parameter, "sourceType") ?? "Form";
            var matchedSourceType = NormalizeChoice(sourceType, ParameterSourceTypes, out var rawSourceType);
            if (matchedSourceType is null)
            {
                issues.Add(new PocAiSpecIssue(
                    $"{path}.sourceType",
                    "SPEC_VALUE_INVALID",
                    $"参数 '{parameterCode}' 的 sourceType '{rawSourceType}' 不受支持，仅允许 {string.Join(" / ", ParameterSourceTypes)}。"));
            }
            var defaultExpression = Text(parameter, "defaultExpression") ?? string.Empty;
            AssertDesign(
                issues,
                $"{path}.defaultExpression",
                defaultExpression.Length <= 256,
                "SPEC_VALUE_INVALID",
                $"参数 '{parameterCode}' 的 defaultExpression 超过 256 字符。");
            if (!parameterCodes.Add(parameterCode))
            {
                issues.Add(new PocAiSpecIssue(path, "SPEC_VALUE_INVALID", $"参数编码 '{parameterCode}' 重复。"));
                continue;
            }
            parameters.Add(new PocAiSpecParameterDesign(
                parameterCode,
                Text(parameter, "displayName") ?? string.Empty,
                matchedDataType ?? (dataType ?? string.Empty).Trim(),
                matchedControlType ?? (controlType ?? string.Empty).Trim(),
                matchedSourceType ?? (sourceType ?? string.Empty).Trim(),
                Text(parameter, "urlKey"),
                string.IsNullOrWhiteSpace(defaultExpression) ? null : defaultExpression,
                Text(parameter, "fixedValue"),
                BooleanOr(parameter, "isVisible", true),
                BooleanOr(parameter, "isReadOnly", false),
                BooleanOr(parameter, "isRequired", false),
                BooleanOr(parameter, "allowUrlPrefill", false),
                IntegerOr(parameter, "displayOrder", index + 1, issues)));
        }

        // 第三段：变量绑定对齐——每个数据集变量必须有同名同类型（忽略大小写）参数承接。
        if (datasetDesign is not null)
        {
            foreach (var variable in datasetDesign.Variables)
            {
                var bound = parameters.Any(candidate =>
                    candidate.Code.Equals(variable.Code, StringComparison.OrdinalIgnoreCase) &&
                    candidate.DataType.Equals(variable.DataType, StringComparison.OrdinalIgnoreCase));
                AssertDesign(
                    issues,
                    "dataset.variables",
                    bound,
                    "SPEC_VARIABLE_BINDING_MISSING",
                    $"数据集变量 '@{variable.Code}'（{variable.DataType}）缺少同名同类型的报表参数承接，请在 parameters 中补充。");
            }
        }

        var presentations = ReadFieldPresentations(report, issues);
        if (issues.Count > 0 || name is null || sheetName is null)
        {
            return null;
        }
        return new PocAiSpecReportDesign(
            code, name, sheetName, previewMode, pageSize, BooleanOr(preview, "showExactTotal", false),
            defaultMode, normalMaxRows, normalMaxCells, normalTimeoutSeconds, largeExportEnabled,
            rowsPerSheet, fileRetentionHours, parameters, presentations);
    }

    /// <summary>读取可选的 report.fieldPresentations；结构逐项校验，字段值映射原样透传。</summary>
    /// <param name="report">report 节点。</param>
    /// <param name="issues">错误收集器。</param>
    /// <returns>字段显示映射列表；未声明时为 null。</returns>
    private static IReadOnlyList<PocFieldPresentationDefinition>? ReadFieldPresentations(
        JObject report,
        List<PocAiSpecIssue> issues)
    {
        var raw = report["fieldPresentations"] as JArray;
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        var presentations = new List<PocFieldPresentationDefinition>();
        for (var index = 0; index < raw.Count; index++)
        {
            var presentation = raw[index] as JObject;
            if (presentation is null)
            {
                issues.Add(new PocAiSpecIssue(
                    $"report.fieldPresentations[{index}]",
                    "SPEC_VALUE_INVALID",
                    "fieldPresentations 项必须是对象。"));
                continue;
            }
            var mappings = new List<PocValueOption>();
            var rawMappings = presentation["valueMappings"] as JArray ?? [];
            for (var mappingIndex = 0; mappingIndex < rawMappings.Count; mappingIndex++)
            {
                var mapping = rawMappings[mappingIndex] as JObject;
                if (mapping is null)
                {
                    issues.Add(new PocAiSpecIssue(
                        $"report.fieldPresentations[{index}].valueMappings[{mappingIndex}]",
                        "SPEC_VALUE_INVALID",
                        "valueMappings 项必须是对象。"));
                    continue;
                }
                mappings.Add(new PocValueOption(Text(mapping, "value") ?? string.Empty, Text(mapping, "label") ?? string.Empty));
            }
            presentations.Add(new PocFieldPresentationDefinition(Text(presentation, "code") ?? string.Empty, mappings));
        }
        return presentations;
    }

    /// <summary>读取并校验 spec 的 template 节：标题/参数行 token、列声明与合计行。</summary>
    /// <param name="spec">spec 根对象。</param>
    /// <param name="datasetDesign">数据集设计（列字段精确对齐需要）。</param>
    /// <param name="reportDesign">报表设计（参数引用校验需要）。</param>
    /// <param name="issues">错误收集器。</param>
    /// <returns>规范化模板设计；结构缺失时为 null。</returns>
    private PocAiSpecTemplateDesign? ReadTemplateDesign(
        JObject spec,
        PocAiSpecDatasetDesign? datasetDesign,
        PocAiSpecReportDesign? reportDesign,
        List<PocAiSpecIssue> issues)
    {
        var template = spec["template"] as JObject;
        if (template is null)
        {
            issues.Add(new PocAiSpecIssue("template", "SPEC_SECTION_MISSING", "缺少 template 节。"));
            return null;
        }
        AssertAllowedKeys("template", "template", template, issues);

        // 第一段：标题与参数行。title 允许混排 params token；detail token 一律拒绝（明细列在 columns 声明）。
        var title = Text(template, "title");
        AssertDesign(issues, "template.title", !string.IsNullOrWhiteSpace(title), "SPEC_VALUE_INVALID", "template.title 必填。");
        var parameterLine = Text(template, "parameterLine") ?? string.Empty;
        foreach (var (text, path) in new[] { (title, "template.title"), (parameterLine, "template.parameterLine") })
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            foreach (var (kind, tokenCode) in MatchTokens(text))
            {
                AssertDesign(
                    issues,
                    path,
                    !kind.Equals("detail", StringComparison.OrdinalIgnoreCase),
                    "SPEC_VALUE_INVALID",
                    "标题/参数行不允许出现 detail token，明细列请在 template.columns 中声明。");
                AssertDesign(
                    issues,
                    path,
                    reportDesign?.Parameters.Any(item => item.Code.Equals(tokenCode, StringComparison.Ordinal)) == true,
                    "SPEC_VALUE_INVALID",
                    $"标题/参数行引用了未定义参数 '{{{{params.{tokenCode}}}}}'。");
            }
            var residual = TemplateTokenRegex().Replace(text, string.Empty);
            AssertDesign(
                issues,
                path,
                !residual.Contains("{{", StringComparison.Ordinal) && !residual.Contains("}}", StringComparison.Ordinal),
                "SPEC_VALUE_INVALID",
                "标题/参数行含无法识别的花括号 token，请检查 {{params.编码}} 写法。");
        }

        // 第二段：列声明——field 必须精确（区分大小写）存在于 dataset.fields，列序号即物理列坐标。
        var columns = new List<PocAiSpecColumnDesign>();
        var columnFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (column, index) in Items(template, "columns"))
        {
            AssertAllowedKeys("column", $"template.columns[{index}]", column, issues);
            var path = $"template.columns[{index}]";
            var field = Text(column, "field");
            AssertDesign(issues, path, !string.IsNullOrEmpty(field), "SPEC_VALUE_INVALID", "template.columns 存在缺少 field 的列。");
            var exactField = datasetDesign?.Fields.FirstOrDefault(item => item.Code == field);
            AssertDesign(
                issues,
                path,
                exactField is not null,
                "SPEC_VALUE_INVALID",
                $"template.columns 的 field '{field}' 不在 dataset.fields 中（区分大小写），请先在数据集字段里声明。");
            if (field is not null && !columnFields.Add(field))
            {
                issues.Add(new PocAiSpecIssue(path, "SPEC_VALUE_INVALID", $"template.columns 的 field '{field}' 重复。"));
                continue;
            }
            var header = Text(column, "header");
            columns.Add(new PocAiSpecColumnDesign(
                field!,
                string.IsNullOrEmpty(header) ? field! : header,
                exactField?.DataType ?? "string",
                columns.Count));
        }
        AssertDesign(issues, "template.columns", columns.Count > 0, "SPEC_VALUE_INVALID", "template.columns 必填，至少一列。");

        // 第三段：合计行——字段必须同为明细列且不得占用首列（首列留给合计标签）。
        var totals = new List<PocAiSpecTotalDesign>();
        foreach (var (total, index) in Items(template, "totals"))
        {
            AssertAllowedKeys("total", $"template.totals[{index}]", total, issues);
            var path = $"template.totals[{index}]";
            var totalField = Text(total, "field");
            AssertDesign(issues, path, !string.IsNullOrEmpty(totalField), "SPEC_VALUE_INVALID", "template.totals 存在缺少 field 的合计项。");
            var label = Text(total, "label");
            AssertDesign(issues, path, !string.IsNullOrEmpty(label), "SPEC_VALUE_INVALID", $"合计项 '{totalField}' 缺少 label 文本。");
            var matchedColumn = columns.FirstOrDefault(item => item.Field == totalField);
            AssertDesign(
                issues,
                path,
                matchedColumn is not null,
                "SPEC_VALUE_INVALID",
                $"template.totals 的 field '{totalField}' 不在 template.columns 中，合计列必须同为明细列。");
            AssertDesign(
                issues,
                path,
                matchedColumn is null || matchedColumn.ColumnIndex > 0,
                "SPEC_VALUE_INVALID",
                $"合计项 '{totalField}' 落在首列；首列用于放置合计标签，请调整列顺序。");
            if (matchedColumn is not null)
            {
                totals.Add(new PocAiSpecTotalDesign(totalField!, label!, matchedColumn.ColumnIndex));
            }
        }

        if (issues.Count > 0 || title is null)
        {
            return null;
        }
        return new PocAiSpecTemplateDesign(title, parameterLine, columns, totals);
    }

    /// <summary>校验 SQL 只读性与变量引用：复用 PocReadOnlySqlValidator（与后端同一防线），错误落到 dataset.queryText 路径。</summary>
    /// <param name="datasetDesign">数据集设计（queryText 与变量清单）。</param>
    /// <param name="previewMode">预览模式（Paged 要求稳定 ORDER BY）。</param>
    /// <param name="issues">错误收集器。</param>
    private static void AssertSqlReferences(
        PocAiSpecDatasetDesign? datasetDesign,
        string? previewMode,
        List<PocAiSpecIssue> issues)
    {
        if (datasetDesign is null)
        {
            return;
        }

        try
        {
            _ = PocReadOnlySqlValidator.Validate(
                datasetDesign.QueryText,
                datasetDesign.Variables.Select(item => item.Code),
                previewMode is not null && previewMode.Equals("Paged", StringComparison.OrdinalIgnoreCase)
                    ? PocPreviewMode.Paged
                    : PocPreviewMode.Full);
        }
        catch (PocDatasetConfigurationException exception)
        {
            issues.Add(new PocAiSpecIssue("dataset.queryText", exception.Code, exception.Message));
        }
    }
}

/// <summary>校验辅助与包构造的内部设计数据模型；全部为不可变记录。</summary>
/// <param name="Code">数据集编码。</param>
/// <param name="Name">数据集名称。</param>
/// <param name="DataSourceCode">数据源编码提示。</param>
/// <param name="DataSourceType">规范化的数据源方言。</param>
/// <param name="QueryText">单条只读 SQL。</param>
/// <param name="TimeoutSeconds">查询超时秒数。</param>
/// <param name="MaxRows">最大行数。</param>
/// <param name="Variables">变量契约。</param>
/// <param name="Fields">字段契约。</param>
public sealed record PocAiSpecDatasetDesign(
    string Code,
    string Name,
    string DataSourceCode,
    string DataSourceType,
    string QueryText,
    int TimeoutSeconds,
    int MaxRows,
    IReadOnlyList<PocDatasetVariableDefinition> Variables,
    IReadOnlyList<PocDatasetFieldDefinition> Fields);

/// <summary>报表节规范化设计数据。</summary>
/// <param name="Parameters">参数清单。</param>
/// <param name="FieldPresentations">可选字段显示映射。</param>
public sealed record PocAiSpecReportDesign(
    string Code,
    string Name,
    string SheetName,
    string PreviewMode,
    int PageSize,
    bool ShowExactTotal,
    bool LargeDefaultMode,
    int NormalMaxRows,
    int NormalMaxCells,
    int NormalTimeoutSeconds,
    bool LargeExportEnabled,
    int RowsPerSheet,
    int FileRetentionHours,
    IReadOnlyList<PocAiSpecParameterDesign> Parameters,
    IReadOnlyList<PocFieldPresentationDefinition>? FieldPresentations);

/// <summary>单个参数的规范化设计数据（options 由报表中心配置页承载，spec 不带）。</summary>
public sealed record PocAiSpecParameterDesign(
    string Code,
    string DisplayName,
    string DataType,
    string ControlType,
    string SourceType,
    string? UrlKey,
    string? DefaultExpression,
    string? FixedValue,
    bool IsVisible,
    bool IsReadOnly,
    bool IsRequired,
    bool AllowUrlPrefill,
    int DisplayOrder);

/// <summary>模板节规范化设计数据。</summary>
/// <param name="Title">标题行文本（可混排 params token）。</param>
/// <param name="ParameterLine">参数说明行。</param>
/// <param name="Columns">明细列（列序号即物理坐标）。</param>
/// <param name="Totals">合计项。</param>
public sealed record PocAiSpecTemplateDesign(
    string Title,
    string ParameterLine,
    IReadOnlyList<PocAiSpecColumnDesign> Columns,
    IReadOnlyList<PocAiSpecTotalDesign> Totals);

/// <summary>明细列设计：字段、表头、字段数据类型与物理列坐标。</summary>
public sealed record PocAiSpecColumnDesign(string Field, string Header, string FieldDataType, int ColumnIndex);

/// <summary>合计项设计：字段、标签与列坐标。</summary>
public sealed record PocAiSpecTotalDesign(string Field, string Label, int ColumnIndex);
