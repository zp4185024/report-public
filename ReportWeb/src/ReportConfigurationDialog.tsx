import { useEffect, useMemo, useState } from 'react';
import {
  ArrowDown,
  ArrowUp,
  Columns3,
  Database,
  LoaderCircle,
  Plus,
  Save,
  SlidersHorizontal,
  Trash2,
  X,
  Zap,
} from 'lucide-react';
import { reportApi } from './api';
import type {
  ColumnDefinition,
  ParameterDefinition,
  ReportConfigurationDocument,
  SaveReportConfigurationRequest,
} from './types';

type ConfigurationTab = 'source' | 'parameters' | 'columns';

interface ReportConfigurationDialogProps {
  open: boolean;
  document: ReportConfigurationDocument | null;
  onClose: () => void;
  onSaved: (document: ReportConfigurationDocument) => void;
}

function toDraft(document: ReportConfigurationDocument): SaveReportConfigurationRequest {
  return {
    name: document.name,
    sheetName: document.sheetName,
    preview: { ...document.preview },
    export: { ...document.export },
    dataSource: {
      code: document.dataSource.code,
      name: document.dataSource.name,
      dbType: document.dataSource.dbType,
      server: document.dataSource.server,
      port: document.dataSource.port,
      database: document.dataSource.database,
      userName: document.dataSource.userName,
      password: null,
      timeoutSeconds: document.dataSource.timeoutSeconds,
      maxRows: document.dataSource.maxRows,
      enabled: document.dataSource.enabled,
    },
    dataset: { ...document.dataset },
    parameters: document.parameters.map((parameter) => ({ ...parameter })),
    columns: document.columns.map((column) => ({
      ...column,
      dataType: column.dataType === 'text' ? 'string' : column.dataType,
    })),
    revision: document.revision,
  };
}

function createParameter(index: number): ParameterDefinition {
  return {
    code: `parameter${index + 1}`,
    displayName: `查询参数 ${index + 1}`,
    controlType: 'text',
    sourceType: 'Form',
    defaultValue: null,
    visible: true,
    dataType: 'string',
    defaultExpression: null,
    fixedValue: null,
    readOnly: false,
    required: false,
    allowUrlPrefill: false,
    urlKey: null,
  };
}

function createColumn(index: number): ColumnDefinition {
  return {
    code: `field${index + 1}`,
    header: `结果字段 ${index + 1}`,
    dataType: 'string',
    width: 120,
    formulaTemplate: null,
  };
}

