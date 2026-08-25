import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type FormEvent,
} from 'react';
import type { IWorkbookData } from '@univerjs/presets';
import {
  ChevronLeft,
  ChevronRight,
  Eye,
  FileDown,
  FileSpreadsheet,
  FileUp,
  Filter,
  LoaderCircle,
  PanelTopClose,
  PanelTopOpen,
  Pencil,
  Printer,
  Save,
  Search,
  Settings2,
  Share2,
  X,
} from 'lucide-react';
import { createReportApi } from './api';
import {
  prepareSavedRevisionForExport,
  saveBeforeDataExportMessage,
  selectSnapshotForSave,
  waitForLargeExportJob,
} from './dataExportFlow';
import { scanTemplateBindings } from './templateBinding';
import { createTemplatePreviewPlan } from './templatePreview';
import {
  applyLocalDataQuery,
  readMultiSelectValues,
  serializeMultiSelectValues,
} from './localDataQuery';
import { createPreviewToolbarPresentation } from './previewToolbar';
import { createPageLoadingPresentation } from './pageLoading';
import { parameterInputAttributes, parameterInputValue } from './parameterInput';
import { TemplateFieldPanel } from './TemplateFieldPanel';
import { ReportConfigurationDialog } from './ReportConfigurationDialog';
import { ShareReportDialog, type SharedParameterSummary } from './ShareReportDialog';
import { StatusToasts } from './StatusToasts';
import {
  createReportShareUrl,
  readReportQueryParameters,
  readReportShareParameters,
} from './reportShare';
import {
  createInitialReportRequest,
  findMissingRequiredParameterCodes,
  refreshReportRequestFromUrl,
} from './reportRequest';
import { logSqlDebug } from './sqlDebug';
import {
  UniverTemplateHost,
  type TemplateEditorMode,
  type UniverTemplateHandle,
} from './UniverTemplateHost';
import type {
  ExecuteRequest,
  ExecuteResult,
  ExportJob,
  ReportDefinition,
  ReportConfigurationDocument,
  TemplateDocument,
} from './types';

/**
 * 可编辑模板验证页的状态编排层。
 * 它负责 HTTP、设计/预览模式和保存并发；所有具体工作簿副作用封装在 UniverTemplateHost 中。
 */

// 把稳定的后台错误码翻译为设计人员可理解的提示，同时保留原始 code 便于日志定位。
const backendErrorMessages: Readonly<Record<string, string>> = {
  REPORT_DEFINITION_NOT_FOUND: '找不到报表定义',
  REPORT_TEMPLATE_NOT_FOUND: '找不到报表模板',
  TEMPLATE_BINDING_INVALID: '模板字段绑定校验失败',
  TEMPLATE_SNAPSHOT_INVALID: '模板内容校验失败',
  TEMPLATE_XLSX_EXTENSION_REQUIRED: '只支持导入 XLSX 模板',
  TEMPLATE_XLSX_FILE_REQUIRED: '请选择要导入的 XLSX 模板',
  TEMPLATE_XLSX_FILE_TOO_LARGE: '模板文件超过 10 MiB 限制',
  TEMPLATE_XLSX_INVALID: 'XLSX 模板无法读取或文件已损坏',
  TEMPLATE_XLSX_TOO_MANY_SHEETS: 'XLSX 工作表数量超过限制',
  TEMPLATE_XLSX_TOO_MANY_CELLS: 'XLSX 已用单元格数量超过限制',
  TEMPLATE_SAVE_REQUIRED: '请先保存模板再导出数据',
  TEMPLATE_REVISION_CHANGED: '模板版本已变化，请刷新后重新导出',
  NORMAL_EXPORT_ROW_LIMIT_EXCEEDED: '数据行数超过普通导出限制，请使用大文件导出',
  NORMAL_EXPORT_CELL_LIMIT_EXCEEDED: '数据单元格超过普通导出限制，请使用大文件导出',
  NORMAL_EXPORT_TIMEOUT: '普通导出超时，请使用大文件导出',
  REPORT_CONFIGURATION_REVISION_CONFLICT: '报表配置已被其它页面修改，请重新打开配置',
  DATA_SOURCE_CONNECTION_FAILED: '无法连接到数据源',
  DATA_SOURCE_PASSWORD_REQUIRED: '连接信息变化后必须填写新密码',
  READ_ONLY_SQL_REQUIRED: '只允许配置单条只读 SELECT 查询',
  EXTERNAL_PARAMETER_CONTEXT_REQUIRED: '缺少接口参数所需的必填 URL 动态参数',
  EXTERNAL_PARAMETER_SOURCE_NOT_FOUND: '接口参数源不存在',
  EXTERNAL_PARAMETER_SOURCE_DISABLED: '接口参数源已停用',
  EXTERNAL_PARAMETER_REQUEST_FAILED: '接口参数请求失败',
  EXTERNAL_PARAMETER_TIMEOUT: '接口参数请求超时',
  EXTERNAL_PARAMETER_RESPONSE_INVALID: '接口返回内容与字段映射不一致',
  SHARE_PROTECTION_PASSWORD_REQUIRED: '开启共享数据保护前必须设置解除保护密码',
  SHARE_PROTECTION_PASSWORD_INVALID: '解除保护密码长度必须为 8 到 128 个字符',
};

/** 根据后台定义建立参数字典；只有明确允许 URL 预填的参数才读取查询字符串。 */
function createInitialRequest(
  definition?: ReportDefinition,
  sharedParameters?: Readonly<Record<string, string>>,
): ExecuteRequest {
  return createInitialReportRequest(definition, sharedParameters);
}

