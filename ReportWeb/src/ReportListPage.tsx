import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ExternalLink, FilePenLine, LoaderCircle, Plus, RefreshCw, Search, X } from 'lucide-react';
import { reportCatalogApi } from './api';
import { PaginationBar } from './PaginationBar';
import { StatusToasts } from './StatusToasts';
import type { CreateReportRequest, DatasetOption, ReportSummaryDocument } from './types';

const emptyCreateRequest: CreateReportRequest = {
  reportCode: '',
  name: '',
  sheetName: '',
  datasetCode: '',
};

/**
 * 将 ReportListPage 中的 format Updated At 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 按 Report List Page 规则筛选 ReportListPage 数据，并返回满足条件的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export default function ReportListPage() {
  const [reports, setReports] = useState<ReportSummaryDocument[]>([]);
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(15);
  const [totalReports, setTotalReports] = useState(0);
  const loadRequest = useRef(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [createOptionsLoading, setCreateOptionsLoading] = useState(false);
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  // 第一阶段：构造 ReportListPage 当前阶段需要的中间数据和执行参数。
  const [datasets, setDatasets] = useState<DatasetOption[]>([]);
  const [createRequest, setCreateRequest] = useState<CreateReportRequest>(emptyCreateRequest);

  /**
   * 请求模板列表当前页，并同步服务端返回的总数和规范化分页参数。
   * @param requestedPage - 请求的页码。
   * @param requestedPageSize - 请求的每页条数。
   * @param requestedQuery - 请求的搜索关键字。
   */
  const load = async (
    requestedPage = page,
    requestedPageSize = pageSize,
    requestedQuery = query,
  ) => {
    const requestId = loadRequest.current + 1;
    loadRequest.current = requestId;
    setLoading(true);
    setError(null);
    try {
      const result = await reportCatalogApi.reportsPage({
        page: requestedPage,
        pageSize: requestedPageSize,
        query: requestedQuery,
      });
      if (requestId !== loadRequest.current) return;
      setReports(result.items);
      setTotalReports(result.total);
      setPage(result.page);
      setPageSize(result.pageSize);
    } catch (exception) {
      if (requestId !== loadRequest.current) return;
      setError(exception instanceof Error ? exception.message : '模板列表加载失败。');
    } finally {
      if (requestId === loadRequest.current) setLoading(false);
    }
  };

  useEffect(() => {
    void load(page, pageSize, query);
  }, [page, pageSize, query]);

  useEffect(() => {
    setPage(1);
  }, [query, pageSize]);

  const reportPageCount = Math.max(1, Math.ceil(totalReports / pageSize));
  const currentReportPage = Math.min(page, reportPageCount);

  useEffect(() => {
    if (page > reportPageCount) setPage(reportPageCount);
  }, [page, reportPageCount]);

  /**
   * 准备 ReportListPage 的 open Create 交互状态并打开对应界面。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const openCreate = async () => {
    setCreateOpen(true);
    setCreateError(null);
    setCreateOptionsLoading(true);
    try {
      // 第2阶段：调用 ReportListPage 依赖的异步能力，等待并校验返回数据。
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

  /**
   * 结束 ReportListPage 的 close Create 交互并清理临时状态。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const closeCreate = () => {
    if (creating) return;
    setCreateOpen(false);
    setCreateError(null);
  };

  /**
   * 构建 ReportListPage 所需的 create Report 内容，并保证生成结果符合调用约定。
   * @param event - 触发当前处理流程的界面或业务事件。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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

    // 第3阶段：根据 ReportListPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
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
      // 第7阶段：根据 ReportListPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setCreateError(exception instanceof Error ? exception.message : '模板创建失败。');
      // 第6阶段：根据 ReportListPage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setCreating(false);
    }
  };

  // 第4阶段：遍历 ReportListPage 当前数据集合，逐项转换并维护处理结果。
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
            onClick={() => void load(page, pageSize, query)}
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
          // 第5阶段：检查 ReportListPage 当前条件，并按匹配分支执行对应处理。
          if (createError !== null) setCreateError(null);
          else setError(null);
        }}
      />

      <section className="report-table-section" aria-label="模板列表">
        <div className="report-table-meta">
          <span>{query.trim() ? '匹配' : '共'} {totalReports} 个模板</span>
          <span>当前页 {reports.length} 个</span>
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
              ) : reports.length === 0 ? (
                <tr><td colSpan={8} className="report-table-empty">没有匹配的模板</td></tr>
              ) : reports.map((report) => (
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
        <PaginationBar
          total={totalReports}
          page={currentReportPage}
          pageSize={pageSize}
          onPageChange={setPage}
          onPageSizeChange={setPageSize}
          itemLabel="模板"
        />
      </section>

      {createOpen && (
        <div className="configuration-backdrop" role="presentation">
          <form className="create-report-dialog" aria-labelledby="create-report-title" onSubmit={(event) => {
            // 第8阶段：在弹窗表单提交时转发 createReport，由它校验输入并执行创建。
            void createReport(event);
          }}>
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
