import { LocaleType, type IWorkbookData } from '@univerjs/presets';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createReportApi,
  dataAssetApi,
  jnpfMigrationApi,
  reportApi,
  reportCatalogApi,
  reportCenterFeaturesApi,
} from './api';
import type {
  DataSourceInput,
  CreateReportRequest,
  DataSourceDocument,
  DatasetDocument,
  DatasetPreviewRequest,
  ExecuteRequest,
  ExportJob,
  ReportConfigurationDocument,
  ReportSummaryDocument,
  SaveTemplateRequest,
  SaveReportConfigurationRequest,
  TemplateBinding,
  TemplateDocument,
  TemplateXlsxImportResult,
} from './types';

const snapshot: IWorkbookData = {
  id: 'template-workbook',
  name: '模板',
  appVersion: '1.0.0',
  locale: LocaleType.ZH_CN,
  styles: {},
  sheetOrder: [],
  sheets: {},
};

const binding: TemplateBinding = {
  version: 1,
  parameterBindings: [],
  detailBinding: null,
};

const document: TemplateDocument = {
  reportCode: 'dept-workload',
  snapshot,
  binding,
  revision: 3,
  validationWarning: null,
};

const executeRequest: ExecuteRequest = {
  page: 3,
  parameters: {
    hospitalId: 'H01',
    deptId: 'D02',
    personId: 'P03',
    startDate: '2026-07-01',
    endDate: '2026-07-14',
  },
};

const dataSourceInput: DataSourceInput = {
  code: 'report_center',
  name: '报表中心 SQL Server',
  dbType: 'SqlServer',
  server: '127.0.0.1',
  port: 1433,
  database: 'report_center',
  userName: 'report_reader',
  password: null,
  timeoutSeconds: 30,
  maxRows: 20_000,
  enabled: true,
  revision: 1,
};
const dataSourceDocument: DataSourceDocument = {
  code: dataSourceInput.code,
  name: dataSourceInput.name,
  dbType: dataSourceInput.dbType,
  server: dataSourceInput.server,
  port: dataSourceInput.port,
  database: dataSourceInput.database,
  userName: dataSourceInput.userName,
  passwordConfigured: true,
  timeoutSeconds: dataSourceInput.timeoutSeconds,
  maxRows: dataSourceInput.maxRows,
  enabled: dataSourceInput.enabled,
  revision: dataSourceInput.revision,
};
const datasetDocument: DatasetDocument = {
  code: 'detail',
  name: '科室工作量明细',
  dataSourceCode: 'report_center',
  queryText: 'SELECT @deptId AS departmentName',
  variables: [{
    code: 'deptId',
    displayName: '科室',
    dataType: 'string',
    required: false,
  }],
  fields: [{
    code: 'departmentName',
    displayName: '科室',
    dataType: 'string',
  }],
  timeoutSeconds: 30,
  maxRows: 20_000,
  enabled: true,
  revision: 1,
};
const configuration: ReportConfigurationDocument = {
  reportCode: 'dept-workload',
  name: '科室工作量统计',
  sheetName: '科室工作量',
  preview: { mode: 'Full', pageSize: 20, showExactTotal: false },
  export: {
    defaultMode: 'Normal',
    normalMaxRows: 20_000,
    normalMaxCells: 500_000,
    normalTimeoutSeconds: 60,
    largeExportEnabled: true,
    rowsPerSheet: 1_000_000,
    fileRetentionHours: 24,
  },
  datasetCode: 'detail',
  datasets: [{
    code: 'detail',
    name: '科室工作量明细',
    dataSourceCode: 'report_center',
    variables: [{
      code: 'deptId',
      displayName: '科室',
      dataType: 'string',
      required: false,
    }],
    fields: [{
      code: 'departmentName',
      displayName: '科室',
      dataType: 'string',
    }],
    enabled: true,
  }],
  parameters: [{
    code: 'deptId',
    displayName: '科室',
    controlType: 'text',
    sourceType: 'Form',
    defaultValue: null,
    visible: true,
    dataType: 'string',
    defaultExpression: null,
    fixedValue: null,
    readOnly: false,
    required: false,
    allowUrlPrefill: true,
    urlKey: 'deptId',
  }],
  fields: [{
    code: 'departmentName',
    displayName: '科室',
    dataType: 'string',
  }],
  revision: 3,
};

