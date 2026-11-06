import type {
  AiChatMessageView,
  AiChatProgressDocument,
  AiChatSeqMessageView,
  AiChatSeqResult,
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

/**
 * 计算消息流中已渲染的最大 Seq：作为下一次 progress 轮询的 afterSeq 增量游标。
 * 本地乐观消息（seq=null）不参与计算；全部为本地消息时返回 0 表示从头取。
 * @param messages - 当前消息流。
 * @returns 最大 Seq；无服务端消息时为 0。
 */
export function latestAiSeq(messages: AiChatSeqMessageView[]): number {
  return messages.reduce((max, message) =>
    message.seq !== null && message.seq > max ? message.seq : max, 0);
}

/**
 * 合并一次 progress 轮询结果：按 Seq 去重后追加增量消息。
 * 当增量首条是与服务端用户消息同文的乐观消息（seq=null 的本地用户气泡）时，
 * 用服务端版本替换本地版本，避免“正在思考”期间用户消息出现两次。
 * @param existing - 当前消息流（可能含本地乐观用户消息）。
 * @param progress - 本次轮询返回的循环状态与增量消息。
 * @returns 合并后的消息流；无增量时原样返回。
 */
export function mergeAiProgressMessages(
  existing: AiChatSeqMessageView[],
  progress: AiChatProgressDocument,
): AiChatSeqMessageView[] {
  const knownSeqs = new Set(
    existing.filter((message) => message.seq !== null).map((message) => message.seq as number),
  );
  const additions = progress.messages.filter(
    (message) => message.seq === null || !knownSeqs.has(message.seq),
  );
  if (additions.length === 0) {
    return existing;
  }
  const pendingIndex = existing.findIndex((message) => message.seq === null && message.role === 'user');
  const first = additions[0];
  if (pendingIndex >= 0 && first.role === 'user'
    && (first.content ?? '') === (existing[pendingIndex].content ?? '')) {
    const withoutPending = existing.filter((_, index) => index !== pendingIndex);
    return [...withoutPending, ...additions];
  }
  return [...existing, ...additions];
}

/**
 * POST /chat 返回后校正消息流：丢掉本地乐观消息与已被最终响应覆盖的轮询消息，
 * 以服务端权威顺序重建本轮内容；会话过期时服务端上下文已重建，直接采用本轮消息。
 * @param existing - 轮询期间累积的消息流。
 * @param result - POST /chat 的最终响应（含本轮全部消息与 Seq）。
 * @returns 校正后的完整消息流。
 */
export function reconcileAiTurnMessages(
  existing: AiChatSeqMessageView[],
  result: AiChatSeqResult,
): AiChatSeqMessageView[] {
  if (result.expired) {
    return [...result.messages];
  }
  const turnSeqs = new Set(
    result.messages.filter((message) => message.seq !== null).map((message) => message.seq as number),
  );
  const kept = existing.filter(
    (message) => message.seq !== null && !turnSeqs.has(message.seq),
  );
  return [...kept, ...result.messages];
}

/**
 * 判定工具循环进度是否为失败态：前端据此把“正在思考”替换为失败提示。
 * @param loopState - progress 接口返回的循环状态。
 * @returns 是否 failed。
 */
export function isAiLoopFailed(loopState: AiChatProgressDocument['loopState']): boolean {
  return loopState === 'failed';
}
