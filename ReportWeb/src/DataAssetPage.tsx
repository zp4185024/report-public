import { useEffect, useMemo, useState } from 'react';
import {
  ArrowLeft,
  Braces,
  Database,
  Eye,
  LoaderCircle,
  Plus,
  Save,
  Server,
  Trash2,
  Zap,
} from 'lucide-react';
import { dataAssetApi } from './api';
import type {
  ColumnDefinition,
  DataSourceDocument,
  DataSourceInput,
  DatasetDocument,
  DatasetPreviewResult,
} from './types';

type AssetKind = 'data-sources' | 'datasets';

function sourceDraft(document: DataSourceDocument): DataSourceInput {
  return { ...document, password: null };
}

function nextCode(prefix: string, codes: readonly string[]): string {
  let index = codes.length + 1;
  while (codes.includes(`${prefix}_${index}`)) index += 1;
  return `${prefix}_${index}`;
}

function newDataSource(existing: readonly DataSourceDocument[]): DataSourceInput {
  return {
    code: nextCode('data_source', existing.map((item) => item.code)),
    name: '新建 SQL Server 数据源',
    dbType: 'SqlServer',
    server: '',
    port: 1433,
    database: '',
    userName: '',
    password: null,
    passwordConfigured: false,
    timeoutSeconds: 30,
    maxRows: 20_000,
    enabled: true,
    revision: 0,
  };
}

function newColumn(index: number): ColumnDefinition {
  return {
    code: `field${index + 1}`,
    header: `字段 ${index + 1}`,
    dataType: 'string',
    width: 120,
    formulaTemplate: null,
  };
}

function newDataset(
  existing: readonly DatasetDocument[],
  sources: readonly DataSourceDocument[],
): DatasetDocument {
  return {
    code: nextCode('dataset', existing.map((item) => item.code)),
    name: '新建数据集',
    dataSourceCode: sources.find((item) => item.enabled)?.code ?? sources[0]?.code ?? '',
    queryText: 'SELECT 1 AS value',
    parameterCodes: [],
    columns: [{ code: 'value', header: '值', dataType: 'number', width: 120, formulaTemplate: null }],
    timeoutSeconds: 30,
    maxRows: 20_000,
    enabled: true,
    revision: 0,
  };
}

function sqlParameters(queryText: string): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const match of queryText.matchAll(/(?<!@)@([A-Za-z][A-Za-z0-9_]{0,63})/g)) {
    const code = match[1];
    const key = code.toLowerCase();
    if (!seen.has(key)) {
      seen.add(key);
      result.push(code);
    }
  }
  return result;
}

