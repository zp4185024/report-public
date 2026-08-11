import { useEffect, useMemo, useState } from 'react';
import { FilePenLine, LoaderCircle, RefreshCw, Search } from 'lucide-react';
import { reportCatalogApi } from './api';
import type { ReportSummaryDocument } from './types';

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
        </div>
      </header>

      {error !== null && <div className="error-strip report-list-message">{error}</div>}

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
    </main>
  );
}
