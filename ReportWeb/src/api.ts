import type {
  ExecuteRequest,
  ExecuteResult,
  ExportJob,
  ReportDefinition,
  SaveTemplateRequest,
  TemplateDocument,
  TemplateXlsxImportResult,
} from './types';
import type { IWorkbookData } from '@univerjs/presets';

/**
 * 报表中心 POC 的浏览器 API 适配层。
 * 页面只处理业务对象和 Error，不直接依赖 Furion 的统一响应包结构。
 */
const reportCode = 'dept-workload';
const basePath = `/api/report-center/poc/${reportCode}`;

interface ApiEnvelope<T> {
  code: number;
  message?: string;
  result: T;
}

/** 解包 Furion 统一响应；HTTP 失败和业务 code 失败都转换为同一种 Error。 */
async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init);
  const body = (await response.json()) as ApiEnvelope<T>;
  if (!response.ok || body.code !== 200) {
    throw new Error(body.message || `请求失败（${body.code || response.status}）`);
  }

  return body.result;
}

/**
 * 下载接口直接消费 Blob，并优先使用 Content-Disposition 中的 UTF-8 文件名。
 * Object URL 只在本次点击期间存在，触发下载后立即释放，避免反复导出积累浏览器内存。
 */
async function requestDownload(
  path: string,
  init?: RequestInit,
  fallbackFilename = '科室工作量.xlsx',
): Promise<void> {
  const response = await fetch(path, init);

  if (!response.ok) {
    let message = `下载失败（${response.status}）`;
    try {
      const body = (await response.json()) as ApiEnvelope<unknown>;
      message = body.message || message;
    } catch {
      // 非 JSON 错误响应使用 HTTP 状态信息。
    }
    throw new Error(message);
  }

  const blob = await response.blob();
  const disposition = response.headers.get('content-disposition') || '';
  const encodedName = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  const filename = encodedName ? decodeURIComponent(encodedName) : fallbackFilename;
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

/** 固定报表的全部接口。保留导出方法供原核心验证使用，模板编辑页当前不展示导出入口。 */
export const reportApi = {
  definition: () => requestJson<ReportDefinition>(`${basePath}/definition`),

  template: () => requestJson<TemplateDocument>(`${basePath}/template`),

  saveTemplate: (request: SaveTemplateRequest) =>
    requestJson<TemplateDocument>(`${basePath}/template`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /** 导入只返回一个待确认草稿，不会直接覆盖数据库中的模板。 */
  importTemplate: (file: File) => {
    const form = new FormData();
    form.append('file', file);
    return requestJson<TemplateXlsxImportResult>(`${basePath}/template/import-xlsx`, {
      method: 'POST',
      body: form,
    });
  },

  /** 导出当前编辑器快照，包含尚未保存的样式、公式和布局修改。 */
  exportTemplate: (snapshot: IWorkbookData) =>
    requestDownload(
      `${basePath}/template/export-xlsx`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ snapshot }),
      },
      '报表模板.xlsx',
    ),

  execute: (request: ExecuteRequest) =>
    requestJson<ExecuteResult>(`${basePath}/execute`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  downloadNormal: (request: ExecuteRequest) =>
    requestDownload(`${basePath}/exports/normal`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  startLarge: (request: ExecuteRequest) =>
    requestJson<ExportJob>(`${basePath}/exports/large`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  getJob: (jobNo: string) =>
    requestJson<ExportJob>(`/api/report-center/poc/export-jobs/${encodeURIComponent(jobNo)}`),

  cancelJob: (jobNo: string) =>
    requestJson<ExportJob>(`/api/report-center/poc/export-jobs/${encodeURIComponent(jobNo)}/cancel`, {
      method: 'POST',
    }),

  downloadJob: (jobNo: string) =>
    requestDownload(`/api/report-center/poc/export-jobs/${encodeURIComponent(jobNo)}/download`),
};
