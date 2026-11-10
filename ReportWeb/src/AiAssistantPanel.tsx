import { memo, useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent, type ComponentProps, type ComponentRef, type KeyboardEvent } from 'react';
import { Button, ConfigProvider, Input, Popover, Select } from 'antd';
import { Bubble, Sender, ThoughtChain } from '@ant-design/x';
import type { ThoughtChainItemType } from '@ant-design/x';
import {
  ArrowDown,
  Bot,
  Check,
  CircleCheck,
  CircleX,
  Database,
  LoaderCircle,
  Paperclip,
  RefreshCw,
  Search,
  Upload,
  X,
  Zap,
} from 'lucide-react';
import { aiApi, dataAssetApi, reportPackageApi } from './api';
import {
  buildAiBubbleItems,
  deriveAiThinkingStatus,
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
  type AiThoughtChainNode,
} from './aiAssistantChat';
import type { AiThinkingStatus } from './aiAssistantChat';
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
import { aiDataSourceTableSubtitle, filterAiDataSourceTables, sliceAiTableRenderList } from './aiDataSourceTables';
import type {
  AiChatDraft,
  AiChatSeqMessageView,
  AiChatSeqResult,
  AiDataSourceOption,
  AiDataSourceTable,
  ReportPackageDryRunResult,
} from './types';
import './AiAssistantPanel.css';

/** progress 轮询周期（毫秒）：POST /chat 进行中每 2 秒拉一次循环状态与增量消息。 */
const AiProgressPollIntervalMs = 2000;

/** 思考条耗时与停滞检测的刷新周期（毫秒）：本地 1 秒一跳，驱动“已思考 Ns”计数与 45 秒停滞判定。 */
const AiThinkingTickIntervalMs = 1000;

/** 距底部小于该像素视为“贴着底部”，自动滚动保持开启；上滚超过它即暂停自动滚。 */
const AiScrollPinnedGapPx = 80;

/** 距底部超过该像素才显示“回到底部”按钮，避免轻微滚动就出现按钮打扰阅读。 */
const AiBackToBottomGapPx = 160;

/** 面板局部主题 token：主色 #2563eb 与页面主按钮同源；圆角 13px 介于既有 12px 卡片与 14px 气泡之间。 */
const AiPanelTheme = {
  token: {
    colorPrimary: '#2563eb',
    colorInfo: '#2563eb',
    borderRadius: 13,
    colorTextBase: '#0f172a',
    colorTextSecondary: '#64748b',
  },
} as const;

/** 工具轨迹节点图标：成功绿勾 / 失败红叉，配合内置 status 语义色。 */
const AiThoughtNodeIcons = {
  success: <CircleCheck size={15} aria-hidden="true" />,
  error: <CircleX size={15} aria-hidden="true" />,
} as const;

/** 用户气泡承载的 extraInfo（Bubble 不下传 DOM，仅用于 footer 角标判定）。 */
interface AiUserBubbleExtra {
  guidanceBadge: boolean;
}

/**
 * 渲染运行中的动态思考条：三点跳动 + deriveAiThinkingStatus 推导的“正在调用 xxx（第 N 轮）· 已思考 Ns”。
 * 作为 Bubble.List 的 thinking 角色内容渲染，随消息流一起滚动。
 * @param props.status - 由 progress 消息与本地秒表推导出的思考状态。
 * @returns 思考条 React 节点。
 */
function AiThinkingBubble({ status }: { status: AiThinkingStatus }) {
  return (
    <div className="ai-thinking-bar" role="status">
      <span className="ai-thinking-dots" aria-hidden="true">
        <i />
        <i />
        <i />
      </span>
      <span className="ai-thinking-text">
        <strong>{status.headline}</strong>
        <small>{status.elapsedLabel} · 多轮查数会逐条展示</small>
      </span>
    </div>
  );
}

/**
 * 渲染工具轨迹 ThoughtChain（memo 化）：把本轮工具消息映射为链式节点（中文标签 + 工具名 +
 * 行数徽标 + SQL 摘要描述），失败节点红色态；节点默认折叠，点击展开 SQL/错误详情全文。
 * 节点数据数组在轮询合并时保持引用稳定，memo 让同一批节点在秒表刷新时不重复重建。
 * @param props.nodes - 工具消息映射出的节点数据（buildAiThoughtChainItems 产物）。
 * @returns ThoughtChain React 节点；无节点时返回 null。
 */
const AiToolThoughtChain = memo(function AiToolThoughtChain({ nodes }: { nodes: AiThoughtChainNode[] }) {
  const items = useMemo<ThoughtChainItemType[]>(() => nodes.map((node) => ({
    key: node.key,
    icon: node.success ? AiThoughtNodeIcons.success : AiThoughtNodeIcons.error,
    title: (
      <span className={node.success ? 'ai-thought-title' : 'ai-thought-title failed'}>
        {node.label}
        <small>{node.toolName}</small>
        {node.rowCount !== null && node.rowCount !== undefined && (
          <span className="ai-thought-rows">{node.rowCount} 行</span>
        )}
      </span>
    ),
    description: node.sqlSummary
      ? <code className="ai-thought-sql" title={node.sqlSummary}>{node.sqlSummary}</code>
      : (node.success ? '工具执行完成' : '执行失败，展开查看错误详情'),
    content: <pre className="ai-thought-detail">{node.detail}</pre>,
    status: node.status,
    collapsible: true,
  })), [nodes]);
  if (nodes.length === 0) {
    return null;
  }
  return <ThoughtChain className="ai-thought-chain" items={items} />;
});

