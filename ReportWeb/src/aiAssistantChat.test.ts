import { describe, expect, it } from 'vitest';
import {
  AiSlowTurnHintThresholdMs,
  aiToolCardVisual,
  aiToolDetail,
  buildAiBubbleItems,
  buildAiThoughtChainItems,
  deriveAiThinkingStatus,
  formatAiToolLabel,
  isAiDisabledError,
  isAiLoopFailed,
  isAiRetryableError,
  isAiTurnStalled,
  latestAiSeq,
  markAiGuidanceSeq,
  mergeAiProgressMessages,
  reconcileAiTurnMessages,
  resetAiTurnStateAfterAbort,
  resolveAiDataSourceOptions,
  shouldPreCreateAiSession,
  shouldSendAsGuidance,
} from './aiAssistantChat';
import type {
  AiChatMessageView,
  AiChatProgressDocument,
  AiChatSeqMessageView,
  AiChatSeqResult,
  AiDataSourceOption,
  DataSourceDocument,
} from './types';

/**
 * 构造测试用的工具消息（与后端 PocAiChatMessageView 契约一致）。
 * @param overrides - 需要覆盖的字段。
 * @returns 完整工具消息。
 */
function toolMessage(overrides: Partial<AiChatSeqMessageView>): AiChatSeqMessageView {
  return {
    role: 'tool',
    content: null,
    toolName: 'run_readonly_sql',
    toolCallId: 'call_1',
    sqlSummary: 'SELECT COUNT(1) AS Total FROM PatientInfo',
    rowCount: 4,
    success: true,
    truncated: false,
    seq: 1,
    ...overrides,
  };
}

/**
 * 构造测试用的对话响应（Seq 形态）。
 * @param messages - 本轮消息。
 * @param expired - 是否判定会话过期。
 * @returns 对话响应。
 */
function chatResult(messages: AiChatSeqMessageView[], expired = false): AiChatSeqResult {
  return { sessionId: 'session-1', messages, expired, draft: null };
}

/**
 * 构造服务端用户消息。
 * @param content - 消息文本。
 * @param seq - 会话内序号。
 * @returns 用户消息。
 */
function userMessage(content: string, seq: number): AiChatSeqMessageView {
  return {
    role: 'user',
    content,
    toolName: null,
    toolCallId: null,
    sqlSummary: null,
    rowCount: null,
    success: true,
    truncated: false,
    seq,
  };
}

/**
 * 构造服务端助手文本消息。
 * @param content - 消息文本。
 * @param seq - 会话内序号。
 * @returns 助手消息。
 */
function assistantMessage(content: string, seq: number): AiChatSeqMessageView {
  return {
    role: 'assistant',
    content,
    toolName: null,
    toolCallId: null,
    sqlSummary: null,
    rowCount: null,
    success: true,
    truncated: false,
    seq,
  };
}

/**
 * 构造本地乐观用户消息（seq 为 null，等待服务端版本替换）。
 * @param content - 消息文本。
 * @returns 乐观用户消息。
 */
function optimisticUserMessage(content: string): AiChatSeqMessageView {
  return {
    role: 'user',
    content,
    toolName: null,
    toolCallId: null,
    sqlSummary: null,
    rowCount: null,
    success: true,
    truncated: false,
    seq: null,
  };
}

/**
 * 构造 progress 轮询文档。
 * @param loopState - 循环状态。
 * @param messages - 增量消息。
 * @returns 轮询文档。
 */
function progress(loopState: AiChatProgressDocument['loopState'], messages: AiChatSeqMessageView[]): AiChatProgressDocument {
  return { loopState, messages };
}

describe('formatAiToolLabel', () => {
  it('renders tool name, sql summary and row count as one collapsed line', () => {
    const label = formatAiToolLabel('run_readonly_sql', 'SELECT COUNT(1) FROM PatientInfo', 4);

    expect(label).toBe('run_readonly_sql · SELECT COUNT(1) FROM PatientInfo · 4 行');
  });

  it('omits sql and row count segments when absent', () => {
    expect(formatAiToolLabel('list_data_sources', null, null)).toBe('list_data_sources');
  });

  it('truncates long sql summaries with an ellipsis', () => {
    const longSql = 'S'.repeat(200);

    const label = formatAiToolLabel('sample_rows', longSql, 20);

    expect(label.startsWith('sample_rows · ')).toBe(true);
    expect(label.endsWith('… · 20 行')).toBe(true);
    expect(label.length).toBeLessThan(longSql.length);
  });
});

