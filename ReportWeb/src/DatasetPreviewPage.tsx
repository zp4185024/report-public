import { useEffect, useMemo, useState } from 'react';
import { ArrowLeft, Database, LoaderCircle, Play, PlugZap, Save } from 'lucide-react';
import { dataAssetApi } from './api';
import { externalParameterDocumentContext } from './externalParameterContext';
import { StatusToasts } from './StatusToasts';
import type {
  DatasetDocument,
  DatasetFieldDefinition,
  DatasetPreviewResult,
  DatasetVariableDefinition,
  ExternalParameterSourceDocument,
} from './types';

function backUrl(): string {
  const search = new URLSearchParams(window.location.search);
  search.delete('datasetCode');
  const query = search.toString();
  return `/data-assets${query ? `?${query}` : ''}`;
}

function valueFromUrl(variable: DatasetVariableDefinition): string {
  return new URLSearchParams(window.location.search).get(variable.code) ?? '';
}

function inputType(variable: DatasetVariableDefinition): 'date' | 'number' | 'text' {
  if (variable.dataType === 'date') return 'date';
  if (['int', 'long', 'decimal'].includes(variable.dataType)) return 'number';
  return 'text';
}

function mergeFieldNames(
  previewFields: readonly DatasetFieldDefinition[],
  savedFields: readonly DatasetFieldDefinition[],
): DatasetFieldDefinition[] {
  return previewFields.map((field) => {
    const saved = savedFields.find((item) => item.code.toLowerCase() === field.code.toLowerCase());
    return saved === undefined ? field : { ...field, displayName: saved.displayName };
  });
}