const saveConfigurationRequest: SaveReportConfigurationRequest = {
  name: configuration.name,
  sheetName: configuration.sheetName,
  preview: configuration.preview,
  export: configuration.export,
  datasetCode: configuration.datasetCode,
  parameters: configuration.parameters,
  revision: configuration.revision,
  shareProtection: { enabled: false, newPassword: null },
};

const fetchMock = vi.fn();

/**
 * 验证 json Response 场景下的预期行为，并防止相关逻辑回归。
 * @param body - 符合当前接口契约的请求输入数据。
 * @param status - status 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: vi.fn().mockResolvedValue(body),
  } as unknown as Response;
}

/**
 * 验证 invalid Json Response 场景下的预期行为，并防止相关逻辑回归。
 * @param status - status 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function invalidJsonResponse(status: number): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: vi.fn().mockRejectedValue(new SyntaxError('Unexpected end of JSON input')),
  } as unknown as Response;
}

/**
 * 验证 download Response 场景下的预期行为，并防止相关逻辑回归。
 * @param filename - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function downloadResponse(filename: string): Response {
  return {
    ok: true,
    status: 200,
    blob: vi.fn().mockResolvedValue(new Blob(['xlsx'])),
    headers: {
      get: vi.fn().mockReturnValue(`attachment; filename*=UTF-8''${encodeURIComponent(filename)}`),
    },
  } as unknown as Response;
}

describe('reportApi template endpoints', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('loads the editable template from the report template path', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: document }));

    await expect(reportApi.template()).resolves.toEqual(document);

    expect(fetchMock).toHaveBeenCalledOnce();
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/dept-workload/template',
      undefined,
    );
  });

  it('loads the template catalog from the report list path', async () => {
    const reports: ReportSummaryDocument[] = [{
      reportCode: 'dept-workload',
      name: '科室工作量统计',
      sheetName: '科室工作量',
      datasetCode: 'detail',
      enabled: true,
      configurationRevision: 2,
      templateRevision: 3,
      updatedAt: '2026-08-11T10:30:00+08:00',
    }];
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: reports }));

    await expect(reportCatalogApi.reports()).resolves.toEqual(reports);

    expect(fetchMock).toHaveBeenCalledWith('/api/report-center/poc/reports', undefined);
  });

  it('loads one filtered report catalog page with server-side pagination', async () => {
    const result = {
      items: [],
      page: 2,
      pageSize: 15,
      total: 16,
      totalPages: 2,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(reportCatalogApi.reportsPage({
      page: 2,
      pageSize: 15,
      query: 'jnpf_user',
    })).resolves.toEqual(result);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/reports/page?page=2&pageSize=15&query=jnpf_user',
      undefined,
    );
  });

  it('loads optional report center feature switches', async () => {
    const features = { jnpfMigrationMenuEnabled: false };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: features }));

    await expect(reportCenterFeaturesApi.get()).resolves.toEqual(features);
    expect(fetchMock).toHaveBeenCalledWith('/api/report-center/features', undefined);
  });

  it('loads enabled datasets for the template creation form', async () => {
    const options = {
      datasets: configuration.datasets,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: options }));

    await expect(reportCatalogApi.createOptions()).resolves.toEqual(options);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/reports/create-options',
      undefined,
    );
  });

  it('creates a report configuration before opening the template editor', async () => {
    const request: CreateReportRequest = {
      reportCode: 'outpatient-log',
      name: '门诊日志',
      sheetName: '门诊日志',
      datasetCode: 'detail',
    };
    const created = {
      ...configuration,
      reportCode: request.reportCode,
      name: request.name,
      sheetName: request.sheetName,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: created }));

    await expect(reportCatalogApi.create(request)).resolves.toEqual(created);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/reports');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
  });

  it('uses the selected report code for every editor endpoint', async () => {
    const outpatientApi = createReportApi('outpatient-log');
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: document }));

    await outpatientApi.template();

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/outpatient-log/template',
      undefined,
    );
  });

  it('loads the masked report configuration from its management path', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: configuration }));

    await expect(reportApi.configuration()).resolves.toEqual(configuration);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/dept-workload/configuration',
      undefined,
    );
  });

  it('saves the editable report configuration with optimistic revision', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: configuration }));

    await expect(reportApi.saveConfiguration(saveConfigurationRequest)).resolves.toEqual(configuration);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/dept-workload/configuration');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body as string)).toEqual(saveConfigurationRequest);
  });

  it('tests a data source without reading its stored password', async () => {
    const result = { success: true, durationMilliseconds: 12, databaseVersion: '16.0' };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(dataAssetApi.testDataSource(dataSourceInput)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sources/test');
    expect(JSON.parse(init.body as string)).toEqual(dataSourceInput);
  });

  it('loads global data sources outside the report configuration path', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: [dataSourceDocument] }));

    await expect(dataAssetApi.dataSources()).resolves.toEqual([dataSourceDocument]);
    expect(fetchMock).toHaveBeenCalledWith('/api/report-center/poc/data-sources', undefined);
  });

  it('loads one filtered dataset page with server-side pagination', async () => {
    const result = {
      items: [],
      page: 1,
      pageSize: 30,
      total: 3,
      totalPages: 1,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(dataAssetApi.datasetsPage({
      page: 1,
      pageSize: 30,
      query: 'detail',
    })).resolves.toEqual(result);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/datasets/page?page=1&pageSize=30&query=detail',
      undefined,
    );
  });

  it('loads every report that references a reusable dataset', async () => {
    const references = [{
      reportCode: 'dept-workload',
      reportName: '科室工作量统计',
      sheetName: '科室工作量',
      enabled: true,
    }];
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: references }));

    await expect(dataAssetApi.datasetReferences('detail')).resolves.toEqual(references);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/datasets/detail/references',
      undefined,
    );
  });

  it('saves a reusable dataset by its global code', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: datasetDocument }));

    await expect(dataAssetApi.saveDataset(datasetDocument)).resolves.toEqual(datasetDocument);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/datasets/detail');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(init.body as string)).toEqual(datasetDocument);
  });

  it('previews a saved reusable dataset by its global code', async () => {
    const request: DatasetPreviewRequest = {
      parameters: { deptId: 'D02' },
    };
    const result = {
      fields: [{ code: 'departmentName', displayName: 'departmentName', dataType: 'string' }],
      rows: [['检验科']],
      hasMore: false,
      limit: 50,
      durationMilliseconds: 8,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(dataAssetApi.previewDataset('detail', request)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/datasets/detail/preview');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
  });

  it('saves only snapshot, binding and revision as JSON with PUT', async () => {
    const request: SaveTemplateRequest = {
      snapshot,
      binding,
      revision: 3,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: document }));

    await expect(reportApi.saveTemplate(request)).resolves.toEqual(document);

    expect(fetchMock).toHaveBeenCalledOnce();
    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/dept-workload/template');
    expect(init.method).toBe('PUT');
    expect(init.headers).toEqual({ 'Content-Type': 'application/json' });
    expect(JSON.parse(init.body as string)).toEqual({
      snapshot,
      binding,
      revision: 3,
    });
  });

  it('throws the envelope message when the envelope code is not 200', async () => {
    fetchMock.mockResolvedValue(jsonResponse({
      code: 409,
      message: '模板版本已过期',
      result: null,
    }));

    await expect(reportApi.template()).rejects.toThrow('模板版本已过期');
  });

  it('reports an unavailable backend when the proxy returns an empty error response', async () => {
    fetchMock.mockResolvedValue(invalidJsonResponse(502));

    await expect(dataAssetApi.externalParameterSources())
      .rejects.toThrow('后端服务不可用（HTTP 502）。');
  });

  it('reports a connection failure when fetch cannot reach the backend', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));

    await expect(dataAssetApi.externalParameterSources())
      .rejects.toThrow('无法连接后端服务，请确认服务已启动。');
  });

  it('uploads one xlsx file as multipart form data without forcing a content type', async () => {
    const file = new File(['xlsx'], '测试模板.xlsx', {
      type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    });
    const imported: TemplateXlsxImportResult = {
      snapshot,
      binding,
      univerVersion: '0.25.1',
      mappingVersion: 1,
      usedCellCount: 8,
      warnings: [],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: imported }));

    await expect(reportApi.importTemplate(file)).resolves.toEqual(imported);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/dept-workload/template/import-xlsx');
    expect(init.method).toBe('POST');
    expect(init.headers).toBeUndefined();
    expect(init.body).toBeInstanceOf(FormData);
    expect((init.body as FormData).get('file')).toBe(file);
  });

  it('posts the current snapshot when exporting an editable template', async () => {
    const click = vi.fn();
    const anchor = { href: '', download: '', click };
    const createObjectURL = vi.fn().mockReturnValue('blob:template');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('document', { createElement: vi.fn().mockReturnValue(anchor) });
    vi.stubGlobal('URL', { createObjectURL, revokeObjectURL });
    fetchMock.mockResolvedValue(downloadResponse('科室工作量统计.xlsx'));

    await reportApi.exportTemplate(snapshot);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/dept-workload/template/export-xlsx');
    expect(init.method).toBe('POST');
    expect(init.headers).toEqual({ 'Content-Type': 'application/json' });
    expect(JSON.parse(init.body as string)).toEqual({ snapshot });
    expect(anchor.download).toBe('科室工作量统计.xlsx');
    expect(click).toHaveBeenCalledOnce();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:template');
  });

  it('sends the saved revision and query when downloading report data', async () => {
    const anchor = { href: '', download: '', click: vi.fn() };
    vi.stubGlobal('document', { createElement: vi.fn().mockReturnValue(anchor) });
    vi.stubGlobal('URL', {
      createObjectURL: vi.fn().mockReturnValue('blob:data'),
      revokeObjectURL: vi.fn(),
    });
    fetchMock.mockResolvedValue(downloadResponse('科室工作量_数据.xlsx'));

    await reportApi.downloadNormalData(3, executeRequest);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/dept-workload/exports/normal');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({
      templateRevision: 3,
      query: executeRequest,
    });
    expect(anchor.download).toBe('科室工作量_数据.xlsx');
  });

  /** 验证用户确认的文件名会覆盖服务端 Content-Disposition 下载名。 */
  it('uses a custom filename when downloading report data', async () => {
    const anchor = { href: '', download: '', click: vi.fn() };
    vi.stubGlobal('document', { createElement: vi.fn().mockReturnValue(anchor) });
    vi.stubGlobal('URL', {
      createObjectURL: vi.fn().mockReturnValue('blob:custom-data'),
      revokeObjectURL: vi.fn(),
    });
    fetchMock.mockResolvedValue(downloadResponse('服务端默认.xlsx'));

    await reportApi.downloadNormalData(3, executeRequest, '门诊汇总');

    expect(anchor.download).toBe('门诊汇总.xlsx');
  });

  it('sends the saved revision and query when starting a large data export', async () => {
    const job: ExportJob = {
      jobNo: 'job-1',
      status: 'Pending',
      totalRows: 137,
      writtenRows: 0,
      error: null,
      createdAt: '2026-07-15T10:00:00+08:00',
      completedAt: null,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: job }));

    await expect(reportApi.startLargeDataExport(3, executeRequest)).resolves.toEqual(job);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/dept-workload/exports/large');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({
      templateRevision: 3,
      query: executeRequest,
    });
  });

  it('uses the JNPF report migration endpoints without sending credentials', async () => {
    const result = {
      inventory: [],
      results: [],
      reportDataSourceCode: 'report_center',
      reportDataSourceName: '报表运行数据源',
      reportDataSourceDbType: 'SqlServer',
      writeTargetDatabase: 'hd_report_center',
      writeTargetDbType: 'SqlServer',
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(jnpfMigrationApi.inventory({
      jnpfDataSourceCode: 'jnpf_metadata',
      reportDataSourceCode: 'report_center',
      maxReports: 2,
    })).resolves.toEqual(result);
    const [inventoryPath, inventoryInit] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(inventoryPath).toBe('/api/report-center/poc/jnpf-report-migration/inventory');
    expect(JSON.parse(inventoryInit.body as string)).toEqual({
      jnpfDataSourceCode: 'jnpf_metadata',
      reportDataSourceCode: 'report_center',
      maxReports: 2,
    });
    expect(String(inventoryInit.body)).not.toContain('password');

    await expect(jnpfMigrationApi.migrate({
      jnpfDataSourceCode: 'jnpf_metadata',
      reportDataSourceCode: 'report_center',
      sourceReportIds: ['report-1'],
      maxReports: 1,
    })).resolves.toEqual(result);
    expect(fetchMock.mock.calls[1][0]).toBe('/api/report-center/poc/jnpf-report-migration/migrate');
  });
});