describe('aiToolDetail', () => {
  it('shows the structured error content when the tool failed', () => {
    const detail = aiToolDetail(toolMessage({
      success: false,
      content: '{"error":"TABLE_NAME_INVALID","message":"表名不符合规范"}',
    }));

    expect(detail).toContain('TABLE_NAME_INVALID');
  });

  it('appends a truncation notice for capped results', () => {
    const detail = aiToolDetail(toolMessage({ truncated: true, rowCount: 20 }));

    expect(detail).toContain('截断');
    expect(detail).toContain('20');
  });
});

describe('latestAiSeq', () => {
  it('returns the largest server seq as the polling cursor', () => {
    const messages = [userMessage('问题', 1), assistantMessage('回答', 4)];

    expect(latestAiSeq(messages)).toBe(4);
  });

  it('returns zero when only optimistic local messages exist', () => {
    expect(latestAiSeq([optimisticUserMessage('还在发送中')])).toBe(0);
  });
});

describe('mergeAiProgressMessages', () => {
  it('appends unseen seq messages from the polling tick', () => {
    const existing: AiChatSeqMessageView[] = [userMessage('数一下行数', 1)];
    const tick = progress('running', [
      assistantMessage('我需要先查一下', 2),
      toolMessage({ seq: 3, toolCallId: 'call_1', rowCount: 4 }),
    ]);

    const merged = mergeAiProgressMessages(existing, tick);

    expect(merged).toHaveLength(3);
    expect(merged[1].seq).toBe(2);
    expect(merged[2].role).toBe('tool');
  });

  it('skips messages whose seq was already rendered', () => {
    const existing: AiChatSeqMessageView[] = [userMessage('数一下行数', 1), assistantMessage('我需要先查一下', 2)];
    const tick = progress('running', [assistantMessage('我需要先查一下', 2)]);

    expect(mergeAiProgressMessages(existing, tick)).toBe(existing);
  });

  it('replaces the optimistic user bubble once the server user message arrives', () => {
    const existing: AiChatSeqMessageView[] = [
      assistantMessage('上一轮回答', 1),
      optimisticUserMessage('新问题'),
    ];
    const tick = progress('running', [userMessage('新问题', 2)]);

    const merged = mergeAiProgressMessages(existing, tick);

    expect(merged).toHaveLength(2);
    expect(merged.every((message) => message.seq !== null)).toBe(true);
    expect(merged[1].seq).toBe(2);
  });

  it('keeps the optimistic bubble when polled messages do not include the user turn', () => {
    const existing: AiChatSeqMessageView[] = [optimisticUserMessage('新问题')];
    const tick = progress('running', [toolMessage({ seq: 5 })]);

    const merged = mergeAiProgressMessages(existing, tick);

    expect(merged).toHaveLength(2);
    expect(merged[0].seq).toBeNull();
  });

  it('returns the same list when a tick has no new messages', () => {
    const existing: AiChatSeqMessageView[] = [userMessage('问题', 1)];
    const tick = progress('running', []);

    expect(mergeAiProgressMessages(existing, tick)).toBe(existing);
  });
});

describe('reconcileAiTurnMessages', () => {
  it('drops optimistic and polled duplicates and adopts the final server turn', () => {
    const existing: AiChatSeqMessageView[] = [
      assistantMessage('上一轮回答', 1),
      optimisticUserMessage('新问题'),
      assistantMessage('轮询到的中间回复', 3),
    ];
    const final = chatResult([
      userMessage('新问题', 2),
      assistantMessage('轮询到的中间回复', 3),
      assistantMessage('最终结论', 4),
    ]);

    const reconciled = reconcileAiTurnMessages(existing, final);

    expect(reconciled.map((message) => message.seq)).toEqual([1, 2, 3, 4]);
    expect(reconciled[reconciled.length - 1].content).toBe('最终结论');
  });

  it('replaces the whole stream after session expiry', () => {
    const existing: AiChatSeqMessageView[] = [assistantMessage('旧上下文', 1), optimisticUserMessage('新问题')];
    const final = chatResult([userMessage('过期后的新问题', 1)], true);

    const reconciled = reconcileAiTurnMessages(existing, final);

    expect(reconciled).toHaveLength(1);
    expect(reconciled[0].content).toBe('过期后的新问题');
  });
});