export default function DatasetPreviewPage() {
  const [datasets, setDatasets] = useState<DatasetDocument[]>([]);
  const [externalSources, setExternalSources] = useState<ExternalParameterSourceDocument[]>([]);
  const [datasetCode, setDatasetCode] = useState(
    () => new URLSearchParams(window.location.search).get('datasetCode') ?? '',
  );
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<DatasetPreviewResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [previewing, setPreviewing] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const [next, nextExternalSources] = await Promise.all([
          dataAssetApi.datasets(),
          dataAssetApi.externalParameterSources(),
        ]);
        if (!active) return;
        setDatasets(next);
        setExternalSources(nextExternalSources);
        const requested = next.find((dataset) => dataset.code === datasetCode && dataset.enabled);
        setDatasetCode(requested?.code ?? next.find((dataset) => dataset.enabled)?.code ?? next[0]?.code ?? '');
      } catch (exception) {
        if (active) setError(exception instanceof Error ? exception.message : '数据集加载失败。');
      } finally {
        if (active) setLoading(false);
      }
    };
    void load();
    return () => { active = false; };
  }, []);

  const dataset = useMemo(
    () => datasets.find((item) => item.code === datasetCode) ?? null,
    [datasetCode, datasets],
  );
  const synchronizedFields = useMemo(
    () => result === null || dataset === null
      ? []
      : mergeFieldNames(result.fields, dataset.fields),
    [dataset, result],
  );
  const fieldsChanged = dataset !== null && result !== null &&
    JSON.stringify(dataset.fields) !== JSON.stringify(synchronizedFields);

  const selectDataset = (code: string) => {
    setDatasetCode(code);
    setValues({});
    setResult(null);
    setError(null);
    setStatus(null);
    const search = new URLSearchParams(window.location.search);
    search.set('datasetCode', code);
    window.history.replaceState(null, '', `${window.location.pathname}?${search.toString()}`);
  };

  const preview = async () => {
    if (dataset === null) return;
    setPreviewing(true);
    setError(null);
    setStatus(null);
    try {
      const parameters = Object.fromEntries(dataset.variables
        .filter((variable) => variable.sourceType !== 'External')
        .map((variable) => {
        const value = values[variable.code] ?? valueFromUrl(variable);
        return [variable.code, value.trim() === '' ? null : value];
      }));
      const next = await dataAssetApi.previewDataset(dataset.code, {
        parameters,
        context: externalParameterDocumentContext(externalSources),
      });
      setResult(next);
      setStatus(`返回 ${next.rows.length} 行，耗时 ${next.durationMilliseconds} ms`);
    } catch (exception) {
      setResult(null);
      setError(exception instanceof Error ? exception.message : '数据预览失败。');
    } finally {
      setPreviewing(false);
    }
  };

  const syncFields = async () => {
    if (dataset === null || result === null || !fieldsChanged) return;
    setSyncing(true);
    setError(null);
    try {
      const saved = await dataAssetApi.saveDataset({ ...dataset, fields: synchronizedFields });
      setDatasets((current) => current.map((item) => item.code === saved.code ? saved : item));
      setStatus(`已保存 ${saved.fields.length} 个返回字段，数据集修订号 ${saved.revision}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '返回字段保存失败。');
    } finally {
      setSyncing(false);
    }
  };

  return (
    <main className="dataset-preview-page">
      <header className="asset-header">
        <div className="asset-title-row">
          <a className="icon-button" href={backUrl()} title="返回数据集" aria-label="返回数据集"><ArrowLeft size={18} /></a>
          <div><span className="app-kicker">DATASET</span><h1>数据预览</h1></div>
        </div>
        <div className="runtime-status"><span className="status-dot" />只读查询</div>
      </header>

      <section className="dataset-preview-toolbar">
        <label><span>数据集</span><select disabled={loading || previewing} value={datasetCode} onChange={(event) => selectDataset(event.target.value)}>{datasets.map((item) => <option key={item.code} value={item.code} disabled={!item.enabled}>{item.name}（{item.code}）</option>)}</select></label>
        {dataset !== null && <div className="dataset-preview-meta"><span><Database size={14} />{dataset.dataSourceCode}</span><span>{dataset.fields.length} 个已保存返回字段</span></div>}
        <div className="dataset-preview-actions"><button type="button" className="button button-primary" disabled={dataset === null || previewing || syncing} onClick={() => void preview()}>{previewing ? <LoaderCircle className="spin" size={16} /> : <Play size={16} />}运行预览</button><button type="button" className="button" disabled={!fieldsChanged || previewing || syncing} onClick={() => void syncFields()}>{syncing ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存返回字段</button></div>
      </section>

      {dataset !== null && dataset.variables.length > 0 && <section className="dataset-preview-query">
        {dataset.variables.filter((variable) => variable.sourceType !== 'External').map((variable) => <label key={variable.code}><span>{variable.displayName}（@{variable.code}）{variable.required ? ' *' : ''}</span>{variable.dataType === 'bool' ? <select value={values[variable.code] ?? valueFromUrl(variable)} onChange={(event) => { setValues({ ...values, [variable.code]: event.target.value }); setResult(null); }}><option value="">NULL</option><option value="true">是</option><option value="false">否</option></select> : <input type={inputType(variable)} value={values[variable.code] ?? valueFromUrl(variable)} onChange={(event) => { setValues({ ...values, [variable.code]: event.target.value }); setResult(null); }} />}</label>)}
        {dataset.variables.filter((variable) => variable.sourceType === 'External').map((variable) => <div className="dataset-external-variable" key={variable.code}><PlugZap size={15} /><span><strong>{variable.displayName}</strong><small>@{variable.code} · {variable.sourceCode}.{variable.sourceFieldCode}</small></span></div>)}
      </section>}

      <StatusToasts
        error={error}
        success={status}
        onErrorClose={() => setError(null)}
        onSuccessClose={() => setStatus(null)}
      />

      <section className="dataset-preview-result">
        {loading ? <div className="asset-empty"><LoaderCircle className="spin" size={20} />正在读取</div> : result === null ? <div className="asset-empty">暂无预览结果</div> : <div className="dataset-preview-scroll standalone"><table className="dataset-preview-table"><thead><tr><th>#</th>{result.fields.map((field) => <th key={field.code}>{field.code}</th>)}</tr></thead><tbody>{result.rows.length === 0 ? <tr><td colSpan={result.fields.length + 1} className="dataset-preview-empty">暂无数据</td></tr> : result.rows.map((row, rowIndex) => <tr key={rowIndex}><td>{rowIndex + 1}</td>{result.fields.map((field, columnIndex) => <td key={field.code} title={row[columnIndex] === null ? '' : String(row[columnIndex])}>{row[columnIndex] === null ? <span className="null-value">NULL</span> : String(row[columnIndex])}</td>)}</tr>)}</tbody></table>{result.hasMore && <div className="dataset-preview-limit">仅显示前 {result.limit} 行</div>}</div>}
      </section>
    </main>
  );
}
