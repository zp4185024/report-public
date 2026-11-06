import { describe, expect, it } from 'vitest';
import {
  aiApiKeyPlaceholder,
  createAiSettingsDraft,
  toAiSettingsInput,
  validateAiSettingsDraft,
} from './aiSettingsForm';
import type { AiSettings } from './types';

/**
 * 构造设置页测试用的服务端设置文档，字段与后端 PocAiSettingsDocument 打码契约一致。
 * @param overrides - 需要覆盖的字段。
 * @returns 完整设置文档。
 */
function settingsDocument(overrides: Partial<AiSettings>): AiSettings {
  return {
    isEnabled: true,
    baseUrl: 'http://llm.test/v1',
    apiKeyMasked: 'sk-***9876',
    apiKeyConfigured: true,
    model: 'deepseek-chat',
    visionModel: '',
    sampleRowLimit: 20,
    sqlProbeEnabled: true,
    sqlProbeRowLimit: 100,
    maxToolRounds: 8,
    updateTime: '2026-11-05T10:00:00',
    ...overrides,
  };
}

describe('createAiSettingsDraft', () => {
  it('loads server settings but keeps the api key field empty for masked echo', () => {
    const draft = createAiSettingsDraft(settingsDocument({}));

    expect(draft.apiKey).toBe('');
    expect(draft.baseUrl).toBe('http://llm.test/v1');
    expect(draft.model).toBe('deepseek-chat');
    expect(draft.isEnabled).toBe(true);
    expect(draft.sampleRowLimit).toBe(20);
    expect(draft.sqlProbeEnabled).toBe(true);
    expect(draft.sqlProbeRowLimit).toBe(100);
    expect(draft.maxToolRounds).toBe(8);
  });
});

describe('aiApiKeyPlaceholder', () => {
  it('shows the masked server value when a key is already configured', () => {
    const placeholder = aiApiKeyPlaceholder(settingsDocument({ apiKeyConfigured: true, apiKeyMasked: 'sk-***9876' }));

    expect(placeholder).toBe('已保存：sk-***9876（留空表示保留）');
    // 打码值必须仍是打码形态，不允许把原文或可还原信息带入界面。
    expect(placeholder).not.toContain('sk-my');
  });

  it('guides first-time configuration when no key exists', () => {
    const placeholder = aiApiKeyPlaceholder(settingsDocument({ apiKeyConfigured: false, apiKeyMasked: '' }));

    expect(placeholder).toBe('未配置，保存后生效');
  });
});

describe('validateAiSettingsDraft', () => {
  it('accepts a fully valid draft', () => {
    const draft = createAiSettingsDraft(settingsDocument({}));

    expect(validateAiSettingsDraft(draft)).toBeNull();
  });

  it('rejects enabled drafts without a valid http(s) base url', () => {
    expect(validateAiSettingsDraft({
      ...createAiSettingsDraft(settingsDocument({})),
      baseUrl: 'not-a-url',
    })).toContain('http(s)');
    expect(validateAiSettingsDraft({
      ...createAiSettingsDraft(settingsDocument({})),
      baseUrl: '   ',
    })).toContain('http(s)');
  });

  it('rejects enabled drafts without a model name', () => {
    expect(validateAiSettingsDraft({
      ...createAiSettingsDraft(settingsDocument({})),
      model: '',
    })).toContain('模型名称');
  });

  it('rejects sample row limits outside 0-100', () => {
    expect(validateAiSettingsDraft({
      ...createAiSettingsDraft(settingsDocument({})),
      sampleRowLimit: 101,
    })).toContain('采样行数上限');
    expect(validateAiSettingsDraft({
      ...createAiSettingsDraft(settingsDocument({})),
      sampleRowLimit: -1,
    })).toContain('采样行数上限');
  });

  it('rejects probe row limits outside 1-1000 and tool rounds outside 1-20', () => {
    expect(validateAiSettingsDraft({
      ...createAiSettingsDraft(settingsDocument({})),
      sqlProbeRowLimit: 1001,
    })).toContain('SQL 探测行数上限');
    expect(validateAiSettingsDraft({
      ...createAiSettingsDraft(settingsDocument({})),
      maxToolRounds: 21,
    })).toContain('最大工具轮数');
  });

  it('keeps disabled drafts valid even when endpoint fields are incomplete', () => {
    const draft = createAiSettingsDraft(settingsDocument({ isEnabled: false, baseUrl: '', model: '' }));

    expect(validateAiSettingsDraft(draft)).toBeNull();
  });
});

describe('toAiSettingsInput', () => {
  it('submits the raw api key field so empty means keep existing server value', () => {
    const draft = createAiSettingsDraft(settingsDocument({}));
    draft.apiKey = '';

    const input = toAiSettingsInput(draft);

    expect(input.apiKey).toBe('');
    expect(input.baseUrl).toBe('http://llm.test/v1');
    expect(input.isEnabled).toBe(true);
  });

  it('trims endpoint and model text before submission', () => {
    const draft = createAiSettingsDraft(settingsDocument({}));
    draft.baseUrl = '  http://llm.test/v1  ';
    draft.model = ' deepseek-chat ';

    const input = toAiSettingsInput(draft);

    expect(input.baseUrl).toBe('http://llm.test/v1');
    expect(input.model).toBe('deepseek-chat');
  });
});
