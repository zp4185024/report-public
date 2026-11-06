import { describe, expect, it } from 'vitest';
import {
  aiToolDetail,
  formatAiToolLabel,
  isAiDisabledError,
  isAiRetryableError,
  nextAiChatMessages,
  resolveAiDataSourceOptions,
} from './aiAssistantChat';
import type { AiChatMessageView, AiChatResult, DataSourceDocument } from './types';

/**
 * 构造测试用的工具消息（与后端 PocAiChatMessageView 契约一致）。
 * @param overrides - 需要覆盖的字段。
 * @returns 完整工具消息。
 */
function toolMessage(overrides: Partial<AiChatMessageView>): AiChatMessageView {
  return {
    role: 'tool',
    content: null,
    toolName: 'run_readonly_sql',
    toolCallId: 'call_1',
    sqlSummary: 'SELECT COUNT(1) AS Total FROM PatientInfo',
    rowCount: 4,
    success: true,
    truncated: false,
    ...overrides,
  };
}

/**
 * 构造测试用的对话响应。
 * @param messages - 本轮消息。
 * @param expired - 是否判定会话过期。
 * @returns 对话响应。
 */
function chatResult(messages: AiChatMessageView[], expired = false): AiChatResult {
  return { sessionId: 'session-1', messages, expired, draft: null };
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

describe('nextAiChatMessages', () => {
  it('appends turn messages to the local stream', () => {
    const existing: AiChatMessageView[] = [
      { role: 'user', content: '上一轮', toolName: null, toolCallId: null, sqlSummary: null, rowCount: null, success: true, truncated: false },
    ];
    const turn: AiChatMessageView[] = [
      { role: 'assistant', content: '回答', toolName: null, toolCallId: null, sqlSummary: null, rowCount: null, success: true, truncated: false },
    ];

    const merged = nextAiChatMessages(existing, chatResult(turn));

    expect(merged).toHaveLength(2);
    expect(merged[1].content).toBe('回答');
  });

  it('replaces the stream after session expiry because server context was rebuilt', () => {
    const existing: AiChatMessageView[] = [
      { role: 'user', content: '旧上下文', toolName: null, toolCallId: null, sqlSummary: null, rowCount: null, success: true, truncated: false },
    ];
    const turn: AiChatMessageView[] = [
      { role: 'user', content: '过期后的新问题', toolName: null, toolCallId: null, sqlSummary: null, rowCount: null, success: true, truncated: false },
    ];

    const merged = nextAiChatMessages(existing, chatResult(turn, true));

    expect(merged).toHaveLength(1);
    expect(merged[0].content).toBe('过期后的新问题');
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

    const options = resolveAiDataSourceOptions(sources);

    // zh-CN 排序下“检验库”排在拉丁字母名称之前；断言只锁定“启用过滤 + 稳定排序”语义。
    expect(options.map((option) => option.code)).toEqual(['lis_db', 'his_db']);
    expect(options.every((option) => !option.dbType.includes('ConnectionString'))).toBe(true);
  });
});
