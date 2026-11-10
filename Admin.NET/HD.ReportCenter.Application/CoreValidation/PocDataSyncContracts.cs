using HD.ReportCenter.ReportEngine.CoreValidation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace HD.ReportCenter.Application.CoreValidation;

/// <summary>字段级映射中的单个转换步骤；Type 是内置转换器名，Args 是键值参数（值均为文本）。</summary>
public sealed record PocSyncConverterStep(
    string Type,
    IReadOnlyDictionary<string, string?>? Args = null);

/// <summary>
/// 单个字段的同步映射：源字段经转换链后写入目标列。
/// TargetType 决定目标列 DDL 与终值校验；Nullable 为 false 时终值为 null 按失败行处理。
/// IsKey 仅在冲突策略为 Upsert 时生效：标记该目标列为“按主键合并”的合并键，
/// 全部映射中必须有且只有一条 IsKey=true 且该列不允许空；其余策略下该标志被忽略
/// （随 DefinitionJson/同步包原样携带，旧 JSON 缺失反序列化为 false，天然兼容）。
/// </summary>
public sealed record PocSyncColumnMapping(
    string SourceField,
    string TargetField,
    string TargetType,
    int? Length,
    bool Nullable,
    IReadOnlyList<PocSyncConverterStep> Converters,
    bool IsKey = false);

/// <summary>
/// 任务调度定义，随 DefinitionJson 一起保存并在导出包中携带。
/// CronExpression 仅调度类型为 Cron 时有值（标准 5 段表达式）；其余类型保持 null，
/// 旧 JSON 反序列化天然兼容（缺失字段为 null）。
/// </summary>
public sealed record PocSyncScheduleDefinition(
    string Type,
    string? Time,
    int IntervalMinutes,
    string? CronExpression = null);

/// <summary>同步任务的完整定义（映射 + 调度），序列化为 DefinitionJson 的对象形态。</summary>
public sealed record PocSyncTaskDefinition(
    IReadOnlyList<PocSyncColumnMapping> Mappings,
    PocSyncScheduleDefinition? Schedule);

/// <summary>数据同步包格式常量与规范化序列化辅助，形态与报表包保持一致（camelCase、保留 null、紧凑）。</summary>
public static class PocDataSyncPackageFormat
{
    /// <summary>标识同步包文件类型的固定字符串，导入校验时必须完全匹配。</summary>
    public const string PackageKind = "hd-report-center.sync-package";

    /// <summary>当前同步包结构版本；不兼容的结构升级必须递增并由校验端拒绝。</summary>
    public const int PackageVersion = 1;

    /// <summary>
    /// 以稳定规则把整份同步包序列化为紧凑 JSON：camelCase 属性名、保留 null、ISO 日期。
    /// 同一份包在任何进程里得到相同字节序列，供 SHA-256 审计摘要计算。
    /// </summary>
    /// <param name="package">待序列化的完整同步包。</param>
    /// <returns>规范化后的紧凑 JSON 文本。</returns>
    public static string SerializeCanonical(PocDataSyncPackage? package)
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
    /// 计算同步包的 SHA-256 审计摘要，导入审计记录用它把每次写入与具体包内容绑定。
    /// </summary>
    /// <param name="package">待摘要的完整同步包。</param>
    /// <returns>小写十六进制 SHA-256；空包返回 "null" 文本的摘要。</returns>
    public static string ComputeSha256(PocDataSyncPackage? package) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(SerializeCanonical(package))),
            0,
            32).ToLowerInvariant();
}

/// <summary>
/// 同步包中的源数据集快照。字段契约与 SQL 原样携带供线上核对，绝不包含数据源连接串或密码；
/// 导入时数据集绑定到用户选定的目标数据源，包内 DataSourceCode/Type 仅作方言比对提示。
/// </summary>
public sealed record PocDataSyncPackageDataset(
    string Code,
    string Name,
    string DataSourceCode,
    string DataSourceType,
    string QueryText,
    JObject FieldSchema,
    int TimeoutSeconds,
    int MaxRows,
    bool IsEnabled);

