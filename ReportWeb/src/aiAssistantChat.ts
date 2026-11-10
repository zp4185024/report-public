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

// ===== UI/UX 整形 + 实时体验补强（抽屉布局/工具卡片/动态思考条/预创建会话/45 秒慢响应提示）追加纯函数 =====

/** 面板打开且选定数据源后判定是否需要预创建会话：仅在“无会话号、已选源、非发送中”时触发一次。 */
export function shouldPreCreateAiSession(
  sessionId: string | null,
  dataSourceCode: string,
  sending: boolean,
): boolean {
  return sessionId === null && dataSourceCode.trim().length > 0 && !sending;
}

/** 动态思考条的单条工具卡片视觉分类：按工具类型选择图标与语义标签。 */
export type AiToolCardKind = 'structure' | 'data' | 'sql' | 'submit' | 'dryrun' | 'generic';

/** 工具卡片的视觉描述：图标分类与中文短标签（失败徽标、行数徽标由卡片另行渲染）。 */
export interface AiToolCardVisual {
  kind: AiToolCardKind;
  label: string;
}

/** 静态工具视觉映射表：查结构/查数据/跑 SQL/提交/试跑 五类各对应一种内联 SVG 图标。 */
const AiToolCardVisualMap: Record<string, AiToolCardVisual> = {
  list_data_sources: { kind: 'structure', label: '查数据源' },
  list_tables: { kind: 'structure', label: '查表清单' },
  describe_table: { kind: 'structure', label: '查表结构' },
  sample_rows: { kind: 'data', label: '采样数据' },
  run_readonly_sql: { kind: 'sql', label: '只读 SQL' },
  submit_report_spec: { kind: 'submit', label: '提交草稿' },
  dry_run_report_spec: { kind: 'dryrun', label: '试跑验证' },
};

/**
 * 把工具名映射为卡片的图标分类与中文标签；未知工具回退通用样式，保证新工具出现时不破版。
 * @param toolName - 服务端消息携带的工具名。
 * @returns 图标分类与标签。
 */
export function aiToolCardVisual(toolName: string | null): AiToolCardVisual {
  if (!toolName) {
    return { kind: 'generic', label: '工具调用' };
  }
  return AiToolCardVisualMap[toolName] ?? { kind: 'generic', label: toolName };
}

/** 动态思考条的状态推导结果：主文案、最近工具名（供图标）、当前轮次与已耗时标签。 */
export interface AiThinkingStatus {
  headline: string;
  toolName: string | null;
  round: number | null;
  elapsedLabel: string;
}

/**
 * 从 progress 累积消息推导动态思考条文案：以“最近一条消息 + 已完成的工具调用数”还原当前阶段，
 * 让长等待（多轮查数）有“正在调用 run_readonly_sql（第 2 轮）/ 已思考 42s”这样的生命感，
 * 替代原来静态的“正在思考……”。纯函数，不含时钟；耗时由组件侧每秒传入。
 * @param messages - 本轮已渲染的消息流（含用户/助手/工具消息）。
 * @param elapsedSeconds - 本轮已耗时秒数（向下取整，负数按 0 处理）。
 * @returns 思考条状态（文案/工具名/轮次/耗时标签）。
 */
export function deriveAiThinkingStatus(
  messages: AiChatSeqMessageView[],
  elapsedSeconds: number,
): AiThinkingStatus {
  const seconds = Math.max(0, Math.floor(elapsedSeconds));
  const elapsedLabel = `已思考 ${seconds}s`;
  const toolMessages = messages.filter((message) => message.role === 'tool');
  const round = toolMessages.length;
  const last = messages.length > 0 ? messages[messages.length - 1] : null;
  if (round === 0) {
    // 尚未进入工具调用：首轮 LLM 请求在途（消息为空或只有用户消息），或模型刚给出计划。
    return { headline: '正在理解问题并规划查询', toolName: null, round: null, elapsedLabel };
  }
  const lastTool = [...toolMessages].reverse().find((message) => message.toolName !== null);
  const toolName = lastTool?.toolName ?? null;
  if (last?.role === 'assistant') {
    // 助手消息（携带 tool_calls）是最新消息：下一轮工具正在执行，轮次 +1。
    return {
      headline: `正在执行第 ${round + 1} 轮工具调用`,
      toolName,
      round: round + 1,
      elapsedLabel,
    };
  }
  // 最新消息是工具结果：模型正在分析该结果或准备下一轮调用，按当前工具语境展示。
  return {
    headline: `正在调用 ${toolName ?? '工具'}（第 ${round} 轮）`,
    toolName,
    round,
    elapsedLabel,
  };
}

/** 慢响应提示阈值（毫秒）：发送后连续 45 秒无任何 progress 消息即提示，可取消不强杀。 */
export const AiSlowTurnHintThresholdMs = 45000;

