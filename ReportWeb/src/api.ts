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
  JnpfMigrationRunResult,
  ReportConfigurationDocument,
  PagedDocument,
  ReportCenterFeatures,
  ReportCreateOptionsDocument,
  ReportDefinition,
  ReportSummaryDocument,
  SaveReportConfigurationRequest,
  SaveTemplateRequest,
  ShareProtectionUnlockResult,
  TemplateDocument,
  TemplateXlsxImportResult,
} from './types';
import { normalizeExportFileName } from './exportFileName';

interface ApiEnvelope<T> {
  code: number;
  message?: string;
  result: T;
}

/**
 * 构造并发送 api 的 request Json 网络请求，随后按接口类型返回响应结果。
 * @param path - path 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param init - init 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, init);
  } catch {
    throw new Error('无法连接后端服务，请确认服务已启动。');
  }

  let body: ApiEnvelope<T>;
  try {
    body = (await response.json()) as ApiEnvelope<T>;
  } catch {
    throw new Error(response.ok
      ? '服务器返回内容不是有效的 JSON。'
      : `后端服务不可用（HTTP ${response.status}）。`);
  }
  if (!response.ok || body.code !== 200) {
    throw new Error(body.message || `请求失败（${body.code || response.status}）`);
  }
  return body.result;
}

/**
 * 构造并发送 api 的 request Download 网络请求，随后按接口类型返回响应结果。
 * @param path - path 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param init - init 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param fallbackFilename - 需要读取、校验或转换的业务值。
 * @param preferredFilename - 用户确认的下载文件名；提供时优先于服务端响应头。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
async function requestDownload(
  path: string,
  init?: RequestInit,
  fallbackFilename = '科室工作量.xlsx',
  preferredFilename?: string,
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
  const filename = preferredFilename?.trim()
    ? normalizeExportFileName(preferredFilename, fallbackFilename)
    : (encodedName ? decodeURIComponent(encodedName) : fallbackFilename);
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

/**
 * 构建 api 所需的 create Report Api 内容，并保证生成结果符合调用约定。
 * @param reportCode - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function createReportApi(reportCode: string) {
  const basePath = `/api/report-center/poc/${encodeURIComponent(reportCode)}`;
  // 第一阶段：整理 createReportApi 已计算的数据，并按方法契约返回最终结果。
  // 第一阶段：整理 createReportApi 已计算的数据，并按方法契约返回最终结果。
  return {
  /**
   * 构造并发送 api 的 definition 网络请求，随后按接口类型返回响应结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  definition: () => requestJson<ReportDefinition>(`${basePath}/definition`),

  /**
   * 构造并发送 api 的 configuration 网络请求，随后按接口类型返回响应结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  configuration: () =>
    requestJson<ReportConfigurationDocument>(`${basePath}/configuration`),

  /**
   * 更新 api 的 save Configuration 状态，并同步相关业务数据。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  saveConfiguration: (request: SaveReportConfigurationRequest) =>
    requestJson<ReportConfigurationDocument>(`${basePath}/configuration`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /**
   * 构造并发送 api 的 unlock Share Protection 网络请求，随后按接口类型返回响应结果。
   * @param password - password 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  unlockShareProtection: (password: string) =>
    requestJson<ShareProtectionUnlockResult>(`${basePath}/share-protection/unlock`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password }),
    }),

  /**
   * 构造并发送 api 的 template 网络请求，随后按接口类型返回响应结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  template: () => requestJson<TemplateDocument>(`${basePath}/template`),

  /**
   * 更新 api 的 save Template 状态，并同步相关业务数据。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  saveTemplate: (request: SaveTemplateRequest) =>
    requestJson<TemplateDocument>(`${basePath}/template`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /**
   * 接收并校验 api 的 import Template 输入，随后写入目标状态。
   * @param file - file 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  importTemplate: (file: File) => {
    // 第2阶段：构造 createReportApi 当前阶段需要的中间数据和执行参数。
    const form = new FormData();
    // 第3阶段：根据 createReportApi 当前分支转换输入数据，并保存该阶段产生的中间结果。
    // 第4阶段：根据 createReportApi 当前分支转换输入数据，并保存该阶段产生的中间结果。
    form.append('file', file);
    // 第2阶段：整理 createReportApi 已计算的数据，并按方法契约返回最终结果。
    // 第3阶段：整理 createReportApi 已计算的数据，并按方法契约返回最终结果。
    return requestJson<TemplateXlsxImportResult>(`${basePath}/template/import-xlsx`, {
      method: 'POST',
      body: form,
    });
  },

  /**
   * 整理 api 的 export Template 数据，并生成可供调用方使用的输出。
   * @param snapshot - 包含工作簿结构、单元格和样式信息的快照数据。
   * @param fileName - 用户确认的下载文件名，可为空以使用服务端默认值。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  exportTemplate: (snapshot: IWorkbookData, fileName?: string) =>
    requestDownload(
      `${basePath}/template/export-xlsx`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ snapshot }),
      },
      '报表模板.xlsx',
      fileName,
    ),

  /**
   * 执行 api 的 execute 核心流程，并保持错误和结果语义稳定。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  execute: (request: ExecuteRequest) =>
    requestJson<ExecuteResult>(`${basePath}/execute`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /**
   * 整理 api 的 download Normal Data 数据，并生成可供调用方使用的输出。
   * @param templateRevision - templateRevision 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @param query - query 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @param fileName - 用户确认的下载文件名，可为空以使用服务端默认值。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  downloadNormalData: (templateRevision: number, query: ExecuteRequest, fileName?: string) =>
    requestDownload(`${basePath}/exports/normal`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ templateRevision, query }),
    }, '科室工作量.xlsx', fileName),

  /**
   * 启动 api 的 start Large Data Export 流程，并建立运行期间所需状态。
   * @param templateRevision - templateRevision 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @param query - query 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  startLargeDataExport: (templateRevision: number, query: ExecuteRequest) =>
    requestJson<ExportJob>(`${basePath}/exports/large`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ templateRevision, query }),
    }),

  /**
   * 查询或读取 api 所需的 get Job 数据，并按调用约定返回结果。
   * @param jobNo - jobNo 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  getJob: (jobNo: string) =>
    requestJson<ExportJob>(`/api/report-center/poc/export-jobs/${encodeURIComponent(jobNo)}`),

  /**
   * 校验 api 的 cancel Job 条件，并返回可供后续流程判断的结果。
   * @param jobNo - jobNo 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  cancelJob: (jobNo: string) =>
    requestJson<ExportJob>(`/api/report-center/poc/export-jobs/${encodeURIComponent(jobNo)}/cancel`, {
      method: 'POST',
    }),

  /**
   * 整理 api 的 download Job 数据，并生成可供调用方使用的输出。
   * @param jobNo - jobNo 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @param fileName - 用户确认的下载文件名，可为空以使用服务端默认值。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  downloadJob: (jobNo: string, fileName?: string) =>
    requestDownload(
      `/api/report-center/poc/export-jobs/${encodeURIComponent(jobNo)}/download`,
      undefined,
      '科室工作量.xlsx',
      fileName,
    ),
  };
}

export const reportApi = createReportApi('dept-workload');

/** 报表中心启动配置使用独立接口，避免菜单显示依赖具体报表或数据集请求。 */
export const reportCenterFeaturesApi = {
  /**
   * 读取报表中心可选功能开关，供入口决定管理菜单和受限路由。
   * @returns 返回报表中心前端功能配置。
   */
  get: () => requestJson<ReportCenterFeatures>('/api/report-center/features'),
};

