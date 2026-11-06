import type {
  AiChatMessageView,
  AiChatResult,
  AiDataSourceOption,
  DataSourceDocument,
} from './types';

/** 工具折叠条 SQL 摘要的最大展示长度；超出截断并带省略号。 */
export const AiToolSqlSummaryMaxLength = 120;

/**
 * 生成工具折叠条的单行标签：工具名 + SQL 摘要 + 行数，让消息流一眼看清每次查数动作。
 * @param toolName - 工具名（如 sample_rows / run_readonly_sql）。
 * @param sqlSummary - 服务端整理的 SQL 单行摘要，可为空（如 list_data_sources）。
 * @param rowCount - 本次返回行数，可为空。
 * @returns 折叠条标签文本。
 */
export function formatAiToolLabel(
  toolName: string,
  sqlSummary: string | null,
  rowCount: number | null,
): string {
  const parts = [toolName];
  if (sqlSummary) {
    const compact =
      sqlSummary.length > AiToolSqlSummaryMaxLength
        ? `${sqlSummary.slice(0, AiToolSqlSummaryMaxLength)}…`
        : sqlSummary;
    parts.push(compact);
  }
  if (rowCount !== null && rowCount !== undefined) {
    parts.push(`${rowCount} 行`);
  }
  return parts.join(' · ');
}

/**
 * 生成折叠条展开后的正文：失败时展示错误 JSON（AI 收到的就是它），成功时展示 SQL/结果预览。
 * @param message - 工具消息。
 * @returns 展开区文本；无内容时返回空串。
 */
export function aiToolDetail(message: AiChatMessageView): string {
  if (!message.success) {
    return message.content ?? '工具执行失败。';
  }
  const lines = [message.sqlSummary ?? ''];
  if (message.truncated) {
    lines.push(`结果已达上限被截断（返回 ${message.rowCount ?? 0} 行）。`);
  }
  return lines.filter((line) => line.length > 0).join('\n');
}

/**
 * 把本轮对话结果合并进本地消息流：会话过期时旧上下文已失效，
 * 只保留本轮消息并按时间顺序排列；未过期则原样追加。
 * @param existing - 本地已有消息流。
 * @param result - 本轮对话响应。
 * @returns 合并后的完整消息流。
 */
export function nextAiChatMessages(
  existing: AiChatMessageView[],
  result: AiChatResult,
): AiChatMessageView[] {
  if (result.expired) {
    return [...result.messages];
  }
  return [...existing, ...result.messages];
}

/**
 * 判定对话失败是否可原样重试：LLM 请求类错误（服务不可达/超时/网关错误）会话已保留可重试，
 * AI_DISABLED 属配置态需管理员处理，参数校验错误重发也不会成功。
 * @param error - 捕获到的异常。
 * @returns 是否提示“重试”。
 */
export function isAiRetryableError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '');
  return /LLM_REQUEST_FAILED|LLM_REQUEST_TIMEOUT|LLM_RESPONSE_INVALID|无法连接后端服务/.test(message);
}

/**
 * 判定错误是否为 AI 关闭降级（AI_DISABLED）；面板据此提示“请先在 AI 设置中启用”，不引导重试。
 * @param error - 捕获到的异常。
 * @returns 是否为 AI 关闭错误。
 */
export function isAiDisabledError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '');
  return message.includes('AI_DISABLED');
}

/**
 * 从数据源管理文档中提取侧栏选择器选项：只保留已启用数据源，并按名称排序稳定展示。
 * @param sources - dataAssetApi.dataSources 返回的脱敏数据源文档。
 * @returns 侧栏可选项（编码/名称/类型）。
 */
export function resolveAiDataSourceOptions(sources: DataSourceDocument[]): AiDataSourceOption[] {
  return sources
    .filter((source) => source.enabled)
    .map((source) => ({ code: source.code, name: source.name, dbType: source.dbType }))
    .sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
}