/**
 * Bubble.List 角色配置（模块级常量保证引用稳定）：用户右侧主色气泡（插话角标挂 outer footer）、
 * AI 左侧带头像浅色气泡、toolTrace/thinking/failure 为消息流内的业务节点渲染。
 * 角色级样式对象全部为静态引用，配合 Bubble 内部 memo，秒表刷新不会重渲染历史气泡。
 */
const AiBubbleRoles: NonNullable<ComponentProps<typeof Bubble.List>['role']> = {
  user: {
    placement: 'end',
    variant: 'filled',
    styles: {
      content: {
        maxWidth: '86%',
        background: 'linear-gradient(135deg, #2563eb, #4f46e5)',
        color: '#fff',
        whiteSpace: 'pre-wrap',
        wordBreak: 'break-word',
        fontSize: 13,
        lineHeight: 1.65,
        boxShadow: '0 4px 12px rgba(37, 99, 235, 0.22)',
      },
    },
    /**
     * 渲染用户气泡 outer footer：仅插话消息（extraInfo.guidanceBadge）显示
     * “插话 · 下一轮生效”角标，明示该消息要等下一轮才进入模型上下文。
     * @param _content - 气泡文本（本角标不需要）。
     * @param info - 气泡上下文信息，携带 items 里写入的 extraInfo。
     * @returns 角标节点或 null。
     */
    footer: (_content, info) => ((info.extraInfo ?? null) as AiUserBubbleExtra | null)?.guidanceBadge
      ? <span className="ai-guidance-badge">插话 · 下一轮生效</span>
      : null,
  },
  ai: {
    placement: 'start',
    avatar: <span className="ai-msg-avatar" aria-hidden="true"><Bot size={13} /></span>,
    variant: 'outlined',
    styles: {
      content: {
        maxWidth: '92%',
        whiteSpace: 'pre-wrap',
        wordBreak: 'break-word',
        fontSize: 13,
        lineHeight: 1.65,
      },
    },
  },
  toolTrace: {
    /**
     * 把 toolTrace 项的内容（节点数据包装对象）渲染为 ThoughtChain 工具轨迹。
     * @param content - items 里写入的 { nodes } 包装对象。
     * @returns ThoughtChain 节点。
     */
    contentRender: (content) => (
      <AiToolThoughtChain nodes={(content as { nodes?: AiThoughtChainNode[] }).nodes ?? []} />
    ),
  },
  thinking: {
    /**
     * 把 thinking 项的内容（思考状态对象）渲染为动态思考条（三点 + 轮次/耗时文案）。
     * @param content - items 里写入的思考状态。
     * @returns 思考条节点。
     */
    contentRender: (content) => <AiThinkingBubble status={content as unknown as AiThinkingStatus} />,
  },
  failure: {
    /**
     * 渲染本轮失败提示气泡：固定文案——会话已保留可直接重试，失败详情在通知条区展示。
     * @returns 失败气泡节点。
     */
    contentRender: () => (
      <div className="ai-bubble failed">本轮处理失败，会话已保留；可直接重试，或换一种问法。</div>
    ),
  },
};

/** Bubble.List 实例类型：通过它拿到真正的滚动容器（scrollBoxNativeElement）供贴底判定复用。 */
type AiBubbleListInstance = ComponentRef<typeof Bubble.List>;

/** 模板列表页右侧的 AI 助手全高抽屉（@ant-design/x 外壳）：品牌头 + 数据源工具行、
 * Bubble.List 消息流（ThoughtChain 工具轨迹 / 草稿卡 / 思考条 / 插话角标）、底部 Sender（运行中
 * 不禁用，Enter 自动分流 guidance 插话，内置停止按钮 abort 当前 fetch）。 */
