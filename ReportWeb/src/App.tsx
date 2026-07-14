import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import {
  ChevronLeft,
  ChevronRight,
  CircleStop,
  Database,
  Download,
  FileDown,
  LoaderCircle,
  Search,
} from 'lucide-react';
import { reportApi } from './api';
import { ReportSheet } from './ReportSheet';
import type { ExecuteRequest, ExecuteResult, ExportJob, ReportDefinition } from './types';

const urlParameters = new URLSearchParams(window.location.search);

function createInitialRequest(): ExecuteRequest {
  return {
    page: 1,
    hospitalId: urlParameters.get('hospitalId'),
    deptId: urlParameters.get('deptId'),
    personId: urlParameters.get('personId'),
    startDate: null,
    endDate: null,
  };
}

export default function App() {
  const [definition, setDefinition] = useState<ReportDefinition | null>(null);
  const [request, setRequest] = useState<ExecuteRequest>(createInitialRequest);
  const [result, setResult] = useState<ExecuteResult | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [queryDuration, setQueryDuration] = useState(0);
  const [renderDuration, setRenderDuration] = useState(0);
  const [normalExporting, setNormalExporting] = useState(false);
  const [largeJob, setLargeJob] = useState<ExportJob | null>(null);
  const [largeExportStarting, setLargeExportStarting] = useState(false);

  const execute = useCallback(async (nextRequest: ExecuteRequest) => {
    setLoading(true);
    setError(null);
    const startedAt = performance.now();
    try {
      const nextResult = await reportApi.execute(nextRequest);
      setRequest(nextRequest);
      setResult(nextResult);
      setQueryDuration(performance.now() - startedAt);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '报表查询失败');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    let active = true;
    const initialize = async () => {
      try {
        const nextDefinition = await reportApi.definition();
        if (!active) {
          return;
        }
        setDefinition(nextDefinition);
        const nextRequest: ExecuteRequest = {
          ...createInitialRequest(),
          startDate: nextDefinition.parameters.find((item) => item.code === 'startDate')?.defaultValue ?? null,
          endDate: nextDefinition.parameters.find((item) => item.code === 'endDate')?.defaultValue ?? null,
        };
        await execute(nextRequest);
      } catch (exception) {
        if (active) {
          setError(exception instanceof Error ? exception.message : '报表初始化失败');
          setLoading(false);
        }
      }
    };

    void initialize();
    return () => {
      active = false;
    };
  }, [execute]);

  useEffect(() => {
    if (!largeJob || !['Pending', 'Running'].includes(largeJob.status)) {
      return;
    }

    let active = true;
    const timer = window.setInterval(async () => {
      try {
        const job = await reportApi.getJob(largeJob.jobNo);
        if (active) {
          setLargeJob(job);
        }
      } catch (exception) {
        if (active) {
          setError(exception instanceof Error ? exception.message : '导出任务查询失败');
        }
      }
    }, 1_000);

    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [largeJob]);

  const progress = useMemo(() => {
    if (!largeJob || largeJob.totalRows === 0) {
      return 0;
    }
    return Math.min(100, Math.round(largeJob.writtenRows / largeJob.totalRows * 100));
  }, [largeJob]);

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    void execute({ ...request, page: 1 });
  };

  const normalExport = async () => {
    setNormalExporting(true);
    setError(null);
    try {
      await reportApi.downloadNormal(request);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '普通导出失败');
    } finally {
      setNormalExporting(false);
    }
  };

  const startLargeExport = async () => {
    setLargeExportStarting(true);
    setError(null);
    try {
      setLargeJob(await reportApi.startLarge(request));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '大文件任务启动失败');
    } finally {
      setLargeExportStarting(false);
    }
  };

  const cancelLargeExport = async () => {
    if (!largeJob) {
      return;
    }
    try {
      setLargeJob(await reportApi.cancelJob(largeJob.jobNo));
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '取消导出失败');
    }
  };

  return (
    <main className="app-shell">
      <header className="app-header">
        <div>
          <div className="app-kicker">HD REPORT CENTER</div>
          <h1>{definition?.name ?? '报表中心核心验证'}</h1>
        </div>
        <div className="runtime-status" title="公式在 Univer Web Worker 中计算">
          <span className="status-dot" />
          Univer Worker
        </div>
      </header>

      <form className="query-band" onSubmit={handleSubmit}>
        <label>
          <span>医院</span>
          <input
            value={request.hospitalId ?? ''}
            onChange={(event) => setRequest({ ...request, hospitalId: event.target.value || null })}
            placeholder="全部医院"
          />
        </label>
        <label>
          <span>科室</span>
          <input
            value={request.deptId ?? ''}
            onChange={(event) => setRequest({ ...request, deptId: event.target.value || null })}
            placeholder="全部科室"
          />
        </label>
        <label>
          <span>人员</span>
          <input
            value={request.personId ?? ''}
            onChange={(event) => setRequest({ ...request, personId: event.target.value || null })}
            placeholder="全部人员"
          />
        </label>
        <label>
          <span>开始日期</span>
          <input
            type="date"
            value={request.startDate ?? ''}
            onChange={(event) => setRequest({ ...request, startDate: event.target.value || null })}
          />
        </label>
        <label>
          <span>结束日期</span>
          <input
            type="date"
            value={request.endDate ?? ''}
            onChange={(event) => setRequest({ ...request, endDate: event.target.value || null })}
          />
        </label>
        <div className="query-actions">
          <button className="button button-primary" type="submit" disabled={loading}>
            {loading ? <LoaderCircle className="spin" size={16} /> : <Search size={16} />}
            查询
          </button>
          <button
            className="button"
            type="button"
            disabled={normalExporting || loading}
            onClick={() => void normalExport()}
            title="普通 Excel 导出"
          >
            {normalExporting ? <LoaderCircle className="spin" size={16} /> : <Download size={16} />}
            普通导出
          </button>
          <button
            className="button"
            type="button"
            disabled={largeExportStarting || loading || largeJob?.status === 'Running'}
            onClick={() => void startLargeExport()}
            title="后台生成 10 万行 Excel"
          >
            {largeExportStarting ? <LoaderCircle className="spin" size={16} /> : <Database size={16} />}
            大文件
          </button>
        </div>
      </form>

      {error && <div className="error-strip" role="alert">{error}</div>}

      {largeJob && (
        <section className="job-strip" aria-label="大文件导出任务">
          <div className="job-summary">
            <Database size={16} />
            <strong>{largeJob.status === 'Completed' ? '大文件已完成' : '大文件导出'}</strong>
            <span>{largeJob.writtenRows.toLocaleString()} / {largeJob.totalRows.toLocaleString()} 行</span>
          </div>
          <div className="progress-track" aria-label={`导出进度 ${progress}%`}>
            <div className="progress-value" style={{ width: `${progress}%` }} />
          </div>
          <div className="job-actions">
            {['Pending', 'Running'].includes(largeJob.status) && (
              <button className="icon-button" type="button" onClick={() => void cancelLargeExport()} title="取消导出" aria-label="取消导出">
                <CircleStop size={17} />
              </button>
            )}
            {largeJob.status === 'Completed' && (
              <button className="button" type="button" onClick={() => void reportApi.downloadJob(largeJob.jobNo)}>
                <FileDown size={16} />
                下载
              </button>
            )}
          </div>
        </section>
      )}

      <section className="report-workspace">
        <div className="report-toolbar">
          <div className="report-stats">
            <span>第 {result?.page ?? 1} 页</span>
            <span>{result?.rows.length ?? 0} 行</span>
            <span>查询 {queryDuration.toFixed(0)} ms</span>
            <span>渲染 {renderDuration.toFixed(0)} ms</span>
          </div>
          <div className="pager">
            <button
              className="icon-button"
              type="button"
              title="上一页"
              aria-label="上一页"
              disabled={loading || !result || result.page <= 1}
              onClick={() => result && void execute({ ...request, page: result.page - 1 })}
            >
              <ChevronLeft size={18} />
            </button>
            <button
              className="icon-button"
              type="button"
              title="下一页"
              aria-label="下一页"
              disabled={loading || !result?.hasMore}
              onClick={() => result && void execute({ ...request, page: result.page + 1 })}
            >
              <ChevronRight size={18} />
            </button>
          </div>
        </div>

        <div className="sheet-frame">
          {definition && result ? (
            <ReportSheet
              definition={definition}
              result={result}
              onRendered={setRenderDuration}
            />
          ) : (
            <div className="loading-sheet"><LoaderCircle className="spin" size={22} /></div>
          )}
        </div>
      </section>
    </main>
  );
}
