import type { AiSettings, AiSettingsInput } from './types';

/** AI 设置表单草稿：apiKey 恒为输入框原文，空串表示“保留服务端已存密钥”。 */
export interface AiSettingsDraft {
  isEnabled: boolean;
  baseUrl: string;
  apiKey: string;
  model: string;
  /// 视觉模型名；空串 = 回退主 model（主模型具备视觉能力时单模型即可）。
  visionModel: string;
  sampleRowLimit: number;
  sqlProbeEnabled: boolean;
  sqlProbeRowLimit: number;
  maxToolRounds: number;
}

/** 与后端校验一致的限额边界：采样 0-100（0 禁用）、探测 1-1000、轮数 1-20。 */
export const AiSettingsLimits = {
  sampleRowLimitMax: 100,
  sqlProbeRowLimitMax: 1000,
  maxToolRoundsMax: 20,
} as const;

/**
 * 由服务端设置文档构造表单草稿；apiKey 刻意置空，配合 placeholder 展示打码值，
 * 保证“打码回显不会被误回存为空串以外的内容”，空输入在提交时表示保留原密钥。
 * @param document - 服务端返回的打码设置文档。
 * @returns 可编辑的表单草稿。
 */
export function createAiSettingsDraft(document: AiSettings): AiSettingsDraft {
  return {
    isEnabled: document.isEnabled,
    baseUrl: document.baseUrl,
    apiKey: '',
    model: document.model,
    visionModel: document.visionModel ?? '',
    sampleRowLimit: document.sampleRowLimit,
    sqlProbeEnabled: document.sqlProbeEnabled,
    sqlProbeRowLimit: document.sqlProbeRowLimit,
    maxToolRounds: document.maxToolRounds,
  };
}

/**
 * 计算密钥输入框的占位文本：已配置时展示服务端打码值（sk-***last4），
 * 未配置时提示可留空；原文永不进入前端状态。
 * @param document - 服务端返回的打码设置文档。
 * @returns 密钥输入框的 placeholder 文本。
 */
export function aiApiKeyPlaceholder(document: AiSettings): string {
  return document.apiKeyConfigured
    ? `已保存：${document.apiKeyMasked}（留空表示保留）`
    : '未配置，保存后生效';
}

/**
 * 校验设置草稿；与后端 PocAiSettingsService 的校验逐条对应，错误文案可直接展示。
 * @param draft - 当前表单草稿。
 * @returns 第一个校验错误文案；全部合法时返回 null。
 */
export function validateAiSettingsDraft(draft: AiSettingsDraft): string | null {
  const baseUrl = draft.baseUrl.trim();
  const model = draft.model.trim();
  if (draft.isEnabled) {
    if (!baseUrl || !/^https?:\/\/.+/.test(baseUrl)) {
      return '启用 AI 时必须提供合法的 http(s) 服务地址。';
    }
    if (!model) {
      return '启用 AI 时必须提供模型名称。';
    }
  }
  if (!Number.isInteger(draft.sampleRowLimit) ||
    draft.sampleRowLimit < 0 ||
    draft.sampleRowLimit > AiSettingsLimits.sampleRowLimitMax) {
    return `采样行数上限必须是 0-${AiSettingsLimits.sampleRowLimitMax} 的整数（0 表示禁用采样）。`;
  }
  if (!Number.isInteger(draft.sqlProbeRowLimit) ||
    draft.sqlProbeRowLimit < 1 ||
    draft.sqlProbeRowLimit > AiSettingsLimits.sqlProbeRowLimitMax) {
    return `SQL 探测行数上限必须是 1-${AiSettingsLimits.sqlProbeRowLimitMax} 的整数。`;
  }
  if (!Number.isInteger(draft.maxToolRounds) ||
    draft.maxToolRounds < 1 ||
    draft.maxToolRounds > AiSettingsLimits.maxToolRoundsMax) {
    return `最大工具轮数必须是 1-${AiSettingsLimits.maxToolRoundsMax} 的整数。`;
  }
  if (draft.visionModel.trim().length > 128) {
    return '视觉模型名称过长（上限 128 字符）。';
  }
  return null;
}

/**
 * 把表单草稿转换为保存输入；密钥原样提交（空串=保留原值），文本字段先去首尾空白。
 * @param draft - 当前表单草稿。
 * @returns 可直接提交给 POST /ai/settings 的输入。
 */
export function toAiSettingsInput(draft: AiSettingsDraft): AiSettingsInput {
  return {
    isEnabled: draft.isEnabled,
    baseUrl: draft.baseUrl.trim(),
    apiKey: draft.apiKey,
    model: draft.model.trim(),
    sampleRowLimit: draft.sampleRowLimit,
    sqlProbeEnabled: draft.sqlProbeEnabled,
    sqlProbeRowLimit: draft.sqlProbeRowLimit,
    maxToolRounds: draft.maxToolRounds,
    visionModel: draft.visionModel.trim(),
  };
}
