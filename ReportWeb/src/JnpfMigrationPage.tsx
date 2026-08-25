import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, LoaderCircle, Play, RefreshCw, ServerCog } from 'lucide-react';
import { dataAssetApi, jnpfMigrationApi } from './api';
import { StatusToasts } from './StatusToasts';
import type { DataSourceDocument, JnpfMigrationInventoryItem, JnpfMigrationResultItem } from './types';

const defaultMaxReports = 3;

function statusLabel(status: string | null): string {
  if (status === 'Ready') return '可迁移';
  if (status === 'Blocked') return '需处理';
  if (status === 'Excluded') return '已排除';
  return status || '未知';
}

function splitNotes(value: string | null): string[] {
  return (value || '').split(' | ').map((item) => item.trim()).filter(Boolean);
}

function resultTone(result: JnpfMigrationResultItem): string {
  return result.status === 'Inserted' || result.status === 'Skipped' ? 'success' : 'warning';
}

export default function JnpfMigrationPage() {
  const [dataSources, setDataSources] = useState<DataSourceDocument[]>([]);
  const [jnpfDataSourceCode, setJnpfDataSourceCode] = useState('');
  const [reportDataSourceCode, setReportDataSourceCode] = useState('');
  const [maxReports, setMaxReports] = useState(defaultMaxReports);
  const [writeTarget, setWriteTarget] = useState<{ database: string; dbType: string } | null>(null);
  const [inventory, setInventory] = useState<JnpfMigrationInventoryItem[]>([]);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [results, setResults] = useState<JnpfMigrationResultItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const canUseDataSource = (item: DataSourceDocument, expectedType: DataSourceDocument['dbType']) =>
    item.enabled && item.passwordConfigured && item.dbType === expectedType;

  const sourceIsReady = dataSources.some(
    (item) => item.code === jnpfDataSourceCode && canUseDataSource(item, 'MySql'),
  );
  const reportDataSourceIsReady = dataSources.some(
    (item) => item.code === reportDataSourceCode && item.enabled && item.passwordConfigured,
  );

  const dataSourceOptionNote = (
    item: DataSourceDocument,
    expectedType: DataSourceDocument['dbType'],
  ): string => {
    if (!item.enabled) return '（已停用）';
    if (!item.passwordConfigured) return '（未配置密码）';
    if (item.dbType !== expectedType) return `（需要 ${expectedType}）`;
    return '';
  };

  useEffect(() => {
    let active = true;
    const loadDataSources = async () => {
      try {
        const sources = await dataAssetApi.dataSources();
        if (!active) return;
        setDataSources(sources);
      } catch (exception) {
        if (active) setError(exception instanceof Error ? exception.message : '数据源加载失败。');
      }
    };
    void loadDataSources();
    return () => { active = false; };
  }, []);

  const readyItems = useMemo(
    () => inventory.filter((item) => item.migratable && item.status === 'Ready' && item.sourceReportId),
    [inventory],
  );
  const selectedReadyCount = selectedIds.filter((id) => readyItems.some((item) => item.sourceReportId === id)).length;

  const clearMigrationState = () => {
    setInventory([]);
    setSelectedIds([]);
    setResults([]);
    setWriteTarget(null);
    setNotice(null);
  };

  const loadInventory = async () => {
    if (!sourceIsReady || !reportDataSourceIsReady) {
      setError('请先选择可用的 JNPF 来源数据源和报表运行数据源。');
      return;
    }
    setLoading(true);
    setError(null);
    setNotice(null);
    setResults([]);
    try {
      const response = await jnpfMigrationApi.inventory({
        jnpfDataSourceCode,
        reportDataSourceCode,
        maxReports,
      });
      setWriteTarget({ database: response.writeTargetDatabase, dbType: response.writeTargetDbType });
      setInventory(response.inventory);
      setSelectedIds(response.inventory
        .filter((item) => item.migratable && item.status === 'Ready' && item.sourceReportId)
        .slice(0, maxReports)
        .map((item) => item.sourceReportId!));
      setNotice(`盘点完成，共发现 ${response.inventory.length} 个启用报表。`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '读取迁移清单失败。');
    } finally {
      setLoading(false);
    }
  };

  const toggleSelection = (id: string) => {
    setSelectedIds((current) => current.includes(id)
      ? current.filter((item) => item !== id)
      : current.length >= maxReports ? current : [...current, id]);
  };

  const executeMigration = async () => {
    if (selectedIds.length === 0) return;
    if (!window.confirm(`确认迁移选中的 ${selectedIds.length} 个报表？迁移会写入目标库。`)) return;
    setLoading(true);
    setError(null);
    setNotice(null);
    try {
      const response = await jnpfMigrationApi.migrate({
        jnpfDataSourceCode,
        reportDataSourceCode,
        sourceReportIds: selectedIds,
        maxReports,
      });
      setWriteTarget({ database: response.writeTargetDatabase, dbType: response.writeTargetDbType });
      setInventory(response.inventory);
      setResults(response.results);
      setNotice(`迁移处理完成，返回 ${response.results.length} 项结果。`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '执行迁移失败。');
    } finally {
      setLoading(false);
    }
  };

  return (
    <main className="jnpf-migration-page">
      <header className="jnpf-migration-header">
        <div>
          <span className="app-kicker">JNPF / REPORT CENTER</span>
          <h1>JNPF 报表迁移</h1>
          <p>读取 JNPF 报表配置，选择后写入当前报表中心。</p>
        </div>
        <div className="jnpf-migration-actions">
          <button type="button" className="button" onClick={() => void loadInventory()} disabled={loading || !sourceIsReady || !reportDataSourceIsReady}>
            <RefreshCw className={loading ? 'spin' : undefined} size={16} />读取清单
          </button>
          <button type="button" className="button button-primary" onClick={() => void executeMigration()} disabled={loading || selectedReadyCount === 0}>
            <Play size={16} />执行迁移（{selectedReadyCount}）
          </button>
        </div>
      </header>

      <StatusToasts error={error} onErrorClose={() => setError(null)} />
      {notice && <div className="jnpf-migration-notice"><CheckCircle2 size={17} />{notice}</div>}

      <section className="jnpf-migration-settings" aria-label="迁移筛选条件">
        <div className="jnpf-migration-setting">
          <label htmlFor="jnpf-source-datasource">JNPF 来源数据源</label>
          <select id="jnpf-source-datasource" value={jnpfDataSourceCode} onChange={(event) => {
            setJnpfDataSourceCode(event.target.value);
            clearMigrationState();
          }}>
            <option value="">请选择数据源</option>
            {dataSources.map((item) => <option key={item.code} value={item.code} disabled={!canUseDataSource(item, 'MySql')}>{item.name}（{item.code} · {item.dbType} · {item.server}:{item.port}/{item.database}）{dataSourceOptionNote(item, 'MySql')}</option>)}
          </select>
        </div>
        <div className="jnpf-migration-setting">
          <label htmlFor="report-runtime-datasource">报表运行数据源</label>
          <select id="report-runtime-datasource" value={reportDataSourceCode} onChange={(event) => {
            setReportDataSourceCode(event.target.value);
            clearMigrationState();
          }}>
            <option value="">请选择数据源</option>
            {dataSources.map((item) => <option key={`runtime-${item.code}`} value={item.code} disabled={!item.enabled || !item.passwordConfigured}>{item.name}（{item.code} · {item.dbType} · {item.server}:{item.port}/{item.database}）{!item.enabled ? '（已停用）' : !item.passwordConfigured ? '（未配置密码）' : ''}</option>)}
          </select>
        </div>
        <div className="jnpf-migration-setting">
          <label>写入目标</label>
          <output>{writeTarget ? `${writeTarget.database}（${writeTarget.dbType} · 当前连接）` : '当前报表中心连接（服务端确定）'}</output>
        </div>
        <div className="jnpf-migration-setting compact">
          <label htmlFor="his-max-reports">本批上限</label>
          <input id="his-max-reports" type="number" min={1} max={50} value={maxReports} onChange={(event) => {
            const next = Math.min(50, Math.max(1, Number(event.target.value) || 1));
            setMaxReports(next);
            setSelectedIds((current) => current.slice(0, next));
          }} />
        </div>
        <div className="jnpf-migration-credential"><ServerCog size={18} /><span>写入目标为当前报表中心连接；报表运行数据源从数据源管理中选择。仅迁移报表定义、数据集 SQL、参数和模板，不读取业务数据，也不执行报表 SQL。<a href="/data-sources">数据源管理</a></span></div>
      </section>

      <section className="jnpf-migration-table-section" aria-label="迁移清单">
        <div className="jnpf-migration-table-meta"><span>清单 {inventory.length} 项</span><span>已选 {selectedReadyCount} / {maxReports}</span></div>
        <div className="jnpf-migration-table-scroll">
          <table className="jnpf-migration-table">
            <thead><tr><th aria-label="选择" /><th>报表</th><th>数据源</th><th>字段 / 参数</th><th>状态</th><th>标记</th></tr></thead>
            <tbody>
              {loading && inventory.length === 0 ? <tr><td colSpan={6} className="jnpf-migration-empty"><LoaderCircle className="spin" size={19} />正在读取报表清单</td></tr>
                : inventory.length === 0 ? <tr><td colSpan={6} className="jnpf-migration-empty">点击“读取清单”开始盘点。</td></tr>
                  : inventory.map((item) => {
                    const id = item.sourceReportId || '';
                    const canSelect = item.migratable && item.status === 'Ready' && Boolean(id);
                    const selected = selectedIds.includes(id);
                    const notes = [...splitNotes(item.blockers), ...splitNotes(item.markers)];
                    return <tr key={`${id}-${item.sourceVersionId}`}>
                      <td><input type="checkbox" checked={selected} disabled={!canSelect || (selectedIds.length >= maxReports && !selected)} onChange={() => toggleSelection(id)} aria-label={`选择${item.reportName || id}`} /></td>
                      <td><strong>{item.reportName || '未命名报表'}</strong><small>{item.targetReportCode || item.sourceReportCode || id}</small></td>
                      <td>{item.dataSourceNames || '未配置'}</td>
                      <td>{item.fieldCount} / {item.parameterCount}<small>明细 {item.detailFieldCount}</small></td>
                      <td><span className={`jnpf-status ${item.status === 'Ready' ? 'ready' : item.status === 'Blocked' ? 'blocked' : 'excluded'}`}>{statusLabel(item.status)}</span></td>
                      <td>{notes.length === 0 ? <span className="jnpf-muted">无</span> : <div className="jnpf-notes">{notes.map((note) => <span key={note}><AlertTriangle size={13} />{note}</span>)}</div>}</td>
                    </tr>;
                  })}
            </tbody>
          </table>
        </div>
      </section>

      {results.length > 0 && <section className="jnpf-migration-results" aria-label="迁移结果">
        <h2>本次迁移结果</h2>
        {results.map((result, index) => <div className={`jnpf-result-row ${resultTone(result)}`} key={`${result.reportCode}-${index}`}>
          {result.status === 'Inserted' || result.status === 'Skipped' ? <CheckCircle2 size={16} /> : <AlertTriangle size={16} />}
          <strong>{result.reportName || result.reportCode || '未知报表'}</strong><span>{result.status || '未知'}</span><small>{result.message}</small>
        </div>)}
      </section>}
    </main>
  );
}
