import type { IWorkbookData } from '@univerjs/presets';
import type {
  DataSyncDryRunRequest,
  DataSyncImportRequest,
  DataSyncImportResult,
  DataSyncPackage,
  DataSyncTryRunRequest,
  DataSyncTryRunResult,
  DataSyncValidateResult,
  DataSourceDocument,
  DataSourceInput,
  DataSourceTestResult,
  DatasetDocument,
  DatasetPreviewRequest,
  DatasetPreviewResult,
  DatasetReferenceDocument,
  CreateReportRequest,
  DeletedDocument,
  ExecuteRequest,
  ExecuteResult,
  ExportJob,
  ExternalParameterSourceDocument,
  ExternalParameterSourceInput,
  ExternalParameterSourceTestRequest,
  ExternalParameterSourceTestResult,
  JnpfMigrationRunResult,
  ReportConfigurationDocument,
  ReportPackage,
  ReportPackageDryRunRequest,
  ReportPackageDryRunResult,
  ReportPackageImportRequest,
  ReportPackageImportResult,
  ReportPackageValidateResult,
  PagedDocument,
  ReportCenterFeatures,
  ReportCreateOptionsDocument,
  ReportDefinition,
  ReportSummaryDocument,
  SaveReportConfigurationRequest,
  SaveTemplateRequest,
  ShareProtectionUnlockResult,
  SyncRunLogDocument,
  SyncSourcePreviewRequest,
  SyncTableColumnsDocument,
  SyncTableMetadataDocument,
  SyncTaskDocument,
  SyncTaskInput,
  TemplateDocument,
  TemplateXlsxImportResult,
} from './types';
import type {
  AiChatRequest,
  AiChatResult,
  AiConnectionTestResult,
  AiSessionClosedDocument,
  AiSettings,
  AiSettingsInput,
  AiSyncMatchRequest,
  AiSyncMatchResult,
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
 * 请求返回 JSON 信封的接口，解出 Furion 统一信封里的 result 后序列化为
 * 2 空格缩进的 JSON 文件触发浏览器下载。适用于返回 DTO 对象而非文件流的导出端点：
 * 信封（code/type/message/result）只用于解析，不落盘，文件内容是纯业务对象。
 * @param path - 请求路径。
 * @param init - fetch 初始化参数；无请求体的 GET 可传 undefined。
 * @param fallbackFilename - 下载文件名；本助手不读取 Content-Disposition，由调用方直接给定。
 * @returns 下载触发后无返回值；网络不通、信封 code 非 200 或响应非 JSON 时抛出面向管理员的中文错误。
 */
async function requestJsonFileDownload(
  path: string,
  init: RequestInit | undefined,
  fallbackFilename: string,
): Promise<void> {
  let response: Response;
  try {
    response = await fetch(path, init);
  } catch {
    throw new Error('无法连接后端服务，请确认服务已启动。');
  }

  // 阶段：解析信封。与 requestJson 共用同一错误策略，导出失败时把后端 message 原样透出。
  let body: ApiEnvelope<unknown>;
  try {
    body = (await response.json()) as ApiEnvelope<unknown>;
  } catch {
    throw new Error(response.ok
      ? '服务器返回内容不是有效的 JSON。'
      : `后端服务不可用（HTTP ${response.status}）。`);
  }
  if (!response.ok || body.code !== 200) {
    throw new Error(body.message || `请求失败（${body.code || response.status}）`);
  }

  // 阶段：序列化纯业务对象并触发下载，避免把信封包装一并写进文件让导入向导误判。
  const blob = new Blob([JSON.stringify(body.result, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fallbackFilename;
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
  /**
   * 删除模板及其参数、模板快照等配套数据；被引用或不存在时后端回传冲突/404 说明。
   * @param reportCode - 待删除模板（报表）的编码。
   * @returns 回传被删除编码的确认回执。
   */
  deleteReport: (reportCode: string) =>
    requestJson<DeletedDocument>(
      `/api/report-center/poc/reports/${encodeURIComponent(reportCode)}`,
      { method: 'DELETE' },
    ),
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
   * 删除未被数据集引用的全局数据源；仍被引用时后端回传 409 和引用数据集清单。
   * @param code - 待删除数据源的编码。
   * @returns 回传被删除编码的确认回执。
   */
  deleteDataSource: (code: string) =>
    requestJson<DeletedDocument>(
      `/api/report-center/poc/data-sources/${encodeURIComponent(code)}`,
      { method: 'DELETE' },
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
   * 删除未被报表引用的全局数据集；仍被引用时后端回传 409 和引用报表清单。
   * @param code - 待删除数据集的编码。
   * @returns 回传被删除编码的确认回执。
   */
  deleteDataset: (code: string) =>
    requestJson<DeletedDocument>(
      `/api/report-center/poc/datasets/${encodeURIComponent(code)}`,
      { method: 'DELETE' },
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
   * 删除未被引用的外部参数源；报表参数或数据集变量仍绑定时后端回传 409 和引用清单。
   * @param code - 待删除接口参数源的编码。
   * @returns 回传被删除编码的确认回执。
   */
  deleteExternalParameterSource: (code: string) =>
    requestJson<DeletedDocument>(
      `/api/report-center/poc/external-parameter-sources/${encodeURIComponent(code)}`,
      { method: 'DELETE' },
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

/**
 * 报表包导入导出接口集：开发机导出 JSON 包，线上向导完成校验、试跑和导入。
 * 包请求体只携带业务定义，不包含连接串或密码。
 */
export const reportPackageApi = {
  /**
   * 下载指定报表的完整报表包 JSON，用于开发机到线上环境的跨环境迁移。
   * 端点返回 Furion 统一信封（code/type/message/result），这里先解出 result 再把
   * 纯报表包对象序列化为 2 空格缩进 JSON 落盘，避免导入向导拿到信封包装后误判类型标识；
   * 文件名固定为 `<reportCode>.report-package.json`，信封 code 非 200 或响应异常时
   * 抛出面向管理员的中文错误信息，不产生半截文件。
   * @param reportCode - 待导出包的报表编码。
   * @returns 下载完成后无业务返回值；失败时抛出面向管理员的错误信息。
   */
  exportReportPackage: (reportCode: string) =>
    requestJsonFileDownload(
      `/api/report-center/poc/reports/${encodeURIComponent(reportCode)}/package`,
      undefined,
      `${reportCode}.report-package.json`,
    ),

  /**
   * 提交整份报表包做服务端结构校验和目标库冲突探测；errors 非空时包不可导入。
   * @param requestPackage - 前端解析出的完整报表包对象。
   * @returns 校验报告（摘要、错误、警告和冲突探测结果）。
   */
  validateReportPackage: (requestPackage: ReportPackage) =>
    requestJson<ReportPackageValidateResult>('/api/report-center/poc/report-packages/validate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ package: requestPackage }),
    }),

  /**
   * 用选定的目标数据源真实执行包内数据集 SQL，返回最多 50 行样例数据供导入前确认。
   * @param request - 包、目标数据源编码和参数样例值。
   * @returns 样例列、行、耗时以及字段契约比对结论。
   */
  dryRunReportPackage: (request: ReportPackageDryRunRequest) =>
    requestJson<ReportPackageDryRunResult>('/api/report-center/poc/report-packages/dry-run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /**
   * 确认导入报表包：按选择的模式写入数据集与报表定义，服务端负责写审计文件。
   * @param request - 包、目标数据源、导入模式、是否替换数据集和是否已试跑。
   * @returns 数据集与报表各自执行的动作摘要及警告。
   */
  importReportPackage: (request: ReportPackageImportRequest) =>
    requestJson<ReportPackageImportResult>('/api/report-center/poc/report-packages/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),
};

/**
 * 数据同步任务接口集：任务 CRUD、映射预览、试跑、后台运行与运行日志，
 * 以及同步包的导出、校验、试跑（dry-run）和导入。包请求体只携带业务定义，不含连接串或密码。
 */
export const dataSyncApi = {
  /**
   * 构造并发送 api 的 tasks 网络请求，列出全部同步任务及其映射定义。
   * @returns 全量同步任务文档列表。
   */
  tasks: () => requestJson<SyncTaskDocument[]>('/api/report-center/poc/data-sync-tasks'),

  /**
   * 按编码读取单个同步任务及其映射定义，供设计器加载最新修订号。
   * @param code - 同步任务编码。
   * @returns 同步任务文档。
   */
  getTask: (code: string) =>
    requestJson<SyncTaskDocument>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}`,
    ),

  /**
   * 新增或更新同步任务；映射与调度在保存时由服务端完成编译级校验。
   * @param code - 同步任务编码。
   * @param request - 任务保存输入（含乐观并发期望修订号）。
   * @returns 保存后的同步任务文档（服务端返回的最新修订号）。
   */
  saveTask: (code: string, request: SyncTaskInput) =>
    requestJson<SyncTaskDocument>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 删除同步任务；运行日志由服务端保留作执行审计，被占用或不存在时后端回传冲突/404。
   * @param code - 待删除同步任务的编码。
   * @returns 回传被删除编码的确认回执。
   */
  deleteTask: (code: string) =>
    requestJson<DeletedDocument>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}`,
      { method: 'DELETE' },
    ),

  /**
   * 预览源数据集的真实列与样例行（不做映射转换），供映射画布拉线；字段携带数据集声明中的中文注释。
   * @param code - 同步任务编码。
   * @param request - 数据集变量取值。
   * @returns 字段清单（含可选注释）与样例行。
   */
  previewMapping: (code: string, request: DatasetPreviewRequest) =>
    requestJson<DatasetPreviewResult>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}/preview-mapping`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 端到端试跑：真实读源并按映射转换（最多 50 行），不写目标库。
   * @param code - 同步任务编码。
   * @param request - 试跑请求（行数上限与变量取值）。
   * @returns 转换前后样例对照与失败统计。
   */
  tryRun: (code: string, request: DataSyncTryRunRequest) =>
    requestJson<DataSyncTryRunResult>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}/try-run`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 启动后台同步并立即返回运行日志占位；防重入由 Running 日志保证，前端据此轮询 runs。
   * @param code - 同步任务编码。
   * @returns 本次运行的初始运行日志。
   */
  runTask: (code: string) =>
    requestJson<SyncRunLogDocument>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}/run`,
      { method: 'POST' },
    ),

  /**
   * 按任务编码列出最近的运行日志（服务端倒序），供任务页展示和轮询。
   * @param code - 同步任务编码。
   * @returns 运行日志列表。
   */
  taskRuns: (code: string) =>
    requestJson<SyncRunLogDocument[]>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}/runs`,
    ),

  /**
   * 读取指定任务的同步包 JSON（源数据集快照 + 映射 + 调度 + 目标描述），
   * 由调用方负责触发浏览器下载；包内不含连接串/密码。
   * @param code - 同步任务编码。
   * @returns 完整同步包对象。
   */
  getTaskPackage: (code: string) =>
    requestJson<DataSyncPackage>(
      `/api/report-center/poc/data-sync-tasks/${encodeURIComponent(code)}/package`,
    ),

  /**
   * 提交整份同步包做服务端结构校验和目标库冲突探测；errors 非空时包不可导入。
   * @param requestPackage - 前端解析出的完整同步包对象。
   * @returns 校验报告（摘要、错误、警告和冲突探测结果）。
   */
  validatePackage: (requestPackage: DataSyncPackage) =>
    requestJson<DataSyncValidateResult>('/api/report-center/poc/data-sync-packages/validate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ package: requestPackage }),
    }),

  /**
   * 用选定的目标数据源真实执行包内源数据集并按映射转换（最多 50 行），不写目标库。
   * @param request - 包、目标数据源编码。
   * @returns 转换前后样例对照与失败统计。
   */
  dryRunPackage: (request: DataSyncDryRunRequest) =>
    requestJson<DataSyncTryRunResult>(
      '/api/report-center/poc/data-sync-packages/dry-run',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 确认导入同步包：服务端重跑全部校验后按冲突策略落库，并记录审计文件。
   * @param request - 包、目标数据源、导入模式与是否已试跑。
   * @returns 数据集与同步任务各自执行的动作摘要及警告。
   */
  importPackage: (request: DataSyncImportRequest) =>
    requestJson<DataSyncImportResult>(
      '/api/report-center/poc/data-sync-packages/import',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),

  /**
   * 列出数据源连接库内的基础表名（仅 TABLE 类型，不含视图），供设计器源取数选表与目标表下拉。
   * @param dataSourceCode - 数据源编码。
   * @returns 表清单文档。
   */
  listTables: (dataSourceCode: string) =>
    requestJson<SyncTableMetadataDocument>(
      `/api/report-center/poc/data-sync-tasks/metadata/tables?dataSourceCode=${encodeURIComponent(dataSourceCode)}`,
    ),

  /**
   * 读取目标表的真实列清单（列名/数据库类型/可空/长度/注释），供映射表格的目标字段下拉与注释展示。
   * @param dataSourceCode - 数据源编码。
   * @param table - 表名（按目录原样大小写传入）。
   * @returns 列清单文档（含可选列注释）。
   */
  listTableColumns: (dataSourceCode: string, table: string) =>
    requestJson<SyncTableColumnsDocument>(
      `/api/report-center/poc/data-sync-tasks/metadata/columns?dataSourceCode=${encodeURIComponent(dataSourceCode)}&table=${encodeURIComponent(table)}`,
    ),

  /**
   * 源取数预览（任务保存前可用）：对“数据源 + SQL”做真实受限查询（≤50 行，不写库），
   * 返回动态列与样例行，供映射表格在任务尚未落库时取到真实源字段。
   * @param request - 源取数预览请求（数据源编码 + SQL 文本 + 选表模式下的可选源表名）。
   * @returns 字段清单（含可选列注释）与样例行。
   */
  previewSource: (request: SyncSourcePreviewRequest) =>
    requestJson<DatasetPreviewResult>(
      '/api/report-center/poc/data-sync-tasks/preview-source',
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      },
    ),
};

/** 产品内 AI 能力接口（阶段一：设置、连通性测试、只读查数对话）；后端在 AI 关闭时按 AI_DISABLED fail-fast。 */
export const aiApi = {
  /**
   * 读取 AI 设置（ApiKey 打码）；未配置时返回默认关闭配置，前端据此控制 AI 入口渲染。
   * @returns 打码后的设置文档。
   */
  getSettings: () => requestJson<AiSettings>('/api/report-center/ai/settings'),

  /**
   * 保存 AI 设置；apiKey 传空表示保留服务端已存值。
   * @param request - 设置输入。
   * @returns 保存后的打码设置文档。
   */
  saveSettings: (request: AiSettingsInput) =>
    requestJson<AiSettings>('/api/report-center/ai/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /**
   * 对已保存配置做连通性测试；AI 未启用或配置不完整时后端回传 AI_DISABLED 错误。
   * @returns 测试结果（成功/失败原因/耗时）。
   */
  testConnection: () =>
    requestJson<AiConnectionTestResult>('/api/report-center/ai/settings/test', { method: 'POST' }),

  /**
   * 模板 AI 对话：一次回复内完成工具循环；sessionId 为空表示新会话。
   * @param request - 对话请求（会话号、数据源与消息文本）。
   * @returns 本轮新增消息与会话号。
   */
  chat: (request: AiChatRequest) =>
    requestJson<AiChatResult>('/api/report-center/ai/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),

  /**
   * 显式结束会话并清空服务端内存上下文；会话不存在时同样返回成功（幂等）。
   * @param sessionId - 待关闭的会话编号。
   * @returns 关闭回执。
   */
  closeSession: (sessionId: string) =>
    requestJson<AiSessionClosedDocument>(
      `/api/report-center/ai/sessions/${encodeURIComponent(sessionId)}`,
      { method: 'DELETE' },
    ),

  /**
   * 同步 AI 智能字段匹配：同名基线 + LLM 歧义补齐 + 白名单转换链建议；
   * 服务端不落库，AI 关闭时按 AI_DISABLED fail-fast。
   * @param request - 匹配请求（直接载荷或 taskCode）。
   * @returns 建议列表、降级标记与原因。
   */
  syncMatch: (request: AiSyncMatchRequest) =>
    requestJson<AiSyncMatchResult>('/api/report-center/ai/sync-match', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    }),
};
