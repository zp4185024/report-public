using HD.ReportCenter.ReportEngine.CoreValidation.Exchange;
using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.ReportEngine.CoreValidation.Export;

/// <summary>
/// 一次数据导出的不可变输入。创建后不再回查模板或映射数据库，避免长任务混用不同修订版本。
/// </summary>
public sealed record PocTemplateDataExportPlan(
    string ReportCode,
    int TemplateRevision,
    JObject Snapshot,
    PocTemplateBinding Binding,
    PocExecuteRequest Query,
    PocExportSettings Export,
    PocExchangeRuleSet Mappings);
