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

/**
 * 负责 DatasetPreviewPage 的 back Url 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function backUrl(): string {
  const search = new URLSearchParams(window.location.search);
  search.delete('datasetCode');
  const query = search.toString();
  return `/data-assets${query ? `?${query}` : ''}`;
}

/**
 * 创建并返回 DatasetPreviewPage 的 value From Url 初始数据，避免不同调用方共享可变对象。
 * @param variable - variable 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function valueFromUrl(variable: DatasetVariableDefinition): string {
  return new URLSearchParams(window.location.search).get(variable.code) ?? '';
}

/**
 * 负责 DatasetPreviewPage 的 input Type 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param variable - variable 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function inputType(variable: DatasetVariableDefinition): 'date' | 'number' | 'text' {
  if (variable.dataType === 'date') return 'date';
  if (['int', 'long', 'decimal'].includes(variable.dataType)) return 'number';
  return 'text';
}

/**
 * 按照 DatasetPreviewPage 的业务约束完成 merge Field Names 数据合并或复制。
 * @param previewFields - 参与映射、显示或数据写入的字段集合。
 * @param savedFields - 参与映射、显示或数据写入的字段集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function mergeFieldNames(
  previewFields: readonly DatasetFieldDefinition[],
  savedFields: readonly DatasetFieldDefinition[],
): DatasetFieldDefinition[] {
  return previewFields.map((field) => {
    const saved = savedFields.find((item) => item.code.toLowerCase() === field.code.toLowerCase());
    return saved === undefined ? field : { ...field, displayName: saved.displayName };
  });
}

/**
 * 按 Dataset Preview Page 规则筛选 DatasetPreviewPage 数据，并返回满足条件的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export default function DatasetPreviewPage() {
  const [datasets, setDatasets] = useState<DatasetDocument[]>([]);
  const [externalSources, setExternalSources] = useState<ExternalParameterSourceDocument[]>([]);
  const [datasetCode, setDatasetCode] = useState(
    () => new URLSearchParams(window.location.search).get('datasetCode') ?? '',
  );
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<DatasetPreviewResult | null>(null);
  // 第一阶段：构造 DatasetPreviewPage 当前阶段需要的中间数据和执行参数。
  const [loading, setLoading] = useState(true);
  const [previewing, setPreviewing] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    /**
     * 查询或读取 DatasetPreviewPage 所需的 load 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
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
  // 第2阶段：构造 DatasetPreviewPage 当前阶段需要的中间数据和执行参数。
  const synchronizedFields = useMemo(
    () => result === null || dataset === null
      ? []
      : mergeFieldNames(result.fields, dataset.fields),
    [dataset, result],
  );
  const fieldsChanged = dataset !== null && result !== null &&
    JSON.stringify(dataset.fields) !== JSON.stringify(synchronizedFields);

  /**
   * 负责 DatasetPreviewPage 的 select Dataset 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
   * @param code - 需要读取、校验或转换的业务值。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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

  /**
   * 按 preview 规则筛选 DatasetPreviewPage 数据，并返回满足条件的结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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
      // 第3阶段：根据 DatasetPreviewPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setResult(next);
      setStatus(`返回 ${next.rows.length} 行，耗时 ${next.durationMilliseconds} ms`);
    } catch (exception) {
      setResult(null);
      setError(exception instanceof Error ? exception.message : '数据预览失败。');
    } finally {
      setPreviewing(false);
    }
  };

  /**
   * 遍历 DatasetPreviewPage 数据并执行 sync Fields 转换，生成后续流程需要的结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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

  // 第4阶段：遍历 DatasetPreviewPage 当前数据集合，逐项转换并维护处理结果。
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
