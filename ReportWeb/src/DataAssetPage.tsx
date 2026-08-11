import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import {
  ArrowLeft,
  Braces,
  Database,
  Eye,
  FilePenLine,
  Link2,
  LoaderCircle,
  Plus,
  Save,
  Server,
  Zap,
} from 'lucide-react';
import { dataAssetApi } from './api';
import type {
  DataSourceDocument,
  DataSourceDbType,
  DataSourceInput,
  DatasetDocument,
  DatasetReferenceDocument,
  DatasetVariableDefinition,
} from './types';

type AssetKind = 'data-sources' | 'datasets';

const SqlEditor = lazy(() => import('./SqlEditor'));

const DATABASE_TYPES: ReadonlyArray<{
  value: DataSourceDbType;
  label: string;
  defaultPort: number;
  databaseLabel: string;
}> = [
  { value: 'SqlServer', label: 'SQL Server', defaultPort: 1433, databaseLabel: '数据库' },
  { value: 'Oracle', label: 'Oracle', defaultPort: 1521, databaseLabel: '服务名' },
  { value: 'MySql', label: 'MySQL', defaultPort: 3306, databaseLabel: '数据库' },
  { value: 'PostgreSQL', label: 'PostgreSQL', defaultPort: 5432, databaseLabel: '数据库' },
];

function databaseType(dbType: DataSourceDbType) {
  return DATABASE_TYPES.find((item) => item.value === dbType) ?? DATABASE_TYPES[0];
}

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
    name: '新建数据源',
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

function newDataset(
  existing: readonly DatasetDocument[],
  sources: readonly DataSourceDocument[],
): DatasetDocument {
  return {
    code: nextCode('dataset', existing.map((item) => item.code)),
    name: '新建数据集',
    dataSourceCode: sources.find((item) => item.enabled)?.code ?? sources[0]?.code ?? '',
    queryText: 'SELECT 1 AS value',
    variables: [],
    fields: [],
    timeoutSeconds: 30,
    maxRows: 20_000,
    enabled: true,
    revision: 0,
  };
}