describe('isAiLoopFailed', () => {
  it('marks only the failed loop state', () => {
    expect(isAiLoopFailed('failed')).toBe(true);
    expect(isAiLoopFailed('running')).toBe(false);
    expect(isAiLoopFailed('completed')).toBe(false);
    expect(isAiLoopFailed('idle')).toBe(false);
  });
});

describe('error classification', () => {
  it('marks llm transport failures as retryable', () => {
    expect(isAiRetryableError(new Error('LLM_REQUEST_TIMEOUT: LLM 服务响应超时'))).toBe(true);
    expect(isAiRetryableError(new Error('LLM_REQUEST_FAILED: LLM 服务返回 HTTP 502'))).toBe(true);
    expect(isAiRetryableError(new Error('无法连接后端服务，请确认服务已启动。'))).toBe(true);
  });

  it('does not ask users to retry configuration errors', () => {
    expect(isAiRetryableError(new Error('AI_DISABLED: AI 能力未启用'))).toBe(false);
    expect(isAiRetryableError(new Error('AI_MESSAGE_REQUIRED: 对话内容不能为空。'))).toBe(false);
  });

  it('identifies ai-disabled errors for admin guidance', () => {
    expect(isAiDisabledError(new Error('AI_DISABLED: AI 能力未启用，请在“AI 设置”页开启后再使用。'))).toBe(true);
    expect(isAiDisabledError(new Error('LLM_REQUEST_FAILED: LLM 服务返回 HTTP 401'))).toBe(false);
  });
});

describe('resolveAiDataSourceOptions', () => {
  it('keeps only enabled sources and sorts by localized name', () => {
    const sources: DataSourceDocument[] = [
      {
        code: 'lis_db', name: '检验库', dbType: 'Oracle', server: '', port: 0, database: '',
        userName: '', passwordConfigured: true, timeoutSeconds: 30, maxRows: 1000, enabled: true, revision: 1,
      },
      {
        code: 'his_db', name: 'HIS 主库', dbType: 'SqlServer', server: '', port: 0, database: '',
        userName: '', passwordConfigured: true, timeoutSeconds: 30, maxRows: 1000, enabled: true, revision: 1,
      },
      {
        code: 'old_db', name: '停用库', dbType: 'MySql', server: '', port: 0, database: '',
        userName: '', passwordConfigured: false, timeoutSeconds: 30, maxRows: 1000, enabled: false, revision: 1,
      },
    ];

    const options: AiDataSourceOption[] = resolveAiDataSourceOptions(sources);

    // zh-CN 排序下“检验库”排在拉丁字母名称之前；断言只锁定“启用过滤 + 稳定排序”语义。
    expect(options.map((option) => option.code)).toEqual(['lis_db', 'his_db']);
    expect(options.every((option) => !option.dbType.includes('ConnectionString'))).toBe(true);
  });
});

describe('shouldPreCreateAiSession', () => {
  it('triggers only when no session exists, a source is picked and not sending', () => {
    expect(shouldPreCreateAiSession(null, 'his_db', false)).toBe(true);
  });

  it('does not trigger when a session id already exists', () => {
    expect(shouldPreCreateAiSession('ai-abc', 'his_db', false)).toBe(false);
  });

  it('does not trigger before a data source is selected', () => {
    expect(shouldPreCreateAiSession(null, '', false)).toBe(false);
    expect(shouldPreCreateAiSession(null, '   ', false)).toBe(false);
  });

  it('does not trigger while a turn is in flight', () => {
    expect(shouldPreCreateAiSession(null, 'his_db', true)).toBe(false);
  });
});