/**
 * 判定当前轮是否已“停滞”到需要提示：以发送时刻与最近一次 progress 响应时刻的较大值为基准，
 * 距今超过阈值即提示；每次轮询成功都会刷新基准，因此只要还在吐增量就不会误报。
 * @param startedAtMs - 本轮发送时刻（毫秒时间戳）。
 * @param nowMs - 当前时刻（毫秒时间戳），由调用方传入以便纯函数测试。
 * @param lastProgressAtMs - 最近一次 progress 响应时刻；尚无响应时传 null（回退发送时刻）。
 * @param thresholdMs - 阈值毫秒数，缺省 45 秒。
 * @returns 是否应显示“模型响应较慢”提示条。
 */
export function isAiTurnStalled(
  startedAtMs: number,
  nowMs: number,
  lastProgressAtMs: number | null,
  thresholdMs: number = AiSlowTurnHintThresholdMs,
): boolean {
  const baseline = lastProgressAtMs === null ? startedAtMs : Math.max(startedAtMs, lastProgressAtMs);
  return nowMs - baseline >= thresholdMs;
}

// ===== @ant-design/x 外壳迁移追加纯函数（Bubble/ThoughtChain 数据映射、插话分流、中止复位） =====

/** 单个工具调用节点的展示数据：组件侧直接映射为 ThoughtChain 链式节点（失败节点红色态）。 */
export interface AiThoughtChainNode {
  /** 节点唯一键：服务端 Seq 优先，本地乐观消息回退局部序号。 */
  key: string;
  /** 工具名原文（如 run_readonly_sql），节点标题的次要行。 */
  toolName: string;
  /** 工具中文短标签（aiToolCardVisual 映射），节点标题主行。 */
  label: string;
  /** 服务端整理的 SQL 单行摘要；无 SQL 的工具为 null。 */
  sqlSummary: string | null;
  /** 本次返回行数；无行数为 null。 */
  rowCount: number | null;
  /** 工具是否执行成功；false 时节点呈红色错误态。 */
  success: boolean;
  /** 展开后的详情文本（失败为错误 JSON，成功为 SQL/结果预览）。 */
  detail: string;
  /** ThoughtChain 节点状态：成功 success / 失败 error。 */
  status: 'success' | 'error';
}

/** 消息流到气泡列表的单条视图模型；toolTrace 表示“空的中间 assistant 轮次 + 其工具轨迹”合并体。 */
export interface AiBubbleItemViewModel {
  /** 气泡唯一键：与旧 key 规则一致（seq-<seq> / local-<index>）。 */
  key: string;
  /** 渲染角色：user 右侧气泡、assistant 左侧气泡、toolTrace 渲染为 ThoughtChain。 */
  kind: 'user' | 'assistant' | 'toolTrace';
  /** 气泡文本；assistant 空文本回退“（模型未返回文本）”占位。 */
  content: string;
  /** 是否展示“插话 · 下一轮生效”角标（服务端 Seq 已记录或本地乐观插话在途）。 */
  guidanceBadge: boolean;
  /** 该气泡下挂的 ThoughtChain 节点（仅 toolTrace 使用）。 */
  thoughtNodes: AiThoughtChainNode[];
}

/**
 * 发送分流判定（插话模式核心）：仅当“上一轮工具循环仍在运行且已有会话号”时，
 * 本次发送改走 guidance 插话端点；空闲发送走正常 POST /chat，尚无会话号时发送兜底现场建号。
 * @param sending - 是否存在在途轮次（POST /chat 未返回）。
 * @param sessionId - 当前会话号；尚无会话时为 null。
 * @returns true 表示本次发送应调用 guidance 端点。
 */
export function shouldSendAsGuidance(sending: boolean, sessionId: string | null): boolean {
  return sending && sessionId !== null;
}

/**
 * 把一条 guidance 端点响应记入插话 Seq 集合：用于给对应气泡持久打上“插话”角标
 * （乐观上屏角标会被 progress 校正抹掉，服务端 Seq 是校正后仍保留角标的依据）。
 * @param existing - 已记录的插话 Seq 集合。
 * @param message - guidance 响应消息视图；seq 为 null/0（尚未落号）时忽略。
 * @returns 去重后的新集合；重复或空 Seq 原样返回原引用。
 */
export function markAiGuidanceSeq(existing: number[], message: AiChatSeqMessageView): number[] {
  const seq = message.seq;
  if (seq === null || seq <= 0 || existing.includes(seq)) {
    return existing;
  }
  return [...existing, seq];
}

/**
 * 把工具消息映射为 ThoughtChain 链式节点数据：内容节点=中文标签 + SQL 摘要 + 行数徽标，
 * 失败节点呈红色错误态；详情文本复用 aiToolDetail（失败展示错误 JSON，成功展示 SQL/截断提示）。
 * @param messages - 工具消息列表（通常来自合并后的消息流过滤）。
 * @returns 顺序对应的节点数据数组。
 */
