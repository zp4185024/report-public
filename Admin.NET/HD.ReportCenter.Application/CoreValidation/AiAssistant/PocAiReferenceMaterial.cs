using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>附件单条输入（前端 base64 随消息内联）；内容不落库，仅随请求进入内存解析。</summary>
/// <param name="FileName">原始文件名（含扩展名），用于类型判定与审计元信息。</param>
/// <param name="ContentType">浏览器报告的 MIME 类型；仅展示用途，类型判定以扩展名为准。</param>
/// <param name="SizeBytes">原始字节数；前端先拦，后端按解析出的实际字节复核。</param>
/// <param name="DataBase64">文件内容 base64（不含 data: 前缀）。</param>
public sealed record PocAiChatAttachment(string FileName, string ContentType, long SizeBytes, string DataBase64);

/// <summary>参考材料中单列：列名 + 可选备注（类型倾向/口径提示），列序即数组顺序。</summary>
/// <param name="Name">参考列名（提炼自 Excel 表头 / PDF 切列 / 视觉识别）。</param>
/// <param name="Note">可选备注；Excel 场景写入采样推断的取值类型倾向，视觉场景可写口径提示。</param>
public sealed record PocAiReferenceColumn(string Name, string? Note);

/// <summary>
/// 统一参考材料契约（阶段四）：Excel/PDF/图片三类附件解析后的同一形状，
/// 序列化为「参考材料」段注入主模型系统上下文。附件内容不落库，仅随内存会话存活。
/// </summary>
/// <param name="SourceKind">来源类型：excel / pdf / image。</param>
/// <param name="FileName">原始文件名（审计与提示词展示用）。</param>
/// <param name="Title">参考物料标题（Excel 标题行 / PDF 首行 / 视觉识别的标题）；可为空。</param>
/// <param name="Columns">参考列清单（名称 + 可选备注），列序即参考列序。</param>
/// <param name="Warnings">解析警告（合并单元格摘要、无文本层、视觉降级说明等），随材料注入提示词。</param>
/// <param name="ParamsHint">参数提示（参考物料中出现的筛选/期间描述）；可为空。</param>
/// <param name="TotalsHint">合计提示（参考物料中出现的合计/小计线索）；可为空。</param>
public sealed record PocAiReferenceMaterial(
    string SourceKind,
    string FileName,
    string? Title,
    IReadOnlyList<PocAiReferenceColumn> Columns,
    IReadOnlyList<string> Warnings,
    string? ParamsHint = null,
    string? TotalsHint = null)
{
    /// <summary>单条消息附件数量上限（spec §4）。</summary>
    public const int MaxAttachmentsPerMessage = 3;

    /// <summary>会话内参考材料存量上限：超出后裁剪最早的材料，防止长会话把系统提示词撑爆。</summary>
    public const int MaxMaterialsPerSession = 6;

    /// <summary>图片附件大小上限（5MB，spec §4）。</summary>
    public const long ImageMaxBytes = 5 * 1024 * 1024;

    /// <summary>xlsx/pdf 附件大小上限（10MB，spec §4）。</summary>
    public const long DocumentMaxBytes = 10 * 1024 * 1024;

    /// <summary>图片类型白名单（按扩展名判定，大小写不敏感）。</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>文档类型白名单（按扩展名判定，大小写不敏感）。</summary>
    public static readonly string[] DocumentExtensions = [".xlsx", ".pdf"];

    /// <summary>规范序列化设置：驼峰字段、忽略空值，控制注入提示词的体积。</summary>
    public static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    /// <summary>校验单条附件的类型与大小；违规抛出带稳定错误码的配置异常（API 层统一映射 400）。</summary>
    /// <param name="attachment">待校验的附件输入。</param>
    public static void Validate(PocAiChatAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var extension = Path.GetExtension(attachment.FileName ?? string.Empty).ToLowerInvariant();
        if (ImageExtensions.Contains(extension))
        {
            if (attachment.SizeBytes > ImageMaxBytes)
            {
                throw new PocDatasetConfigurationException(
                    "AI_ATTACHMENT_INVALID",
                    $"图片 '{attachment.FileName}' 超过 5MB 上限，请压缩后再上传。");
            }
            return;
        }
        if (DocumentExtensions.Contains(extension))
        {
            if (attachment.SizeBytes > DocumentMaxBytes)
            {
                throw new PocDatasetConfigurationException(
                    "AI_ATTACHMENT_INVALID",
                    $"附件 '{attachment.FileName}' 超过 10MB 上限，请拆分后再上传。");
            }
            return;
        }
        throw new PocDatasetConfigurationException(
            "AI_ATTACHMENT_INVALID",
            $"附件 '{attachment.FileName}' 类型不受支持，仅允许图片（png/jpg/jpeg/webp）、Excel（xlsx）与 PDF。");
    }
}
