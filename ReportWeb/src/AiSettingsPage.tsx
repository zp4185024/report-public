import { useEffect, useState } from 'react';
import { Bot, LoaderCircle, Save, Zap } from 'lucide-react';
import { aiApi } from './api';
import {
  aiApiKeyPlaceholder,
  createAiSettingsDraft,
  toAiSettingsInput,
  validateAiSettingsDraft,
  type AiSettingsDraft,
} from './aiSettingsForm';
import { StatusToasts } from './StatusToasts';
import type { AiSettings } from './types';

/**
 * 按 AI 设置页的表单约束更新草稿中的单个数值字段；输入非法时保持原值，
 * 由 validateAiSettingsDraft 在保存时给出统一文案。
 * @param value - 输入框原始文本。
 * @param fallback - 更新前的字段值。
 * @returns 解析后的整数；无法解析时返回原值。
 */
function parseLimitInput(value: string, fallback: number): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) && value.trim() !== '' ? Math.trunc(parsed) : fallback;
}

/** AI 能力全局配置页：启用开关、LLM 端点、密钥打码回显与各项安全限额。 */
export default function AiSettingsPage() {
  const [draft, setDraft] = useState<AiSettingsDraft | null>(null);
  const [loaded, setLoaded] = useState<AiSettings | null>(null);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  // 阶段一：读取服务端打码配置并初始化表单草稿；失败时展示错误并保持空态。
  useEffect(() => {
    let active = true;
    void aiApi.getSettings()
      .then((document) => {
        if (!active) return;
        setLoaded(document);
        setDraft(createAiSettingsDraft(document));
      })
      .catch((exception) => {
        if (active) setError(exception instanceof Error ? exception.message : 'AI 设置加载失败。');
      });
    return () => { active = false; };
  }, []);

  /**
   * 保存当前草稿：先做前端校验（与后端规则一致），通过后提交；
   * apiKey 为空表示保留服务端已存密钥，保存结果重新打码回显。
   */
  const save = async () => {
    // 阶段二：保存前做前端校验（规则与后端一致），通过后提交并回读打码结果刷新表单。
    if (draft === null || saving) return;
    const validationError = validateAiSettingsDraft(draft);
    if (validationError !== null) {
      setError(validationError);
      return;
    }
    setSaving(true);
    setError(null);
    setStatus(null);
    try {
      const saved = await aiApi.saveSettings(toAiSettingsInput(draft));
      setLoaded(saved);
      setDraft(createAiSettingsDraft(saved));
      setStatus(`AI 设置已保存${saved.isEnabled ? '，AI 能力已启用' : '（AI 能力保持关闭）'}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : 'AI 设置保存失败。');
    } finally {
      setSaving(false);
    }
  };

  /**
   * 对“已保存”的配置做连通性测试；未保存或未启用时给出引导提示，
   * 测试直接透传后端返回的成功/失败原因。
   */
  const testConnection = async () => {
    // 阶段三：连通性测试只针对“已保存且已启用”的配置，结果直接透传后端返回的原因。
    if (testing || loaded === null) return;
    if (loaded.updateTime === null) {
      setError('请先保存 AI 设置，再进行连通性测试。');
      return;
    }
    if (!loaded.isEnabled) {
      setError('AI 能力当前为关闭状态，启用并保存后再测试。');
      return;
    }
    setTesting(true);
    setError(null);
    setStatus(null);
    try {
      const result = await aiApi.testConnection();
      if (result.success) {
        setStatus(result.message);
      } else {
        setError(result.message);
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '连通性测试失败。');
    } finally {
      setTesting(false);
    }
  };

  // 阶段四：未加载完成时渲染空态；读取失败复用错误提示区域。
  if (draft === null || loaded === null) {
    return (
      <main className="asset-page ai-settings-page">
        <header className="asset-header">
          <div className="asset-title-row"><div><span className="app-kicker">AI ASSISTANT</span><h1>AI 设置</h1></div></div>
        </header>
        <div className="asset-empty">
          {error ? error : <><LoaderCircle className="spin" size={20} />正在读取 AI 设置</>}
        </div>
        <StatusToasts error={null} success={null} />
      </main>
    );
  }

  // 阶段五：汇总页头启用状态文案，管理员无需展开表单即可确认开关。
  const enablementText = loaded.isEnabled ? 'AI 已启用' : 'AI 已关闭';
  // 阶段六：视觉模型输入为空时回退主模型，页头文案提示该回退语义，避免管理员误以为必须配置。
  const visionFallbackHint = draft.visionModel.trim() === '' ? '（视觉解析回退主模型）' : '';

  // 阶段六：渲染基础配置与安全限额卡片；密钥输入框占位承载打码回显，原文不进前端状态。
  return (
    <main className="asset-page ai-settings-page">
      <header className="asset-header">
        <div className="asset-title-row">
          <div>
            <span className="app-kicker">AI ASSISTANT</span>
            <h1>AI 设置</h1>
            <p>AI 能力默认关闭；启用后报表列表页才会出现 AI 助手入口，且所有查数动作都有行数与审计约束。</p>
          </div>
        </div>
        <div className="runtime-status">
          <span className={`status-dot ${loaded.isEnabled ? '' : 'disabled'}`} />
          {enablementText}
        </div>
      </header>

      {/* 阶段六：基础配置卡片——启用开关、服务地址与打码回显的密钥输入。 */}
      {/* 阶段七：视觉模型与安全限额卡片——附件参考能力的开关与上限，与后端校验逐条对应。 */}

      <section className="ai-settings-card" aria-label="AI 基础配置">
        <h2><Bot size={17} />基础配置</h2>
        {/* 基础配置表单：密钥输入框恒为空串起步，占位文本承载打码回显，避免原文进入前端状态。 */}
        <div className="asset-form-grid ai-settings-grid">
          <label className="toggle-field">
            <span>启用 AI 能力</span>
            <input
              type="checkbox"
              checked={draft.isEnabled}
              onChange={(event) => setDraft({ ...draft, isEnabled: event.target.checked })}
            />
          </label>
          <label className="wide-field">
            <span>服务地址（OpenAI 兼容 baseUrl）</span>
            <input
              value={draft.baseUrl}
              placeholder="如 https://llm.internal/v1"
              onChange={(event) => setDraft({ ...draft, baseUrl: event.target.value })}
            />
          </label>
          <label className="wide-field">
            <span>ApiKey{loaded.apiKeyConfigured ? `（已保存：${loaded.apiKeyMasked}）` : '（未配置）'}</span>
            <input
              type="password"
              value={draft.apiKey}
              placeholder={aiApiKeyPlaceholder(loaded)}
              autoComplete="new-password"
              onChange={(event) => setDraft({ ...draft, apiKey: event.target.value })}
            />
          </label>
          <label>
            <span>模型名称</span>
            <input
              value={draft.model}
              placeholder="如 deepseek-chat"
              onChange={(event) => setDraft({ ...draft, model: event.target.value })}
            />
          </label>
          <label>
            {/* 阶段六：视觉模型字段——附件图片理解专用；模型名非密钥正常回显，留空回退主模型。 */}
            <span>视觉模型（附件图片理解，可留空）{visionFallbackHint}</span>
            <input
              value={draft.visionModel}
              placeholder="留空 = 回退主模型（主模型具备视觉能力时单模型即可）"
              onChange={(event) => setDraft({ ...draft, visionModel: event.target.value })}
            />
          </label>
        </div>
      </section>

      {/* 阶段七：安全限额卡片——采样/SQL 探测/工具轮数三项上限，与后端校验逐条对应。 */}
      {/* 阶段八：操作区——连通性测试只对已保存且已启用的配置开放，保存按钮提交前先跑前端校验。 */}
      <section className="ai-settings-card" aria-label="AI 安全限额">
        <h2>安全限额</h2>
        <div className="asset-form-grid ai-settings-grid">
          <label>
            <span>采样行数上限（0 禁用采样，≤100）</span>
            <input
              type="number"
              min={0}
              max={100}
              value={draft.sampleRowLimit}
              onChange={(event) => setDraft({ ...draft, sampleRowLimit: parseLimitInput(event.target.value, draft.sampleRowLimit) })}
            />
          </label>
          <label className="toggle-field">
            <span>允许只读 SQL 探测</span>
            <input
              type="checkbox"
              checked={draft.sqlProbeEnabled}
              onChange={(event) => setDraft({ ...draft, sqlProbeEnabled: event.target.checked })}
            />
          </label>
          <label>
            <span>SQL 探测行数上限（≤1000）</span>
            <input
              type="number"
              min={1}
              max={1000}
              value={draft.sqlProbeRowLimit}
              onChange={(event) => setDraft({ ...draft, sqlProbeRowLimit: parseLimitInput(event.target.value, draft.sqlProbeRowLimit) })}
            />
          </label>
          <label>
            <span>单次回复最大工具轮数（≤20）</span>
            <input
              type="number"
              min={1}
              max={20}
              value={draft.maxToolRounds}
              onChange={(event) => setDraft({ ...draft, maxToolRounds: parseLimitInput(event.target.value, draft.maxToolRounds) })}
            />
          </label>
        </div>
        <p className="ai-settings-note">
          采样与探测结果会外发给所配置的 LLM 服务；每次工具调用都会写入 RcAiAuditLog 供事后追溯。
        </p>
      </section>

      {/* 操作区：测试连通性只对已保存且已启用的配置开放，保存按钮提交前会先跑前端校验。 */}
      <footer className="ai-settings-footer">
        <button
          type="button"
          className="button"
          disabled={testing || saving || !loaded.isEnabled}
          title={loaded.isEnabled ? '对已保存配置发起一次最小对话请求' : 'AI 关闭时无法测试'}
          onClick={() => void testConnection()}
        >
          {testing ? <LoaderCircle className="spin" size={16} /> : <Zap size={16} />}测试连通性
        </button>
        <button
          type="button"
          className="button button-primary"
          disabled={saving || testing}
          onClick={() => void save()}
        >
          {saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存设置
        </button>
      </footer>

      <StatusToasts
        error={error}
        success={status}
        onErrorClose={() => setError(null)}
        onSuccessClose={() => setStatus(null)}
      />
    </main>
  );
}