export default function AiAssistantPanel({ onClose }: { onClose: () => void }) {
  const [sources, setSources] = useState<AiDataSourceOption[]>([]);
  const [selectedSource, setSelectedSource] = useState('');
  const [messages, setMessages] = useState<AiChatSeqMessageView[]>([]);
  const [input, setInput] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // 本轮工具循环失败态——POST /chat 抛错或轮询到 failed 时置位，渲染失败气泡。
  const [turnFailed, setTurnFailed] = useState(false);
  // 草稿卡片状态：随每轮 /chat 响应刷新；校验/试跑/导入动作在卡片上由用户触发。
  const [draft, setDraft] = useState<AiChatDraft | null>(null);
  const [draftBusy, setDraftBusy] = useState(false);
  const [draftResult, setDraftResult] = useState<string | null>(null);
  const [dryRunResult, setDryRunResult] = useState<ReportPackageDryRunResult | null>(null);
  const sessionIdRef = useRef<string | null>(null);
  // 进度轮询定时器与消息快照（interval 回调里读最新消息算 afterSeq，避免闭包过期）。
  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const messagesRef = useRef<AiChatSeqMessageView[]>([]);
  // 预创建防重入标记：面板打开/换源触发一次建号，避免 effect 与发送兜底并发建出多个空会话。
  const preCreateInFlightRef = useRef(false);
  // 动态思考条：本轮发送时刻与最近一次 progress 响应时刻，驱动“已思考 Ns”与 45 秒停滞判定。
  const turnStartedAtRef = useRef<number | null>(null);
  const lastProgressAtRef = useRef<number | null>(null);
  const [thinkingSeconds, setThinkingSeconds] = useState(0);
  const [turnStalled, setTurnStalled] = useState(false);
  const [stalledDismissed, setStalledDismissed] = useState(false);
  // 附件参考：待发送附件条（≤3 个）与隐藏的文件选择器引用；内容不落库，随会话过期。
  const [attachments, setAttachments] = useState<AiAttachmentDraft[]>([]);
  const [attachmentsBusy, setAttachmentsBusy] = useState(false);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  // 当前数据源的表/视图清单（“AI 眼中的数据源”透明化）：头部图标按钮 + Popover 浮层展示。
  const [tables, setTables] = useState<AiDataSourceTable[]>([]);
  const [tablesLoading, setTablesLoading] = useState(false);
  const [tablesError, setTablesError] = useState<string | null>(null);
  const [tablesOpen, setTablesOpen] = useState(false);
  const [tableFilter, setTableFilter] = useState('');
  // 滚动行为：贴底自动滚，用户上滚即暂停；消息区外包一层相对定位容器承载“回到底部”按钮。
  const messagesScrollRef = useRef<HTMLDivElement | null>(null);
  const autoScrollRef = useRef(true);
  const [showBackToBottom, setShowBackToBottom] = useState(false);
  // 插话模式：已确认的插话 Seq 集合（服务端校正后仍保留角标）与在途本地插话文本。
  const [guidanceSeqs, setGuidanceSeqs] = useState<number[]>([]);
  const [pendingGuidance, setPendingGuidance] = useState<string | null>(null);
  // 当前在途 POST /chat 的 AbortController：Sender 停止按钮触发 abort，后端随断开中止循环。
  const abortRef = useRef<AbortController | null>(null);

  // 轮询与滚动都依赖最新消息列表；每次渲染同步快照供定时器回调读取。
  useEffect(() => {
    messagesRef.current = messages;
  }, [messages]);

  /**
   * 停止进度轮询：POST 返回、发送失败、结束会话或组件卸载时调用，保证不留悬挂定时器。
   */
  const stopProgressPolling = () => {
    if (pollTimerRef.current !== null) {
      clearInterval(pollTimerRef.current);
      pollTimerRef.current = null;
    }
  };

  /**
   * 启动进度轮询：每 2 秒拉一次 progress，按 Seq 合并增量消息并感知 failed 状态；
   * 每次成功响应刷新 lastProgressAt 基准（喂给 45 秒停滞判定），轮询失败静默忽略，
   * 以 POST /chat 的最终响应校正消息流。
   */
  const startProgressPolling = () => {
    stopProgressPolling();
    pollTimerRef.current = setInterval(() => {
      const sessionId = sessionIdRef.current;
      if (!sessionId) return;
      void aiApi.chatProgress(sessionId, latestAiSeq(messagesRef.current))
        .then((progress) => {
          lastProgressAtRef.current = Date.now();
          if (isAiLoopFailed(progress.loopState)) {
            setTurnFailed(true);
          }
          setMessages((current) => mergeAiProgressMessages(current, progress));
        })
        .catch(() => undefined);
    }, AiProgressPollIntervalMs);
  };

  // 组件卸载（面板关闭）时清理轮询定时器与在途请求，避免对已消失面板的悬挂请求。
  useEffect(() => () => {
    stopProgressPolling();
    abortRef.current?.abort();
  }, []);

  /**
   * 发送中每秒一跳的本地计时：重置本轮计时基准、推导“已思考 Ns”，并按 45 秒阈值判定停滞。
   * 定时器只在 sending 期间存活，卸载/结束时自动清理，不产生空转开销。
   */
  useEffect(() => {
    if (!sending) return;
    const startedAt = Date.now();
    turnStartedAtRef.current = startedAt;
    lastProgressAtRef.current = startedAt;
    setThinkingSeconds(0);
    setTurnStalled(false);
    setStalledDismissed(false);
    const ticker = setInterval(() => {
      const now = Date.now();
      setThinkingSeconds(Math.floor((now - startedAt) / 1000));
      setTurnStalled(isAiTurnStalled(startedAt, now, lastProgressAtRef.current));
    }, AiThinkingTickIntervalMs);
    return () => clearInterval(ticker);
  }, [sending]);

  // 阶段一：加载数据源选项，为头部选择器提供“已启用数据源”候选并默认选中第一项。
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

  // 预创建空会话：面板打开且选定数据源后立即建号，让首轮对话也能从发消息瞬间开始按 Seq
  // 轮询 progress。失败保持静默——发送链路有现场补建兜底；若返回前用户已发送，释放多余会话防泄漏。
  useEffect(() => {
    if (!shouldPreCreateAiSession(sessionIdRef.current, selectedSource, sending)) return;
    if (preCreateInFlightRef.current) return;
    let cancelled = false;
    preCreateInFlightRef.current = true;
    void aiApi.createSession({ dataSourceCode: selectedSource })
      .then((document) => {
        if (cancelled) {
          void aiApi.closeSession(document.sessionId).catch(() => undefined);
          return;
        }
        if (sessionIdRef.current !== null) {
          // 竞态兜底：发送已先行建号，释放本次多余预创建的空会话。
          void aiApi.closeSession(document.sessionId).catch(() => undefined);
          return;
        }
        sessionIdRef.current = document.sessionId;
      })
      .catch(() => undefined)
      .finally(() => {
        preCreateInFlightRef.current = false;
      });
    return () => { cancelled = true; };
  }, [selectedSource, sending]);

  // 消息流贴底自动滚：新消息/草稿/思考条变化时平滑滚到底部；用户上滚后由滚动回调暂停。
  useEffect(() => {
    if (!autoScrollRef.current) return;
    const container = messagesScrollRef.current;
    if (!container) return;
    container.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
  }, [messages, sending, thinkingSeconds, draft, draftResult, dryRunResult]);

  /**
   * 消息区滚动回调：按“距底部距离”判定是否贴底，贴底时恢复自动滚、上滚时暂停并显示回底按钮。
   * 直接读取滚动容器几何信息，不做节流——scroll 事件频率本身可控，计算为 O(1)。
   */
  const handleMessagesScroll = useCallback(() => {
    const container = messagesScrollRef.current;
    if (!container) return;
    const distance = container.scrollHeight - container.scrollTop - container.clientHeight;
    const pinned = distance < AiScrollPinnedGapPx;
    autoScrollRef.current = pinned;
    setShowBackToBottom(!pinned && distance > AiBackToBottomGapPx);
  }, []);

  /**
   * Bubble.List ref 回调：从组件实例中取出真实滚动容器（scrollBoxNativeElement），
   * 让既有贴底判定/回底按钮继续工作在 antd-x 的滚动结构上。
   * @param instance - Bubble.List 暴露的实例；卸载时为 null。
   */
  const handleBubbleListRef = useCallback((instance: AiBubbleListInstance | null) => {
    messagesScrollRef.current = instance?.scrollBoxNativeElement ?? null;
  }, []);

  /**
   * “回到底部”按钮：恢复贴底状态并平滑滚到底，按钮随之隐藏。
   */
  const jumpToLatest = useCallback(() => {
    const container = messagesScrollRef.current;
    autoScrollRef.current = true;
    setShowBackToBottom(false);
    container?.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
  }, []);

  // 数据源选中后拉取表/视图清单，让用户看到“AI 眼中的数据源”；切换数据源时清空旧清单与过滤词。
  useEffect(() => {
    if (!selectedSource) {
      setTables([]);
      setTablesError(null);
      setTableFilter('');
      return;
    }
    let active = true;
    setTablesLoading(true);
    setTablesError(null);
    setTableFilter('');
    void aiApi.dataSourceTables(selectedSource)
      .then((items) => {
        if (active) setTables(items);
      })
      .catch((exception) => {
        if (active) setTables([]);
        if (active) setTablesError(exception instanceof Error ? exception.message : '表清单加载失败。');
      })
      .finally(() => {
        if (active) setTablesLoading(false);
      });
    return () => { active = false; };
  }, [selectedSource]);

  // 动态思考条状态：由本轮已渲染消息推导工具/轮次文案，耗时每秒刷新（纯函数可测）。
  const thinkingStatus = useMemo(
    () => deriveAiThinkingStatus(messages, thinkingSeconds),
    [messages, thinkingSeconds],
  );
  // 45 秒慢响应提示条：只在发送中、已停滞且未被用户手动关掉时展示（只提示不强杀）。
  const showSlowBanner = sending && turnStalled && !stalledDismissed;

  /**
   * 清空本地会话状态：结束会话与切换数据源共用，保证界面与服务端上下文一致归零，
   * 下次发送重新走预创建/现场补建链路；插话角标集合一并清空。
   */
  const resetLocalSessionState = () => {
    sessionIdRef.current = null;
    stopProgressPolling();
    setMessages([]);
    setNotice(null);
    setError(null);
    setTurnFailed(false);
    setDraft(null);
    setDraftResult(null);
    setDryRunResult(null);
    setAttachments([]);
    setTurnStalled(false);
    setStalledDismissed(false);
    setThinkingSeconds(0);
    setGuidanceSeqs([]);
    setPendingGuidance(null);
    autoScrollRef.current = true;
    setShowBackToBottom(false);
  };

  /**
   * 结束当前会话：清空服务端内存上下文与本地状态；未建立会话时仅清空本地展示。
   */
  const endSession = async () => {
    // 结束会话——先清本地展示，再通知服务端释放内存上下文，保证界面即时反馈。
    if (sending) return;
    const sessionId = sessionIdRef.current;
    resetLocalSessionState();
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
   * 发送一条正常对话消息（POST /chat 链路）：本地先插乐观用户消息 → 无会话号时现场补建
   * （正常情况面板打开时已预创建）→ 立即 POST 并并行每 2 秒轮询 progress 增量渲染 ThoughtChain
   * → 返回后停轮询并按 Seq 校正消息流、渲染草稿卡。携带 AbortController 供停止按钮中断。
   * @param message - 已去空白并校验非空的用户输入。
   */
  const sendChatTurn = async (message: string) => {
    if (!selectedSource) {
      setError('请先选择数据源，再开始对话。');
      return;
    }
    setError(null);
    setNotice(null);
    setTurnFailed(false);
    // 记录本轮提交的输入与附件引用：放弃等待后原 POST 迟到返回时，只在内容未被改动的前提下清理。
    const sentAttachments = attachments;
    const controller = new AbortController();
    abortRef.current = controller;
    setMessages((current) => [...current, {
      role: 'user' as const,
      content: message,
      toolName: null,
      toolCallId: null,
      sqlSummary: null,
      rowCount: null,
      success: true,
      truncated: false,
      seq: null,
    }]);
    setSending(true);
    // 会话号兜底：预创建尚未完成/失败时现场补建；补建失败不阻断发送，该轮退化为不可轮询。
    if (sessionIdRef.current === null) {
      try {
        const preCreated = await aiApi.createSession({ dataSourceCode: selectedSource });
        if (sessionIdRef.current === null) {
          sessionIdRef.current = preCreated.sessionId;
        }
      } catch {
        // 静默降级：发送主链路会以最终响应或错误提示收口。
      }
    }
    if (sessionIdRef.current !== null) {
      startProgressPolling();
    }
    try {
      const result = await aiApi.chat({
        sessionId: sessionIdRef.current,
        dataSourceCode: selectedSource,
        message,
        attachments: attachments.length > 0 ? toAiChatAttachments(attachments) : undefined,
      }, { signal: controller.signal }) as AiChatSeqResult;
      // POST 返回后停轮询，按 Seq 校正消息流（去乐观消息与轮询重复），刷新草稿与输入。
      stopProgressPolling();
      sessionIdRef.current = result.sessionId;
      setMessages((current) => reconcileAiTurnMessages(current, result));
      setDraft(result.draft);
      setDraftResult(null);
      setDryRunResult(null);
      setInput((current) => (current === message ? '' : current));
      setAttachments((current) => (current === sentAttachments ? [] : current));
      if (result.expired) {
        setNotice('会话已过期，AI 上下文已清空，本轮已按新会话继续。');
      }
    } catch (exception) {
      // 失败处理——主动停止走 abort 复位（不置失败态）；LLM 错误保留会话可重试。
      stopProgressPolling();
      if (controller.signal.aborted) {
        // 主动停止：仅当仍是本轮控制器时刷新复位提示；新一轮已启动则静默吞掉旧请求的错误。
        if (abortRef.current === controller) {
          setNotice(resetAiTurnStateAfterAbort().notice);
        }
        return;
      }
      setTurnFailed(true);
      if (isAiDisabledError(exception)) {
        setError('AI 能力未启用或配置不完整，请管理员在“AI 设置”页开启后再试。');
      } else if (isAiRetryableError(exception)) {
        setError(`${exception instanceof Error ? exception.message : '对话失败'}；会话已保留，可直接重试。`);
      } else {
        setError(exception instanceof Error ? exception.message : '对话失败。');
      }
    } finally {
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
      setSending(false);
    }
  };

  /**
   * 发送插话纠正（running 中方向修正）：乐观上屏带“插话”角标 → POST guidance 端点 →
   * 用返回的真实 Seq 记入角标集合；运行中的 progress 轮询会以服务端消息替换乐观气泡，
   * 轮询已停（极端竞态）时现场去重并入，保证角标气泡始终只出现一次。
   * @param message - 已去空白并校验非空的插话文本。
   */
  const sendGuidance = async (message: string) => {
    const sessionId = sessionIdRef.current;
    if (!sessionId) return;
    setError(null);
    setNotice(null);
    setPendingGuidance(message);
    setMessages((current) => [...current, {
      role: 'user' as const,
      content: message,
      toolName: null,
      toolCallId: null,
      sqlSummary: null,
      rowCount: null,
      success: true,
      truncated: false,
      seq: null,
    }]);
    try {
      const reply = await aiApi.appendGuidance(sessionId, { message }) as AiChatSeqMessageView;
      setGuidanceSeqs((current) => markAiGuidanceSeq(current, reply));
      setInput((current) => (current === message ? '' : current));
      setMessages((current) => {
        // 去掉同文的乐观气泡；服务端消息（含 Seq）若尚未由轮询并入则现场补上。
        const withoutOptimistic = current.filter(
          (item) => !(item.seq === null && item.role === 'user' && item.content === message),
        );
        const alreadyMerged = reply.seq !== null
          && current.some((item) => item.seq !== null && item.seq === reply.seq);
        return alreadyMerged ? withoutOptimistic : [...withoutOptimistic, reply];
      });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '插话发送失败。');
    } finally {
      setPendingGuidance(null);
    }
  };

  /**
   * 发送统一入口：running 中且已有会话号 → guidance 插话端点；空闲 → 正常 /chat 轮次；
   * running 中但会话号缺失（预创建在途的极端窗口）→ 提示稍后再发，不产生孤儿请求。
   * @param message - Sender 提交的原始文本。
   */
  const submitTurn = (message: string) => {
    const text = message.trim();
    if (!text) return;
    if (shouldSendAsGuidance(sending, sessionIdRef.current)) {
      void sendGuidance(text);
      return;
    }
    if (sending) {
      setError('会话初始化中，请稍候再发送。');
      return;
    }
    void sendChatTurn(text);
  };

  /**
   * Sender 内置停止按钮（loading 态显示）：abort 当前 /chat 请求——浏览器断开连接，
   * 后端无硬超时工具循环随断开中止；本地立即复位发送/失败态并给出提示。
   */
  const abortTurn = useCallback(() => {
    const controller = abortRef.current;
    if (!controller) return;
    controller.abort();
    stopProgressPolling();
    setNotice(resetAiTurnStateAfterAbort().notice);
    setError(null);
    setSending(false);
  }, []);

  /**
   * Sender 键盘接管：running 中组件内部 Enter 提交被 loading 门禁拦截，
   * 此处把 Enter 接管为插话发送并返回 false 阻断默认行为；空闲时交给组件内置链路。
   * @param event - Sender 文本域键盘事件。
   * @returns false 表示已接管（阻断组件内部 Enter 提交），其余返回 void。
   */
  const handleSenderKeyDown = (event: KeyboardEvent<Element>): void | false => {
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) {
      return;
    }
    if (sending && sessionIdRef.current !== null) {
      event.preventDefault();
      const text = input.trim();
      if (text) {
        void sendGuidance(text);
      }
      return false;
    }
  };

  /**
   * 处理附件选择：逐个校验类型/大小与数量上限，读取 base64 后进入附件条；
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

  /**
   * 对草稿包发起服务端校验：结果原样展示，错误明细供用户转告 AI 或自行修正。
   */
  const runDraftValidate = useCallback(async () => {
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
  }, [draft, draftBusy]);

  /**
   * 用当前选中数据源试跑草稿包：复用后端 50 行/30 秒限额，结果只展示不落库。
   */
  const runDraftDryRun = useCallback(async () => {
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
  }, [draft, draftBusy, selectedSource]);

  /**
   * 确认后导入草稿包：Create 模式 + 已试跑标记；成功后提示刷新模板列表。
   */
  const runDraftImport = useCallback(async () => {
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
  }, [draft, draftBusy, selectedSource]);

  /**
   * 放弃等待当前慢轮次（45 秒提示条上的“重试”）：只放弃本地等待——停轮询、恢复输入态，
   * 原 POST 继续在后台运行，返回后仍会按 Seq 校正消息流补全本轮内容；用户可立即重新提问。
   */
  const abandonSlowTurn = () => {
    stopProgressPolling();
    setTurnStalled(false);
    setStalledDismissed(true);
    setSending(false);
    setNotice('上一条消息仍在后台处理，返回后将自动补全；您也可以直接重新提问。');
  };

  /**
   * 渲染草稿卡片（消息流内业务节点）：列/参数 chip 流 + 状态徽标 + 校验/试跑/导入动作；
   * 保留自研 aiDraftCard 结构，作为 Bubble.List 的 draft 角色内容随消息流滚动。
   * @returns 草稿卡 React 节点；无草稿时返回 null。
   */
  const renderDraftCard = () => {
    // 空态守卫：无草稿时 Bubble.List 不渲染 draft 节点。
    if (draft === null) {
      return null;
    }
    // 卡片头：报表名 + 状态徽标；spec 编译失败时整卡退化为错误提示。
    return (
      <section className="ai-draft-card" aria-label="报表草稿">
        <header className="ai-draft-header">
          <strong>报表草稿{draft.package ? `：${draft.package.report.name}` : ''}</strong>
          <span className={aiDraftBadgeClass(draft.status)}>{aiDraftStatusLabel(draft.status)}</span>
        </header>
        {draft.package === null ? (
          <p className="ai-draft-empty">spec 尚未编译通过，AI 正在按错误清单修正。</p>
        ) : (
          <>
            {/* 编译产物明细区：列/参数 chip 流 + 最近一次校验/试跑结果文本。 */}
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
            {/* 动作区：校验/试跑/导入（导入走既有报表包链路，主色实底）；运行中整排禁用。 */}
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
    );
  };

  // 消息流 → Bubble.List 数据（useMemo 稳定引用）：秒表刷新只重建思考条节点，历史气泡不重渲染。
  const messageItems = useMemo(() => buildAiBubbleItems(messages, {
    guidanceSeqs,
    pendingGuidanceContent: pendingGuidance,
  }).map((item) => {
    if (item.kind === 'toolTrace') {
      return { key: item.key, role: 'toolTrace', content: { nodes: item.thoughtNodes } };
    }
    if (item.kind === 'user') {
      return {
        key: item.key,
        role: 'user',
        content: item.content,
        extraInfo: { guidanceBadge: item.guidanceBadge } satisfies AiUserBubbleExtra,
      };
    }
    return { key: item.key, role: 'ai', content: item.content };
  }), [messages, guidanceSeqs, pendingGuidance]);

  // 追加消息流内的动态业务节点：空态引导、思考条（running）、失败气泡、草稿卡。
  // draft 卡的闭包读取 draft/draftResult 等状态，因此这些状态进入依赖，确保卡片内容即时刷新。
  const listItems = useMemo(() => {
    const items: ComponentProps<typeof Bubble.List>['items'] = [...messageItems];
    if (messages.length === 0 && !sending && draft === null) {
      items.push({
        key: '__empty',
        role: 'system',
        content: (
          <div className="ai-panel-empty">
            <Bot size={16} />
            <span>选择数据源后，用自然语言描述你想了解的数据（例如“患者费用表里费用类型有哪些”）。</span>
          </div>
        ),
      });
    }
    if (sending) {
      items.push({ key: '__thinking', role: 'thinking', content: { ...thinkingStatus } });
    } else if (turnFailed) {
      items.push({ key: '__failure', role: 'failure', content: 'turn-failed' });
    }
    if (draft !== null) {
      items.push({ key: '__draft', role: 'draft', content: 'draft-card', contentRender: renderDraftCard });
    }
    return items;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- renderDraftCard 为组件内联闭包，随下方依赖整体刷新。
  }, [messageItems, messages.length, sending, thinkingStatus, turnFailed, draft, draftBusy, draftResult, dryRunResult, selectedSource]);

  /**
   * 渲染数据源选项（antd Select）：名称 + 编码 + 数据库类型，名称排序沿用纯函数结果。
   * @returns Select options。
   */
  const sourceOptions = useMemo(() => sources.map((source) => ({
    value: source.code,
    label: `${source.name}（${source.code} · ${source.dbType}）`,
  })), [sources]);

  /**
   * 切换数据源：视为重新发起对话——清空本地与服务端上下文，关闭旧会话防泄漏。
   * @param code - 新选中的数据源编码。
   */
  const handleSourceChange = (code: string) => {
    setSelectedSource(code);
    const previousSession = sessionIdRef.current;
    resetLocalSessionState();
    if (previousSession) void aiApi.closeSession(previousSession).catch(() => undefined);
  };

  // 渲染三段式抽屉：顶部固定（品牌头 + 数据源工具行 + 外发警示 + 通知条）、中部 Bubble.List
  // 消息流（ThoughtChain 工具轨迹/草稿卡/思考条/插话角标）、底部固定（附件条 + Sender）。

  return (
    <>
      <div className="ai-drawer-overlay" onClick={onClose} aria-hidden="true" />
      <ConfigProvider theme={AiPanelTheme}>
        <aside className="ai-assistant-panel" aria-label="AI 助手">
          <header className="ai-panel-header">
            <div className="ai-panel-brand">
              <span className="ai-panel-avatar" aria-hidden="true">
                <Bot size={18} />
              </span>
              <span className="ai-panel-heading">
                <strong>AI 报表助手</strong>
                <span className="ai-panel-status">
                  <i className={sending ? 'ai-status-dot running' : 'ai-status-dot'} aria-hidden="true" />
                  {sending ? '处理中' : '空闲'}
                </span>
              </span>
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
            </div>
            {/* 数据源搬头部（第二行）：antd Select compact 截断显示 + 表清单 Popover 图标按钮。 */}
            <div className="ai-panel-toolbar">
              <Select
                size="small"
                className="ai-panel-source-select"
                aria-label="选择数据源"
                value={selectedSource || undefined}
                placeholder={sources.length === 0 ? '暂无可用数据源' : '选择数据源'}
                disabled={sending || sources.length === 0}
                options={sourceOptions}
                onChange={handleSourceChange}
              />
              <Popover
                trigger="click"
                placement="bottomRight"
                arrow={false}
                open={tablesOpen}
                onOpenChange={setTablesOpen}
                content={(
                  <div className="ai-table-popover">
                    <Input
                      size="small"
                      allowClear
                      prefix={<Search size={13} />}
                      value={tableFilter}
                      placeholder="过滤表名或注释…"
                      aria-label="过滤数据源表清单"
                      onChange={(event) => setTableFilter(event.target.value)}
                    />
                    <div className="ai-table-list-scroll">
                      {(() => {
                        if (tablesError) {
                          return <div className="ai-table-list-hint">{tablesError}</div>;
                        }
                        const filtered = filterAiDataSourceTables(tables, tableFilter);
                        const rendered = sliceAiTableRenderList(filtered);
                        if (filtered.length === 0) {
                          return (
                            <div className="ai-table-list-hint">
                              {tablesLoading ? '清单加载中…' : '该数据源暂无表/视图，或没有匹配项。'}
                            </div>
                          );
                        }
                        return (
                          <>
                            {rendered.items.map((table) => (
                              <div className="ai-table-list-row" key={table.name}>
                                <i className={table.kind === 'view' ? 'ai-table-kind view' : 'ai-table-kind'}>
                                  {table.kind === 'view' ? '视图' : '表'}
                                </i>
                                <span className="ai-table-name" title={aiDataSourceTableSubtitle(table)}>{table.name}</span>
                                {table.comment && <small className="ai-table-comment">{table.comment}</small>}
                              </div>
                            ))}
                            {rendered.hiddenCount > 0 && (
                              <div className="ai-table-list-hint">
                                共 {filtered.length} 项，已显示前 {rendered.items.length} 项，输入过滤缩小范围。
                              </div>
                            )}
                          </>
                        );
                      })()}
                    </div>
                  </div>
                )}
              >
                <Button
                  size="small"
                  className="ai-tables-trigger"
                  icon={<Database size={14} aria-hidden="true" />}
                  loading={tablesLoading}
                  title="查看数据源表/视图清单（AI 可见的范围）"
                >
                  表清单 {tables.length}
                </Button>
              </Popover>
            </div>
          </header>

          <div className="ai-panel-alerts">
            <div className="ai-panel-warning" role="note">
              <Check size={12} aria-hidden="true" />
              对话内容与采样数据将发送至所配置的 LLM 服务，请勿粘贴敏感信息。
            </div>
            {notice && <div className="ai-panel-notice">{notice}</div>}
            {error && <div className="ai-panel-error">{error}</div>}
            {showSlowBanner && (
              <div className="ai-panel-slow" role="alert">
                <span className="ai-panel-slow-text">
                  模型响应较慢，仍在等待（已 {thinkingSeconds}s）。可点此重试，原请求仍在后台继续。
                </span>
                <span className="ai-panel-slow-actions">
                  <button type="button" className="ai-slow-button" onClick={abandonSlowTurn}>重试</button>
                  <button
                    type="button"
                    className="ai-slow-button ghost"
                    onClick={() => setStalledDismissed(true)}
                  >
                    继续等待
                  </button>
                </span>
              </div>
            )}
          </div>

          <div className="ai-messages-wrap">
            <Bubble.List
              className="ai-panel-messages"
              aria-label="AI 对话消息流"
              ref={handleBubbleListRef}
              autoScroll={false}
              onScroll={handleMessagesScroll}
              role={AiBubbleRoles}
              items={listItems}
            />
            {showBackToBottom && (
              <button type="button" className="ai-back-bottom" onClick={jumpToLatest}>
                <ArrowDown size={13} />
                回到底部
              </button>
            )}
          </div>

          <footer className="ai-panel-footer">
            {/* 附件条：展示待发送附件（文件名+大小）并可逐条撤回；发送失败时保留以便重试。 */}
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
            {/* 隐藏文件选择器：附件 base64 随消息内联提交，不新增上传端点。 */}
            <input
              ref={fileInputRef}
              type="file"
              multiple
              accept=".png,.jpg,.jpeg,.webp,.xlsx,.pdf"
              aria-label="选择参考材料附件"
              className="ai-attachment-input"
              onChange={(event) => { void handleAttachmentSelection(event); }}
            />
            {/* Sender：Enter 发送 / Shift+Enter 换行；running 时显示内置停止按钮（abort），
                Enter 由 handleSenderKeyDown 接管为插话发送（走 guidance 端点）。 */}
            <Sender
              className="ai-panel-composer"
              value={input}
              placeholder="向 AI 助手提问…（Enter 发送）"
              onChange={(value) => setInput(value)}
              onSubmit={submitTurn}
              onCancel={abortTurn}
              loading={sending}
              onKeyDown={handleSenderKeyDown}
              prefix={(
                <Button
                  type="text"
                  size="small"
                  title="添加参考材料附件（图片/Excel/PDF，最多 3 个）"
                  aria-label="添加附件"
                  disabled={attachmentsBusy}
                  icon={attachmentsBusy ? <LoaderCircle className="spin" size={16} /> : <Paperclip size={16} />}
                  onClick={() => fileInputRef.current?.click()}
                />
              )}
            />
          </footer>
        </aside>
      </ConfigProvider>
    </>
  );
}
