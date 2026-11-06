using Newtonsoft.Json.Linq;

namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>阶段一注册的只读查数工具清单（静态 OpenAI tools schema）；提交/试跑类工具按设计后置，此处刻意不存在。</summary>
public static class PocAiToolRegistry
{
    /// <summary>list_data_sources：列出可用的数据源（编码/名称/类型），永不含连接串。</summary>
    public const string ListDataSources = "list_data_sources";

    /// <summary>list_tables：列出某数据源下的表清单（表名+注释）。</summary>
    public const string ListTables = "list_tables";

    /// <summary>describe_table：读取某表的列结构（列名/类型/可空/注释）。</summary>
    public const string DescribeTable = "describe_table";

    /// <summary>sample_rows：按代码生成的 TOP/LIMIT 包装采样前 N 行，AI 不写 SQL。</summary>
    public const string SampleRows = "sample_rows";

    /// <summary>run_readonly_sql：受开关与行数上限约束的只读 SQL 探测。</summary>
    public const string RunReadonlySql = "run_readonly_sql";

    /// <summary>submit_report_spec：提交报表 spec，服务端编译 + 全链校验（内存态，不落库）。</summary>
    public const string SubmitReportSpec = "submit_report_spec";

    /// <summary>dry_run_report_spec：对编译通过的 spec 做受限试跑（最多 50 行 / 30 秒）。</summary>
    public const string DryRunReportSpec = "dry_run_report_spec";

    /// <summary>返回注册给 LLM 的全部工具 schema；顺序稳定，便于审计与会话回放一致。</summary>
    /// <returns>OpenAI tools 格式的工具定义列表。</returns>
    public static IReadOnlyList<JObject> ListTools()
    {
        // 第一段：本地资产工具——list_data_sources 不带任何连接信息，作为会话入口工具。
        return
        [
            Tool(ListDataSources, "列出报表中心已配置且启用的数据源清单（编码、名称、数据库类型），不包含任何连接信息。", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray()
            }),
            // 第二段：结构探查工具——list_tables / describe_table 按需获取表与列，表名校验在执行侧强制。
            Tool(ListTables, "列出指定数据源下的全部表名与注释。调用前先用 list_data_sources 确认数据源编码。", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["dataSourceCode"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "目标数据源编码，来自 list_data_sources。"
                    }
                },
                ["required"] = new JArray("dataSourceCode")
            }),
            Tool(DescribeTable, "读取指定表的列结构：列名、数据库类型、是否可空、注释。", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["dataSourceCode"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "目标数据源编码。"
                    },
                    ["tableName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "表名，可带 schema 前缀（如 dbo.PatientInfo）。"
                    }
                },
                ["required"] = new JArray("dataSourceCode", "tableName")
            }),
            // 第三段：取数工具——sample_rows 的 SQL 由代码包装，run_readonly_sql 受开关与行数上限约束。
            Tool(SampleRows, "采样指定表的前 N 行数据（SELECT * 形式，由系统生成 TOP/LIMIT 包装，你不需要也不能写 SQL）。", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["dataSourceCode"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "目标数据源编码。"
                    },
                    ["tableName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "表名，可带 schema 前缀。"
                    },
                    ["limit"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "采样行数；超过管理员配置的采样上限时按上限截取。"
                    }
                },
                ["required"] = new JArray("dataSourceCode", "tableName")
            }),
            Tool(RunReadonlySql, "在指定数据源上执行一条只读 SELECT/WITH 验证性查询并返回前 N 行。禁止多语句、DML/DDL 和 @参数占位。结果按管理员配置的行数上限读取侧截断。", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["dataSourceCode"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "目标数据源编码。"
                    },
                    ["sql"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "单条只读 SELECT 或 WITH 查询文本，不要包含分号。"
                    },
                    ["limit"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "返回行数上限；超过管理员配置时按上限截取。"
                    }
                },
                ["required"] = new JArray("dataSourceCode", "sql")
            }),
            // 阶段二：产物交付工具。spec 由服务端编译并全链校验，错误列表携带 spec 字段路径回传自修。
            Tool(SubmitReportSpec, "提交报表 spec（JSON 对象，specVersion=1）交付报表草稿。服务端会编译并全链校验；失败时返回带字段路径的错误列表，请按提示修正后重新提交。提交通过后用户界面会出现草稿卡片。", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["spec"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "完整报表 spec 对象，格式必须严格遵守系统提示词中的 spec 产物契约。"
                    }
                },
                ["required"] = new JArray("spec")
            }),
            Tool(DryRunReportSpec, "对编译通过的报表 spec 做真实试跑：最多返回 50 行样例数据与列清单，并比对字段契约。目标数据源固定为当前会话数据源，类型必须与 spec 的 dataSourceType 一致。", new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["spec"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "可选；不传时使用本会话最近一次提交通过的 spec。传入时先重新编译校验。"
                    },
                    ["sampleParameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "样例参数取值（参数编码 → 字符串值）；必填参数必须提供。"
                    }
                },
                ["required"] = new JArray()
            })
        ];
    }

    /// <summary>构造单个 OpenAI tools 元素；统一 function 类型，参数 schema 由调用处给定。</summary>
    /// <param name="name">工具名。</param>
    /// <param name="description">给模型看的使用说明与硬约束。</param>
    /// <param name="parameters">JSON Schema 形式的参数定义。</param>
    /// <returns>OpenAI tools 格式的工具定义。</returns>
    private static JObject Tool(string name, string description, JObject parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = parameters
        }
    };

    /// <summary>判断工具名是否属于本阶段注册的只读查数工具；未注册工具一律拒绝执行。</summary>
    /// <param name="toolName">LLM 请求的工具名。</param>
    /// <returns>已注册返回 true。</returns>
    public static bool IsRegistered(string toolName) => toolName is
        ListDataSources or ListTables or DescribeTable or SampleRows or RunReadonlySql
        or SubmitReportSpec or DryRunReportSpec;

    /// <summary>判断是否属于产物交付类工具（submit/dry_run）；这类调用由对话服务处理，不走只读取数通道。</summary>
    /// <param name="toolName">LLM 请求的工具名。</param>
    /// <returns>属于产物工具时返回 true。</returns>
    public static bool IsSpecTool(string toolName) => toolName is SubmitReportSpec or DryRunReportSpec;
}
