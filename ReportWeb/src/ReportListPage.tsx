import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { ExternalLink, FilePenLine, LoaderCircle, Plus, RefreshCw, Search, X } from 'lucide-react';
import { reportCatalogApi } from './api';
import { StatusToasts } from './StatusToasts';
import type { CreateReportRequest, DatasetOption, ReportSummaryDocument } from './types';

const emptyCreateRequest: CreateReportRequest = {
  reportCode: '',
  name: '',
  sheetName: '',
  datasetCode: '',
};

function formatUpdatedAt(value: string | null): string {
  if (!value) return '暂无记录';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(date);
}

export default function ReportListPage() {
  const [reports, setReports] = useState<ReportSummaryDocument[]>([]);
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [createOptionsLoading, setCreateOptionsLoading] = useState(false);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [datasets, setDatasets] = useState<DatasetOption[]>([]);
  const [createRequest, setCreateRequest] = useState<CreateReportRequest>(emptyCreateRequest);

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      setReports(await reportCatalogApi.reports());
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '模板列表加载失败。');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
  }, []);

  const filteredReports = useMemo(() => {
    const keyword = query.trim().toLocaleLowerCase('zh-CN');
    if (!keyword) return reports;
    return reports.filter((report) => [
      report.name,
      report.reportCode,
      report.sheetName,
      report.datasetCode,
    ].some((value) => value.toLocaleLowerCase('zh-CN').includes(keyword)));
  }, [query, reports]);

  const openCreate = async () => {
    setCreateOpen(true);
    setCreateError(null);
    setCreateOptionsLoading(true);
    try {
      const options = await reportCatalogApi.createOptions();
      setDatasets(options.datasets);
      setCreateRequest({
        ...emptyCreateRequest,
        datasetCode: options.datasets[0]?.code ?? '',
      });
    } catch (exception) {
      setCreateError(exception instanceof Error ? exception.message : '创建选项加载失败。');
    } finally {
      setCreateOptionsLoading(false);
    }
  };

  const closeCreate = () => {
    if (creating) return;
    setCreateOpen(false);
    setCreateError(null);
  };

  const createReport = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const normalizedCode = createRequest.reportCode.trim().toLowerCase();
    if (!/^[a-z][a-z0-9_-]{0,63}$/.test(normalizedCode)) {
      setCreateError('模板编码必须以字母开头，只能包含小写字母、数字、下划线和连字符。');
      return;
    }
    if (!createRequest.name.trim() || !createRequest.sheetName.trim()) {
      setCreateError('模板名称和工作表名称不能为空。');
      return;
    }
    if (!createRequest.datasetCode) {
      setCreateError('请先选择一个数据集。');
      return;
    }

    setCreating(true);
    setCreateError(null);
    try {
      const created = await reportCatalogApi.create({
        ...createRequest,
        reportCode: normalizedCode,
        name: createRequest.name.trim(),
        sheetName: createRequest.sheetName.trim(),
      });
      window.location.assign(`/reports/${encodeURIComponent(created.reportCode)}`);
    } catch (exception) {
      setCreateError(exception instanceof Error ? exception.message : '模板创建失败。');
      setCreating(false);
    }
  };

  return (
    <main className="report-list-page">
      <header className="report-list-header">
        <div>
          <span className="app-kicker">REPORT CENTER</span>
          <h1>模板列表</h1>
          <p>统一管理报表配置、模板内容和所引用的数据集。</p>
        </div>
        <div className="report-list-tools">
          <label className="report-search">
            <Search size={17} />
            <input
              type="search"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="搜索名称、编码或数据集"
              aria-label="搜索模板"
            />
          </label>
          <button
            type="button"
            className="icon-button"
            title="刷新模板列表"
            aria-label="刷新模板列表"
            disabled={loading}
            onClick={() => void load()}
          >
            <RefreshCw className={loading ? 'spin' : undefined} size={18} />
          </button>
          <button
            type="button"
            className="button button-primary report-create-button"
            onClick={() => void openCreate()}
          >
            <Plus size={17} />新增模板
          </button>
        </div>
      </header>

      <StatusToasts
        error={createError ?? error}
        onErrorClose={() => {
          if (createError !== null) setCreateError(null);
          else setError(null);
        }}
      />

      <section className="report-table-section" aria-label="模板列表">
        <div className="report-table-meta">
          <span>共 {reports.length} 个模板</span>
          {query.trim() && <span>当前显示 {filteredReports.length} 个</span>}
        </div>
        <div className="report-table-scroll">
          <table className="report-table">
            <thead>
              <tr>
                <th>模板</th>
                <th>模板编码</th>
                <th>数据集</th>
                <th>状态</th>
                <th>配置版本</th>
                <th>模板版本</th>
                <th>更新时间</th>
                <th aria-label="操作" />
              </tr>
            </thead>
            <tbody>
              {loading ? (
                <tr><td colSpan={8} className="report-table-empty"><LoaderCircle className="spin" size={20} />正在读取模板</td></tr>
              ) : filteredReports.length === 0 ? (
                <tr><td colSpan={8} className="report-table-empty">没有匹配的模板</td></tr>
              ) : filteredReports.map((report) => (
                <tr key={report.reportCode}>
                  <td><strong>{report.name}</strong><small>{report.sheetName}</small></td>
                  <td><code>{report.reportCode}</code></td>
                  <td>{report.datasetCode}</td>
                  <td><span className={report.enabled ? 'report-status enabled' : 'report-status'}>{report.enabled ? '启用' : '停用'}</span></td>
                  <td>v{report.configurationRevision}</td>
                  <td>{report.templateRevision > 0 ? `v${report.templateRevision}` : '未保存'}</td>
                  <td>{formatUpdatedAt(report.updatedAt)}</td>
                  <td>
                    <a className="button report-edit-link" href={`/reports/${encodeURIComponent(report.reportCode)}`}>
                      <FilePenLine size={16} />编辑
                    </a>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {createOpen && (
        <div className="configuration-backdrop" role="presentation">
          <form className="create-report-dialog" aria-labelledby="create-report-title" onSubmit={(event) => void createReport(event)}>
            <header className="configuration-header">
              <div>
                <h2 id="create-report-title">新增模板</h2>
                <p>创建报表配置后进入模板编辑器。</p>
              </div>
              <button type="button" className="icon-button" title="关闭" aria-label="关闭新增模板" disabled={creating} onClick={closeCreate}>
                <X size={18} />
              </button>
            </header>

            <div className="create-report-body">
              <div className="configuration-form-grid create-report-grid">
                <label>
                  <span>模板编码</span>
                  <input autoFocus maxLength={64} value={createRequest.reportCode} onChange={(event) => setCreateRequest({ ...createRequest, reportCode: event.target.value })} placeholder="如 outpatient-log" />
                </label>
                <label>
                  <span>模板名称</span>
                  <input maxLength={128} value={createRequest.name} onChange={(event) => setCreateRequest({ ...createRequest, name: event.target.value })} />
                </label>
                <label>
                  <span>工作表名称</span>
                  <input maxLength={31} value={createRequest.sheetName} onChange={(event) => setCreateRequest({ ...createRequest, sheetName: event.target.value })} />
                </label>
                <label>
                  <span>数据集</span>
                  <select disabled={createOptionsLoading || datasets.length === 0} value={createRequest.datasetCode} onChange={(event) => setCreateRequest({ ...createRequest, datasetCode: event.target.value })}>
                    {datasets.map((dataset) => <option key={dataset.code} value={dataset.code}>{dataset.name}（{dataset.code}）</option>)}
                  </select>
                </label>
              </div>

              {createOptionsLoading && <div className="create-report-state"><LoaderCircle className="spin" size={18} />正在读取数据集</div>}
              {!createOptionsLoading && datasets.length === 0 && createError === null && (
                <div className="create-report-state empty">
                  <span>当前没有可用数据集</span>
                  <a className="button" href="/datasets"><ExternalLink size={16} />前往数据集</a>
                </div>
              )}
            </div>

            <footer className="configuration-footer">
              <div>
                <button type="button" className="button" disabled={creating} onClick={closeCreate}>取消</button>
                <button type="submit" className="button button-primary" disabled={creating || createOptionsLoading || datasets.length === 0}>
                  {creating ? <LoaderCircle className="spin" size={16} /> : <Plus size={16} />}创建并编辑
                </button>
              </div>
            </footer>
          </form>
        </div>
      )}
    </main>
  );
}