/// <summary>
/// 数据同步包根契约：开发机导出的任务定义（源数据集快照 + 映射 + 调度 + 目标描述）。
/// 包内不出现任何连接串、密码或数据库 Id；TargetDataSourceCode 仅作导入时的默认选择提示。
/// </summary>
/// <param name="TaskCode">同步任务编码；导入时按它在目标环境探测冲突与执行 Create/Replace。</param>
public sealed record PocDataSyncPackage(
    string PackageKind,
    int PackageVersion,
    DateTimeOffset ExportedAt,
    string TaskCode,
    PocDataSyncPackageDataset SourceDataset,
    string TargetDataSourceCode,
    string TargetDataSourceType,
    string TargetTable,
    string ConflictPolicy,
    string ScheduleType,
    string? ScheduleTime,
    int IntervalMinutes,
    IReadOnlyList<PocSyncColumnMapping> Mappings,
    string? ScheduleCron = null);

/// <summary>校验或导入接口的请求体：直接携带整份解析后的同步包与目标数据源选择。</summary>
public sealed record PocDataSyncValidateRequest(PocDataSyncPackage? Package);

/// <summary>结构化校验报告中的单条错误或警告；Code 是稳定业务错误码，Message 面向管理员。</summary>
public sealed record PocDataSyncIssue(string Code, string Message);

/// <summary>与当前库的冲突探测结果；数据集一致性结论在数据集不存在时为 null。</summary>
public sealed record PocDataSyncCollisions(
    bool TaskCodeExists,
    bool DatasetCodeExists,
    bool? DatasetIdentical,
    bool? DatasetReferencedByReports,
    bool TargetDataSourceExists,
    bool? TargetDataSourceTypeMatch);

/// <summary>校验报告里的包内容摘要，供导入向导展示将导入什么。</summary>
public sealed record PocDataSyncPackageSummary(
    string TaskCode,
    string SourceDatasetCode,
    string TargetTable,
    int MappingCount,
    string TargetDataSourceType);

/// <summary>
/// 同步包校验结果：结构、映射完整性、目标数据源与冲突探测逐项检查；
/// errors 非空表示包不可导入，warnings 只是提示不阻断。
/// </summary>
public sealed record PocDataSyncValidateResult(
    bool Ok,
    IReadOnlyList<PocDataSyncIssue> Errors,
    IReadOnlyList<PocDataSyncIssue> Warnings,
    PocDataSyncCollisions? Collisions,
    PocDataSyncPackageSummary? PackageSummary);

/// <summary>同步任务试跑请求：行数上限（服务端封顶 50）与源数据集变量的可选取值。</summary>
public sealed record PocDataSyncTryRunRequest(
    int? RowLimit,
    IReadOnlyDictionary<string, string?>? Parameters = null);

/// <summary>同步包试跑请求：包 + 用户选择的目标数据源 + 数据集变量样例取值。</summary>
public sealed record PocDataSyncDryRunRequest(
    PocDataSyncPackage? Package,
    string? TargetDataSourceCode,
    IReadOnlyDictionary<string, string?>? Parameters = null);

/// <summary>试跑样例行：转换前后的字段值对照，键分别为源字段名与目标列名。</summary>
public sealed record PocDataSyncSampleRow(
    IReadOnlyDictionary<string, object?> Source,
    IReadOnlyDictionary<string, object?> Target);

/// <summary>
/// 试跑结果：最多 50 行端到端读+转换（不写目标库），附目标列结论。
/// errors 非空表示映射与真实数据不兼容，样本仅供参考。
/// </summary>
public sealed record PocDataSyncTryRunResult(
    bool Ok,
    IReadOnlyList<string> TargetColumns,
    IReadOnlyList<PocDataSyncSampleRow> Samples,
    long ElapsedMilliseconds,
    bool RowCountCapped,
    long FailedRows,
    IReadOnlyList<string> Errors);