describe('deriveAiThinkingStatus', () => {
  it('shows a planning headline before any tool round starts', () => {
    const status = deriveAiThinkingStatus([optimisticUserMessage('数一下行数')], 3);

    expect(status.headline).toBe('正在理解问题并规划查询');
    expect(status.toolName).toBeNull();
    expect(status.round).toBeNull();
    expect(status.elapsedLabel).toBe('已思考 3s');
  });

  it('derives the running tool name and round from the latest polled messages', () => {
    const messages = [
      userMessage('数一下行数', 1),
      assistantMessage('我先统计一下', 2),
      toolMessage({ seq: 3, toolName: 'run_readonly_sql', rowCount: 4 }),
    ];

    const status = deriveAiThinkingStatus(messages, 42.7);

    expect(status.headline).toBe('正在调用 run_readonly_sql（第 1 轮）');
    expect(status.toolName).toBe('run_readonly_sql');
    expect(status.round).toBe(1);
    expect(status.elapsedLabel).toBe('已思考 42s');
  });

  it('advances to the next round headline when the latest message is an assistant plan', () => {
    const messages = [
      userMessage('数一下行数', 1),
      toolMessage({ seq: 2, toolName: 'describe_table', rowCount: null }),
      toolMessage({ seq: 3, toolName: 'sample_rows', rowCount: 5 }),
      assistantMessage('再取一次样', 4),
    ];

    const status = deriveAiThinkingStatus(messages, 8);

    expect(status.headline).toBe('正在执行第 3 轮工具调用');
    expect(status.toolName).toBe('sample_rows');
    expect(status.round).toBe(3);
  });

  it('clamps negative elapsed seconds and keeps the label stable for empty streams', () => {
    const status = deriveAiThinkingStatus([], -5);

    expect(status.headline).toBe('正在理解问题并规划查询');
    expect(status.elapsedLabel).toBe('已思考 0s');
  });
});

describe('aiToolCardVisual', () => {
  it('maps every known tool to a dedicated icon kind and Chinese label', () => {
    expect(aiToolCardVisual('describe_table')).toEqual({ kind: 'structure', label: '查表结构' });
    expect(aiToolCardVisual('sample_rows')).toEqual({ kind: 'data', label: '采样数据' });
    expect(aiToolCardVisual('run_readonly_sql')).toEqual({ kind: 'sql', label: '只读 SQL' });
    expect(aiToolCardVisual('submit_report_spec')).toEqual({ kind: 'submit', label: '提交草稿' });
    expect(aiToolCardVisual('dry_run_report_spec')).toEqual({ kind: 'dryrun', label: '试跑验证' });
    expect(aiToolCardVisual('list_tables')).toEqual({ kind: 'structure', label: '查表清单' });
  });

  it('falls back to the generic card for unknown or missing tool names', () => {
    expect(aiToolCardVisual('future_tool')).toEqual({ kind: 'generic', label: 'future_tool' });
    expect(aiToolCardVisual(null)).toEqual({ kind: 'generic', label: '工具调用' });
  });
});

describe('isAiTurnStalled', () => {
  it('flags a turn only after the threshold elapsed since sending', () => {
    const startedAt = 1_000_000;
    expect(isAiTurnStalled(startedAt, startedAt + AiSlowTurnHintThresholdMs - 1, null)).toBe(false);
    expect(isAiTurnStalled(startedAt, startedAt + AiSlowTurnHintThresholdMs, null)).toBe(true);
  });

  it('resets the baseline whenever progress messages keep arriving', () => {
    const startedAt = 1_000_000;
    const lastProgress = startedAt + 40_000;
    // 虽然整轮已超过 45 秒，但最近 5 秒内仍有增量，说明链路活着，不应提示。
    expect(isAiTurnStalled(startedAt, startedAt + 46_000, lastProgress)).toBe(false);
    expect(isAiTurnStalled(startedAt, startedAt + 86_000, lastProgress)).toBe(true);
  });

  it('honours a custom threshold for tests', () => {
    const startedAt = 2_000_000;
    expect(isAiTurnStalled(startedAt, startedAt + 4_999, null, 5_000)).toBe(false);
    expect(isAiTurnStalled(startedAt, startedAt + 5_000, null, 5_000)).toBe(true);
  });
});

describe('shouldSendAsGuidance', () => {
  it('routes to the guidance endpoint only while a turn is running with a live session', () => {
    expect(shouldSendAsGuidance(true, 'session-1')).toBe(true);
    // 空闲发送走正常 /chat，即便已有历史会话号。
    expect(shouldSendAsGuidance(false, 'session-1')).toBe(false);
    // 无会话号的运行态（首回合话未建）：降级走正常链路。
    expect(shouldSendAsGuidance(true, null)).toBe(false);
  });
});

