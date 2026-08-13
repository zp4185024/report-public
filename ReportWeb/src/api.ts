import type { IWorkbookData } from '@univerjs/presets';
import type {
  DataSourceDocument,
  DataSourceInput,
  DataSourceTestResult,
  DatasetDocument,
  DatasetPreviewRequest,
  DatasetPreviewResult,
  DatasetReferenceDocument,
  CreateReportRequest,
  ExecuteRequest,
  ExecuteResult,
  ExportJob,
  ExternalParameterSourceDocument,
  ExternalParameterSourceInput,
  ExternalParameterSourceTestRequest,
  ExternalParameterSourceTestResult,
  ReportConfigurationDocument,
  ReportCreateOptionsDocument,
  ReportDefinition,
  ReportSummaryDocument,
  SaveReportConfigurationRequest,
  SaveTemplateRequest,
  ShareProtectionUnlockResult,
  TemplateDocument,
  TemplateXlsxImportResult,
} from './types';

interface ApiEnvelope<T> {
  code: number;
  message?: string;
  result: T;
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init);
  const body = (await response.json()) as ApiEnvelope<T>;
  if (!response.ok || body.code !== 200) {
    throw new Error(body.message || `请求失败（${body.code || response.status}）`);
  }
  return body.result;
}

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

export function createReportApi(reportCode: string) {
  const basePath = `/api/report-center/poc/${encodeURIComponent(reportCode)}`;
  return {
  definition: () => requestJson<ReportDefinition>(`${basePath}/definition`),

  configuration: () =>
    requestJson<ReportConfigurationDocument>(`${basePath}/configuration`),

  saveConfiguration: (request: SaveReportConfigurationRequest) =>
    requestJson<ReportConfigurationDocument>(`${basePath}/configuration`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  unlockShareProtection: (password: string) =>
    requestJson<ShareProtectionUnlockResult>(`${basePath}/share-protection/unlock`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password }),
    }),

  template: () => requestJson<TemplateDocument>(`${basePath}/template`),

  saveTemplate: (request: SaveTemplateRequest) =>
    requestJson<TemplateDocument>(`${basePath}/template`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  importTemplate: (file: File) => {
    const form = new FormData();
    form.append('file', file);
    return requestJson<TemplateXlsxImportResult>(`${basePath}/template/import-xlsx`, {
      method: 'POST',
      body: form,
    });
  },

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

  downloadNormalData: (templateRevision: number, query: ExecuteRequest) =>
    requestDownload(`${basePath}/exports/normal`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ templateRevision, query }),
    }),

  startLargeDataExport: (templateRevision: number, query: ExecuteRequest) =>
    requestJson<ExportJob>(`${basePath}/exports/large`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ templateRevision, query }),
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
}

export const reportApi = createReportApi('dept-workload');

export const reportCatalogApi = {
  reports: () => requestJson<ReportSummaryDocument[]>('/api/report-center/poc/reports'),
  createOptions: () =>
    requestJson<ReportCreateOptionsDocument>('/api/report-center/poc/reports/create-options'),
  create: (request: CreateReportRequest) =>
    requestJson<ReportConfigurationDocument>('/api/report-center/poc/reports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),
};

/** 数据源和数据集是跨报表复用的全局资源，使用独立 API 与报表配置解耦。 */
export const dataAssetApi = {
  dataSources: () => requestJson<DataSourceDocument[]>('/api/report-center/poc/data-sources'),

  saveDataSource: (request: DataSourceInput) =>
    requestJson<DataSourceDocument>(
      `/api/report-center/poc/data-sources/${encodeURIComponent(request.code)}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  testDataSource: (request: DataSourceInput) =>
    requestJson<DataSourceTestResult>('/api/report-center/poc/data-sources/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  datasets: () => requestJson<DatasetDocument[]>('/api/report-center/poc/datasets'),

  datasetReferences: (code: string) =>
    requestJson<DatasetReferenceDocument[]>(
      `/api/report-center/poc/datasets/${encodeURIComponent(code)}/references`,
    ),

  saveDataset: (request: DatasetDocument) =>
    requestJson<DatasetDocument>(
      `/api/report-center/poc/datasets/${encodeURIComponent(request.code)}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  previewDataset: (code: string, request: DatasetPreviewRequest) =>
    requestJson<DatasetPreviewResult>(
      `/api/report-center/poc/datasets/${encodeURIComponent(code)}/preview`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
      },
    ),

  externalParameterSources: () =>
    requestJson<ExternalParameterSourceDocument[]>(
      '/api/report-center/poc/external-parameter-sources',
    ),

  saveExternalParameterSource: (request: ExternalParameterSourceInput) =>
    requestJson<ExternalParameterSourceDocument>(
      `/api/report-center/poc/external-parameter-sources/${encodeURIComponent(request.code)}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  testExternalParameterSource: (
    code: string,
    request: ExternalParameterSourceTestRequest,
  ) => requestJson<ExternalParameterSourceTestResult>(
    `/api/report-center/poc/external-parameter-sources/${encodeURIComponent(code)}/test`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    },
  ),
};