/// <summary>
/// 导入提交请求。Mode 为 Create（新建）或 Replace（先删旧任务再建）；
/// DryRunPerformed 由前端记录是否已完成试跑，仅进入审计文件。
/// </summary>
public sealed record PocDataSyncImportRequest(
    PocDataSyncPackage? Package,
    string? TargetDataSourceCode,
    string? Mode,
    bool DryRunPerformed);

/// <summary>导入完成后的动作摘要，告诉管理员数据集和同步任务各自执行了什么动作。</summary>
public sealed record PocDataSyncImportResult(
    string TaskCode,
    string DatasetAction,
    string TaskAction,
    IReadOnlyList<string> Warnings);

/// <summary>同步任务对外文档：任务列配置 + 解析后的映射定义，供任务管理页与映射画布消费。</summary>
public sealed record PocSyncTaskDocument(
    string Code,
    string Name,
    string SourceDatasetCode,
    string TargetDataSourceCode,
    string TargetTable,
    string ConflictPolicy,
    string ScheduleType,
    string? ScheduleTime,
    int IntervalMinutes,
    bool Enabled,
    int Revision,
    DateTimeOffset? LastRunAt,
    IReadOnlyList<PocSyncColumnMapping> Mappings,
    string? ScheduleCron = null);

/// <summary>
/// 同步任务保存输入；Revision 是乐观并发期望修订号，0 表示首次落库。
/// SourceDatasetCode 保留旧客户端与同步包导入的兼容路径；设计器新契约改为携带 Source 节，
/// 服务端据此自动维护 `sync_src_&lt;taskCode&gt;` 源数据集，两条路径互斥（Source 优先）。
/// </summary>
public sealed record PocSyncTaskInput(
    string Code,
    string Name,
    string? SourceDatasetCode,
    string TargetDataSourceCode,
    string TargetTable,
    string ConflictPolicy,
    string ScheduleType,
    string? ScheduleTime,
    int IntervalMinutes,
    IReadOnlyList<PocSyncColumnMapping> Mappings,
    bool Enabled,
    int Revision,
    PocSyncTaskSourceInput? Source = null,
    string? ScheduleCron = null);

/// <summary>
/// 任务保存输入的源取数节：数据源编码 + 原样 SQL（选表模式由前端按方言生成 SELECT）。
/// 保存时服务端校验 SQL 只读后自动把它落成/更新为任务专属源数据集，任务实体仍存 SourceDatasetCode，
/// 包导出、执行器与调度链路不感知该变化。SourceTable 是选表模式下前端已知的源表名：
/// 提供时保存服务端读取该表的列清单与注释，把中文含义写入源数据集字段的 displayName，
/// 供数据集管理页与任务预览展示；写 SQL 模式不传，字段契约保持为空。
/// Variables 是源 SQL 的取数参数契约（编码与 @引用一一对应），随源数据集 FieldSchemaJson
/// 一起落库并随同步包自动携带；固定值/滚动表达式在每次运行开始时由服务端现算兜底。
/// </summary>
public sealed record PocSyncTaskSourceInput(
    string DataSourceCode,
    string QueryText,
    string? Name = null,
    string? SourceTable = null,
    IReadOnlyList<PocDatasetVariableDefinition>? Variables = null);

/// <summary>数据源表清单响应：列出该数据源连接库内的基础表名，供源/目标表下拉选择。</summary>
public sealed record PocSyncTableMetadataDocument(
    string DataSourceCode,
    IReadOnlyList<string> Tables);

/// <summary>
/// 目标表列元数据：列名、数据库类型文本、可空标记与列注释（数据库里的 MS_Description/
/// COMMENT 等中文业务含义）。Description 取不到时为 null，前端只在有值时展示，不造占位文案。
/// IsPrimaryKey 来自数据库真实主键信息，供 Upsert 冲突策略在映射表格里提示“建议选它当合并键”。
/// </summary>
public sealed record PocSyncColumnMetadataDocument(
    string Name,
    string DataType,
    bool Nullable,
    int? Length,
    string? Description = null,
    bool IsPrimaryKey = false);

