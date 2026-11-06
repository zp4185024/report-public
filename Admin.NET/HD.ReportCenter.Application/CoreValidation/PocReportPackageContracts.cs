using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>
/// 报表包格式常量与审计辅助。包是开发机与线上环境之间交换报表定义的唯一载体，
/// 序列化始终保持对象形态（快照、绑定、设置均为 JSON 对象而非字符串），便于人工审阅和 diff。
/// </summary>
public static class PocReportPackageFormat
{
    /// <summary>标识报表包文件类型的固定字符串，导入校验时必须完全匹配。</summary>
    public const string PackageKind = "hd-report-center.report-package";

    /// <summary>当前报表包结构版本；结构不兼容的升级必须递增该值并由校验端拒绝旧包。</summary>
    public const int PackageVersion = 1;

    /// <summary>
    /// 以稳定规则把整份报表包序列化为紧凑 JSON：camelCase 属性名、保留 null、ISO 日期。
    /// 该形态同时用于 SHA-256 审计摘要计算，保证同一份包在任何进程里得到相同字节序列。
    /// </summary>
    /// <param name="package">待序列化的完整报表包。</param>
    /// <returns>规范化后的紧凑 JSON 文本。</returns>
    public static string SerializeCanonical(PocReportPackage? package)
    {
        if (package is null)
        {
            return "null";
        }

        return JsonConvert.SerializeObject(
            package,
            Formatting.None,
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Include
            });
    }

    /// <summary>
    /// 计算报表包的 SHA-256 审计摘要，导入审计记录用它把每次写入与具体包内容绑定。
    /// </summary>
    /// <param name="package">待摘要的完整报表包。</param>
    /// <returns>小写十六进制 SHA-256；空包返回 "null" 文本的摘要。</returns>
    public static string ComputeSha256(PocReportPackage? package) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(SerializeCanonical(package))),
            0,
            32).ToLowerInvariant();
}

/// <summary>
/// 报表包中的报表定义节。只携带业务内容，不含数据库 Id、时间戳和修订号；
/// 分享保护口令摘要在导出时被剥离，导入后由线上管理员重新设置。
/// </summary>
public sealed record PocReportPackageReport(
    string Code,
    string Name,
    string SheetName,
    string DatasetCode,
    PocPreviewSettings PreviewSettings,
    PocExportSettings ExportSettings,
    IReadOnlyList<PocFieldPresentationDefinition>? FieldPresentations,
    bool IsEnabled);

/// <summary>报表包中的单个报表参数定义；字段与 RcReportParameter 实体一一对应但不含数据库主键。</summary>
public sealed record PocReportPackageParameter(
    string Code,
    string DisplayName,
    string DataType,
    string ControlType,
    string SourceType,
    string? SourceCode,
    string? SourceFieldCode,
    string? UrlKey,
    string? DefaultExpression,
    string? FixedValue,
    IReadOnlyList<PocValueOption>? Options,
    bool IsVisible,
    bool IsReadOnly,
    bool IsRequired,
    bool AllowUrlPrefill,
    int DisplayOrder);

/// <summary>
/// 报表包中的模板节。Snapshot 是 Univer 工作簿快照对象，Binding 是导出时的规范绑定；
/// Revision 仅用于展示导出时的模板版本，导入后重新从 1 开始。
/// </summary>
public sealed record PocReportPackageTemplate(
    JObject Snapshot,
    PocTemplateBinding Binding,
    int Revision);

/// <summary>
/// 报表包中的数据集节。FieldSchema 使用对象形式（variables + fields）便于人工阅读和 diff；
/// 数据集不携带数据源连接信息，导入时由用户选择目标数据源。
/// </summary>
public sealed record PocReportPackageDataset(
    string Code,
    string Name,
    string QueryText,
    PocDatasetSchemaDocument FieldSchema,
    int TimeoutSeconds,
    int MaxRows,
    bool IsEnabled);

