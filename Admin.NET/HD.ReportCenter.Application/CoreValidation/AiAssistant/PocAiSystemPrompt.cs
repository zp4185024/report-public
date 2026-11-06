using System.Reflection;
using System.Text;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>
/// AI 系统提示词：从编译嵌入资源加载模板（改提示词需发版，与后端校验器强一致），
/// 并在会话发起时动态注入数据源清单摘要与所选数据源的表名列表。
/// </summary>
public static class PocAiSystemPrompt
{
    /// <summary>嵌入资源逻辑名；与 csproj 的 EmbeddedResource LogicalName 一一对应。</summary>
    public const string ResourceName =
        "HD.ReportCenter.Application.CoreValidation.AiAssistant.PocAiSystemPrompt.md";

    /// <summary>动态注入段的数据源列表上限；只注入摘要，避免把上下文撑爆。</summary>
    private const int MaxDataSourceLines = 20;

    /// <summary>动态注入段的每源表名列表上限；结构靠按需 describe_table，不带列信息。</summary>
    private const int MaxTableNamesPerSource = 60;

    private static readonly Lazy<string> Template = new(LoadTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>编译完整系统提示词：静态模板 + 动态数据源摘要段 + 会话参考材料段；模板缺失时直接失败，绝不静默降级为空提示词。</summary>
    /// <param name="tableSummary">所选数据源（及全部数据源）的表清单摘要文本；可为空表示尚未注入。</param>
    /// <param name="referenceSection">会话附件解析出的「参考材料」段文本；null 表示会话没有参考材料。</param>
    /// <returns>完整的系统提示词文本。</returns>
    public static string Build(string? tableSummary, string? referenceSection = null)
    {
        var dynamicSection = string.IsNullOrWhiteSpace(tableSummary)
            ? "（当前会话未注入数据源表清单；请先用 list_data_sources / list_tables 自行确认可用范围。）"
            : tableSummary!.Trim();
        var reference = string.IsNullOrWhiteSpace(referenceSection)
            ? "（本会话当前没有参考材料附件；不要虚构任何参考列。）"
            : referenceSection!.Trim();
        return Template.Value
            .Replace("{0}", dynamicSection, StringComparison.Ordinal)
            .Replace("{1}", reference, StringComparison.Ordinal);
    }

    /// <summary>把会话上的参考材料集合渲染为「参考材料」提示词段：材料元信息 + 列清单 JSON，模型按该段提炼列名列序。</summary>
    /// <param name="materials">会话内累积的参考材料（已按会话上限裁剪）。</param>
    /// <returns>提示词段文本；集合为空返回 null（调用方按"无参考材料"处理）。</returns>
    public static string? BuildReferenceSection(IReadOnlyCollection<PocAiReferenceMaterial> materials)
    {
        if (materials.Count == 0)
        {
            return null;
        }
        var builder = new StringBuilder();
        builder.AppendLine("### 参考材料（用户上传附件的结构化解析结果）");
        // 第一段：逐份材料渲染元信息（来源/文件名/标题）与参考列清单（列序即数组顺序）。
        foreach (var material in materials)
        {
            builder.AppendLine($"- 附件「{material.FileName}」（{material.SourceKind}）");
            if (!string.IsNullOrWhiteSpace(material.Title))
            {
                builder.AppendLine($"  标题：{material.Title}");
            }
            if (material.Columns.Count > 0)
            {
                builder.AppendLine($"  列（{material.Columns.Count} 列，按参考列序）：");
                foreach (var column in material.Columns)
                {
                    builder.AppendLine(column.Note is null
                        ? $"    - {column.Name}"
                        : $"    - {column.Name}（{column.Note}）");
                }
            }
            else
            {
                builder.AppendLine("  列：（未能解析出参考列）");
            }
            // 第二段：参数/合计提示与解析警告随材料注入，让模型知道参考的口径线索与解析局限。
            if (!string.IsNullOrWhiteSpace(material.ParamsHint))
            {
                builder.AppendLine($"  参数提示：{material.ParamsHint}");
            }
            if (!string.IsNullOrWhiteSpace(material.TotalsHint))
            {
                builder.AppendLine($"  合计提示：{material.TotalsHint}");
            }
            foreach (var warning in material.Warnings)
            {
                builder.AppendLine($"  警告：{warning}");
            }
        }
        builder.AppendLine("规则：列名列序从参考提炼；参考列与库字段的映射用查数工具确认，冲突以库字段为准并在回复中说明映射；不要编造参考里没有的列。");
        return builder.ToString().TrimEnd();
    }

    /// <summary>构建“数据源清单 + 所选源表名”摘要：只含编码/名称/类型与表名列表，不含任何结构或连接信息。</summary>
    /// <param name="dataSources">已启用数据源集合（编码/名称/类型）。</param>
    /// <param name="tablesBySource">各数据源的表名列表；加载失败的数据源允许缺省。</param>
    /// <param name="selectedDataSourceCode">会话绑定的数据源；置顶展示。</param>
    /// <returns>可注入提示词的摘要文本。</returns>
    public static string BuildDataSourceSummary(
        IReadOnlyCollection<(string Code, string Name, string DbType)> dataSources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tablesBySource,
        string? selectedDataSourceCode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### 已配置数据源与表清单摘要");
        builder.AppendLine();
        var ordered = dataSources
            .OrderByDescending(item => string.Equals(item.Code, selectedDataSourceCode, StringComparison.OrdinalIgnoreCase))
            .Take(MaxDataSourceLines)
            .ToArray();
        foreach (var dataSource in ordered)
        {
            var marker = string.Equals(dataSource.Code, selectedDataSourceCode, StringComparison.OrdinalIgnoreCase)
                ? "（当前会话数据源）"
                : string.Empty;
            builder.AppendLine($"- {dataSource.Code} {marker}：{dataSource.Name} [{dataSource.DbType}]");
            if (tablesBySource.TryGetValue(dataSource.Code, out var tables))
            {
                var preview = string.Join("、", tables.Take(MaxTableNamesPerSource));
                builder.AppendLine($"  表清单：{preview}{(tables.Count > MaxTableNamesPerSource ? " …" : string.Empty)}");
            }
        }

        if (dataSources.Count > MaxDataSourceLines)
        {
            builder.AppendLine($"（另有 {dataSources.Count - MaxDataSourceLines} 个数据源未列出，可用 list_data_sources 查询全部。）");
        }
        builder.AppendLine();
        builder.AppendLine("表清单只是名字摘要；写查询前请用 describe_table 确认列结构。");
        return builder.ToString().TrimEnd();
    }

    /// <summary>从嵌入资源读取提示词模板；资源缺失抛出明确异常，保证发版一致性被构建期与运行期同时守护。</summary>
    /// <returns>模板原文。</returns>
    private static string LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少 AI 系统提示词嵌入资源：{ResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
