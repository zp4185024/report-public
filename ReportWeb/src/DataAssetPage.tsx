import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react';
import {
  Braces,
  Eye,
  FilePenLine,
  Link2,
  LoaderCircle,
  Plus,
  Search,
  Save,
  Server,
  Zap,
} from 'lucide-react';
import { dataAssetApi } from './api';
import { PaginationBar } from './PaginationBar';
import { StatusToasts } from './StatusToasts';
import type {
  DataSourceDocument,
  DataSourceDbType,
  DataSourceInput,
  DatasetDocument,
  DatasetReferenceDocument,
  DatasetVariableDefinition,
  ExternalParameterSourceDocument,
} from './types';
import type { SqlParameterPresentation } from './SqlEditor';

export type AssetKind = 'data-sources' | 'datasets';

interface DataAssetPageProps {
  initialKind: AssetKind;
}

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

/**
 * 按 database Type 规则筛选 DataAssetPage 数据，并返回满足条件的结果。
 * @param dbType - dbType 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function databaseType(dbType: DataSourceDbType) {
  return DATABASE_TYPES.find((item) => item.value === dbType) ?? DATABASE_TYPES[0];
}

/**
 * 创建并返回 DataAssetPage 的 source Draft 配置对象，集中提供相关状态和操作入口。
 * @param document - document 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function sourceDraft(document: DataSourceDocument): DataSourceInput {
  return { ...document, password: null };
}

/**
 * 负责 DataAssetPage 的 next Code 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param prefix - prefix 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param codes - codes 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function nextCode(prefix: string, codes: readonly string[]): string {
  let index = codes.length + 1;
  while (codes.includes(`${prefix}_${index}`)) index += 1;
  return `${prefix}_${index}`;
}

/**
 * 构建 DataAssetPage 所需的 new Data Source 内容，并保证生成结果符合调用约定。
 * @param existing - existing 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 构建 DataAssetPage 所需的 new Dataset 内容，并保证生成结果符合调用约定。
 * @param existing - existing 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param sources - sources 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 负责 DataAssetPage 的 sql Variable Codes 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param queryText - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 按 materialize Variables 规则筛选 DataAssetPage 数据，并返回满足条件的结果。
 * @param queryText - 需要读取、校验或转换的业务值。
 * @param current - current 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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
    sourceType: 'Form',
    sourceCode: null,
    sourceFieldCode: null,
  });
}

/**
 * 负责 DataAssetPage 的 preview Url 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param datasetCode - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function previewUrl(datasetCode: string): string {
  const search = new URLSearchParams(window.location.search);
  search.set('datasetCode', datasetCode);
  return `/data-assets/preview?${search.toString()}`;
}

/**
 * 负责 DataAssetPage 的 template Url 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param reportCode - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function templateUrl(reportCode: string): string {
  const search = new URLSearchParams(window.location.search);
  search.delete('datasetCode');
  const suffix = search.size > 0 ? `?${search.toString()}` : '';
  return `/reports/${encodeURIComponent(reportCode)}${suffix}`;
}

/**
 * 按 Data Asset Page 规则筛选 DataAssetPage 数据，并返回满足条件的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export default function DataAssetPage({ initialKind }: DataAssetPageProps) {
  const [kind, setKind] = useState<AssetKind>(initialKind);
  const [dataSources, setDataSources] = useState<DataSourceDocument[]>([]);
  const [datasets, setDatasets] = useState<DatasetDocument[]>([]);
  const [assetQuery, setAssetQuery] = useState('');
  const [assetPage, setAssetPage] = useState(1);
  const [assetPageSize, setAssetPageSize] = useState(15);
  const [datasetTotal, setDatasetTotal] = useState(0);
  const [datasetListLoading, setDatasetListLoading] = useState(true);
  const skipInitialDatasetPage = useRef(true);
  const [externalSources, setExternalSources] = useState<ExternalParameterSourceDocument[]>([]);
  const [dataSourceDraft, setDataSourceDraft] = useState<DataSourceInput | null>(null);
  const [datasetDraft, setDatasetDraft] = useState<DatasetDocument | null>(null);
  const [loading, setLoading] = useState(true);
  // 第一阶段：构造 DataAssetPage 当前阶段需要的中间数据和执行参数。
  const [saving, setSaving] = useState(false);
  // 第一阶段：构造 DataAssetPage 当前阶段需要的中间数据和执行参数。
  const [testing, setTesting] = useState(false);
  const [referencesOpen, setReferencesOpen] = useState(false);
  const [referencesLoading, setReferencesLoading] = useState(false);
  const [datasetReferences, setDatasetReferences] = useState<DatasetReferenceDocument[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => setKind(initialKind), [initialKind]);

  useEffect(() => {
    let active = true;
    /**
     * 查询或读取 DataAssetPage 所需的 load 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    const load = async () => {
      try {
        const [nextSources, nextDatasetsPage, nextExternalSources] = await Promise.all([
          dataAssetApi.dataSources(),
          dataAssetApi.datasetsPage({ page: 1, pageSize: 15, query: '' }),
          dataAssetApi.externalParameterSources(),
        ]);
        if (!active) return;
        setDataSources(nextSources);
        setDatasets(nextDatasetsPage.items);
        setDatasetTotal(nextDatasetsPage.total);
        setDatasetListLoading(false);
        setExternalSources(nextExternalSources);
        setDataSourceDraft(nextSources[0] ? sourceDraft(nextSources[0]) : newDataSource([]));
        setDatasetDraft(nextDatasetsPage.items[0] ?? newDataset([], nextSources));
      } catch (exception) {
        if (active) setError(exception instanceof Error ? exception.message : '数据资源加载失败。');
      } finally {
        if (active) {
          setLoading(false);
          setDatasetListLoading(false);
        }
      }
    };
    void load();
    // 第2阶段：整理 DataAssetPage 已计算的数据，并按方法契约返回最终结果。
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (!initialKind || initialKind !== 'datasets') return;
    if (skipInitialDatasetPage.current) {
      skipInitialDatasetPage.current = false;
      return;
    }
    let active = true;
    setDatasetListLoading(true);
    /**
     * 查询数据集管理列表当前页，并在搜索或翻页后保留编辑器状态。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    const loadDatasetPage = async () => {
      try {
        const result = await dataAssetApi.datasetsPage({
          page: assetPage,
          pageSize: assetPageSize,
          query: assetQuery,
        });
        if (!active) return;
        setDatasets(result.items);
        setDatasetTotal(result.total);
        setAssetPage(result.page);
        setAssetPageSize(result.pageSize);
      } catch (exception) {
        if (active) setError(exception instanceof Error ? exception.message : '数据集列表加载失败。');
      } finally {
        if (active) setDatasetListLoading(false);
      }
    };
    void loadDatasetPage();
    return () => { active = false; };
  }, [assetPage, assetPageSize, assetQuery, initialKind]);

  // 第2阶段：构造 DataAssetPage 当前阶段需要的中间数据和执行参数。
  const datasetVariables = useMemo(
    () => datasetDraft === null
      ? []
      : materializeVariables(datasetDraft.queryText, datasetDraft.variables),
    [datasetDraft],
  );
  const datasetDbType = dataSources.find(
    (source) => source.code === datasetDraft?.dataSourceCode,
  )?.dbType ?? 'SqlServer';
  const sqlParameters = useMemo<SqlParameterPresentation[]>(() => datasetVariables.map((variable) => ({
    code: variable.code,
    category: variable.sourceType === 'External' ? 'external' : 'form',
  })), [datasetVariables]);

  const normalizedAssetQuery = assetQuery.trim().toLocaleLowerCase('zh-CN');
  const filteredDataSources = useMemo(() => {
    if (!normalizedAssetQuery) return dataSources;
    return dataSources.filter((source) => [
      source.name,
      source.code,
      source.server,
      source.database,
    ].some((value) => value.toLocaleLowerCase('zh-CN').includes(normalizedAssetQuery)));
  }, [dataSources, normalizedAssetQuery]);
  const filteredAssetsTotal = kind === 'data-sources' ? filteredDataSources.length : datasetTotal;
  const assetPageCount = Math.max(1, Math.ceil(filteredAssetsTotal / assetPageSize));
  const currentAssetPage = Math.min(assetPage, assetPageCount);
  const visibleDataSources = useMemo(
    () => filteredDataSources.slice((currentAssetPage - 1) * assetPageSize, currentAssetPage * assetPageSize),
    [assetPageSize, currentAssetPage, filteredDataSources],
  );
  const visibleDatasets = useMemo(
    () => datasets,
    [datasets],
  );

  useEffect(() => {
    setAssetPage(1);
  }, [assetQuery, assetPageSize, kind]);

  useEffect(() => {
    if (assetPage > assetPageCount) setAssetPage(assetPageCount);
  }, [assetPage, assetPageCount]);

  /**
   * 负责 DataAssetPage 的 select Source 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
   * @param source - 本次转换或写入使用的数据对象。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const selectSource = (source: DataSourceDocument) => {
    setDataSourceDraft(sourceDraft(source));
    setError(null);
    setStatus(null);
  };

  /**
   * 更新 DataAssetPage 的 update Source Connection 状态，并同步相关业务数据。
   * @param patch - patch 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @param clearPassword - clearPassword 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const updateSourceConnection = (
    patch: Partial<Pick<DataSourceInput, 'dbType' | 'server' | 'port' | 'database' | 'userName'>>,
    clearPassword = false,
  ) => {
    setDataSourceDraft((current) => {
      // 第3阶段：检查 DataAssetPage 当前条件，并按匹配分支执行对应处理。
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

  /**
   * 遍历 DataAssetPage 数据并执行 select Dataset 转换，生成后续流程需要的结果。
   * @param dataset - dataset 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const selectDataset = (dataset: DatasetDocument) => {
    // 第3阶段：遍历 DataAssetPage 当前数据集合，逐项转换并维护处理结果。
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

  /**
   * 准备 DataAssetPage 的 show References 交互状态并打开对应界面。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const showReferences = async () => {
    if (datasetDraft === null || datasetDraft.revision === 0) return;
    if (referencesOpen) {
      // 第4阶段：根据 DataAssetPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
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

  /**
   * 更新 DataAssetPage 的 save Source 状态，并同步相关业务数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const saveSource = async () => {
    if (dataSourceDraft === null) return;
    if (!dataSourceDraft.code.trim() || !dataSourceDraft.name.trim() || !dataSourceDraft.server.trim() || !dataSourceDraft.database.trim() || !dataSourceDraft.userName.trim()) {
      setError('编码、名称、服务器、数据库和用户名不能为空。');
      return;
    }
    // 第4阶段：根据 DataAssetPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
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
      // 第5阶段：根据 DataAssetPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setError(exception instanceof Error ? exception.message : '数据源保存失败。');
    } finally {
      setSaving(false);
    }
  };

  /**
   * 负责 DataAssetPage 的 test Source 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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

  /**
   * 更新 DataAssetPage 的 update Variable 状态，并同步相关业务数据。
   * @param code - 需要读取、校验或转换的业务值。
   * @param patch - patch 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const updateVariable = (code: string, patch: Partial<DatasetVariableDefinition>) => {
    setDatasetDraft((current) => {
      if (current === null) return current;
      // 第5阶段：遍历 DataAssetPage 当前数据集合，逐项转换并维护处理结果。
      const variables = materializeVariables(current.queryText, current.variables).map((variable) =>
        variable.code.toLowerCase() === code.toLowerCase()
          ? { ...variable, ...patch, code: variable.code }
          : variable,
      );
      // 第6阶段：整理 DataAssetPage 已计算的数据，并按方法契约返回最终结果。
      return { ...current, variables };
    });
  };

  /**
   * 更新 DataAssetPage 的 save Dataset 状态，并同步相关业务数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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
      if (stored === undefined) setDatasetTotal((current) => current + 1);
      setDatasetDraft(saved);
      setStatus(queryChanged
        ? `数据集已保存，当前修订号 ${saved.revision}；SQL 已变化，请重新预览并保存返回字段。`
        : `数据集已保存，当前修订号 ${saved.revision}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '数据集保存失败。');
    } finally {
      // 第6阶段：根据 DataAssetPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      // 第7阶段：根据 DataAssetPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setSaving(false);
    }
  };

  return (
    <main className="asset-page">
      <header className="asset-header">
        <div className="asset-title-row">
          <div><span className="app-kicker">REPORT CENTER</span><h1>{kind === 'data-sources' ? '数据源管理' : '数据集管理'}</h1></div>
        </div>
        <div className="runtime-status"><span className="status-dot" />全局复用资源</div>
      </header>

      <StatusToasts
        error={error}
        success={status}
        onErrorClose={() => setError(null)}
        onSuccessClose={() => setStatus(null)}
      />

      <section className="asset-workspace">
        <aside className="asset-list">
          <div className="asset-list-header">
            <strong>{kind === 'data-sources' ? '数据源' : '数据集'}</strong>
            <button type="button" className="icon-button" title={kind === 'data-sources' ? '新增数据源' : '新增数据集'} aria-label={kind === 'data-sources' ? '新增数据源' : '新增数据集'} onClick={() => {
              setError(null); setStatus(null);
              // 第7阶段：检查 DataAssetPage 当前条件，并按匹配分支执行对应处理。
              // 第8阶段：检查 DataAssetPage 当前条件，并按匹配分支执行对应处理。
              if (kind === 'data-sources') setDataSourceDraft(newDataSource(dataSources));
              else setDatasetDraft(newDataset(datasets, dataSources));
            }}><Plus size={17} /></button>
          </div>
          <label className="asset-list-search">
            <Search size={15} />
            <input
              type="search"
              value={assetQuery}
              onChange={(event) => setAssetQuery(event.target.value)}
              placeholder={kind === 'data-sources' ? '搜索名称、编码或服务器' : '搜索名称、编码或数据源'}
              aria-label={kind === 'data-sources' ? '搜索数据源' : '搜索数据集'}
            />
          </label>
          <div className="asset-list-scroll">
            {loading || (kind === 'datasets' && datasetListLoading) ? <div className="asset-empty"><LoaderCircle className="spin" size={20} />正在读取</div> : kind === 'data-sources' ? visibleDataSources.map((source) => (
              <button key={source.code} type="button" className={`asset-list-item ${dataSourceDraft?.code === source.code && dataSourceDraft.revision > 0 ? 'active' : ''}`} onClick={() => selectSource(source)}>
                <Server size={17} /><span><strong>{source.name}</strong><small>{databaseType(source.dbType).label} · {source.server}/{source.database}</small></span><i className={source.enabled ? 'enabled' : ''}>{source.enabled ? '启用' : '停用'}</i>
              </button>
            )) : visibleDatasets.map((dataset) => (
              <button key={dataset.code} type="button" className={`asset-list-item ${datasetDraft?.code === dataset.code && datasetDraft.revision > 0 ? 'active' : ''}`} onClick={() => selectDataset(dataset)}>
                <Braces size={17} /><span><strong>{dataset.name}</strong><small>{dataset.code} · {dataset.variables.length} 个变量</small></span><i className={dataset.enabled ? 'enabled' : ''}>{dataset.enabled ? '启用' : '停用'}</i>
              </button>
            ))}
          </div>
          <PaginationBar
            total={filteredAssetsTotal}
            page={currentAssetPage}
            pageSize={assetPageSize}
            onPageChange={setAssetPage}
            onPageSizeChange={setAssetPageSize}
            itemLabel={kind === 'data-sources' ? '数据源' : '数据集'}
          />
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
                <label><span>新密码（明文）</span><input type="text" autoComplete="off" placeholder={dataSourceDraft.passwordConfigured ? '留空保留原密码' : '请输入密码'} value={dataSourceDraft.password ?? ''} onChange={(event) => setDataSourceDraft({ ...dataSourceDraft, password: event.target.value || null })} /></label>
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
                    parameters={sqlParameters}
                    onChange={(queryText) => setDatasetDraft({ ...datasetDraft, queryText })}
                  />
                </Suspense>
              </div>
              <div className="dataset-parameter-line"><strong>SQL 输入变量</strong><span className="parameter-legend form">查询输入</span><span className="parameter-legend external">外部接口</span><span>{datasetVariables.length === 0 ? '无' : datasetVariables.map((variable) => `@${variable.code}`).join('、')}</span></div>
              <div className="dataset-columns-header"><div><strong>变量定义</strong><span>{datasetVariables.length} 个变量</span></div></div>
              <div className="configuration-table-scroll asset-column-scroll"><table className="configuration-table dataset-variable-table"><thead><tr><th>SQL 变量</th><th>业务名称</th><th>数据类型</th><th>参数来源</th><th>接口字段</th><th>必填</th></tr></thead><tbody>{datasetVariables.length === 0 ? <tr><td colSpan={6} className="dataset-preview-empty">当前 SQL 没有输入变量</td></tr> : datasetVariables.map((variable) => {
                // 第8阶段：构造 DataAssetPage 当前阶段需要的中间数据和执行参数。
                // 第9阶段：构造 DataAssetPage 当前阶段需要的中间数据和执行参数。
                const source = externalSources.find((item) => item.code === variable.sourceCode);
                // 第9阶段：遍历 DataAssetPage 当前数据集合，逐项转换并维护处理结果。
                // 第10阶段：遍历 DataAssetPage 当前数据集合，逐项转换并维护处理结果。
                return <tr key={variable.code}><td><input disabled value={`@${variable.code}`} /></td><td><input value={variable.displayName} onChange={(event) => updateVariable(variable.code, { displayName: event.target.value })} /></td><td><select disabled={variable.sourceType === 'External'} value={variable.dataType} onChange={(event) => updateVariable(variable.code, { dataType: event.target.value })}><option value="string">文本</option><option value="int">整数</option><option value="long">长整数</option><option value="decimal">小数</option><option value="date">日期</option><option value="datetime">日期时间</option><option value="bool">布尔值</option></select></td><td><select value={variable.sourceType === 'External' ? variable.sourceCode ?? '' : 'Form'} onChange={(event) => {
                  const external = externalSources.find((item) => item.code === event.target.value);
                  const field = external?.fields.find((item) =>
                    item.code.toLowerCase() === variable.code.toLowerCase()) ?? external?.fields[0];
                  updateVariable(variable.code, external === undefined ? {
                    sourceType: 'Form', sourceCode: null, sourceFieldCode: null,
                  } : {
                    sourceType: 'External', sourceCode: external.code,
                    sourceFieldCode: field?.code ?? null,
                    dataType: field?.dataType ?? 'string',
                    displayName: field?.displayName ?? variable.displayName,
                  });
                }}><option value="Form">查询输入</option>{externalSources.map((item) => <option key={item.code} value={item.code} disabled={!item.enabled}>{item.name}</option>)}</select></td><td>{variable.sourceType === 'External' ? <select value={variable.sourceFieldCode ?? ''} onChange={(event) => {
                  const field = source?.fields.find((item) => item.code === event.target.value);
                  updateVariable(variable.code, {
                    sourceFieldCode: field?.code ?? null,
                    dataType: field?.dataType ?? 'string',
                    displayName: field?.displayName ?? variable.displayName,
                  });
                }}><option value="">请选择字段</option>{source?.fields.map((field) => <option key={field.code} value={field.code}>{field.displayName}（{field.code}）</option>)}</select> : <span className="source-field-empty">不适用</span>}</td><td><input type="checkbox" checked={variable.required} onChange={(event) => updateVariable(variable.code, { required: event.target.checked })} /></td></tr>;
              })}</tbody></table></div>
            </>
          ) : <div className="asset-empty">请选择资源</div>}
        </div>
      </section>
    </main>
  );
}