function parameterDisplayValue(
  parameter: ReportDefinition['parameters'][number],
  value: string,
): string {
  if (parameter.controlType.toLowerCase() === 'multiselect') {
    return readMultiSelectValues(value)
      .map((item) => parameter.options?.find((option) => option.value === item)?.label ?? item)
      .join('、');
  }
  if (parameter.controlType.toLowerCase() === 'select') {
    return parameter.options?.find((option) => option.value === value)?.label ?? value;
  }
  if (parameter.dataType === 'bool' || parameter.controlType === 'checkbox') {
    if (value === 'true') return '是';
    if (value === 'false') return '否';
  }
  return value;
}

async function copyText(text: string): Promise<void> {
  if (navigator.clipboard?.writeText !== undefined) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  document.body.append(textarea);
  textarea.select();
  const copied = document.execCommand('copy');
  textarea.remove();
  if (!copied) throw new Error('浏览器未允许复制到剪贴板。');
}

function toReportDefinition(configuration: ReportConfigurationDocument): ReportDefinition {
  return {
    code: configuration.reportCode,
    name: configuration.name,
    sheetName: configuration.sheetName,
    preview: configuration.preview,
    export: configuration.export,
    parameters: configuration.parameters,
    fields: configuration.fields,
    fieldPresentations: configuration.fieldPresentations ?? [],
    shareProtection: configuration.shareProtection ?? { enabled: false, passwordConfigured: false },
    externalSources: configuration.externalSources ?? [],
  };
}

/** 统一处理领域冲突、后台错误码和未知异常，避免每个事件处理器重复拼接提示。 */
function errorMessage(error: unknown, fallback: string): string {
  const detail = error instanceof Error
    ? error.message
    : typeof error === 'string' && error.length > 0
      ? error
      : '';

  if (detail.includes('TEMPLATE_REVISION_CONFLICT')) {
    return '模板已被其它页面修改，请刷新后重新编辑';
  }

  const backendCode = Object.keys(backendErrorMessages).find((code) => detail.includes(code));
  if (backendCode !== undefined) {
    return `${backendErrorMessages[backendCode]}（${detail}）`;
  }
  return detail.length > 0 ? `${fallback}：${detail}` : fallback;
}

interface AppProps {
  reportCode: string;
  shared?: boolean;
}