export function buildAiThoughtChainItems(messages: AiChatSeqMessageView[]): AiThoughtChainNode[] {
  return messages.map((message, index) => {
    const visual = aiToolCardVisual(message.toolName);
    return {
      key: message.seq !== null ? `tool-${message.seq}` : `tool-local-${index}`,
      toolName: message.toolName ?? '',
      label: visual.label,
      sqlSummary: message.sqlSummary,
      rowCount: message.rowCount,
      success: message.success,
      detail: aiToolDetail(message),
      status: message.success ? ('success' as const) : ('error' as const),
    };
  });
}

/**
 * 把消息流映射为气泡列表数据（Bubble.List 的 items 数据源）：
 * - 用户消息 → 右侧气泡；插话消息（服务端 Seq 已记录，或本地乐观插话在途）携带角标。
 * - 有正文的 assistant 消息 → 左侧带头像气泡。
 * - 空正文的中间 assistant 轮次（tool_calls 轮）与其后连续工具消息合并为一条 toolTrace，
 *   组件侧渲染为 ThoughtChain；无工具跟随的空 assistant 回退“（模型未返回文本）”占位气泡。
 * 纯函数，输出与 React 组件解耦，便于 Vitest 直接断言映射结构。
 * @param messages - 当前消息流（含乐观与轮询合并结果）。
 * @param options - guidanceSeqs：已确认的插话 Seq 集合；pendingGuidanceContent：在途本地插话文本。
 * @returns 气泡视图模型数组，顺序与消息流一致。
 */
export function buildAiBubbleItems(
  messages: AiChatSeqMessageView[],
  options: { guidanceSeqs: number[]; pendingGuidanceContent: string | null },
): AiBubbleItemViewModel[] {
  const items: AiBubbleItemViewModel[] = [];
  /** 当前收集中的 toolTrace 组：由空正文 assistant 轮次开启，后续工具消息挂入其下。 */
  let pendingTrace: AiBubbleItemViewModel | null = null;
  /** 落盘当前 toolTrace 组：零节点（空 assistant 无工具跟随）时回退为占位文本气泡。 */
  const flushTrace = () => {
    if (pendingTrace === null) return;
    if (pendingTrace.thoughtNodes.length === 0) {
      pendingTrace.kind = 'assistant';
      pendingTrace.content = '（模型未返回文本）';
    }
    items.push(pendingTrace);
    pendingTrace = null;
  };
  messages.forEach((message, index) => {
    const key = message.seq !== null ? `seq-${message.seq}` : `local-${index}`;
    if (message.role === 'tool') {
      const node = buildAiThoughtChainItems([message])[0];
      if (pendingTrace === null) {
        pendingTrace = { key, kind: 'toolTrace', content: '', guidanceBadge: false, thoughtNodes: [node] };
      } else {
        pendingTrace.thoughtNodes.push(node);
      }
      return;
    }
    if (message.role === 'assistant') {
      // 空正文的中间 assistant 轮次（tool_calls 轮）：开启/延续 toolTrace 组，与其工具合并展示。
      if (!message.content) {
        if (pendingTrace === null) {
          pendingTrace = { key, kind: 'toolTrace', content: '', guidanceBadge: false, thoughtNodes: [] };
        }
        return;
      }
      flushTrace();
      items.push({
        key,
        kind: 'assistant',
        content: message.content,
        guidanceBadge: false,
        thoughtNodes: [],
      });
      return;
    }
    flushTrace();
    // 用户消息：角标判定 = 服务端 Seq 已记录为插话，或本地乐观消息与在途插话文本一致。
    const guidanceBadge = (message.seq !== null && options.guidanceSeqs.includes(message.seq))
      || (message.seq === null && message.content === options.pendingGuidanceContent
        && options.pendingGuidanceContent !== null);
    items.push({ key, kind: 'user', content: message.content || '', guidanceBadge, thoughtNodes: [] });
  });
  flushTrace();
  return items;
}

/** 中止当前轮次后的复位结果：发送/失败态归零、轮询已停、给出面向用户的中止提示文案。 */
export interface AiTurnStateAfterAbort {
  /** 发送态复位为 false（停止是主动行为，不是失败）。 */
  sending: boolean;
  /** 不置失败态：中止不代表本轮处理失败。 */
  turnFailed: boolean;
  /** 用户提示文案：说明后端循环已随连接断开中止，可重新提问。 */
  notice: string;
}

/**
 * 计算中止当前轮次后的复位状态（Sender 停止按钮的语义）：停止是用户主动行为——
 * 不置失败态、恢复输入可用；原 POST 因 AbortController 断开，后端工具循环随之中止。
 * @returns 复位后的轮次状态与提示文案。
 */
export function resetAiTurnStateAfterAbort(): AiTurnStateAfterAbort {
  return {
    sending: false,
    turnFailed: false,
    notice: '已停止等待本轮回复；后端工具循环已随连接断开中止，可重新提问。',
  };
}
