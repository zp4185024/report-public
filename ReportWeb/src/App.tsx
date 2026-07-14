import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from 'react';
import type { IWorkbookData } from '@univerjs/presets';
import {
  ChevronLeft,
  ChevronRight,
  Eye,
  LoaderCircle,
  Pencil,
  Save,
  Search,
} from 'lucide-react';
import { reportApi } from './api';
import { scanTemplateBindings } from './templateBinding';
import { createTemplatePreviewPlan } from './templatePreview';
import { TemplateFieldPanel } from './TemplateFieldPanel';
import {
  UniverTemplateHost,
  type TemplateEditorMode,
  type UniverTemplateHandle,
} from './UniverTemplateHost';
import type {
  ExecuteRequest,
  ExecuteResult,
  ReportDefinition,
  TemplateDocument,
} from './types';

const urlParameters = new URLSearchParams(window.location.search);

const backendErrorMessages: Readonly<Record<string, string>> = {
  REPORT_DEFINITION_NOT_FOUND: '找不到报表定义',
  REPORT_TEMPLATE_NOT_FOUND: '找不到报表模板',
  TEMPLATE_BINDING_INVALID: '模板字段绑定校验失败',
  TEMPLATE_SNAPSHOT_INVALID: '模板内容校验失败',
};

function createInitialRequest(): ExecuteRequest {
  return {
    page: 1,
    hospitalId: urlParameters.get('hospitalId'),
    deptId: urlParameters.get('deptId'),
    personId: urlParameters.get('personId'),
    startDate: null,
    endDate: null,
  };
}

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
  const [previewing, setPreviewing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [queryDuration, setQueryDuration] = useState(0);
  const [renderDuration, setRenderDuration] = useState(0);

  const editorRef = useRef<UniverTemplateHandle>(null);
  const draftSnapshotRef = useRef<IWorkbookData | null>(null);
  const operationRef = useRef(false);

  useEffect(() => {
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

        const nextRequest: ExecuteRequest = {
          ...createInitialRequest(),
          startDate: nextDefinition.parameters.find((item) => item.code === 'startDate')?.defaultValue ?? null,
          endDate: nextDefinition.parameters.find((item) => item.code === 'endDate')?.defaultValue ?? null,
        };
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

    const warnBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warnBeforeUnload);
    return () => window.removeEventListener('beforeunload', warnBeforeUnload);
  }, [dirty]);

  const handleEditorChanged = useCallback(() => {
    setDirty(true);
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
    let designSnapshot: IWorkbookData | null = null;
    let renderAttempted = false;
    try {
      let snapshot: IWorkbookData;
      if (mode === 'design') {
        const editor = editorRef.current;
        if (editor === null) {
          throw new Error('模板编辑器尚未准备完成。');
        }
        snapshot = editor.saveDraft();
        designSnapshot = snapshot;
        draftSnapshotRef.current = snapshot;
        setDraftSnapshot(snapshot);
      } else {
        const savedDraft = draftSnapshotRef.current;
        if (savedDraft === null) {
          throw new Error('没有可用于数据预览的模板草稿。');
        }
        snapshot = savedDraft;
      }

      const binding = scanTemplateBindings(snapshot, definition);
      const nextRequest = { ...request, page };
      const queryStartedAt = performance.now();
      const nextResult = await reportApi.execute(nextRequest);
      setQueryDuration(performance.now() - queryStartedAt);

      const plan = createTemplatePreviewPlan(snapshot, binding, nextResult);
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

  const saveTemplate = useCallback(async () => {
    if (
      operationRef.current ||
      mode !== 'design' ||
      definition === null ||
      templateDocument === null
    ) {
      return;
    }

    operationRef.current = true;
    setSaving(true);
    setError(null);
    try {
      const editor = editorRef.current;
      if (editor === null) {
        throw new Error('模板编辑器尚未准备完成。');
      }
      const snapshot = editor.saveDraft();
      const binding = scanTemplateBindings(snapshot, definition);
      const savedDocument = await reportApi.saveTemplate({
        snapshot,
        binding,
        revision: templateDocument.revision,
      });

      draftSnapshotRef.current = savedDocument.snapshot;
      setTemplateDocument(savedDocument);
      setDraftSnapshot(savedDocument.snapshot);
      setDirty(false);
    } catch (exception) {
      setError(errorMessage(exception, '模板保存失败'));
    } finally {
      operationRef.current = false;
      setSaving(false);
    }
  }, [definition, mode, templateDocument]);

  const insertBinding = useCallback((token: string, kind: 'parameter' | 'detail') => {
    try {
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

  const updateRequest = useCallback((field: keyof ExecuteRequest, value: string) => {
    setRequest((current) => ({
      ...current,
      [field]: value || null,
    }));
  }, []);

  const handleSubmit = useCallback((event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    void enterPreview(1);
  }, [enterPreview]);

  const busy = loading || saving || previewing;
  const revision = templateDocument?.revision;

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
        <label>
          <span>医院</span>
          <input
            value={request.hospitalId ?? ''}
            onChange={(event) => updateRequest('hospitalId', event.target.value)}
            placeholder="全部医院"
          />
        </label>
        <label>
          <span>科室</span>
          <input
            value={request.deptId ?? ''}
            onChange={(event) => updateRequest('deptId', event.target.value)}
            placeholder="全部科室"
          />
        </label>
        <label>
          <span>人员</span>
          <input
            value={request.personId ?? ''}
            onChange={(event) => updateRequest('personId', event.target.value)}
            placeholder="全部人员"
          />
        </label>
        <label>
          <span>开始日期</span>
          <input
            type="date"
            value={request.startDate ?? ''}
            onChange={(event) => updateRequest('startDate', event.target.value)}
          />
        </label>
        <label>
          <span>结束日期</span>
          <input
            type="date"
            value={request.endDate ?? ''}
            onChange={(event) => updateRequest('endDate', event.target.value)}
          />
        </label>
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

      <section className="report-workspace">
        <div className="report-toolbar">
          {mode === 'preview' ? (
            <div className="report-stats">
              <span>第 {result?.page ?? 1} 页</span>
              <span>{result?.rows.length ?? 0} 行</span>
            </div>
          ) : (
            <div className="design-status">
              <span className="formula-dot" />
              模板编辑
            </div>
          )}

          {mode === 'preview' && (
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
    </main>
  );
}