export const reportCatalogApi = {
  /**
   * 构造并发送 api 的 reports 网络请求，随后按接口类型返回响应结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  reports: () => requestJson<ReportSummaryDocument[]>('/api/report-center/poc/reports'),
  /**
   * 按关键字读取报表管理列表的单页数据，服务端负责筛选和总数统计。
   * @param request - 当前页码、页容量和可选搜索词。
   * @returns 返回当前页报表及分页元数据。
   */
  reportsPage: (request: { page: number; pageSize: number; query: string }) => {
    const params = new URLSearchParams({
      page: String(request.page),
      pageSize: String(request.pageSize),
    });
    if (request.query.trim()) params.set('query', request.query.trim());
    return requestJson<PagedDocument<ReportSummaryDocument>>(
      `/api/report-center/poc/reports/page?${params.toString()}`,
    );
  },
  /**
   * 构建 api 所需的 create Options 内容，并保证生成结果符合调用约定。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  createOptions: () =>
    requestJson<ReportCreateOptionsDocument>('/api/report-center/poc/reports/create-options'),
  /**
   * 构建 api 所需的 create 内容，并保证生成结果符合调用约定。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  create: (request: CreateReportRequest) =>
    requestJson<ReportConfigurationDocument>('/api/report-center/poc/reports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),
};

/** 数据源和数据集是跨报表复用的全局资源，使用独立 API 与报表配置解耦。 */
export const dataAssetApi = {
  /**
   * 构造并发送 api 的 data Sources 网络请求，随后按接口类型返回响应结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  dataSources: () => requestJson<DataSourceDocument[]>('/api/report-center/poc/data-sources'),

  /**
   * 更新 api 的 save Data Source 状态，并同步相关业务数据。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  saveDataSource: (request: DataSourceInput) =>
    requestJson<DataSourceDocument>(
      `/api/report-center/poc/data-sources/${encodeURIComponent(request.code)}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 构造并发送 api 的 test Data Source 网络请求，随后按接口类型返回响应结果。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  testDataSource: (request: DataSourceInput) =>
    requestJson<DataSourceTestResult>('/api/report-center/poc/data-sources/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /**
   * 构造并发送 api 的 datasets 网络请求，随后按接口类型返回响应结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  datasets: () => requestJson<DatasetDocument[]>('/api/report-center/poc/datasets'),
  /**
   * 按关键字读取数据集管理列表的单页数据，服务端负责筛选和总数统计。
   * @param request - 当前页码、页容量和可选搜索词。
   * @returns 返回当前页数据集及分页元数据。
   */
  datasetsPage: (request: { page: number; pageSize: number; query: string }) => {
    const params = new URLSearchParams({
      page: String(request.page),
      pageSize: String(request.pageSize),
    });
    if (request.query.trim()) params.set('query', request.query.trim());
    return requestJson<PagedDocument<DatasetDocument>>(
      `/api/report-center/poc/datasets/page?${params.toString()}`,
    );
  },

  /**
   * 构造并发送 api 的 dataset References 网络请求，随后按接口类型返回响应结果。
   * @param code - 需要读取、校验或转换的业务值。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  datasetReferences: (code: string) =>
    requestJson<DatasetReferenceDocument[]>(
      `/api/report-center/poc/datasets/${encodeURIComponent(code)}/references`,
    ),

  /**
   * 更新 api 的 save Dataset 状态，并同步相关业务数据。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  saveDataset: (request: DatasetDocument) =>
    requestJson<DatasetDocument>(
      `/api/report-center/poc/datasets/${encodeURIComponent(request.code)}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 构造并发送 api 的 preview Dataset 网络请求，随后按接口类型返回响应结果。
   * @param code - 需要读取、校验或转换的业务值。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  previewDataset: (code: string, request: DatasetPreviewRequest) =>
    requestJson<DatasetPreviewResult>(
      `/api/report-center/poc/datasets/${encodeURIComponent(code)}/preview`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
      },
    ),

  /**
   * 构造并发送 api 的 external Parameter Sources 网络请求，随后按接口类型返回响应结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  externalParameterSources: () =>
    requestJson<ExternalParameterSourceDocument[]>(
      '/api/report-center/poc/external-parameter-sources',
    ),

  /**
   * 更新 api 的 save External Parameter Source 状态，并同步相关业务数据。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  saveExternalParameterSource: (request: ExternalParameterSourceInput) =>
    requestJson<ExternalParameterSourceDocument>(
      `/api/report-center/poc/external-parameter-sources/${encodeURIComponent(request.code)}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 构造并发送 api 的 test External Parameter Source 网络请求，随后按接口类型返回响应结果。
   * @param code - 需要读取、校验或转换的业务值。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
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

export const jnpfMigrationApi = {
  /**
   * 构造并发送 api 的 inventory 网络请求，随后按接口类型返回响应结果。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  inventory: (request: {
    jnpfDataSourceCode: string;
    reportDataSourceCode: string;
    maxReports: number;
  }) => requestJson<JnpfMigrationRunResult>('/api/report-center/poc/jnpf-report-migration/inventory', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  }),

  /**
   * 构造并发送 api 的 migrate 网络请求，随后按接口类型返回响应结果。
   * @param request - 本次操作使用的输入数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  migrate: (request: {
    jnpfDataSourceCode: string;
    reportDataSourceCode: string;
    sourceReportIds: string[];
    maxReports: number;
  }) => requestJson<JnpfMigrationRunResult>('/api/report-center/poc/jnpf-report-migration/migrate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  }),
};