export default function DataAssetPage() {
  const [kind, setKind] = useState<AssetKind>('data-sources');
  const [dataSources, setDataSources] = useState<DataSourceDocument[]>([]);
  const [datasets, setDatasets] = useState<DatasetDocument[]>([]);
  const [dataSourceDraft, setDataSourceDraft] = useState<DataSourceInput | null>(null);
  const [datasetDraft, setDatasetDraft] = useState<DatasetDocument | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [previewParameters, setPreviewParameters] = useState<Record<string, string>>({});
  const [previewResult, setPreviewResult] = useState<DatasetPreviewResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const [nextSources, nextDatasets] = await Promise.all([
          dataAssetApi.dataSources(),
          dataAssetApi.datasets(),
        ]);
        if (!active) return;
        setDataSources(nextSources);
        setDatasets(nextDatasets);
        setDataSourceDraft(nextSources[0] ? sourceDraft(nextSources[0]) : newDataSource([]));
        setDatasetDraft(nextDatasets[0] ?? newDataset([], nextSources));
      } catch (exception) {
        if (active) setError(exception instanceof Error ? exception.message : '数据资源加载失败。');
      } finally {
        if (active) setLoading(false);
      }
    };
    void load();
    return () => { active = false; };
  }, []);

  const inferredParameters = useMemo(
    () => sqlParameters(datasetDraft?.queryText ?? ''),
    [datasetDraft?.queryText],
  );
  const urlParameters = useMemo(() => new URLSearchParams(window.location.search), []);

  const selectSource = (source: DataSourceDocument) => {
    setDataSourceDraft(sourceDraft(source));
    setError(null);
    setStatus(null);
  };

  const selectDataset = (dataset: DatasetDocument) => {
    setDatasetDraft({ ...dataset, columns: dataset.columns.map((column) => ({ ...column })) });
    setPreviewParameters({});
    setPreviewResult(null);
    setError(null);
    setStatus(null);
  };

  const saveSource = async () => {
    if (dataSourceDraft === null) return;
    if (!dataSourceDraft.code.trim() || !dataSourceDraft.name.trim() || !dataSourceDraft.server.trim() || !dataSourceDraft.database.trim() || !dataSourceDraft.userName.trim()) {
      setError('编码、名称、服务器、数据库和用户名不能为空。');
      return;
    }
    setSaving(true);
    setError(null);
    setStatus(null);
    try {
      const saved = await dataAssetApi.saveDataSource(dataSourceDraft);
      const next = [...dataSources.filter((item) => item.code !== saved.code), saved]
        .sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
      setDataSources(next);
      setDataSourceDraft(sourceDraft(saved));
      setStatus(`数据源已保存，当前修订号 ${saved.revision}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据源保存失败。');
    } finally {
      setSaving(false);
    }
  };

  const testSource = async () => {
    if (dataSourceDraft === null) return;
    setTesting(true);
    setError(null);
    setStatus(null);
    try {
      const result = await dataAssetApi.testDataSource(dataSourceDraft);
      setStatus(`连接成功，耗时 ${result.durationMilliseconds} ms，SQL Server ${result.databaseVersion}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据源连接失败。');
    } finally {
      setTesting(false);
    }
  };

  const updateColumn = (index: number, patch: Partial<ColumnDefinition>) => {
    setDatasetDraft((current) => {
      if (current === null) return current;
      const columns = [...current.columns];
      columns[index] = { ...columns[index], ...patch };
      return { ...current, columns };
    });
  };

  const saveDataset = async () => {
    if (datasetDraft === null) return;
    if (!datasetDraft.code.trim() || !datasetDraft.name.trim() || !datasetDraft.dataSourceCode || !datasetDraft.queryText.trim()) {
      setError('编码、名称、数据源和 SQL 不能为空。');
      return;
    }
    if (datasetDraft.columns.length === 0) {
      setError('数据集至少需要一个字段。');
      return;
    }
    setSaving(true);
    setError(null);
    setStatus(null);
    try {
      const saved = await dataAssetApi.saveDataset({
        ...datasetDraft,
        parameterCodes: inferredParameters,
      });
      const next = [...datasets.filter((item) => item.code !== saved.code), saved]
        .sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
      setDatasets(next);
      setDatasetDraft(saved);
      setStatus(`数据集已保存，当前修订号 ${saved.revision}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据集保存失败。');
    } finally {
      setSaving(false);
    }
  };

  const previewDataset = async () => {
    if (datasetDraft === null) return;
    if (!datasetDraft.dataSourceCode || !datasetDraft.queryText.trim()) {
      setError('数据源和 SQL 不能为空。');
      return;
    }
    setPreviewing(true);
    setError(null);
    setStatus(null);
    try {
      const parameters = Object.fromEntries(inferredParameters.map((code) => {
        const value = previewParameters[code] ?? urlParameters.get(code) ?? '';
        return [code, value.trim() === '' ? null : value];
      }));
      const result = await dataAssetApi.previewDataset({
        dataSourceCode: datasetDraft.dataSourceCode,
        queryText: datasetDraft.queryText,
        parameters,
        timeoutSeconds: datasetDraft.timeoutSeconds,
      });
      setPreviewResult(result);
      setStatus(`预览完成，返回 ${result.rows.length} 行，耗时 ${result.durationMilliseconds} ms`);
    } catch (exception) {
      setPreviewResult(null);
      setError(exception instanceof Error ? exception.message : '数据集预览失败。');
    } finally {
      setPreviewing(false);
    }
  };

  return (
    <main className="asset-page">
      <header className="asset-header">
        <div className="asset-title-row">
          <a className="icon-button" href={`/${window.location.search}`} title="返回报表设计" aria-label="返回报表设计"><ArrowLeft size={18} /></a>
          <div><span className="app-kicker">REPORT CENTER</span><h1>数据资源管理</h1></div>
        </div>
        <div className="runtime-status"><span className="status-dot" />全局复用资源</div>
      </header>

      <nav className="asset-tabs" aria-label="数据资源类型">
        <button type="button" className={kind === 'data-sources' ? 'active' : ''} onClick={() => setKind('data-sources')}><Server size={16} />数据源<span>{dataSources.length}</span></button>
        <button type="button" className={kind === 'datasets' ? 'active' : ''} onClick={() => setKind('datasets')}><Database size={16} />数据集<span>{datasets.length}</span></button>
      </nav>

      <div className="asset-messages">
        {error !== null && <div className="error-strip">{error}</div>}
        {status !== null && <div className="success-strip">{status}</div>}
      </div>

      <section className="asset-workspace">
        <aside className="asset-list">
          <div className="asset-list-header">
            <strong>{kind === 'data-sources' ? '数据源' : '数据集'}</strong>
            <button type="button" className="icon-button" title={kind === 'data-sources' ? '新增数据源' : '新增数据集'} aria-label={kind === 'data-sources' ? '新增数据源' : '新增数据集'} onClick={() => {
              setError(null); setStatus(null);
              if (kind === 'data-sources') setDataSourceDraft(newDataSource(dataSources));
              else {
                setDatasetDraft(newDataset(datasets, dataSources));
                setPreviewParameters({});
                setPreviewResult(null);
              }
            }}><Plus size={17} /></button>
          </div>
          <div className="asset-list-scroll">
            {loading ? <div className="asset-empty"><LoaderCircle className="spin" size={20} />正在读取</div> : kind === 'data-sources' ? dataSources.map((source) => (
              <button key={source.code} type="button" className={`asset-list-item ${dataSourceDraft?.code === source.code && dataSourceDraft.revision > 0 ? 'active' : ''}`} onClick={() => selectSource(source)}>
                <Server size={17} /><span><strong>{source.name}</strong><small>{source.code} · {source.server}/{source.database}</small></span><i className={source.enabled ? 'enabled' : ''}>{source.enabled ? '启用' : '停用'}</i>
              </button>
            )) : datasets.map((dataset) => (
              <button key={dataset.code} type="button" className={`asset-list-item ${datasetDraft?.code === dataset.code && datasetDraft.revision > 0 ? 'active' : ''}`} onClick={() => selectDataset(dataset)}>
                <Braces size={17} /><span><strong>{dataset.name}</strong><small>{dataset.code} · {dataset.columns.length} 个字段</small></span><i className={dataset.enabled ? 'enabled' : ''}>{dataset.enabled ? '启用' : '停用'}</i>
              </button>
            ))}
          </div>
        </aside>

        <div className="asset-editor">
          {kind === 'data-sources' && dataSourceDraft !== null ? (
            <>
              <div className="asset-editor-header"><div><h2>{dataSourceDraft.revision === 0 ? '新增数据源' : dataSourceDraft.name}</h2><p>修订号 {dataSourceDraft.revision}</p></div><div><button type="button" className="button" disabled={testing || saving} onClick={() => void testSource()}>{testing ? <LoaderCircle className="spin" size={16} /> : <Zap size={16} />}测试连接</button><button type="button" className="button button-primary" disabled={saving || testing} onClick={() => void saveSource()}>{saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存</button></div></div>
              <div className="asset-form-grid">
                <label><span>数据源编码</span><input disabled={dataSourceDraft.revision > 0} value={dataSourceDraft.code} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, code: event.target.value })} /></label>
                <label><span>数据源名称</span><input value={dataSourceDraft.name} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, name: event.target.value })} /></label>
                <label><span>数据库类型</span><select value={dataSourceDraft.dbType} disabled><option value="SqlServer">SQL Server</option></select></label>
                <label className="toggle-field"><span>状态</span><input type="checkbox" checked={dataSourceDraft.enabled} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, enabled: event.target.checked })} /></label>
                <label className="wide-field"><span>服务器</span><input value={dataSourceDraft.server} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, server: event.target.value })} /></label>
                <label><span>端口</span><input type="number" min="1" max="65535" value={dataSourceDraft.port} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, port: Number(event.target.value) })} /></label>
                <label><span>数据库</span><input value={dataSourceDraft.database} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, database: event.target.value })} /></label>
                <label><span>用户名</span><input autoComplete="username" value={dataSourceDraft.userName} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, userName: event.target.value })} /></label>
                <label><span>新密码</span><input type="password" autoComplete="new-password" placeholder={dataSourceDraft.passwordConfigured ? '留空保留原密码' : '请输入密码'} value={dataSourceDraft.password ?? ''} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, password: event.target.value || null })} /></label>
                <label><span>连接超时（秒）</span><input type="number" min="1" max="600" value={dataSourceDraft.timeoutSeconds} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, timeoutSeconds: Number(event.target.value) })} /></label>
                <label><span>默认最大行数</span><input type="number" min="1" max="1000000" value={dataSourceDraft.maxRows} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, maxRows: Number(event.target.value) })} /></label>
              </div>
            </>
          ) : kind === 'datasets' && datasetDraft !== null ? (
            <>
              <div className="asset-editor-header"><div><h2>{datasetDraft.revision === 0 ? '新增数据集' : datasetDraft.name}</h2><p>修订号 {datasetDraft.revision}</p></div><button type="button" className="button button-primary" disabled={saving || previewing} onClick={() => void saveDataset()}>{saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存</button></div>
              <div className="asset-form-grid dataset-form-grid">
                <label><span>数据集编码</span><input disabled={datasetDraft.revision > 0} value={datasetDraft.code} onChange={(event) => setDatasetDraft({ ...datasetDraft, code: event.target.value })} /></label>
                <label><span>数据集名称</span><input value={datasetDraft.name} onChange={(event) => setDatasetDraft({ ...datasetDraft, name: event.target.value })} /></label>
                <label><span>数据源</span><select value={datasetDraft.dataSourceCode} onChange={(event) => { setDatasetDraft({ ...datasetDraft, dataSourceCode: event.target.value }); setPreviewResult(null); }}>{dataSources.map((source) => <option key={source.code} value={source.code} disabled={!source.enabled}>{source.name}（{source.code}）</option>)}</select></label>
                <label className="toggle-field"><span>状态</span><input type="checkbox" checked={datasetDraft.enabled} onChange={(event) => setDatasetDraft({ ...datasetDraft, enabled: event.target.checked })} /></label>
                <label><span>查询超时（秒）</span><input type="number" min="1" max="600" value={datasetDraft.timeoutSeconds} onChange={(event) => setDatasetDraft({ ...datasetDraft, timeoutSeconds: Number(event.target.value) })} /></label>
                <label><span>最大返回行数</span><input type="number" min="1" max="1000000" value={datasetDraft.maxRows} onChange={(event) => setDatasetDraft({ ...datasetDraft, maxRows: Number(event.target.value) })} /></label>
              </div>
              <label className="asset-sql-field"><span>参数化只读 SQL</span><textarea spellCheck={false} value={datasetDraft.queryText} onChange={(event) => { setDatasetDraft({ ...datasetDraft, queryText: event.target.value }); setPreviewResult(null); }} /></label>
              <div className="dataset-parameter-line"><strong>识别到的参数</strong><span>{inferredParameters.length === 0 ? '无' : inferredParameters.map((code) => `@${code}`).join('、')}</span></div>
              <section className="dataset-preview-section">
                <div className="dataset-preview-header">
                  <div><strong>数据预览</strong><span>最多 50 行</span></div>
                  <button type="button" className="button" disabled={previewing || saving} onClick={() => void previewDataset()}>{previewing ? <LoaderCircle className="spin" size={16} /> : <Eye size={16} />}预览数据</button>
                </div>
                {inferredParameters.length > 0 && <div className="dataset-preview-parameters">{inferredParameters.map((code) => <label key={code}><span>@{code}</span><input value={previewParameters[code] ?? urlParameters.get(code) ?? ''} onChange={(event) => { setPreviewParameters({ ...previewParameters, [code]: event.target.value }); setPreviewResult(null); }} /></label>)}</div>}
                {previewResult !== null && <div className="dataset-preview-scroll"><table className="dataset-preview-table"><thead><tr><th>#</th>{previewResult.columns.map((column, index) => <th key={`${index}-${column}`}>{column || `列 ${index + 1}`}</th>)}</tr></thead><tbody>{previewResult.rows.length === 0 ? <tr><td colSpan={previewResult.columns.length + 1} className="dataset-preview-empty">暂无数据</td></tr> : previewResult.rows.map((row, rowIndex) => <tr key={rowIndex}><td>{rowIndex + 1}</td>{previewResult.columns.map((_, columnIndex) => <td key={columnIndex} title={row[columnIndex] === null ? '' : String(row[columnIndex])}>{row[columnIndex] === null ? <span className="null-value">NULL</span> : String(row[columnIndex])}</td>)}</tr>)}</tbody></table>{previewResult.hasMore && <div className="dataset-preview-limit">仅显示前 {previewResult.limit} 行</div>}</div>}
              </section>
              <div className="dataset-columns-header"><div><strong>字段结构</strong><span>{datasetDraft.columns.length} 个字段</span></div><button type="button" className="button" onClick={() => setDatasetDraft({ ...datasetDraft, columns: [...datasetDraft.columns, newColumn(datasetDraft.columns.length)] })}><Plus size={16} />新增字段</button></div>
              <div className="configuration-table-scroll asset-column-scroll"><table className="configuration-table column-table"><thead><tr><th>字段编码</th><th>列标题</th><th>数据类型</th><th>列宽</th><th>公式模板</th><th>操作</th></tr></thead><tbody>{datasetDraft.columns.map((column, index) => <tr key={`${index}-${column.code}`}><td><input value={column.code} onChange={(event) => updateColumn(index, { code: event.target.value })} /></td><td><input value={column.header} onChange={(event) => updateColumn(index, { header: event.target.value })} /></td><td><select value={column.dataType} onChange={(event) => updateColumn(index, { dataType: event.target.value, formulaTemplate: event.target.value === 'formula' ? column.formulaTemplate || '=A{row}' : null })}><option value="string">文本</option><option value="number">数字</option><option value="date">日期</option><option value="datetime">日期时间</option><option value="boolean">布尔值</option><option value="formula">公式</option></select></td><td><input type="number" min="40" max="500" value={column.width} onChange={(event) => updateColumn(index, { width: Number(event.target.value) })} /></td><td><input disabled={column.dataType !== 'formula'} value={column.formulaTemplate ?? ''} onChange={(event) => updateColumn(index, { formulaTemplate: event.target.value || null })} /></td><td><button type="button" className="table-icon danger" title="删除字段" aria-label={`删除字段 ${column.header}`} onClick={() => setDatasetDraft({ ...datasetDraft, columns: datasetDraft.columns.filter((_, itemIndex) => itemIndex !== index) })}><Trash2 size={15} /></button></td></tr>)}</tbody></table></div>
            </>
          ) : <div className="asset-empty">请选择资源</div>}
        </div>
      </section>
    </main>
  );
}
