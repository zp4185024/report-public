import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, LoaderCircle, Play, RefreshCw, ServerCog } from 'lucide-react';
import { dataAssetApi, jnpfMigrationApi } from './api';
import { StatusToasts } from './StatusToasts';
import type { DataSourceDocument, JnpfMigrationInventoryItem, JnpfMigrationResultItem } from './types';

const defaultMaxReports = 3;

/**
 * 负责 JnpfMigrationPage 的 status Label 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param status - status 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function statusLabel(status: string | null): string {
  if (status === 'Ready') return '可迁移';
  if (status === 'Blocked') return '需处理';
  if (status === 'Excluded') return '已排除';
  return status || '未知';
}

/**
 * 按 split Notes 规则筛选 JnpfMigrationPage 数据，并返回满足条件的结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function splitNotes(value: string | null): string[] {
  return (value || '').split(' | ').map((item) => item.trim()).filter(Boolean);
}

/**
 * 负责 JnpfMigrationPage 的 result Tone 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param result - 上一步或远程调用返回的处理结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function resultTone(result: JnpfMigrationResultItem): string {
  return result.status === 'Inserted' || result.status === 'Skipped' ? 'success' : 'warning';
}

/**
 * 按 Jnpf Migration Page 规则筛选 JnpfMigrationPage 数据，并返回满足条件的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export default function JnpfMigrationPage() {
  const [dataSources, setDataSources] = useState<DataSourceDocument[]>([]);
  const [jnpfDataSourceCode, setJnpfDataSourceCode] = useState('');
  const [reportDataSourceCode, setReportDataSourceCode] = useState('');
  const [maxReports, setMaxReports] = useState(defaultMaxReports);
  const [writeTarget, setWriteTarget] = useState<{ database: string; dbType: string } | null>(null);
  const [inventory, setInventory] = useState<JnpfMigrationInventoryItem[]>([]);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  // 第一阶段：构造 JnpfMigrationPage 当前阶段需要的中间数据和执行参数。
  const [results, setResults] = useState<JnpfMigrationResultItem[]>([]);
  // 第一阶段：构造 JnpfMigrationPage 当前阶段需要的中间数据和执行参数。
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  /**
   * 校验 JnpfMigrationPage 的 can Use Data Source 条件，并返回可供后续流程判断的结果。
   * @param item - 需要遍历、筛选或写入的业务数据集合。
   * @param expectedType - expectedType 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const canUseDataSource = (item: DataSourceDocument, expectedType: DataSourceDocument['dbType']) =>
    item.enabled && item.passwordConfigured && item.dbType === expectedType;

  const sourceIsReady = dataSources.some(
    (item) => item.code === jnpfDataSourceCode && canUseDataSource(item, 'MySql'),
  );
  const reportDataSourceIsReady = dataSources.some(
    (item) => item.code === reportDataSourceCode && item.enabled && item.passwordConfigured,
  );

  /**
   * 创建并返回 JnpfMigrationPage 的 data Source Option Note 初始数据，避免不同调用方共享可变对象。
   * @param item - 需要遍历、筛选或写入的业务数据集合。
   * @param expectedType - expectedType 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const dataSourceOptionNote = (
    item: DataSourceDocument,
    expectedType: DataSourceDocument['dbType'],
  ): string => {
    if (!item.enabled) return '（已停用）';
    if (!item.passwordConfigured) return '（未配置密码）';
    if (item.dbType !== expectedType) return `（需要 ${expectedType}）`;
    return '';
  };

  // 第2阶段：调用 JnpfMigrationPage 依赖的异步能力，等待并校验返回数据。
  useEffect(() => {
    let active = true;
    /**
     * 查询或读取 JnpfMigrationPage 所需的 load Data Sources 数据，并按调用约定返回结果。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
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
    // 第2阶段：整理 JnpfMigrationPage 已计算的数据，并按方法契约返回最终结果。
    return () => { active = false; };
  }, []);

  const readyItems = useMemo(
    () => inventory.filter((item) => item.migratable && item.status === 'Ready' && item.sourceReportId),
    [inventory],
  );
  const selectedReadyCount = selectedIds.filter((id) => readyItems.some((item) => item.sourceReportId === id)).length;

  /**
   * 移除 JnpfMigrationPage 中与 clear Migration State 对应的数据，并清理关联状态。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const clearMigrationState = () => {
    setInventory([]);
    setSelectedIds([]);
    setResults([]);
    setWriteTarget(null);
    // 第3阶段：根据 JnpfMigrationPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
    setNotice(null);
  };

  /**
   * 查询或读取 JnpfMigrationPage 所需的 load Inventory 数据，并按调用约定返回结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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
      // 第3阶段：遍历 JnpfMigrationPage 当前数据集合，逐项转换并维护处理结果。
      setSelectedIds(response.inventory
        .filter((item) => item.migratable && item.status === 'Ready' && item.sourceReportId)
        .slice(0, maxReports)
        .map((item) => item.sourceReportId!));
      setNotice(`盘点完成，共发现 ${response.inventory.length} 个启用报表。`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '读取迁移清单失败。');
    } finally {
      // 第4阶段：根据 JnpfMigrationPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setLoading(false);
    }
  };

  /**
   * 按 toggle Selection 规则筛选 JnpfMigrationPage 数据，并返回满足条件的结果。
   * @param id - 需要定位的业务记录标识。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const toggleSelection = (id: string) => {
    setSelectedIds((current) => current.includes(id)
      ? current.filter((item) => item !== id)
      : current.length >= maxReports ? current : [...current, id]);
  };

  /**
   * 执行 JnpfMigrationPage 的 execute Migration 核心流程，并保持错误和结果语义稳定。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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
      // 第4阶段：根据 JnpfMigrationPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setError(exception instanceof Error ? exception.message : '执行迁移失败。');
    } finally {
      // 第5阶段：根据 JnpfMigrationPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
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
            // 第5阶段：根据 JnpfMigrationPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
            // 第6阶段：根据 JnpfMigrationPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
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
                    // 第6阶段：构造 JnpfMigrationPage 当前阶段需要的中间数据和执行参数。
                    // 第7阶段：构造 JnpfMigrationPage 当前阶段需要的中间数据和执行参数。
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
