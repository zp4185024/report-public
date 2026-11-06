namespace HD.ReportCenter.Application.CoreValidation.AiAssistant;

/// <summary>产品内 AI 能力的全局单行配置表（RcAiSettings）；IsEnabled 默认关闭，保证 AI 是纯可选能力。</summary>
[SugarTable("RcAiSettings", "AI 助手全局配置表（单行）")]
[SysTable]
public sealed class PocAiSettings : EntityBase
{
    /// <summary>AI 能力总开关；false 时所有 /ai/* 调用型接口 fail-fast，不发起任何 LLM 请求。</summary>
    [SugarColumn(ColumnDescription = "是否启用 AI 能力")]
    public bool IsEnabled { get; set; }

    /// <summary>OpenAI 兼容服务基础地址，如 https://llm.internal/v1；为空视为配置不完整。</summary>
    [SugarColumn(ColumnDescription = "LLM 服务基础地址", Length = 512)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>LLM 服务密钥；与数据源连接串同为明文存库（内网部署），接口回显一律打码。</summary>
    [SugarColumn(ColumnDescription = "LLM 服务密钥", Length = 512, IsNullable = true)]
    public string? ApiKey { get; set; }

    /// <summary>对话使用的模型名，如 deepseek-chat；为空视为配置不完整。</summary>
    [SugarColumn(ColumnDescription = "LLM 模型名称", Length = 128)]
    public string Model { get; set; } = string.Empty;

    /// <summary>视觉模型名（附件图片理解用，OpenAI 兼容 image_url content part）；空则回退主 Model——主模型具备视觉能力时单模型即可。</summary>
    [SugarColumn(ColumnDescription = "视觉模型名称（空回退主模型）", Length = 128)]
    public string VisionModel { get; set; } = string.Empty;

    /// <summary>sample_rows 单次采样行数上限；0 等效禁用采样工具，硬上限 100。</summary>
    [SugarColumn(ColumnDescription = "采样行数上限")]
    public int SampleRowLimit { get; set; } = 20;

    /// <summary>是否允许 AI 使用 run_readonly_sql 工具；false 时该工具整体拒绝。</summary>
    [SugarColumn(ColumnDescription = "是否允许只读 SQL 探测")]
    public bool SqlProbeEnabled { get; set; } = true;

    /// <summary>run_readonly_sql 单次返回行数上限；读取侧截断，读到上限即停。</summary>
    [SugarColumn(ColumnDescription = "SQL 探测行数上限")]
    public int SqlProbeRowLimit { get; set; } = 100;

    /// <summary>单次回复内允许的最大工具轮数；超限后强制要求 AI 给出结论性回复。</summary>
    [SugarColumn(ColumnDescription = "最大工具轮数")]
    public int MaxToolRounds { get; set; } = 8;
}

/// <summary>AI 工具调用审计表（RcAiAuditLog）；每次工具调用写一行，供事后追溯采样与 SQL 探测行为。</summary>
[SugarTable("RcAiAuditLog", "AI 工具调用审计表")]
[SugarIndex("ix_RcAiAuditLog_Time", nameof(Time), OrderByType.Asc)]
[SugarIndex("ix_RcAiAuditLog_SessionId", nameof(SessionId), OrderByType.Asc, nameof(ToolName), OrderByType.Asc)]
[SysTable]
public sealed class PocAiAuditLog : EntityBase
{
    /// <summary>工具调用发生时间（服务器本地时间）。</summary>
    [SugarColumn(ColumnDescription = "调用时间")]
    public DateTime Time { get; set; }

    /// <summary>AI 会话编号；与内存会话存储的 sessionId 一致，便于按会话串联审计。</summary>
    [SugarColumn(ColumnDescription = "会话编号", Length = 64)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>工具名，如 sample_rows、run_readonly_sql。</summary>
    [SugarColumn(ColumnDescription = "工具名称", Length = 64)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>目标数据源编码；本地资产类工具可为空。</summary>
    [SugarColumn(ColumnDescription = "数据源编码", Length = 64, IsNullable = true)]
    public string? DataSourceCode { get; set; }

    /// <summary>本次执行的 SQL 文本；写入前按 2000 字符截断，避免审计表膨胀。</summary>
    [SugarColumn(ColumnDescription = "执行的 SQL（截断存储）", Length = 2000, IsNullable = true)]
    public string? SqlText { get; set; }

    /// <summary>本次返回的数据行数；执行失败时可为空。</summary>
    [SugarColumn(ColumnDescription = "返回行数", IsNullable = true)]
    public int? RowCount { get; set; }

    /// <summary>工具执行耗时（毫秒）。</summary>
    [SugarColumn(ColumnDescription = "执行耗时毫秒")]
    public int DurationMs { get; set; }

    /// <summary>工具是否执行成功；失败原因见 ErrorSummary。</summary>
    [SugarColumn(ColumnDescription = "是否成功")]
    public bool Success { get; set; }

    /// <summary>失败原因摘要；写入前按 512 字符截断，且不允许包含连接串等敏感内容。</summary>
    [SugarColumn(ColumnDescription = "错误摘要", Length = 512, IsNullable = true)]
    public string? ErrorSummary { get; set; }
}
