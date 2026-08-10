import {
  useCallback,
  useEffect,
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
  LoaderCircle,
  Pencil,
  Save,
  Search,
  Settings2,
  X,
} from 'lucide-react';
import { reportApi } from './api';
import {
  prepareSavedRevisionForExport,
  saveBeforeDataExportMessage,
  selectSnapshotForSave,
  waitForLargeExportJob,
} from './dataExportFlow';
import { scanTemplateBindings } from './templateBinding';
import { createTemplatePreviewPlan } from './templatePreview';
import { createPreviewToolbarPresentation } from './previewToolbar';
import { TemplateFieldPanel } from './TemplateFieldPanel';
import { ReportConfigurationDialog } from './ReportConfigurationDialog';
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

// URL 参数在应用启动时读取一次。它们是初始查询条件，之后输入框状态由 React 管理。
const urlParameters = new URLSearchParams(window.location.search);

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
};

/** 根据后台定义建立参数字典；只有明确允许 URL 预填的参数才读取查询字符串。 */
function createInitialRequest(definition?: ReportDefinition): ExecuteRequest {
  const parameters: Record<string, string | null> = {};
  for (const parameter of definition?.parameters ?? []) {
    const urlValue = parameter.allowUrlPrefill
      ? urlParameters.get(parameter.urlKey || parameter.code)
      : null;
    parameters[parameter.code] = urlValue ?? parameter.defaultValue ?? parameter.fixedValue;
  }
  return { page: 1, parameters };
}

