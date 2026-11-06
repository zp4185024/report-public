import { describe, expect, it } from 'vitest';
import {
  aiToolDetail,
  formatAiToolLabel,
  isAiDisabledError,
  isAiLoopFailed,
  isAiRetryableError,
  latestAiSeq,
  mergeAiProgressMessages,
  reconcileAiTurnMessages,
  resolveAiDataSourceOptions,
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
