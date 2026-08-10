import { useEffect, useMemo, useState } from 'react';
import {
  ArrowDown,
  ArrowUp,
  Columns3,
  Database,
  ExternalLink,
  LoaderCircle,
  Plus,
  Save,
  SlidersHorizontal,
  Trash2,
  X,
} from 'lucide-react';
import { reportApi } from './api';
import type {
  ParameterDefinition,
  ReportConfigurationDocument,
  SaveReportConfigurationRequest,
} from './types';

type ConfigurationTab = 'basic' | 'parameters' | 'columns';

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
    datasetCode: document.datasetCode,
    parameters: document.parameters.map((parameter) => ({ ...parameter })),
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

function moveItem<T>(items: readonly T[], index: number, offset: -1 | 1): T[] {
  const target = index + offset;
  if (target < 0 || target >= items.length) return [...items];
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
  const [tab, setTab] = useState<ConfigurationTab>('basic');
  const [draft, setDraft] = useState<SaveReportConfigurationRequest | null>(null);
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      setDraft(null);
      setTab('basic');
      setStatus(null);
      setError(null);
      return;
    }
    if (document !== null && draft === null) setDraft(toDraft(document));
  }, [document, draft, open]);

  useEffect(() => {
    if (!open) return undefined;
    const previousOverflow = globalThis.document.documentElement.style.overflow;
    globalThis.document.documentElement.style.overflow = 'hidden';
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !saving) onClose();
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => {
      window.removeEventListener('keydown', closeOnEscape);
      globalThis.document.documentElement.style.overflow = previousOverflow;
    };
  }, [onClose, open, saving]);

  const selectedDataset = useMemo(
    () => document?.datasets.find((dataset) => dataset.code === draft?.datasetCode) ?? null,
    [document?.datasets, draft?.datasetCode],
  );
  const parameterCodes = useMemo(
    () => new Set(draft?.parameters.map((parameter) => parameter.code.toLowerCase()) ?? []),
    [draft?.parameters],
  );
  const missingParameters = selectedDataset?.parameterCodes.filter(
    (code) => !parameterCodes.has(code.toLowerCase()),
  ) ?? [];

  if (!open) return null;

  const updateParameter = (index: number, patch: Partial<ParameterDefinition>) => {
    setDraft((current) => {
      if (current === null) return current;
      const parameters = [...current.parameters];
      parameters[index] = { ...parameters[index], ...patch };
      return { ...current, parameters };
    });
  };

  const validateDraft = (): string | null => {
    if (draft === null) return '配置尚未加载完成。';
    if (!draft.name.trim() || !draft.sheetName.trim()) return '报表名称和工作表名称不能为空。';
    if (selectedDataset === null) return '请选择一个有效的数据集。';
    if (!selectedDataset.enabled) return '当前数据集已停用，不能用于报表。';
    if (parameterCodes.size !== draft.parameters.length) return '查询参数编码不能重复。';
    if (missingParameters.length > 0) {
      return `数据集需要参数：${missingParameters.join('、')}。`;
    }
    return null;
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
      setStatus(`报表配置已保存，当前修订号 ${saved.revision}`);
      onSaved(saved);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '报表配置保存失败。');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="configuration-backdrop" role="presentation">
      <section className="configuration-dialog" role="dialog" aria-modal="true" aria-labelledby="configuration-title">
        <header className="configuration-header">
          <div>
            <h2 id="configuration-title">报表配置</h2>
            <p>{document === null ? '正在读取配置' : `${document.reportCode} · 修订号 ${document.revision}`}</p>
          </div>
          <button type="button" className="icon-button" title="关闭报表配置" aria-label="关闭报表配置" disabled={saving} onClick={onClose}>
            <X size={18} />
          </button>
        </header>

        <nav className="configuration-tabs" aria-label="配置类别">
          <button type="button" className={tab === 'basic' ? 'active' : ''} onClick={() => setTab('basic')}>
            <Database size={16} />基础设置
          </button>
          <button type="button" className={tab === 'parameters' ? 'active' : ''} onClick={() => setTab('parameters')}>
            <SlidersHorizontal size={16} />查询参数<span>{draft?.parameters.length ?? 0}</span>
          </button>
          <button type="button" className={tab === 'columns' ? 'active' : ''} onClick={() => setTab('columns')}>
            <Columns3 size={16} />数据字段<span>{selectedDataset?.columns.length ?? 0}</span>
          </button>
        </nav>

        <div className="configuration-body">
          {draft === null ? (
            <div className="configuration-loading"><LoaderCircle className="spin" size={22} />正在读取配置</div>
          ) : tab === 'basic' ? (
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

              <fieldset className="dataset-reference-fieldset">
                <legend>使用的数据集</legend>
                <div className="dataset-reference-row">
                  <label>
                    <span>数据集</span>
                    <select value={draft.datasetCode} onChange={(event) => setDraft({ ...draft, datasetCode: event.target.value })}>
                      {document?.datasets.map((dataset) => (
                        <option key={dataset.code} value={dataset.code} disabled={!dataset.enabled}>
                          {dataset.name}（{dataset.code}）{dataset.enabled ? '' : ' · 已停用'}
                        </option>
                      ))}
                    </select>
                  </label>
                  <button type="button" className="button" onClick={() => window.location.assign(`/data-assets${window.location.search}`)}>
                    <ExternalLink size={16} />管理数据资源
                  </button>
                </div>
                {selectedDataset !== null && (
                  <div className="dataset-reference-meta">
                    <span>数据源：{selectedDataset.dataSourceCode}</span>
                    <span>字段：{selectedDataset.columns.length}</span>
                    <span>SQL 参数：{selectedDataset.parameterCodes.length === 0 ? '无' : selectedDataset.parameterCodes.join('、')}</span>
                  </div>
                )}
                {missingParameters.length > 0 && <p className="inline-warning">尚未配置参数：{missingParameters.join('、')}</p>}
              </fieldset>

              <fieldset>
                <legend>导出策略</legend>
                <div className="configuration-form-grid compact-grid">
                  <label><span>默认导出</span><select value={draft.export.defaultMode} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, defaultMode: event.target.value as 'Normal' | 'Large' } })}><option value="Normal">普通导出</option><option value="Large">大文件导出</option></select></label>
                  <label><span>普通导出最大行数</span><input type="number" min="1" max="1000000" value={draft.export.normalMaxRows} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, normalMaxRows: Number(event.target.value) } })} /></label>
                  <label><span>单工作表最大行数</span><input type="number" min="1" max="1048576" value={draft.export.rowsPerSheet} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, rowsPerSheet: Number(event.target.value) } })} /></label>
                  <label className="toggle-field"><span>大文件导出</span><input type="checkbox" checked={draft.export.largeExportEnabled} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, largeExportEnabled: event.target.checked } })} /></label>
                </div>
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
            <div className="configuration-table-section read-only-columns">
              <div className="configuration-section-bar"><span>字段由数据集统一维护，报表中只读引用。</span></div>
              <div className="configuration-table-scroll">
                <table className="configuration-table column-table">
                  <thead><tr><th>顺序</th><th>字段编码</th><th>列标题</th><th>数据类型</th><th>列宽</th><th>公式模板</th></tr></thead>
                  <tbody>{selectedDataset?.columns.map((column, index) => <tr key={column.code}><td>{index + 1}</td><td>{column.code}</td><td>{column.header}</td><td>{column.dataType}</td><td>{column.width}</td><td>{column.formulaTemplate ?? '—'}</td></tr>)}</tbody>
                </table>
              </div>
            </div>
          )}
        </div>

        {error !== null && <div className="configuration-message error">{error}</div>}
        {status !== null && <div className="configuration-message success">{status}</div>}
        <footer className="configuration-footer"><div><button type="button" className="button" disabled={saving} onClick={onClose}>取消</button><button type="button" className="button button-primary" disabled={saving || draft === null} onClick={() => void save()}>{saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存配置</button></div></footer>
      </section>
    </div>
  );
}