export default function App({ reportCode, shared = false }: AppProps) {
  const reportApi = useMemo(() => createReportApi(reportCode), [reportCode]);
  const linkedParameters = useMemo(
    () => shared ? readReportShareParameters() : {},
    [shared],
  );
  // 后台持久状态和查询结果使用 React state，负责界面渲染。
  const [definition, setDefinition] = useState<ReportDefinition | null>(null);
  const [templateDocument, setTemplateDocument] = useState<TemplateDocument | null>(null);
  const [request, setRequest] = useState<ExecuteRequest>(createInitialRequest);
  const [result, setResult] = useState<ExecuteResult | null>(null);
  const [mode, setMode] = useState<TemplateEditorMode>(shared ? 'preview' : 'design');
  const [draftSnapshot, setDraftSnapshot] = useState<IWorkbookData | null>(null);
  const [dirty, setDirty] = useState(false);
  const [initialized, setInitialized] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [importing, setImporting] = useState(false);
  const [exportingTemplate, setExportingTemplate] = useState(false);
  const [exportingData, setExportingData] = useState(false);
  const [printing, setPrinting] = useState(false);
  const [cancellingExport, setCancellingExport] = useState(false);
  const [exportJob, setExportJob] = useState<ExportJob | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [templateWarning, setTemplateWarning] = useState<string | null>(null);
  const [exchangeStatus, setExchangeStatus] = useState<string | null>(null);
  const [queryDuration, setQueryDuration] = useState(0);
  const [renderDuration, setRenderDuration] = useState(0);
  const [configurationOpen, setConfigurationOpen] = useState(false);
  const [configurationDocument, setConfigurationDocument] = useState<ReportConfigurationDocument | null>(null);
  const [shareOpen, setShareOpen] = useState(false);
  const [shareIncludesParameters, setShareIncludesParameters] = useState(true);
  const [sharedToolsExpanded, setSharedToolsExpanded] = useState(false);

  // editorRef 是页面调用 Univer 的窄接口；draftSnapshotRef 保存“最后设计草稿”的同步事实来源。
  // 同一快照另存 state 是为了触发 React 生命周期；异步回调使用 ref 可避免读到闭包中的旧值。
  const editorRef = useRef<UniverTemplateHandle>(null);
  const templateFileInputRef = useRef<HTMLInputElement>(null);
  const draftSnapshotRef = useRef<IWorkbookData | null>(null);
  // 保存、预览、翻页和返回设计共享一个互斥标记，防止两个异步工作簿替换流程交叉执行。
  const operationRef = useRef(false);
  const sharedAutoPreviewAttemptedRef = useRef(false);
  const sharedSnapshotPreparedRef = useRef(false);

  useEffect(() => {
    // 报表定义和模板互不依赖，并行加载可缩短首次可编辑时间。
    // active 标记阻止组件卸载后继续 setState，不试图取消已经发出的普通 GET 请求。
    let active = true;

    const initialize = async () => {
      setLoading(true);
      setError(null);
      try {
        const [nextDefinition, nextTemplateDocument] = await Promise.all([
          reportApi.definition(),
          reportApi.template(),
        ]);
        if (!active) {
          return;
        }

        // URL 提供业务对象参数，日期默认值以后端函数计算结果为准，浏览器不复制月份边界规则。
        const nextRequest = createInitialRequest(
          nextDefinition,
          shared ? linkedParameters : undefined,
        );
        draftSnapshotRef.current = nextTemplateDocument.snapshot;
        setDefinition(nextDefinition);
        setTemplateDocument(nextTemplateDocument);
        setDraftSnapshot(nextTemplateDocument.snapshot);
        setRequest(nextRequest);
        setTemplateWarning(nextTemplateDocument.validationWarning);
        setInitialized(true);
      } catch (exception) {
        if (active) {
          setError(errorMessage(exception, '报表初始化失败'));
        }
      } finally {
        if (active) {
          setLoading(false);
        }
      }
    };

    void initialize();
    return () => {
      active = false;
    };
  }, [linkedParameters, reportApi, shared]);

  useEffect(() => {
    if (!dirty) {
      return;
    }

    // 只有真正的用户命令把 dirty 置为 true；Univer 初始化 mutation 已在宿主中过滤。
    const warnBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warnBeforeUnload);
    return () => window.removeEventListener('beforeunload', warnBeforeUnload);
  }, [dirty]);

  const handleEditorChanged = useCallback(() => {
    if (shared) return;
    setDirty(true);
    setExchangeStatus(null);
  }, [shared]);

  const handleEditorRendered = useCallback((duration: number) => {
    setRenderDuration(duration);
  }, []);

  const handleEditorError = useCallback((editorError: Error) => {
    setError(errorMessage(editorError, '模板编辑器操作失败'));
  }, []);

  const enterPreview = useCallback(async (page = 1) => {
    if (operationRef.current || definition === null || !initialized) {
      return;
    }

    operationRef.current = true;
    setLoading(true);
    setPreviewing(true);
    setError(null);
    // designSnapshot 只用于本次失败回滚；draftSnapshotRef 保存跨页预览都应复用的原始设计草稿。
    let designSnapshot: IWorkbookData | null = null;
    let renderAttempted = false;
    try {
      let snapshot: IWorkbookData;
      if (mode === 'design') {
        // 从设计进入预览时先抓取当前未保存草稿，保证预览看到用户刚刚修改的内容。
        const editor = editorRef.current;
        if (editor === null) {
          throw new Error('模板编辑器尚未准备完成。');
        }
        snapshot = editor.saveDraft();
        designSnapshot = snapshot;
        draftSnapshotRef.current = snapshot;
        setDraftSnapshot(snapshot);
      } else if (shared && result === null && !sharedSnapshotPreparedRef.current) {
        // 分享页初始工作簿是只读的，但仍需经过 Univer 序列化来规范化旧模板中的 mergeData 等运行时结构。
        const editor = editorRef.current;
        if (editor === null) {
          throw new Error('共享报表编辑器尚未准备完成。');
        }
        snapshot = editor.saveDraft();
        draftSnapshotRef.current = snapshot;
        setDraftSnapshot(snapshot);
        sharedSnapshotPreparedRef.current = true;
      } else {
        // 预览翻页不能从当前预览工作簿取快照，否则已经替换的参数和展开行会污染下一页模板。
        const savedDraft = draftSnapshotRef.current;
        if (savedDraft === null) {
          throw new Error('没有可用于数据预览的模板草稿。');
        }
        snapshot = savedDraft;
      }

      // 先扫描草稿，再请求数据；无效 token 不应浪费后台查询，也不能进入具有副作用的渲染阶段。
      const binding = scanTemplateBindings(snapshot, definition);
      const nextRequest = refreshReportRequestFromUrl({ ...request, page }, definition);
      const missingParameterCodes = findMissingRequiredParameterCodes(
        definition,
        nextRequest.parameters,
      );
      if (missingParameterCodes.length > 0) {
        const missingLabels = missingParameterCodes.map((code) => {
          const parameter = definition.parameters.find((item) => item.code === code);
          return parameter === undefined ? code : `${parameter.displayName}（${code}）`;
        });
        throw new Error(`请先填写必填参数：${missingLabels.join('、')}。`);
      }
      const queryStartedAt = performance.now();
      const queryResult = await reportApi.execute(nextRequest);
      logSqlDebug(reportCode, queryResult);
      const nextResult = applyLocalDataQuery(queryResult, definition, nextRequest);
      setQueryDuration(performance.now() - queryStartedAt);

      // 纯函数计划先完成数据/坐标校验，随后才让 Univer 插行和写单元格。
      const plan = createTemplatePreviewPlan(snapshot, binding, nextResult, definition);
      const editor = editorRef.current;
      if (editor === null) {
        throw new Error('模板编辑器尚未准备完成。');
      }
      renderAttempted = true;
      await editor.renderPreview(snapshot, plan);

      setResult(nextResult);
      setRequest(nextRequest);
      setMode('preview');
      if (shared) {
        const rowCountLabel = definition.preview.mode === 'Full'
          ? `共 ${nextResult.rows.length} 行`
          : `本页 ${nextResult.rows.length} 行`;
        setExchangeStatus(`查询完成，${rowCountLabel}`);
      }
    } catch (exception) {
      let message = errorMessage(exception, '数据预览失败');
      if (mode === 'design' && renderAttempted && designSnapshot !== null) {
        // 渲染可能已替换工作簿或插入部分行；失败时必须恢复设计快照，避免用户继续编辑半成品预览。
        try {
          editorRef.current?.loadDesign(designSnapshot);
        } catch (restoreError) {
          message = `${message}；${errorMessage(restoreError, '恢复模板设计失败')}`;
        }
      }
      setError(message);
    } finally {
      operationRef.current = false;
      setPreviewing(false);
      setLoading(false);
    }
  }, [definition, initialized, mode, request, result, shared]);

  useEffect(() => {
    if (!shared || sharedAutoPreviewAttemptedRef.current || !initialized || definition === null) {
      return;
    }

    const hasLinkedParameter = definition.parameters.some((parameter) => {
      return Boolean(
        linkedParameters[parameter.code]?.trim()
        || (parameter.allowUrlPrefill
          && readReportQueryParameters()[parameter.urlKey || parameter.code]?.trim()),
      );
    });
    const hasMissingRequiredParameter = definition.parameters.some((parameter) => (
      parameter.required && !(request.parameters[parameter.code]?.trim())
    ));
    if (!hasLinkedParameter || hasMissingRequiredParameter) {
      sharedAutoPreviewAttemptedRef.current = true;
      return;
    }

    let cancelled = false;
    let timer = 0;
    const previewWhenReady = () => {
      if (cancelled) return;
      if (editorRef.current === null) {
        timer = window.setTimeout(previewWhenReady, 50);
        return;
      }
      sharedAutoPreviewAttemptedRef.current = true;
      void enterPreview(1);
    };
    timer = window.setTimeout(previewWhenReady, 0);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [definition, enterPreview, initialized, linkedParameters, request.parameters, shared]);

  const returnDesign = useCallback(() => {
    if (operationRef.current || mode === 'design') {
      return;
    }

    operationRef.current = true;
    setError(null);
    try {
      // 永远恢复进入预览前保存的草稿，不从只读预览反向提取任何数据或公式缓存值。
      const snapshot = draftSnapshotRef.current;
      const editor = editorRef.current;
      if (snapshot === null || editor === null) {
        throw new Error('没有可恢复的模板草稿。');
      }
      editor.loadDesign(snapshot);
      setMode('design');
    } catch (exception) {
      setError(errorMessage(exception, '返回模板设计失败'));
    } finally {
      operationRef.current = false;
    }
  }, [mode]);

  const persistTemplate = useCallback(async (): Promise<TemplateDocument> => {
    if (definition === null || templateDocument === null) {
      throw new Error('模板文档尚未加载完成。');
    }

    // 设计态读取活动编辑器；预览态必须使用进入预览前保留的设计草稿，不能保存已展开数据副本。
    const snapshot = selectSnapshotForSave(
      mode,
      () => {
        const editor = editorRef.current;
        if (editor === null) {
          throw new Error('模板编辑器尚未准备完成。');
        }
        return editor.saveDraft();
      },
      draftSnapshotRef.current,
    );
    const binding = scanTemplateBindings(snapshot, definition);
    const savedDocument = await reportApi.saveTemplate({
      snapshot,
      binding,
      revision: templateDocument.revision,
    });

    // 使用服务器回包作为新的设计基线；预览工作簿保持只读，不在这里重新加载。
    draftSnapshotRef.current = savedDocument.snapshot;
    setTemplateDocument(savedDocument);
    setDraftSnapshot(savedDocument.snapshot);
    setTemplateWarning(null);
    setDirty(false);
    return savedDocument;
  }, [definition, mode, templateDocument]);

  const saveTemplate = useCallback(async () => {
    if (operationRef.current || mode !== 'design') {
      return;
    }

    operationRef.current = true;
    setSaving(true);
    setError(null);
    try {
      await persistTemplate();
      setExchangeStatus('模板已保存');
    } catch (exception) {
      setError(errorMessage(exception, '模板保存失败'));
    } finally {
      operationRef.current = false;
      setSaving(false);
    }
  }, [mode, persistTemplate]);

  const chooseTemplateFile = useCallback(() => {
    if (operationRef.current || mode !== 'design' || !initialized) {
      return;
    }
    if (dirty && !window.confirm('导入会替换当前未保存的模板草稿，是否继续？')) {
      return;
    }
    templateFileInputRef.current?.click();
  }, [dirty, initialized, mode]);

  const importTemplate = useCallback(async (event: ChangeEvent<HTMLInputElement>) => {
    const input = event.currentTarget;
    const file = input.files?.[0];
    // 立即清空 input，保证连续选择同一个文件也会再次触发 change。
    input.value = '';
    if (file === undefined || operationRef.current || mode !== 'design') {
      return;
    }

    operationRef.current = true;
    setImporting(true);
    setError(null);
    setExchangeStatus(null);
    try {
      const imported = await reportApi.importTemplate(file);
      const editor = editorRef.current;
      if (editor === null) {
        throw new Error('模板编辑器尚未准备完成。');
      }

      // 后台已经完成转换和 token 校验；此处只替换设计草稿，保留原 revision 直到用户主动保存。
      editor.loadDesign(imported.snapshot);
      draftSnapshotRef.current = imported.snapshot;
      setDraftSnapshot(imported.snapshot);
      setResult(null);
      setDirty(true);
      const warningText = imported.warnings.length > 0
        ? `，有 ${imported.warnings.length} 条兼容提示`
        : '';
      setExchangeStatus(
        `已导入 ${imported.usedCellCount} 个单元格（映射版本 ${imported.mappingVersion}）${warningText}，尚未保存`,
      );
    } catch (exception) {
      setError(errorMessage(exception, '模板导入失败'));
    } finally {
      operationRef.current = false;
      setImporting(false);
    }
  }, [mode]);

  const exportTemplate = useCallback(async () => {
    if (
      operationRef.current ||
      mode !== 'design' ||
      definition === null ||
      !initialized
    ) {
      return;
    }

    operationRef.current = true;
    setExportingTemplate(true);
    setError(null);
    setExchangeStatus(null);
    try {
      const editor = editorRef.current;
      if (editor === null) {
        throw new Error('模板编辑器尚未准备完成。');
      }
      const snapshot = editor.saveDraft();
      // 先在浏览器扫描一次以便立即展示坐标错误；后台仍会独立复查，不能信任这一结果。
      scanTemplateBindings(snapshot, definition);
      draftSnapshotRef.current = snapshot;
      setDraftSnapshot(snapshot);
      await reportApi.exportTemplate(snapshot);
      setExchangeStatus('当前模板草稿已导出为 XLSX');
    } catch (exception) {
      setError(errorMessage(exception, '模板导出失败'));
    } finally {
      operationRef.current = false;
      setExportingTemplate(false);
    }
  }, [definition, initialized, mode]);

  const exportData = useCallback(async () => {
    if (
      operationRef.current ||
      definition === null ||
      templateDocument === null ||
      !initialized
    ) {
      return;
    }

    operationRef.current = true;
    setExportingData(true);
    setError(null);
    setExchangeStatus(null);
    setExportJob(null);
    try {
      const savedRevision = await prepareSavedRevisionForExport({
        revision: templateDocument.revision,
        dirty,
        confirmSave: () => window.confirm(saveBeforeDataExportMessage),
        save: async () => {
          setSaving(true);
          try {
            return (await persistTemplate()).revision;
          } finally {
            setSaving(false);
          }
        },
      });
      if (savedRevision === null) {
        return;
      }

      const exportQuery = { ...request, page: 1 };
      if (definition.export.defaultMode === 'Large') {
        const started = await reportApi.startLargeDataExport(savedRevision, exportQuery);
        const completed = await waitForLargeExportJob(
          started,
          reportApi.getJob,
          undefined,
          (job) => {
            setExportJob(job);
            const total = job.totalRows > 0 ? ` / ${job.totalRows}` : '';
            setExchangeStatus(`大文件导出：${job.writtenRows}${total} 行`);
          },
        );
        await reportApi.downloadJob(completed.jobNo);
        setExchangeStatus(`完整数据已导出，共 ${completed.writtenRows} 行`);
      } else {
        await reportApi.downloadNormalData(savedRevision, exportQuery);
        setExchangeStatus('完整数据已按保存模板导出');
      }
    } catch (exception) {
      const message = errorMessage(exception, '数据导出失败');
      if (message.includes('大文件导出已取消')) {
        setExchangeStatus('大文件导出已取消');
      } else {
        setError(message);
      }
    } finally {
      operationRef.current = false;
      setSaving(false);
      setExportingData(false);
    }
  }, [definition, dirty, initialized, persistTemplate, request, templateDocument]);

  const printReport = useCallback(async () => {
    if (
      operationRef.current
      || mode !== 'preview'
      || result === null
      || !initialized
    ) {
      return;
    }

    operationRef.current = true;
    setPrinting(true);
    setError(null);
    setExchangeStatus(null);
    try {
      const editor = editorRef.current;
      if (editor === null) {
        throw new Error('报表预览尚未准备完成。');
      }
      await editor.printCurrentSheet();
      setExchangeStatus('已打开浏览器打印窗口');
    } catch (exception) {
      setError(errorMessage(exception, '报表打印失败'));
    } finally {
      operationRef.current = false;
      setPrinting(false);
    }
  }, [initialized, mode, result]);

  const toggleFilter = useCallback(async () => {
    if (operationRef.current || !initialized || result === null) {
      return;
    }

    operationRef.current = true;
    setError(null);
    setExchangeStatus(null);
    try {
      const editor = editorRef.current;
      if (editor === null) {
        throw new Error('报表预览尚未准备完成。');
      }
      const appliedRange = await editor.toggleFilter();
      setExchangeStatus(
        appliedRange === null
          ? '已关闭列筛选'
          : `已对 ${appliedRange} 启用列筛选`,
      );
    } catch (exception) {
      setError(errorMessage(exception, '切换筛选失败'));
    } finally {
      operationRef.current = false;
    }
  }, [initialized, result]);

  const cancelDataExport = useCallback(async () => {
    if (
      cancellingExport ||
      exportJob === null ||
      (exportJob.status !== 'Pending' && exportJob.status !== 'Running')
    ) {
      return;
    }

    setCancellingExport(true);
    try {
      const next = await reportApi.cancelJob(exportJob.jobNo);
      setExportJob(next);
      setExchangeStatus('正在取消大文件导出');
    } catch (exception) {
      setError(errorMessage(exception, '取消大文件导出失败'));
    } finally {
      setCancellingExport(false);
    }
  }, [cancellingExport, exportJob]);

  const insertBinding = useCallback((token: string, kind: 'parameter' | 'detail') => {
    try {
      // 字段面板只产生 token，具体的活动单元格检查、追加/替换规则由 Univer 宿主统一执行。
      const editor = editorRef.current;
      if (editor === null) {
        throw new Error('模板编辑器尚未准备完成。');
      }
      editor.insertBinding(token, kind);
      setError(null);
    } catch (exception) {
      setError(errorMessage(exception, '插入模板字段失败'));
    }
  }, []);

  const updateRequest = useCallback((code: string, value: string) => {
    setRequest((current) => ({
      ...current,
      parameters: {
        ...current.parameters,
        [code]: value || null,
      },
    }));
  }, []);

  const handleSubmit = useCallback((event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    void enterPreview(1);
  }, [enterPreview]);

  const openConfiguration = useCallback(async () => {
    if (operationRef.current || mode !== 'design') {
      return;
    }
    setConfigurationOpen(true);
    setConfigurationDocument(null);
    setError(null);
    try {
      setConfigurationDocument(await reportApi.configuration());
    } catch (exception) {
      setConfigurationOpen(false);
      setError(errorMessage(exception, '报表配置读取失败'));
    }
  }, [mode]);

  const configurationSaved = useCallback((saved: ReportConfigurationDocument) => {
    const nextDefinition = toReportDefinition(saved);
    setConfigurationDocument(saved);
    setDefinition(nextDefinition);
    setRequest(createInitialRequest(nextDefinition));
    setResult(null);
    setExchangeStatus(`报表配置已保存（修订号 ${saved.revision}）`);
  }, []);

  const visibleShareParameterCodes = useMemo(
    () => definition?.parameters
      .filter((parameter) => parameter.visible && parameter.controlType !== 'hidden')
      .map((parameter) => parameter.code) ?? [],
    [definition],
  );
  const shareParameterSummaries = useMemo<SharedParameterSummary[]>(
    () => definition?.parameters.flatMap((parameter) => {
      if (!visibleShareParameterCodes.includes(parameter.code)) return [];
      const value = request.parameters[parameter.code];
      if (value === null || value.trim() === '') return [];
      return [{
        code: parameter.code,
        label: parameter.displayName,
        value: parameterDisplayValue(parameter, value),
      }];
    }) ?? [],
    [definition, request.parameters, visibleShareParameterCodes],
  );
  const shareUrl = useMemo(
    () => createReportShareUrl({
      reportCode,
      parameters: request.parameters,
      visibleParameterCodes: visibleShareParameterCodes,
      includeParameters: shareIncludesParameters,
    }),
    [reportCode, request.parameters, shareIncludesParameters, visibleShareParameterCodes],
  );
  const copyShareLink = useCallback(async () => {
    setError(null);
    try {
      await copyText(shareUrl);
      setExchangeStatus('分享链接已复制');
    } catch (exception) {
      setError(errorMessage(exception, '复制分享链接失败'));
    }
  }, [shareUrl]);

  // 所有会切换或序列化工作簿的命令共用 busy，UI 禁用状态与 operationRef 的程序防重保持一致。
  const busy = loading
    || saving
    || importing
    || exportingTemplate
    || exportingData
    || printing
    || previewing;
  const pageLoading = createPageLoadingPresentation({
    loading,
    saving,
    importing,
    exportingTemplate,
    exportingData,
    printing,
    previewing,
    cancellingExport,
    mode,
    exportJob,
  });
  const revision = templateDocument?.revision;
  const previewToolbar = createPreviewToolbarPresentation(
    definition?.preview.mode ?? 'Full',
    result,
  );
  const pager = mode === 'preview' && previewToolbar.showPager ? (
    <div className="pager" role="group" aria-label="分页">
      <button
        className="icon-button"
        type="button"
        title="上一页"
        aria-label="上一页"
        disabled={busy || result === null || result.page <= 1}
        onClick={() => result && void enterPreview(result.page - 1)}
      >
        <ChevronLeft size={18} />
      </button>
      <button
        className="icon-button"
        type="button"
        title="下一页"
        aria-label="下一页"
        disabled={busy || !result?.hasMore}
        onClick={() => result && void enterPreview(result.page + 1)}
      >
        <ChevronRight size={18} />
      </button>
    </div>
  ) : null;

  return (
    <main
      className={shared ? 'app-shell shared-report-shell' : 'app-shell'}
      aria-busy={pageLoading !== null}
    >
      {pageLoading !== null && (
        <div className="page-loading-overlay" role="status" aria-live="polite">
          <div className="page-loading-panel">
            <LoaderCircle className="spin page-loading-icon" size={32} aria-hidden="true" />
            <strong>{pageLoading.title}</strong>
            <span>{pageLoading.detail}</span>
            {exportingData && exportJob !== null
              && (exportJob.status === 'Pending' || exportJob.status === 'Running') && (
                <button
                  className="button cancel-export-button"
                  type="button"
                  disabled={cancellingExport}
                  onClick={() => void cancelDataExport()}
                >
                  <X size={16} />
                  取消导出
                </button>
              )}
          </div>
        </div>
      )}
      {!shared && <header
        className={`command-bar ${mode}-mode`}
        aria-label="模板命令栏"
        inert={busy ? true : undefined}
      >
        <div className="command-leading">
          <div className="mode-switch" aria-label="工作模式">
              <button
                type="button"
                className={mode === 'design' ? 'mode-button active' : 'mode-button'}
                aria-pressed={mode === 'design'}
                title="切换到模板设计"
                disabled={busy || mode === 'design'}
                onClick={returnDesign}
              >
                <Pencil size={15} />
                模板设计
              </button>
              <button
                type="button"
                className={mode === 'preview' ? 'mode-button active' : 'mode-button'}
                aria-pressed={mode === 'preview'}
                title="使用当前草稿预览数据"
                disabled={busy || mode === 'preview' || !initialized}
                onClick={() => void enterPreview(1)}
              >
                <Eye size={15} />
                数据预览
              </button>
          </div>
        </div>

        <div className="report-heading" title={definition?.name ?? '模板编辑器'}>
          <h1>{definition?.name ?? '模板编辑器'}</h1>
        </div>

        <div className="command-actions">
          {mode === 'design' && (
            <>
              <button
                className="button configuration-button secondary-command"
                type="button"
                title="配置报表使用的数据集和查询参数"
                disabled={busy || !initialized}
                onClick={() => void openConfiguration()}
              >
                <Settings2 size={16} />
                <span className="button-label">报表配置</span>
              </button>
              <input
                ref={templateFileInputRef}
                className="visually-hidden"
                type="file"
                accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                tabIndex={-1}
                onChange={(event) => void importTemplate(event)}
              />
              <button
                className="button exchange-button secondary-command"
                type="button"
                title="导入 XLSX 作为当前模板草稿"
                disabled={busy || !initialized}
                onClick={chooseTemplateFile}
              >
                <FileUp size={16} />
                <span className="button-label">导入模板</span>
              </button>
              <button
                className="button exchange-button secondary-command"
                type="button"
                title="把当前模板草稿导出为 XLSX"
                disabled={busy || !initialized}
                onClick={() => void exportTemplate()}
              >
                <FileDown size={16} />
                <span className="button-label">导出模板</span>
              </button>
            </>
          )}
          {!shared && (
            <button
              className="button share-button secondary-command"
              type="button"
              title="生成可携带当前参数的分享链接"
              disabled={busy || !initialized}
              onClick={() => setShareOpen(true)}
            >
              <Share2 size={16} />
              <span className="button-label">分享</span>
            </button>
          )}
          <button
            className="button data-export-button"
            type="button"
            title={templateWarning === null
              ? '使用已保存模板导出全部查询数据'
              : '请先修复并保存模板字段绑定'}
            disabled={busy || !initialized || templateWarning !== null}
            onClick={() => void exportData()}
          >
            <FileSpreadsheet size={16} />
            导出数据
          </button>
          {mode === 'preview' && result !== null && (
            <>
              <button
                className="button secondary-command"
                type="button"
                title="选中表头或数据单元格后，为连续数据区域启用或关闭列筛选"
                disabled={busy || !initialized}
                onClick={() => void toggleFilter()}
              >
                <Filter size={16} />
                筛选
              </button>
              <button
                className="button print-button"
                type="button"
                title="打印当前预览工作表"
                disabled={busy || !initialized}
                onClick={() => void printReport()}
              >
                <Printer size={16} />
                打印
              </button>
            </>
          )}
          {exportJob !== null &&
            (exportJob.status === 'Pending' || exportJob.status === 'Running') && (
              <button
                className="button cancel-export-button"
                type="button"
                title="取消正在生成的大文件"
                disabled={cancellingExport}
                onClick={() => void cancelDataExport()}
              >
                {cancellingExport
                  ? <LoaderCircle className="spin" size={16} />
                  : <X size={16} />}
                取消导出
              </button>
            )}
          {!shared && mode === 'design' && (
            <button
              className="button save-button"
              type="button"
              title="保存当前模板草稿"
              disabled={busy || !initialized}
              onClick={() => void saveTemplate()}
            >
              <Save size={16} />
              保存模板
            </button>
          )}
          {!shared && (
            <div className="template-state" title={dirty ? '模板有未保存修改' : '模板已保存'}>
              <span>{revision === undefined ? 'revision --' : `revision ${revision}`}</span>
              <span className={dirty ? 'dirty-state' : 'saved-state'}>
                {dirty ? '未保存' : '已保存'}
              </span>
              {dirty && <span className="dirty-dot" aria-label="有未保存修改" />}
            </div>
          )}
          {(!shared || result !== null) && (
            <div className="duration-state">
              <span>查询 {queryDuration.toFixed(0)} ms</span>
              <span>渲染 {renderDuration.toFixed(0)} ms</span>
            </div>
          )}
          <div className="runtime-status" title="公式由 Univer Web Worker 计算">
            <span className="status-dot" />
            <span className="runtime-label">Univer Worker</span>
          </div>
        </div>
      </header>}

      <form
        className={shared ? 'query-band shared-query-band' : 'query-band'}
        inert={busy ? true : undefined}
        onSubmit={handleSubmit}
      >
        {definition?.parameters
          .filter((parameter) => parameter.visible && parameter.controlType !== 'hidden')
          .map((parameter) => {
            const inputAttributes = parameterInputAttributes(parameter);
            return (
            <label key={parameter.code}>
              <span>{parameter.displayName}</span>
              {parameter.controlType.toLowerCase() === 'select' ? (
                <select
                  value={request.parameters[parameter.code] ?? ''}
                  disabled={parameter.readOnly}
                  required={parameter.required}
                  onChange={(event) => updateRequest(parameter.code, event.target.value)}
                >
                  <option value="">全部</option>
                  {(parameter.options ?? []).map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </select>
              ) : parameter.controlType.toLowerCase() === 'multiselect' ? (
                <select
                  className="query-multi-select"
                  multiple
                  size={Math.min(Math.max((parameter.options ?? []).length + 1, 2), 4)}
                  value={readMultiSelectValues(request.parameters[parameter.code]).length === 0
                    ? ['']
                    : readMultiSelectValues(request.parameters[parameter.code])}
                  disabled={parameter.readOnly}
                  required={parameter.required}
                  onChange={(event) => updateRequest(
                    parameter.code,
                    serializeMultiSelectValues(
                      Array.from(event.currentTarget.selectedOptions, (option) => option.value)
                        .filter(Boolean),
                    ) ?? '',
                  )}
                >
                  <option value="">全部</option>
                  {(parameter.options ?? []).map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </select>
              ) : parameter.dataType === 'bool' || parameter.controlType === 'checkbox' ? (
                <select
                  value={request.parameters[parameter.code] ?? ''}
                  disabled={parameter.readOnly}
                  required={parameter.required}
                  onChange={(event) => updateRequest(parameter.code, event.target.value)}
                >
                  <option value="">全部</option>
                  <option value="true">是</option>
                  <option value="false">否</option>
                </select>
              ) : (
                <input
                  type={inputAttributes.type}
                  min={inputAttributes.min}
                  max={inputAttributes.max}
                  step={inputAttributes.step}
                  value={parameterInputValue(parameter, request.parameters[parameter.code] ?? '')}
                  readOnly={parameter.readOnly}
                  required={parameter.required}
                  onChange={(event) => updateRequest(parameter.code, event.target.value)}
                />
              )}
            </label>
            );
          })}
        <div className="query-actions">
          <button
            className="button button-primary query-button"
            type="submit"
            disabled={busy || !initialized}
            title={shared
              ? '使用当前参数查询共享报表'
              : mode === 'design'
                ? '使用当前模板草稿预览数据'
                : '重新查询当前页数据'}
          >
            {!shared && mode === 'design'
              ? <Eye size={16} />
              : <Search size={16} />}
            {!shared && mode === 'design' ? '预览' : '查询'}
          </button>
          {shared && (
            <>
              <button
                className="button data-export-button"
                type="button"
                title={templateWarning === null
                  ? '使用已保存模板导出全部查询数据'
                  : '请联系报表维护人员修复模板字段绑定'}
                disabled={busy || !initialized || templateWarning !== null}
                onClick={() => void exportData()}
              >
                <FileSpreadsheet size={16} />
                导出数据
              </button>
              {result !== null && (
                <>
                  <button
                    className="button secondary-command"
                    type="button"
                    title="选中表头或数据单元格后，为连续数据区域启用或关闭列筛选"
                    disabled={busy || !initialized}
                    onClick={() => void toggleFilter()}
                  >
                    <Filter size={16} />
                    筛选
                  </button>
                  <button
                    className="button print-button"
                    type="button"
                    title="打印当前预览工作表"
                    disabled={busy || !initialized}
                    onClick={() => void printReport()}
                  >
                    <Printer size={16} />
                    打印
                  </button>
                </>
              )}
              {exportJob !== null &&
                (exportJob.status === 'Pending' || exportJob.status === 'Running') && (
                  <button
                    className="button cancel-export-button"
                    type="button"
                    title="取消正在生成的大文件"
                    disabled={cancellingExport}
                    onClick={() => void cancelDataExport()}
                  >
                    {cancellingExport
                      ? <LoaderCircle className="spin" size={16} />
                      : <X size={16} />}
                    取消导出
                  </button>
                )}
            </>
          )}
        </div>
      </form>

      <StatusToasts
        error={error}
        success={exchangeStatus}
        warning={templateWarning === null
          ? null
          : shared
            ? `${templateWarning} 请联系报表维护人员修复模板。`
            : `${templateWarning} 当前工作簿仍可编辑，请替换失效字段后保存模板。`}
        onErrorClose={() => setError(null)}
        onSuccessClose={() => setExchangeStatus(null)}
        onWarningClose={() => setTemplateWarning(null)}
      />

      <section className="report-workspace" inert={busy ? true : undefined}>
        {!shared && (
          <div className="report-toolbar">
            {mode === 'preview' ? (
              <div className="report-stats">
                {previewToolbar.labels.map((label) => <span key={label}>{label}</span>)}
              </div>
            ) : (
              <div className="design-status">
                <span className="formula-dot" />
                模板编辑
              </div>
            )}

            {pager}
          </div>
        )}

        <div className={mode === 'design' && !shared ? 'workspace-grid design-grid' : 'workspace-grid preview-grid'}>
          {mode === 'design' && !shared && definition && (
            <TemplateFieldPanel
              definition={definition}
              disabled={busy}
              onInsert={insertBinding}
            />
          )}
          <div
            className={shared && result === null ? 'sheet-frame shared-sheet-pending' : 'sheet-frame'}
            inert={shared && result === null ? true : undefined}
          >
            {shared && initialized && (
              <div className="shared-sheet-actions">
                {pager}
                <button
                  className="icon-button shared-tools-toggle"
                  type="button"
                  title={sharedToolsExpanded ? '收起表格工具栏' : '展开表格工具栏'}
                  aria-label={sharedToolsExpanded ? '收起表格工具栏' : '展开表格工具栏'}
                  aria-pressed={sharedToolsExpanded}
                  onClick={() => setSharedToolsExpanded((expanded) => !expanded)}
                >
                  {sharedToolsExpanded
                    ? <PanelTopClose size={17} />
                    : <PanelTopOpen size={17} />}
                </button>
              </div>
            )}
            {initialized && templateDocument ? (
              <UniverTemplateHost
                ref={editorRef}
                initialSnapshot={templateDocument.snapshot}
                mode={mode}
                reportName={definition?.name}
                toolsExpanded={!shared || sharedToolsExpanded}
                suppressNumericTextWarnings={shared}
                onChanged={handleEditorChanged}
                onRendered={handleEditorRendered}
                onError={handleEditorError}
              />
            ) : (
              <div className="loading-sheet">
                {loading ? <LoaderCircle className="spin" size={22} /> : '模板未加载'}
              </div>
            )}
          </div>
        </div>
      </section>

      {!shared && (
        <>
          <ReportConfigurationDialog
            open={configurationOpen}
            document={configurationDocument}
            saveConfiguration={reportApi.saveConfiguration}
            onClose={() => setConfigurationOpen(false)}
            onSaved={configurationSaved}
          />
          <ShareReportDialog
            open={shareOpen}
            reportName={definition?.name ?? reportCode}
            url={shareUrl}
            includeParameters={shareIncludesParameters}
            parameters={shareParameterSummaries}
            onIncludeParametersChange={setShareIncludesParameters}
            onCopy={() => void copyShareLink()}
            onClose={() => setShareOpen(false)}
          />
        </>
      )}
    </main>
  );
}