function toReportDefinition(configuration: ReportConfigurationDocument): ReportDefinition {
  return {
    code: configuration.reportCode,
    name: configuration.name,
    sheetName: configuration.sheetName,
    preview: configuration.preview,
    export: configuration.export,
    parameters: configuration.parameters,
    columns: configuration.columns,
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

export default function App() {
  // 后台持久状态和查询结果使用 React state，负责界面渲染。
  const [definition, setDefinition] = useState<ReportDefinition | null>(null);
  const [templateDocument, setTemplateDocument] = useState<TemplateDocument | null>(null);
  const [request, setRequest] = useState<ExecuteRequest>(createInitialRequest);
  const [result, setResult] = useState<ExecuteResult | null>(null);
  const [mode, setMode] = useState<TemplateEditorMode>('design');
  const [draftSnapshot, setDraftSnapshot] = useState<IWorkbookData | null>(null);
  const [dirty, setDirty] = useState(false);
  const [initialized, setInitialized] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [importing, setImporting] = useState(false);
  const [exportingTemplate, setExportingTemplate] = useState(false);
  const [exportingData, setExportingData] = useState(false);
  const [cancellingExport, setCancellingExport] = useState(false);
  const [exportJob, setExportJob] = useState<ExportJob | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [exchangeStatus, setExchangeStatus] = useState<string | null>(null);
  const [queryDuration, setQueryDuration] = useState(0);
  const [renderDuration, setRenderDuration] = useState(0);
  const [configurationOpen, setConfigurationOpen] = useState(false);
  const [configurationDocument, setConfigurationDocument] = useState<ReportConfigurationDocument | null>(null);

  // editorRef 是页面调用 Univer 的窄接口；draftSnapshotRef 保存“最后设计草稿”的同步事实来源。
  // 同一快照另存 state 是为了触发 React 生命周期；异步回调使用 ref 可避免读到闭包中的旧值。
  const editorRef = useRef<UniverTemplateHandle>(null);
  const templateFileInputRef = useRef<HTMLInputElement>(null);
  const draftSnapshotRef = useRef<IWorkbookData | null>(null);
  // 保存、预览、翻页和返回设计共享一个互斥标记，防止两个异步工作簿替换流程交叉执行。
  const operationRef = useRef(false);

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
        const nextRequest = createInitialRequest(nextDefinition);
        draftSnapshotRef.current = nextTemplateDocument.snapshot;
        setDefinition(nextDefinition);
        setTemplateDocument(nextTemplateDocument);
        setDraftSnapshot(nextTemplateDocument.snapshot);
        setRequest(nextRequest);
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
  }, []);

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
    setDirty(true);
    setExchangeStatus(null);
  }, []);

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
      const nextRequest = { ...request, page };
      const queryStartedAt = performance.now();
      const nextResult = await reportApi.execute(nextRequest);
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
  }, [definition, initialized, mode, request]);

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

  // 所有会切换或序列化工作簿的命令共用 busy，UI 禁用状态与 operationRef 的程序防重保持一致。
  const busy = loading || saving || importing || exportingTemplate || exportingData || previewing;
  const revision = templateDocument?.revision;
  const previewToolbar = createPreviewToolbarPresentation(
    definition?.preview.mode ?? 'Full',
    result,
  );

  return (
    <main className="app-shell">
      <header className="app-header">
        <div className="report-heading">
          <span className="app-kicker">报表模板</span>
          <h1>{definition?.name ?? '科室工作量报表'}</h1>
        </div>
        <div className="runtime-status" title="公式由 Univer Web Worker 计算">
          <span className="status-dot" />
          Univer Worker
        </div>
      </header>

      <div className="command-bar" aria-label="模板命令栏">
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
            {previewing ? <LoaderCircle className="spin" size={15} /> : <Eye size={15} />}
            数据预览
          </button>
        </div>

        <div className="command-actions">
          {mode === 'design' && (
            <>
              <button
                className="button configuration-button"
                type="button"
                title="配置查询参数、数据源和结果字段"
                disabled={busy || !initialized}
                onClick={() => void openConfiguration()}
              >
                <Settings2 size={16} />
                报表配置
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
                className="button exchange-button"
                type="button"
                title="导入 XLSX 作为当前模板草稿"
                disabled={busy || !initialized}
                onClick={chooseTemplateFile}
              >
                {importing ? <LoaderCircle className="spin" size={16} /> : <FileUp size={16} />}
                导入模板
              </button>
              <button
                className="button exchange-button"
                type="button"
                title="把当前模板草稿导出为 XLSX"
                disabled={busy || !initialized}
                onClick={() => void exportTemplate()}
              >
                {exportingTemplate ? <LoaderCircle className="spin" size={16} /> : <FileDown size={16} />}
                导出模板
              </button>
            </>
          )}
          <button
            className="button data-export-button"
            type="button"
            title="使用已保存模板导出全部查询数据"
            disabled={busy || !initialized}
            onClick={() => void exportData()}
          >
            {exportingData
              ? <LoaderCircle className="spin" size={16} />
              : <FileSpreadsheet size={16} />}
            导出数据
          </button>
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
          {mode === 'design' && (
              <button
                className="button save-button"
                type="button"
                title="保存当前模板草稿"
                disabled={busy || !initialized}
                onClick={() => void saveTemplate()}
              >
                {saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}
                保存模板
              </button>
          )}
          <div className="template-state" title={dirty ? '模板有未保存修改' : '模板已保存'}>
            <span>{revision === undefined ? 'revision --' : `revision ${revision}`}</span>
            <span className={dirty ? 'dirty-state' : 'saved-state'}>
              {dirty ? '未保存' : '已保存'}
            </span>
            {dirty && <span className="dirty-dot" aria-label="有未保存修改" />}
          </div>
          <div className="duration-state">
            <span>查询 {queryDuration.toFixed(0)} ms</span>
            <span>渲染 {renderDuration.toFixed(0)} ms</span>
          </div>
        </div>
      </div>

      <form className="query-band" onSubmit={handleSubmit}>
        {definition?.parameters
          .filter((parameter) => parameter.visible && parameter.controlType !== 'hidden')
          .map((parameter) => (
            <label key={parameter.code}>
              <span>{parameter.displayName}</span>
              {parameter.dataType === 'bool' || parameter.controlType === 'checkbox' ? (
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
                  type={parameter.controlType === 'date' || parameter.dataType === 'date'
                    ? 'date'
                    : parameter.dataType === 'long' || parameter.dataType === 'int' || parameter.dataType === 'decimal'
                      ? 'number'
                      : 'text'}
                  value={request.parameters[parameter.code] ?? ''}
                  readOnly={parameter.readOnly}
                  required={parameter.required}
                  onChange={(event) => updateRequest(parameter.code, event.target.value)}
                />
              )}
            </label>
          ))}
        <div className="query-actions">
          <button
            className="button button-primary query-button"
            type="submit"
            disabled={busy || !initialized}
            title={mode === 'design' ? '使用当前模板草稿预览数据' : '重新查询当前页数据'}
          >
            {previewing
              ? <LoaderCircle className="spin" size={16} />
              : mode === 'design'
                ? <Eye size={16} />
                : <Search size={16} />}
            {mode === 'design' ? '预览' : '查询'}
          </button>
        </div>
      </form>

      {error && <div className="error-strip" role="alert">{error}</div>}
      {exchangeStatus && <div className="success-strip" role="status">{exchangeStatus}</div>}

      <section className="report-workspace">
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

          {mode === 'preview' && previewToolbar.showPager && (
            <div className="pager">
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
          )}
        </div>

        <div className={mode === 'design' ? 'workspace-grid design-grid' : 'workspace-grid preview-grid'}>
          {mode === 'design' && definition && (
            <TemplateFieldPanel
              definition={definition}
              disabled={busy}
              onInsert={insertBinding}
            />
          )}
          <div className="sheet-frame">
            {initialized && templateDocument ? (
              <UniverTemplateHost
                ref={editorRef}
                initialSnapshot={templateDocument.snapshot}
                mode={mode}
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

      <ReportConfigurationDialog
        open={configurationOpen}
        document={configurationDocument}
        onClose={() => setConfigurationOpen(false)}
        onSaved={configurationSaved}
      />
    </main>
  );
}