function moveItem<T>(items: readonly T[], index: number, offset: -1 | 1): T[] {
  const target = index + offset;
  if (target < 0 || target >= items.length) {
    return [...items];
  }
  const next = [...items];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

export function ReportConfigurationDialog({
  open,
  document,
  onClose,
  onSaved,
}: ReportConfigurationDialogProps) {
  const [tab, setTab] = useState<ConfigurationTab>('source');
  const [draft, setDraft] = useState<SaveReportConfigurationRequest | null>(null);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      setDraft(null);
      setTab('source');
      setStatus(null);
      setError(null);
      return;
    }
    if (document !== null && draft === null) {
      setDraft(toDraft(document));
    }
  }, [document, draft, open]);

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const previousOverflow = documentElementStyle();
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !saving && !testing) {
        onClose();
      }
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => {
      window.removeEventListener('keydown', closeOnEscape);
      globalThis.document.documentElement.style.overflow = previousOverflow;
    };
  }, [onClose, open, saving, testing]);

  const parameterCodes = useMemo(
    () => new Set(draft?.parameters.map((parameter) => parameter.code) ?? []),
    [draft?.parameters],
  );

  if (!open) {
    return null;
  }

  const setSource = (
    patch: Partial<SaveReportConfigurationRequest['dataSource']>,
  ) => setDraft((current) => current === null
    ? current
    : { ...current, dataSource: { ...current.dataSource, ...patch } });

  const setDataset = (
    patch: Partial<SaveReportConfigurationRequest['dataset']>,
  ) => setDraft((current) => current === null
    ? current
    : { ...current, dataset: { ...current.dataset, ...patch } });

  const updateParameter = (index: number, patch: Partial<ParameterDefinition>) => {
    setDraft((current) => {
      if (current === null) return current;
      const parameters = [...current.parameters];
      parameters[index] = { ...parameters[index], ...patch };
      return { ...current, parameters };
    });
  };

  const updateColumn = (index: number, patch: Partial<ColumnDefinition>) => {
    setDraft((current) => {
      if (current === null) return current;
      const columns = [...current.columns];
      columns[index] = { ...columns[index], ...patch };
      return { ...current, columns };
    });
  };

  const validateDraft = (): string | null => {
    if (draft === null) return '配置尚未加载完成。';
    if (!draft.name.trim() || !draft.sheetName.trim()) return '报表名称和工作表名称不能为空。';
    if (!draft.dataSource.server.trim() || !draft.dataSource.database.trim() || !draft.dataSource.userName.trim()) {
      return '服务器、数据库和用户名不能为空。';
    }
    if (!draft.dataset.queryText.trim()) return '主数据集 SQL 不能为空。';
    if (draft.columns.length === 0) return '至少需要配置一个结果字段。';
    if (parameterCodes.size !== draft.parameters.length) return '查询参数编码不能重复。';
    if (new Set(draft.columns.map((column) => column.code)).size !== draft.columns.length) {
      return '结果字段编码不能重复。';
    }
    return null;
  };

  const testConnection = async () => {
    if (draft === null) return;
    setTesting(true);
    setError(null);
    setStatus(null);
    try {
      const result = await reportApi.testDataSource(draft.dataSource);
      setStatus(`连接成功，耗时 ${result.durationMilliseconds} ms，SQL Server ${result.databaseVersion}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据源连接失败。');
    } finally {
      setTesting(false);
    }
  };

  const save = async () => {
    const validationError = validateDraft();
    if (validationError !== null || draft === null) {
      setError(validationError);
      return;
    }
    setSaving(true);
    setError(null);
    setStatus(null);
    try {
      const saved = await reportApi.saveConfiguration(draft);
      setDraft(toDraft(saved));
      setStatus(`配置已保存，当前修订号 ${saved.revision}`);
      onSaved(saved);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '报表配置保存失败。');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="configuration-backdrop" role="presentation">
      <section
        className="configuration-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="configuration-title"
      >
        <header className="configuration-header">
          <div>
            <h2 id="configuration-title">报表配置</h2>
            <p>{document === null ? '正在读取配置' : `${document.reportCode} · 修订号 ${document.revision}`}</p>
          </div>
          <button
            type="button"
            className="icon-button"
            title="关闭报表配置"
            aria-label="关闭报表配置"
            disabled={saving || testing}
            onClick={onClose}
          >
            <X size={18} />
          </button>
        </header>

        <nav className="configuration-tabs" aria-label="配置类别">
          <button type="button" className={tab === 'source' ? 'active' : ''} onClick={() => setTab('source')}>
            <Database size={16} />数据源与 SQL
          </button>
          <button type="button" className={tab === 'parameters' ? 'active' : ''} onClick={() => setTab('parameters')}>
            <SlidersHorizontal size={16} />查询参数
            <span>{draft?.parameters.length ?? 0}</span>
          </button>
          <button type="button" className={tab === 'columns' ? 'active' : ''} onClick={() => setTab('columns')}>
            <Columns3 size={16} />结果字段
            <span>{draft?.columns.length ?? 0}</span>
          </button>
        </nav>

        <div className="configuration-body">
          {draft === null ? (
            <div className="configuration-loading"><LoaderCircle className="spin" size={22} />正在读取配置</div>
          ) : tab === 'source' ? (
            <div className="configuration-source">
              <fieldset>
                <legend>报表信息</legend>
                <div className="configuration-form-grid compact-grid">
                  <label><span>报表名称</span><input value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} /></label>
                  <label><span>工作表名称</span><input value={draft.sheetName} onChange={(event) => setDraft({ ...draft, sheetName: event.target.value })} /></label>
                  <label><span>预览模式</span><select value={draft.preview.mode} onChange={(event) => setDraft({ ...draft, preview: { ...draft.preview, mode: event.target.value as 'Full' | 'Paged' } })}><option value="Full">整表预览</option><option value="Paged">分页预览</option></select></label>
                  <label><span>每页行数</span><input type="number" min="1" max="10000" value={draft.preview.pageSize} onChange={(event) => setDraft({ ...draft, preview: { ...draft.preview, pageSize: Number(event.target.value) } })} /></label>
                </div>
              </fieldset>

              <fieldset>
                <legend>SQL Server 连接</legend>
                <div className="configuration-form-grid">
                  <label><span>数据源名称</span><input value={draft.dataSource.name} onChange={(event) => setSource({ name: event.target.value })} /></label>
                  <label className="wide-field"><span>服务器</span><input value={draft.dataSource.server} onChange={(event) => setSource({ server: event.target.value })} /></label>
                  <label><span>端口</span><input type="number" min="1" max="65535" value={draft.dataSource.port} onChange={(event) => setSource({ port: Number(event.target.value) })} /></label>
                  <label><span>数据库</span><input value={draft.dataSource.database} onChange={(event) => setSource({ database: event.target.value })} /></label>
                  <label><span>用户名</span><input autoComplete="username" value={draft.dataSource.userName} onChange={(event) => setSource({ userName: event.target.value })} /></label>
                  <label><span>新密码</span><input type="password" autoComplete="new-password" placeholder={document?.dataSource.passwordConfigured ? '留空保留原密码' : '请输入密码'} value={draft.dataSource.password ?? ''} onChange={(event) => setSource({ password: event.target.value || null })} /></label>
                  <label><span>连接超时（秒）</span><input type="number" min="1" max="120" value={draft.dataSource.timeoutSeconds} onChange={(event) => setSource({ timeoutSeconds: Number(event.target.value) })} /></label>
                  <label><span>最大返回行数</span><input type="number" min="1" max="1000000" value={draft.dataSource.maxRows} onChange={(event) => setSource({ maxRows: Number(event.target.value) })} /></label>
                </div>
                <button type="button" className="button source-test-button" disabled={testing || saving} onClick={() => void testConnection()}>
                  {testing ? <LoaderCircle className="spin" size={16} /> : <Zap size={16} />}
                  测试连接
                </button>
              </fieldset>

              <fieldset className="dataset-fieldset">
                <legend>主数据集</legend>
                <div className="configuration-form-grid compact-grid">
                  <label><span>数据集名称</span><input value={draft.dataset.name} onChange={(event) => setDataset({ name: event.target.value })} /></label>
                  <label><span>查询超时（秒）</span><input type="number" min="1" max="600" value={draft.dataset.timeoutSeconds} onChange={(event) => setDataset({ timeoutSeconds: Number(event.target.value) })} /></label>
                  <label><span>最大返回行数</span><input type="number" min="1" max="1000000" value={draft.dataset.maxRows} onChange={(event) => setDataset({ maxRows: Number(event.target.value) })} /></label>
                </div>
                <label className="sql-field">
                  <span>参数化只读 SQL</span>
                  <textarea spellCheck={false} value={draft.dataset.queryText} onChange={(event) => setDataset({ queryText: event.target.value })} />
                </label>
              </fieldset>
            </div>
          ) : tab === 'parameters' ? (
            <div className="configuration-table-section">
              <div className="configuration-section-bar">
                <button type="button" className="button" onClick={() => setDraft({ ...draft, parameters: [...draft.parameters, createParameter(draft.parameters.length)] })}><Plus size={16} />新增参数</button>
              </div>
              <div className="configuration-table-scroll">
                <table className="configuration-table parameter-table">
                  <thead><tr><th>顺序</th><th>参数编码</th><th>显示名称</th><th>数据类型</th><th>来源</th><th>控件</th><th>默认表达式 / 固定值</th><th>选项</th><th>操作</th></tr></thead>
                  <tbody>
                    {draft.parameters.map((parameter, index) => (
                      <tr key={`${index}-${parameter.code}`}>
                        <td><div className="row-order"><button type="button" title="上移" aria-label="上移参数" disabled={index === 0} onClick={() => setDraft({ ...draft, parameters: moveItem(draft.parameters, index, -1) })}><ArrowUp size={14} /></button><button type="button" title="下移" aria-label="下移参数" disabled={index === draft.parameters.length - 1} onClick={() => setDraft({ ...draft, parameters: moveItem(draft.parameters, index, 1) })}><ArrowDown size={14} /></button></div></td>
                        <td><input className="code-input" value={parameter.code} onChange={(event) => updateParameter(index, { code: event.target.value })} /></td>
                        <td><input value={parameter.displayName} onChange={(event) => updateParameter(index, { displayName: event.target.value })} /></td>
                        <td><select value={parameter.dataType} onChange={(event) => updateParameter(index, { dataType: event.target.value })}><option value="string">文本</option><option value="int">整数</option><option value="long">长整数</option><option value="decimal">小数</option><option value="date">日期</option><option value="datetime">日期时间</option><option value="bool">布尔值</option></select></td>
                        <td><select value={parameter.sourceType} onChange={(event) => updateParameter(index, { sourceType: event.target.value, readOnly: event.target.value !== 'Form' })}><option value="Form">查询表单</option><option value="System">系统默认</option><option value="Fixed">固定值</option></select></td>
                        <td><select value={parameter.controlType} onChange={(event) => updateParameter(index, { controlType: event.target.value })}><option value="text">文本框</option><option value="number">数字框</option><option value="date">日期框</option><option value="checkbox">是/否</option><option value="hidden">隐藏</option></select></td>
                        <td><input value={(parameter.sourceType === 'Fixed' ? parameter.fixedValue : parameter.defaultExpression) ?? ''} placeholder={parameter.sourceType === 'Fixed' ? '固定值' : '如 startOfMonth'} onChange={(event) => parameter.sourceType === 'Fixed' ? updateParameter(index, { fixedValue: event.target.value || null }) : updateParameter(index, { defaultExpression: event.target.value || null })} /></td>
                        <td><div className="flag-list"><label><input type="checkbox" checked={parameter.required} onChange={(event) => updateParameter(index, { required: event.target.checked })} />必填</label><label><input type="checkbox" checked={parameter.visible} onChange={(event) => updateParameter(index, { visible: event.target.checked })} />显示</label><label><input type="checkbox" checked={parameter.allowUrlPrefill} onChange={(event) => updateParameter(index, { allowUrlPrefill: event.target.checked, urlKey: event.target.checked ? parameter.urlKey || parameter.code : null })} />URL 预填</label></div></td>
                        <td><button type="button" className="table-icon danger" title="删除参数" aria-label={`删除参数 ${parameter.displayName}`} onClick={() => setDraft({ ...draft, parameters: draft.parameters.filter((_, itemIndex) => itemIndex !== index) })}><Trash2 size={15} /></button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          ) : (
            <div className="configuration-table-section">
              <div className="configuration-section-bar">
                <button type="button" className="button" onClick={() => setDraft({ ...draft, columns: [...draft.columns, createColumn(draft.columns.length)] })}><Plus size={16} />新增字段</button>
              </div>
              <div className="configuration-table-scroll">
                <table className="configuration-table column-table">
                  <thead><tr><th>顺序</th><th>字段编码</th><th>列标题</th><th>数据类型</th><th>列宽</th><th>公式模板</th><th>操作</th></tr></thead>
                  <tbody>
                    {draft.columns.map((column, index) => (
                      <tr key={`${index}-${column.code}`}>
                        <td><div className="row-order"><button type="button" title="上移" aria-label="上移字段" disabled={index === 0} onClick={() => setDraft({ ...draft, columns: moveItem(draft.columns, index, -1) })}><ArrowUp size={14} /></button><button type="button" title="下移" aria-label="下移字段" disabled={index === draft.columns.length - 1} onClick={() => setDraft({ ...draft, columns: moveItem(draft.columns, index, 1) })}><ArrowDown size={14} /></button></div></td>
                        <td><input className="code-input" value={column.code} onChange={(event) => updateColumn(index, { code: event.target.value })} /></td>
                        <td><input value={column.header} onChange={(event) => updateColumn(index, { header: event.target.value })} /></td>
                        <td><select value={column.dataType} onChange={(event) => updateColumn(index, { dataType: event.target.value, formulaTemplate: event.target.value === 'formula' ? column.formulaTemplate || '=A{row}' : null })}><option value="string">文本</option><option value="number">数字</option><option value="date">日期</option><option value="datetime">日期时间</option><option value="boolean">布尔值</option><option value="formula">公式</option></select></td>
                        <td><input className="width-input" type="number" min="40" max="500" value={column.width} onChange={(event) => updateColumn(index, { width: Number(event.target.value) })} /></td>
                        <td><input disabled={column.dataType !== 'formula'} value={column.formulaTemplate ?? ''} placeholder="=C{row}+D{row}" onChange={(event) => updateColumn(index, { formulaTemplate: event.target.value || null })} /></td>
                        <td><button type="button" className="table-icon danger" title="删除字段" aria-label={`删除字段 ${column.header}`} onClick={() => setDraft({ ...draft, columns: draft.columns.filter((_, itemIndex) => itemIndex !== index) })}><Trash2 size={15} /></button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>

        {(error || status) && <div className={error ? 'configuration-message error' : 'configuration-message success'} role={error ? 'alert' : 'status'}>{error ?? status}</div>}
        <footer className="configuration-footer">
          <div>
            <button type="button" className="button" disabled={saving || testing} onClick={onClose}>取消</button>
            <button type="button" className="button button-primary" disabled={draft === null || saving || testing} onClick={() => void save()}>
              {saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}
              保存配置
            </button>
          </div>
        </footer>
      </section>
    </div>
  );
}

function documentElementStyle(): string {
  const previousOverflow = document.documentElement.style.overflow;
  document.documentElement.style.overflow = 'hidden';
  return previousOverflow;
}