/// <summary>目标表列清单响应：目标字段下拉选项的数据来源。</summary>
public sealed record PocSyncTableColumnsDocument(
    string DataSourceCode,
    string Table,
    IReadOnlyList<PocSyncColumnMetadataDocument> Columns);

/// <summary>
/// 源取数预览请求：任务保存前对“数据源 + SQL”做真实受限查询，返回动态列与样例行，
/// 供映射表格在任务尚未落库时取到真实源字段。SourceTable 是选表模式下前端已知的源表名：
/// 提供时按列名匹配数据库注释填充 fields 的 description；写 SQL 模式不传，注释诚实留空。
/// Variables 是设计器正在编辑的变量契约（含固定值/滚动表达式），服务端按当前时间现算兜底；
/// Parameters 是调用方显式给定的变量取值，优先级高于变量默认值——两条通道都缺省时，
/// SQL 引用的变量仍按“必填缺值”拒绝，与保存后的执行语义一致。
/// </summary>
public sealed record PocSyncSourcePreviewRequest(
    string? DataSourceCode,
    string? QueryText,
    string? SourceTable = null,
    IReadOnlyList<PocDatasetVariableDefinition>? Variables = null,
    IReadOnlyDictionary<string, string?>? Parameters = null);

/// <summary>运行日志对外文档：统计与失败明细，供任务页轮询。</summary>
public sealed record PocSyncRunLogDocument(
    string RunNo,
    string TaskCode,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long RowsRead,
    long RowsWritten,
    long RowsFailed,
    string? Message,
    IReadOnlyList<PocDataSyncFailureDetail> Failures);

/// <summary>单行同步失败的定位明细；RowIndex 从 1 开始表示本次运行内的行序。</summary>
public sealed record PocDataSyncFailureDetail(
    long RowIndex,
    string Field,
    string Reason,
    string? OriginalValue);

/// <summary>
/// 数据同步领域异常：导出/校验/试跑/导入/执行过程中拒绝继续的业务错误。
/// Code 是稳定错误码（如 SYNC_TASK_NOT_FOUND、SYNC_MAPPING_INCOMPLETE），API 层据此映射状态码。
/// </summary>
public sealed class PocDataSyncException : Exception
{
    /// <summary>初始化 PocDataSyncException 异常实例，携带稳定业务错误码和面向管理员的提示。</summary>
    /// <param name="code">供前端识别拒绝原因的稳定业务错误码。</param>
    /// <param name="message">说明拒绝原因和下一步操作的中文提示。</param>
    /// <param name="innerException">底层异常（如数据库错误），可空。</param>
    public PocDataSyncException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>供前端识别拒绝原因的稳定业务错误码。</summary>
    public string Code { get; }
}

/// <summary>
/// 单行数据在转换链运行期失败的业务异常。字段名、原因和原始值被同步执行器
/// 记入失败明细（封顶 200 条）后按策略跳过该行，不中断整个同步任务。
/// </summary>
public sealed class PocDataSyncTransformException : Exception
{
    /// <summary>初始化 PocDataSyncTransformException 异常实例，保存字段定位信息与失败原因。</summary>
    /// <param name="field">发生失败的字段（源字段或目标列名）。</param>
    /// <param name="reason">面向排障的失败原因说明。</param>
    /// <param name="originalValue">触发失败的原始值文本（超长截断），null 表示原值为空。</param>
    public PocDataSyncTransformException(string field, string reason, string? originalValue)
        : base($"字段 '{field}' 转换失败：{reason}")
    {
        Field = field;
        Reason = reason;
        OriginalValue = originalValue;
    }

    /// <summary>发生失败的字段名（源字段或目标列名）。</summary>
    public string Field { get; }

    /// <summary>面向排障的失败原因说明。</summary>
    public string Reason { get; }

    /// <summary>触发失败的原始值文本（超长截断）。</summary>
    public string? OriginalValue { get; }
}