/// <summary>
/// 报表包根契约：开发机导出的完整报表定义（报表 + 参数 + 模板 + 数据集）。
/// 包内绝不包含数据源连接串或密码；SourceDataSource* 仅作为导入时的方言比对提示。
/// </summary>
public sealed record PocReportPackage(
    string PackageKind,
    int PackageVersion,
    DateTimeOffset ExportedAt,
    string SourceDataSourceCode,
    string SourceDataSourceType,
    PocReportPackageReport Report,
    IReadOnlyList<PocReportPackageParameter>? Parameters,
    PocReportPackageTemplate Template,
    PocReportPackageDataset Dataset);

/// <summary>校验或导入接口的请求体：直接携带整份解析后的报表包。</summary>
public sealed record PocReportPackageValidateRequest(PocReportPackage? Package);

/// <summary>结构化校验报告中的单条错误或警告；Code 是稳定业务错误码，Message 面向管理员。</summary>
public sealed record PocReportPackageIssue(string Code, string Message);

/// <summary>
/// 与当前目标库的冲突探测结果。DatasetIdentical 在数据集不存在时为 null；
/// DatasetReferencedByOther 只统计除包内报表之外的引用方。
/// </summary>
public sealed record PocReportPackageCollisions(
    bool ReportCodeExists,
    bool DatasetCodeExists,
    bool? DatasetIdentical,
    bool? DatasetReferencedByOther);

/// <summary>校验报告里的包内容摘要，供导入向导展示将导入什么。</summary>
public sealed record PocReportPackageSummary(
    string ReportCode,
    string ReportName,
    string DatasetCode,
    int ParameterCount,
    int FieldCount,
    string SourceDataSourceType);

/// <summary>
/// 报表包校验结果：结构、编码、参数、SQL、变量绑定和模板一致性逐项检查；
/// errors 非空表示包不可导入，warnings 只是提示不阻断。
/// </summary>
public sealed record PocReportPackageValidateResult(
    bool Ok,
    IReadOnlyList<PocReportPackageIssue> Errors,
    IReadOnlyList<PocReportPackageIssue> Warnings,
    PocReportPackageCollisions? Collisions,
    PocReportPackageSummary? PackageSummary);

/// <summary>试跑请求：包 + 用户选择的目标数据源 + 为包参数提供的样例取值。</summary>
public sealed record PocReportPackageDryRunRequest(
    PocReportPackage? Package,
    string? TargetDataSourceCode,
    IReadOnlyDictionary<string, string?>? ParameterValues);

/// <summary>
/// 试跑结果：最多 50 行真实查询数据，每行是以列编码为键的对象。
/// FieldSchemaMismatches 列出实际列与包内字段契约的差异，属警告性质，由前端展示但不阻断导入。
/// </summary>
public sealed record PocReportPackageDryRunResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    long ElapsedMilliseconds,
    bool RowCountCapped,
    bool FieldSchemaMatch,
    IReadOnlyList<string> FieldSchemaMismatches);

/// <summary>
/// 导入提交请求。Mode 为 Create（新建）或 Replace（先删旧报表再建）；
/// ReplaceDataset 控制内容不一致的同编码数据集是否允许替换。
/// </summary>
public sealed record PocReportPackageImportRequest(
    PocReportPackage? Package,
    string? TargetDataSourceCode,
    string? Mode,
    bool ReplaceDataset,
    bool DryRunPerformed);

/// <summary>导入完成后的动作摘要，告诉管理员数据集和报表各自执行了什么动作。</summary>
public sealed record PocReportPackageImportResult(
    string ReportCode,
    string DatasetAction,
    string ReportAction,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 报表包导出/校验/试跑/导入过程中拒绝继续的业务异常。
/// API 层依据 Code 决定状态码（DATASET_IN_USE、REPORT_ALREADY_EXISTS 映射 409，其余 400）。
/// </summary>
public sealed class PocReportPackageException : Exception
{
    /// <summary>初始化 PocReportPackageException 实例，携带稳定业务错误码和面向管理员的提示。</summary>
    /// <param name="code">供前端识别拒绝原因的稳定业务错误码。</param>
    /// <param name="message">说明拒绝原因和下一步操作的中文提示。</param>
    public PocReportPackageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>供前端识别拒绝原因的稳定业务错误码。</summary>
    public string Code { get; }
}