function sqlVariableCodes(queryText: string): string[] {
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

function materializeVariables(
  queryText: string,
  current: readonly DatasetVariableDefinition[],
): DatasetVariableDefinition[] {
  return sqlVariableCodes(queryText).map((code) => current.find(
    (variable) => variable.code.toLowerCase() === code.toLowerCase(),
  ) ?? {
    code,
    displayName: code,
    dataType: 'string',
    required: false,
  });
}

function previewUrl(datasetCode: string): string {
  const search = new URLSearchParams(window.location.search);
  search.set('datasetCode', datasetCode);
  return `/data-assets/preview?${search.toString()}`;
}

function templateUrl(reportCode: string): string {
  const search = new URLSearchParams(window.location.search);
  search.delete('datasetCode');
  search.set('reportCode', reportCode);
  return `/?${search.toString()}`;
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
  const [referencesOpen, setReferencesOpen] = useState(false);
  const [referencesLoading, setReferencesLoading] = useState(false);
  const [datasetReferences, setDatasetReferences] = useState<DatasetReferenceDocument[]>([]);
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

  const datasetVariables = useMemo(
    () => datasetDraft === null
      ? []
      : materializeVariables(datasetDraft.queryText, datasetDraft.variables),
    [datasetDraft],
  );
  const datasetDbType = dataSources.find(
    (source) => source.code === datasetDraft?.dataSourceCode,
  )?.dbType ?? 'SqlServer';

  const selectSource = (source: DataSourceDocument) => {
    setDataSourceDraft(sourceDraft(source));
    setError(null);
    setStatus(null);
  };

  const updateSourceConnection = (
    patch: Partial<Pick<DataSourceInput, 'dbType' | 'server' | 'port' | 'database' | 'userName'>>,
    clearPassword = false,
  ) => {
    setDataSourceDraft((current) => {
      if (current === null) return current;
      const next = { ...current, ...patch, password: clearPassword ? null : current.password };
      const stored = dataSources.find((item) => item.code === next.code);
      const passwordConfigured = stored !== undefined && stored.passwordConfigured &&
        stored.dbType === next.dbType &&
        stored.server === next.server &&
        stored.port === next.port &&
        stored.database === next.database &&
        stored.userName === next.userName;
      return { ...next, passwordConfigured };
    });
  };

  const selectDataset = (dataset: DatasetDocument) => {
    setDatasetDraft({
      ...dataset,
      variables: dataset.variables.map((variable) => ({ ...variable })),
      fields: dataset.fields.map((field) => ({ ...field })),
    });
    setReferencesOpen(false);
    setDatasetReferences([]);
    setError(null);
    setStatus(null);
  };

  const showReferences = async () => {
    if (datasetDraft === null || datasetDraft.revision === 0) return;
    if (referencesOpen) {
      setReferencesOpen(false);
      return;
    }

    setReferencesOpen(true);
    setReferencesLoading(true);
    setError(null);
    try {
      setDatasetReferences(await dataAssetApi.datasetReferences(datasetDraft.code));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据集引用读取失败。');
    } finally {
      setReferencesLoading(false);
    }
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
      setStatus(`连接成功，耗时 ${result.durationMilliseconds} ms，${databaseType(dataSourceDraft.dbType).label} ${result.databaseVersion}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据源连接失败。');
    } finally {
      setTesting(false);
    }
  };

  const updateVariable = (code: string, patch: Partial<DatasetVariableDefinition>) => {
    setDatasetDraft((current) => {
      if (current === null) return current;
      const variables = materializeVariables(current.queryText, current.variables).map((variable) =>
        variable.code.toLowerCase() === code.toLowerCase()
          ? { ...variable, ...patch, code: variable.code }
          : variable,
      );
      return { ...current, variables };
    });
  };

  const saveDataset = async () => {
    if (datasetDraft === null) return;
    if (!datasetDraft.code.trim() || !datasetDraft.name.trim() || !datasetDraft.dataSourceCode || !datasetDraft.queryText.trim()) {
      setError('编码、名称、数据源和 SQL 不能为空。');
      return;
    }
    setSaving(true);
    setError(null);
    setStatus(null);
    try {
      const stored = datasets.find((item) => item.code === datasetDraft.code);
      const queryChanged = stored !== undefined &&
        stored.queryText.trim() !== datasetDraft.queryText.trim();
      const saved = await dataAssetApi.saveDataset({
        ...datasetDraft,
        variables: datasetVariables,
        fields: queryChanged ? [] : datasetDraft.fields,
      });
      const next = [...datasets.filter((item) => item.code !== saved.code), saved]
        .sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
      setDatasets(next);
      setDatasetDraft(saved);
      setStatus(queryChanged
        ? `数据集已保存，当前修订号 ${saved.revision}；SQL 已变化，请重新预览并保存返回字段。`
        : `数据集已保存，当前修订号 ${saved.revision}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据集保存失败。');
    } finally {
      setSaving(false);
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
              else setDatasetDraft(newDataset(datasets, dataSources));
            }}><Plus size={17} /></button>
          </div>
          <div className="asset-list-scroll">
            {loading ? <div className="asset-empty"><LoaderCircle className="spin" size={20} />正在读取</div> : kind === 'data-sources' ? dataSources.map((source) => (
              <button key={source.code} type="button" className={`asset-list-item ${dataSourceDraft?.code === source.code && dataSourceDraft.revision > 0 ? 'active' : ''}`} onClick={() => selectSource(source)}>
                <Server size={17} /><span><strong>{source.name}</strong><small>{databaseType(source.dbType).label} · {source.server}/{source.database}</small></span><i className={source.enabled ? 'enabled' : ''}>{source.enabled ? '启用' : '停用'}</i>
              </button>
            )) : datasets.map((dataset) => (
              <button key={dataset.code} type="button" className={`asset-list-item ${datasetDraft?.code === dataset.code && datasetDraft.revision > 0 ? 'active' : ''}`} onClick={() => selectDataset(dataset)}>
                <Braces size={17} /><span><strong>{dataset.name}</strong><small>{dataset.code} · {dataset.variables.length} 个变量</small></span><i className={dataset.enabled ? 'enabled' : ''}>{dataset.enabled ? '启用' : '停用'}</i>
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
                <label><span>数据库类型</span><select value={dataSourceDraft.dbType} onChange={(event) => { const dbType = event.target.value as DataSourceDbType; updateSourceConnection({ dbType, port: databaseType(dbType).defaultPort }, true); }}>{DATABASE_TYPES.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}</select></label>
                <label className="toggle-field"><span>状态</span><input type="checkbox" checked={dataSourceDraft.enabled} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, enabled: event.target.checked })} /></label>
                <label className="wide-field"><span>服务器</span><input value={dataSourceDraft.server} onChange={(event) => updateSourceConnection({ server: event.target.value })} /></label>
                <label><span>端口</span><input type="number" min="1" max="65535" value={dataSourceDraft.port} onChange={(event) => updateSourceConnection({ port: Number(event.target.value) })} /></label>
                <label><span>{databaseType(dataSourceDraft.dbType).databaseLabel}</span><input value={dataSourceDraft.database} onChange={(event) => updateSourceConnection({ database: event.target.value })} /></label>
                <label><span>用户名</span><input autoComplete="username" value={dataSourceDraft.userName} onChange={(event) => updateSourceConnection({ userName: event.target.value })} /></label>
                <label><span>新密码</span><input type="password" autoComplete="new-password" placeholder={dataSourceDraft.passwordConfigured ? '留空保留原密码' : '请输入密码'} value={dataSourceDraft.password ?? ''} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, password: event.target.value || null })} /></label>
                <label><span>连接超时（秒）</span><input type="number" min="1" max="600" value={dataSourceDraft.timeoutSeconds} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, timeoutSeconds: Number(event.target.value) })} /></label>
                <label><span>默认最大行数</span><input type="number" min="1" max="1000000" value={dataSourceDraft.maxRows} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, maxRows: Number(event.target.value) })} /></label>
              </div>
            </>
          ) : kind === 'datasets' && datasetDraft !== null ? (
            <>
              <div className="asset-editor-header"><div><h2>{datasetDraft.revision === 0 ? '新增数据集' : datasetDraft.name}</h2><p>修订号 {datasetDraft.revision}</p></div><div><button type="button" className="button" disabled={datasetDraft.revision === 0 || saving || referencesLoading} onClick={() => void showReferences()}>{referencesLoading ? <LoaderCircle className="spin" size={16} /> : <Link2 size={16} />}查看引用</button><button type="button" className="button" disabled={datasetDraft.revision === 0 || saving} onClick={() => window.location.assign(previewUrl(datasetDraft.code))}><Eye size={16} />数据预览</button><button type="button" className="button button-primary" disabled={saving} onClick={() => void saveDataset()}>{saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存</button></div></div>
              {referencesOpen && !referencesLoading && (
                <div className="dataset-reference-panel" role="region" aria-label="数据集引用">
                  <strong>引用报表</strong>
                  {datasetReferences.length > 0 ? (
                    <div className="dataset-reference-list">
                      {datasetReferences.map((reference) => (
                        <div className="dataset-reference-item" key={reference.reportCode}>
                          <span>{reference.reportName}（{reference.reportCode}）</span>
                          <button type="button" className="button" onClick={() => window.location.assign(templateUrl(reference.reportCode))}><FilePenLine size={16} />编辑模板</button>
                        </div>
                      ))}
                    </div>
                  ) : <span>当前没有报表引用这个数据集</span>}
                </div>
              )}
              <div className="asset-form-grid dataset-form-grid">
                <label><span>数据集编码</span><input disabled={datasetDraft.revision > 0} value={datasetDraft.code} onChange={(event) => setDatasetDraft({ ...datasetDraft, code: event.target.value })} /></label>
                <label><span>数据集名称</span><input value={datasetDraft.name} onChange={(event) => setDatasetDraft({ ...datasetDraft, name: event.target.value })} /></label>
                <label><span>数据源</span><select value={datasetDraft.dataSourceCode} onChange={(event) => setDatasetDraft({ ...datasetDraft, dataSourceCode: event.target.value })}>{dataSources.map((source) => <option key={source.code} value={source.code} disabled={!source.enabled}>{source.name}（{source.code}）</option>)}</select></label>
                <label className="toggle-field"><span>状态</span><input type="checkbox" checked={datasetDraft.enabled} onChange={(event) => setDatasetDraft({ ...datasetDraft, enabled: event.target.checked })} /></label>
                <label><span>查询超时（秒）</span><input type="number" min="1" max="600" value={datasetDraft.timeoutSeconds} onChange={(event) => setDatasetDraft({ ...datasetDraft, timeoutSeconds: Number(event.target.value) })} /></label>
                <label><span>最大返回行数</span><input type="number" min="1" max="1000000" value={datasetDraft.maxRows} onChange={(event) => setDatasetDraft({ ...datasetDraft, maxRows: Number(event.target.value) })} /></label>
              </div>
              <div className="asset-sql-field">
                <div className="asset-sql-header">
                  <span>参数化只读 SQL</span>
                  <span className="sql-dialect-badge">{databaseType(datasetDbType).label}</span>
                </div>
                <Suspense
                  fallback={(
                    <div className="asset-sql-editor-loading" aria-label="SQL 编辑器加载中">
                      <LoaderCircle className="spin" size={18} />
                    </div>
                  )}
                >
                  <SqlEditor
                    value={datasetDraft.queryText}
                    dbType={datasetDbType}
                    onChange={(queryText) => setDatasetDraft({ ...datasetDraft, queryText })}
                  />
                </Suspense>
              </div>
              <div className="dataset-parameter-line"><strong>SQL 输入变量</strong><span>{datasetVariables.length === 0 ? '无' : datasetVariables.map((variable) => `@${variable.code}`).join('、')}</span></div>
              <div className="dataset-columns-header"><div><strong>变量定义</strong><span>{datasetVariables.length} 个变量</span></div></div>
              <div className="configuration-table-scroll asset-column-scroll"><table className="configuration-table dataset-variable-table"><thead><tr><th>SQL 变量</th><th>业务名称</th><th>数据类型</th><th>必填</th></tr></thead><tbody>{datasetVariables.length === 0 ? <tr><td colSpan={4} className="dataset-preview-empty">当前 SQL 没有输入变量</td></tr> : datasetVariables.map((variable) => <tr key={variable.code}><td><input disabled value={`@${variable.code}`} /></td><td><input value={variable.displayName} onChange={(event) => updateVariable(variable.code, { displayName: event.target.value })} /></td><td><select value={variable.dataType} onChange={(event) => updateVariable(variable.code, { dataType: event.target.value })}><option value="string">文本</option><option value="int">整数</option><option value="long">长整数</option><option value="decimal">小数</option><option value="date">日期</option><option value="datetime">日期时间</option><option value="bool">布尔值</option></select></td><td><input type="checkbox" checked={variable.required} onChange={(event) => updateVariable(variable.code, { required: event.target.checked })} /></td></tr>)}</tbody></table></div>
            </>
          ) : <div className="asset-empty">请选择资源</div>}
        </div>
      </section>
    </main>
  );
}