describe('markAiGuidanceSeq', () => {
  it('records the server seq of a guidance reply once', () => {
    const first = markAiGuidanceSeq([], userMessage('换个口径', 7));
    expect(first).toEqual([7]);
    // 重复响应幂等：同一 Seq 不重复记录。
    expect(markAiGuidanceSeq(first, userMessage('换个口径', 7))).toBe(first);
  });

  it('ignores replies without a usable seq', () => {
    expect(markAiGuidanceSeq([3], optimisticUserMessage('换个口径'))).toEqual([3]);
  });
});

describe('buildAiThoughtChainItems', () => {
  it('maps tool messages into success nodes with label, sql summary and row count', () => {
    const nodes = buildAiThoughtChainItems([toolMessage({ seq: 5 })]);
    expect(nodes).toHaveLength(1);
    expect(nodes[0]).toMatchObject({
      key: 'tool-5',
      toolName: 'run_readonly_sql',
      label: '只读 SQL',
      sqlSummary: 'SELECT COUNT(1) AS Total FROM PatientInfo',
      rowCount: 4,
      status: 'success',
    });
  });

  it('marks failed tools with the error status and error detail text', () => {
    const failed = toolMessage({ seq: 6, success: false, content: '{"error":"boom"}', rowCount: null, sqlSummary: null });
    const nodes = buildAiThoughtChainItems([failed]);
    expect(nodes[0].status).toBe('error');
    expect(nodes[0].detail).toBe('{"error":"boom"}');
  });
});

describe('buildAiBubbleItems', () => {
  it('merges an empty assistant tool-call round with its following tools into one toolTrace item', () => {
    const items = buildAiBubbleItems([
      userMessage('查一下患者表', 1),
      assistantMessage('', 2),
      toolMessage({ seq: 3 }),
      toolMessage({ seq: 4, toolName: 'list_tables', sqlSummary: null, rowCount: 12 }),
      assistantMessage('患者表共有 12 张相关表', 5),
    ], { guidanceSeqs: [], pendingGuidanceContent: null });
    expect(items.map((item) => item.kind)).toEqual(['user', 'toolTrace', 'assistant']);
    expect(items[1].thoughtNodes.map((node) => node.toolName)).toEqual(['run_readonly_sql', 'list_tables']);
    // 文本 assistant 保持独立气泡且不受 toolTrace 合并影响。
    expect(items[2].content).toBe('患者表共有 12 张相关表');
  });

  it('falls back to a placeholder bubble for an empty assistant round without tools', () => {
    const items = buildAiBubbleItems([assistantMessage('', 9)],
      { guidanceSeqs: [], pendingGuidanceContent: null });
    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({ kind: 'assistant', content: '（模型未返回文本）', thoughtNodes: [] });
  });

  it('collects trailing tools into a standalone toolTrace while the loop is still running', () => {
    const items = buildAiBubbleItems([
      userMessage('继续', 1),
      assistantMessage('好的', 2),
      toolMessage({ seq: 3, toolName: 'sample_rows' }),
    ], { guidanceSeqs: [], pendingGuidanceContent: null });
    expect(items.map((item) => item.kind)).toEqual(['user', 'assistant', 'toolTrace']);
    expect(items[2].thoughtNodes[0].label).toBe('采样数据');
  });

  it('badges guidance bubbles by confirmed seq or by matching pending optimistic content', () => {
    const confirmed = buildAiBubbleItems([
      userMessage('只看门诊数据', 4),
      optimisticUserMessage('换一张表'),
    ], { guidanceSeqs: [4], pendingGuidanceContent: '换一张表' });
    expect(confirmed[0].guidanceBadge).toBe(true);
    expect(confirmed[1].guidanceBadge).toBe(true);

    // 普通乐观消息（非插话）不携带角标。
    const plain = buildAiBubbleItems([optimisticUserMessage('普通新问题')],
      { guidanceSeqs: [], pendingGuidanceContent: '别的文本' });
    expect(plain[0].guidanceBadge).toBe(false);
  });
});

describe('resetAiTurnStateAfterAbort', () => {
  it('resets sending state without marking the turn as failed', () => {
    const state = resetAiTurnStateAfterAbort();
    expect(state.sending).toBe(false);
    expect(state.turnFailed).toBe(false);
    expect(state.notice.length).toBeGreaterThan(0);
  });
});
