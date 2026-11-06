import { useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react';
import { Bot, ChevronDown, LoaderCircle, Paperclip, RefreshCw, Send, Upload, X, Zap } from 'lucide-react';
import { aiApi, dataAssetApi, reportPackageApi } from './api';
import {
  aiToolDetail,
  formatAiToolLabel,
  isAiDisabledError,
  isAiRetryableError,
  nextAiChatMessages,
  resolveAiDataSourceOptions,
} from './aiAssistantChat';
import {
  aiAttachmentLabel,
  readAiAttachment,
  toAiChatAttachments,
  validateAiAttachment,
  validateAiAttachmentCount,
  type AiAttachmentDraft,
} from './aiAttachment';
import {
  aiDraftBadgeClass,
  aiDraftStatusLabel,
  canImportDraft,
  collectDraftFields,
  collectDraftParameters,
  formatDraftDryRunResult,
  formatDraftImportSuccess,
  formatDraftValidateResult,
} from './aiDraftCard';
import type {
  AiChatDraft,
  AiChatMessageView,
  AiDataSourceOption,
  ReportPackageDryRunResult,
} from './types';

interface AiAssistantPanelProps {
  onClose: () => void;
}

/**
 * 渲染 AI 侧栏的单条消息：用户/AI 为气泡，工具消息渲染为折叠条（工具名+SQL 摘要+行数，
 * 展开显示 SQL 与截断提示），供消息流统一使用。
 * @param message - 服务端返回的单条展示消息。
 * @param index - 列表下标，用作 React key。
 * @returns 该条消息对应的 React 节点。
 */
function AiMessageItem({ message, index }: { message: AiChatMessageView; index: number }) {
  if (message.role === 'tool') {
    const label = formatAiToolLabel(message.toolName ?? '', message.sqlSummary, message.rowCount);
    return (
      <details className="ai-tool-entry" key={index}>
        <summary>
          <ChevronDown size={14} />
          <span>{label}</span>
          {!message.success && <i className="ai-tool-error-mark">失败</i>}
        </summary>
        <pre>{aiToolDetail(message)}</pre>
      </details>
    );
  }
  return (
    <div className={message.role === 'user' ? 'ai-bubble user' : 'ai-bubble assistant'} key={index}>
      {message.content || '（模型未返回文本）'}
    </div>
  );
}

/** 模板列表页右下角的 AI 助手聊天侧栏：数据源选择器、消息流、工具折叠条与会话管理。 */
export default function AiAssistantPanel({ onClose }: AiAssistantPanelProps) {
  const [sources, setSources] = useState<AiDataSourceOption[]>([]);
  const [selectedSource, setSelectedSource] = useState('');
  const [messages, setMessages] = useState<AiChatMessageView[]>([]);
  const [input, setInput] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // 草稿卡片状态：随每轮 /chat 响应刷新；校验/试跑/导入动作在卡片上由用户触发。
  const [draft, setDraft] = useState<AiChatDraft | null>(null);
  const [draftBusy, setDraftBusy] = useState(false);
  const [draftResult, setDraftResult] = useState<string | null>(null);
  const [dryRunResult, setDryRunResult] = useState<ReportPackageDryRunResult | null>(null);
  const sessionIdRef = useRef<string | null>(null);
  const messagesEndRef = useRef<HTMLDivElement | null>(null);
  // 附件参考（阶段四）：待发送附件条（≤3 个）与隐藏的文件选择器引用；内容不落库，随会话过期。
  const [attachments, setAttachments] = useState<AiAttachmentDraft[]>([]);
  const [attachmentsBusy, setAttachmentsBusy] = useState(false);
  const fileInputRef = useRef<HTMLInputElement | null>(null);

  // 阶段一：加载数据源选项，为侧栏选择器提供“已启用数据源”候选并默认选中第一项。
  useEffect(() => {
    let active = true;
    void dataAssetApi.dataSources()
      .then((items) => {
        if (!active) return;
        const options = resolveAiDataSourceOptions(items);
        setSources(options);
        setSelectedSource((current) => current || options[0]?.code || '');
      })
      .catch((exception) => {
        if (active) setError(exception instanceof Error ? exception.message : '数据源列表加载失败。');
      });
    return () => { active = false; };
  }, []);

  // 阶段二：消息流变化后滚动到底部，保证最新回复与工具折叠条始终可见。
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' });
  }, [messages, sending]);

  /**
   * 发送一条用户消息：服务端在同一请求内完成工具循环，前端只负责按结果刷新消息流。
   * LLM 失败保留会话，输入不清空以便原样重试；会话过期时按新上下文重建消息流。
   * @param event - 触发发送的表单提交事件。
   */
  const send = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    // 阶段三：发送前校验数据源选择；LLM 失败保留会话，输入不清空便于原样重试。
    const message = input.trim();
    if (!message || sending) return;
    if (!selectedSource) {
      setError('请先选择数据源，再开始对话。');
      return;
    }
    setSending(true);
    setError(null);
    setNotice(null);
    try {
      const result = await aiApi.chat({
        sessionId: sessionIdRef.current,
        dataSourceCode: selectedSource,
        message,
        attachments: attachments.length > 0 ? toAiChatAttachments(attachments) : undefined,
      });
      sessionIdRef.current = result.sessionId;
      setMessages((current) => nextAiChatMessages(current, result));
      // 草稿随本轮响应整体刷新（可能为 null 表示尚无草稿），动作结果行同步清零。
      setDraft(result.draft);
      setDraftResult(null);
      setDryRunResult(null);
      setInput('');
      setAttachments([]);
      if (result.expired) {
        setNotice('会话已过期，AI 上下文已清空，本轮已按新会话继续。');
      }
    } catch (exception) {
      if (isAiDisabledError(exception)) {
        setError('AI 能力未启用或配置不完整，请管理员在“AI 设置”页开启后再试。');
      } else if (isAiRetryableError(exception)) {
        setError(`${exception instanceof Error ? exception.message : '对话失败'}；会话已保留，可直接重试。`);
      } else {
        setError(exception instanceof Error ? exception.message : '对话失败。');
      }
    } finally {
      setSending(false);
    }
  };

  /**
   * 结束当前会话：清空服务端内存上下文与本地消息流；未建立会话时仅清空本地展示。
   */
  const endSession = async () => {
    // 阶段四：结束会话——先清本地展示，再通知服务端释放内存上下文，保证界面即时反馈。
    if (sending) return;
    const sessionId = sessionIdRef.current;
    sessionIdRef.current = null;
    setMessages([]);
    setNotice(null);
    setError(null);
    setDraft(null);
    setDraftResult(null);
    setDryRunResult(null);
    setAttachments([]);
    if (sessionId) {
      try {
        await aiApi.closeSession(sessionId);
        setNotice('会话已结束，服务端上下文已清空。');
      } catch (exception) {
        setError(exception instanceof Error ? exception.message : '会话关闭失败。');
      }
    }
  };

  /**
   * 处理附件选择（阶段四）：逐个校验类型/大小与数量上限，读取 base64 后进入附件条；
   * 任一校验失败提示并整体放弃，避免半截附件状态。
   * @param event - 文件选择事件。
   */
  const handleAttachmentSelection = async (event: ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(event.target.files ?? []);
    event.target.value = '';
    if (files.length === 0 || attachmentsBusy) return;
    const countError = validateAiAttachmentCount(attachments.length, files.length);
    if (countError !== null) {
      setError(countError);
      return;
    }
    for (const file of files) {
      const validationError = validateAiAttachment(file);
      if (validationError !== null) {
        setError(validationError);
        return;
      }
    }
    setAttachmentsBusy(true);
    setError(null);
    try {
      const drafts = await Promise.all(files.map((file) => readAiAttachment(file)));
      setAttachments((current) => [...current, ...drafts]);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '附件读取失败。');
    } finally {
      setAttachmentsBusy(false);
    }
  };

  /**
   * 移除附件条中的一条附件；发送前用户仍可撤回。
   * @param index - 待移除的附件下标。
   */
  const removeAttachment = (index: number) => {
    setAttachments((current) => current.filter((_, position) => position !== index));
  };

  // 阶段五：渲染侧栏结构——外发提示、数据源选择、消息流（含工具折叠条）与输入区。
  // 阶段六：草稿卡片动作——校验/试跑/导入全部复用现有 reportPackageApi，
  // 导入权限、信封解包与 Replace 语义完全沿用导入向导链路，不经任何 /ai 接口。

  /**
   * 对草稿包发起服务端校验：结果原样展示，错误明细供用户转告 AI 或自行修正。
   */
  const runDraftValidate = async () => {
    if (draft === null || draft.package === null || draftBusy) return;
    setDraftBusy(true);
    setDraftResult(null);
    try {
      const validation = await reportPackageApi.validateReportPackage(draft.package);
      setDraft({ ...draft, validateResult: validation });
      setDraftResult(formatDraftValidateResult(validation));
    } catch (exception) {
      setDraftResult(exception instanceof Error ? exception.message : '校验失败。');
    } finally {
      setDraftBusy(false);
    }
  };

  /**
   * 用当前选中数据源试跑草稿包：复用后端 50 行/30 秒限额，结果只展示不落库。
   */
  const runDraftDryRun = async () => {
    if (draft === null || draft.package === null || draftBusy || !selectedSource) return;
    setDraftBusy(true);
    setDraftResult(null);
    try {
      const dryRun = await reportPackageApi.dryRunReportPackage({
        package: draft.package,
        targetDataSourceCode: selectedSource,
        parameterValues: {},
      });
      setDryRunResult(dryRun);
      setDraftResult(formatDraftDryRunResult(dryRun));
    } catch (exception) {
      setDraftResult(exception instanceof Error ? exception.message : '试跑失败。');
    } finally {
      setDraftBusy(false);
    }
  };

  /**
   * 确认后导入草稿包：Create 模式 + 已试跑标记；成功后提示刷新模板列表。
   */
  const runDraftImport = async () => {
    if (draft === null || draft.package === null || draftBusy || !canImportDraft(draft.status)) return;
    if (!window.confirm(`确认把报表「${draft.package.report.name}」导入当前报表中心？`)) return;
    setDraftBusy(true);
    setDraftResult(null);
    try {
      const imported = await reportPackageApi.importReportPackage({
        package: draft.package,
        targetDataSourceCode: selectedSource,
        mode: 'Create',
        replaceDataset: false,
        dryRunPerformed: true,
      });
      setNotice(formatDraftImportSuccess(imported.reportCode, imported.datasetAction));
    } catch (exception) {
      setDraftResult(exception instanceof Error ? exception.message : '导入失败。');
    } finally {
      setDraftBusy(false);
    }
  };

  return (
    <aside className="ai-assistant-panel" aria-label="AI 助手">
      <header className="ai-panel-header">
        <div className="ai-panel-title">
          <Bot size={18} />
          <strong>AI 助手</strong>
        </div>
        <div className="ai-panel-actions">
          <button
            type="button"
            className="icon-button"
            title="结束会话并清空上下文"
            aria-label="结束 AI 会话"
            disabled={sending}
            onClick={() => void endSession()}
          >
            <RefreshCw size={16} />
          </button>
          <button type="button" className="icon-button" title="关闭" aria-label="关闭 AI 助手" onClick={onClose}>
            <X size={18} />
          </button>
        </div>
      </header>

      <div className="ai-panel-warning" role="note">
        对话内容与采样数据将发送至所配置的 LLM 服务，请勿在提问中粘贴敏感信息。
      </div>

      <label className="ai-panel-source">
        <span>数据源</span>
        <select
          value={selectedSource}
          disabled={sending || sources.length === 0}
          onChange={(event) => {
            setSelectedSource(event.target.value);
            // 切换数据源视为重新发起对话：清空本地消息与服务端会话，让上下文按新数据源重建。
            const previousSession = sessionIdRef.current;
            sessionIdRef.current = null;
            setMessages([]);
            setNotice(null);
            setDraft(null);
            setDraftResult(null);
            setDryRunResult(null);
            if (previousSession) void aiApi.closeSession(previousSession).catch(() => undefined);
          }}
        >
          {sources.length === 0
            ? <option value="">暂无可用数据源</option>
            : sources.map((source) => (
              <option key={source.code} value={source.code}>
                {source.name}（{source.code} · {source.dbType}）
              </option>
            ))}
        </select>
      </label>

      {notice && <div className="ai-panel-notice">{notice}</div>}
      {error && <div className="ai-panel-error">{error}</div>}

      <div className="ai-panel-messages" aria-live="polite">
        {messages.length === 0 && (
          <div className="ai-panel-empty">
            <Bot size={16} />
            <span>选择数据源后，用自然语言描述你想了解的数据（例如“患者费用表里费用类型有哪些”）。</span>
          </div>
        )}
        {messages.map((message, index) => (
          <AiMessageItem key={`${index}-${message.toolCallId ?? message.role}`} message={message} index={index} />
        ))}
        {sending && (
          <div className="ai-bubble assistant pending">
            <LoaderCircle className="spin" size={16} />
            正在思考（可能包含多轮查数）……
          </div>
        )}
        <div ref={messagesEndRef} />
      </div>

      {/* 阶段七：草稿卡片——列清单/参数清单/状态徽标 + 校验/试跑/导入按钮。 */}
      {draft && (
        <section className="ai-draft-card" aria-label="报表草稿">
          <header className="ai-draft-header">
            <strong>报表草稿{draft.package ? `：${draft.package.report.name}` : ''}</strong>
            <span className={aiDraftBadgeClass(draft.status)}>{aiDraftStatusLabel(draft.status)}</span>
          </header>
          {draft.package === null ? (
            <p className="ai-draft-empty">spec 尚未编译通过，AI 正在按错误清单修正。</p>
          ) : (
            <>
              <div className="ai-draft-section">
                <strong>列清单（{collectDraftFields(draft).length}）</strong>
                <div className="ai-draft-list">
                  {collectDraftFields(draft).map((field) => (
                    <span key={field.code} className="ai-draft-chip">
                      {field.displayName}<small>{field.code} · {field.dataType}</small>
                    </span>
                  ))}
                </div>
              </div>
              <div className="ai-draft-section">
                <strong>参数清单（{collectDraftParameters(draft).length}）</strong>
                <div className="ai-draft-list">
                  {collectDraftParameters(draft).map((parameter) => (
                    <span key={parameter.code} className="ai-draft-chip">
                      {parameter.displayName}{parameter.required ? '（必填）' : ''}
                      <small>{parameter.code} · {parameter.dataType}</small>
                    </span>
                  ))}
                </div>
              </div>
              {draftResult && <div className="ai-draft-result">{draftResult}</div>}
              {dryRunResult && (
                <div className="ai-draft-result muted">
                  列：{dryRunResult.columns.join('、')}
                </div>
              )}
              <footer className="ai-draft-actions">
                <button type="button" className="button" disabled={draftBusy || sending} onClick={() => void runDraftValidate()}>
                  {draftBusy ? <LoaderCircle className="spin" size={14} /> : <Zap size={14} />}校验
                </button>
                <button type="button" className="button" disabled={draftBusy || sending || !selectedSource} onClick={() => void runDraftDryRun()}>
                  试跑
                </button>
                <button
                  type="button"
                  className="button button-primary"
                  disabled={draftBusy || sending || !canImportDraft(draft.status)}
                  title={canImportDraft(draft.status) ? '导入报表与数据集（Create 模式）' : '校验通过后才能导入'}
                  onClick={() => void runDraftImport()}
                >
                  <Upload size={14} />导入
                </button>
              </footer>
            </>
          )}
        </section>
      )}
      {/* 阶段四：附件条——展示待发送附件（文件名+大小）并可逐条撤回；发送失败时保留以便重试。 */}
      {attachments.length > 0 && (
        <div className="ai-panel-attachments" aria-label="待发送附件">
          {attachments.map((attachment, index) => (
            <span className="ai-attachment-chip" key={`${attachment.fileName}-${index}`}>
              <Paperclip size={12} />
              <span>{aiAttachmentLabel(attachment)}</span>
              <button
                type="button"
                aria-label={`移除附件 ${attachment.fileName}`}
                disabled={sending}
                onClick={() => removeAttachment(index)}
              >
                <X size={12} />
              </button>
            </span>
          ))}
        </div>
      )}
      <form className="ai-panel-composer" onSubmit={(event) => { void send(event); }}>
        {/* 阶段四：附件选择——base64 随消息内联提交，不新增上传端点；读取中禁用发送。 */}
        <input
          ref={fileInputRef}
          type="file"
          multiple
          accept=".png,.jpg,.jpeg,.webp,.xlsx,.pdf"
          aria-label="选择参考材料附件"
          className="ai-attachment-input"
          onChange={(event) => { void handleAttachmentSelection(event); }}
        />
        <button
          type="button"
          className="icon-button"
          title="添加参考材料附件（图片/Excel/PDF，最多 3 个）"
          aria-label="添加附件"
          disabled={sending || attachmentsBusy}
          onClick={() => fileInputRef.current?.click()}
        >
          {attachmentsBusy ? <LoaderCircle className="spin" size={16} /> : <Paperclip size={16} />}
        </button>
        <textarea
          value={input}
          placeholder="向 AI 助手提问…（Enter 发送）"
          rows={2}
          disabled={sending}
          onChange={(event) => setInput(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey) {
              event.preventDefault();
              void send(event as unknown as FormEvent<HTMLFormElement>);
            }
          }}
        />
        <button
          type="submit"
          className="button button-primary"
          disabled={sending || !input.trim()}
          aria-label="发送消息"
        >
          {sending ? <LoaderCircle className="spin" size={16} /> : <Send size={16} />}
        </button>
      </form>
    </aside>
  );
}
