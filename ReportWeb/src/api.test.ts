import { LocaleType, type IWorkbookData } from '@univerjs/presets';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createReportApi,
  dataAssetApi,
  dataSyncApi,
  jnpfMigrationApi,
  reportApi,
  reportCatalogApi,
  reportCenterFeaturesApi,
  reportPackageApi,
} from './api';
import type {
  DataSyncImportRequest,
  DataSyncImportResult,
  DataSyncPackage,
  DataSyncTryRunRequest,
  DataSyncTryRunResult,
  DataSyncValidateResult,
  DataSourceInput,
  CreateReportRequest,
  DataSourceDocument,
  DatasetDocument,
  DatasetPreviewRequest,
  ExecuteRequest,
  ExportJob,
  ReportConfigurationDocument,
  ReportPackage,
  ReportPackageDryRunRequest,
  ReportPackageDryRunResult,
  ReportPackageImportRequest,
  ReportPackageImportResult,
  ReportPackageValidateResult,
  ReportSummaryDocument,
  SaveTemplateRequest,
  SaveReportConfigurationRequest,
  SyncRunLogDocument,
  SyncTaskDocument,
  SyncTaskInput,
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

  /** 验证删除模板走 DELETE 且回传删除确认编码。 */
  it('deletes a report with its configuration and template via DELETE', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: { code: 'outpatient-log' } }));

    await expect(reportCatalogApi.deleteReport('outpatient-log'))
      .resolves.toEqual({ code: 'outpatient-log' });

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/reports/outpatient-log');
    expect(init.method).toBe('DELETE');
  });

  /** 验证数据源、数据集和接口参数源分别按各自编码发起 DELETE 请求。 */
  it('deletes data sources, datasets and external parameter sources by code', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: { code: 'ignored' } }));

    await dataAssetApi.deleteDataSource('his_db');
    await dataAssetApi.deleteDataset('detail');
    await dataAssetApi.deleteExternalParameterSource('his');

    expect(fetchMock.mock.calls[0][0]).toBe('/api/report-center/poc/data-sources/his_db');
    expect(fetchMock.mock.calls[1][0]).toBe('/api/report-center/poc/datasets/detail');
    expect(fetchMock.mock.calls[2][0]).toBe('/api/report-center/poc/external-parameter-sources/his');
    for (const call of fetchMock.mock.calls as unknown as [string, RequestInit][]) {
      expect(call[1].method).toBe('DELETE');
    }
  });

  /** 验证删除被引用资源时后端 409 信封里的引用清单会原样抛给页面提示。 */
  it('surfaces the backend reference conflict message when deletion is blocked', async () => {
    fetchMock.mockResolvedValue(jsonResponse({
      code: 409,
      message: 'DATASET_IN_USE: 数据集被门诊日志引用',
      result: null,
    }, 409));

    await expect(dataAssetApi.deleteDataset('detail'))
      .rejects.toThrow('DATASET_IN_USE: 数据集被门诊日志引用');
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

describe('reportPackageApi endpoints', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /** 报表包固定为 JSON 下载；构造一份最小包供校验/试跑/导入请求体复用。 */
  const requestPackage = {
    packageKind: 'hd-report-center.report-package',
    packageVersion: 1,
    exportedAt: '2026-11-02T10:00:00+08:00',
    sourceDataSourceCode: 'report_center',
    sourceDataSourceType: 'SqlServer',
    report: {
      code: 'dept-workload',
      name: '科室工作量统计',
      sheetName: '科室工作量',
      datasetCode: 'detail',
      previewSettings: { mode: 'Full', pageSize: 20, showExactTotal: false },
      exportSettings: {
        defaultMode: 'Normal',
        normalMaxRows: 20_000,
        normalMaxCells: 500_000,
        normalTimeoutSeconds: 60,
        largeExportEnabled: true,
        rowsPerSheet: 1_000_000,
        fileRetentionHours: 24,
      },
      fieldPresentations: [],
      isEnabled: true,
    },
    parameters: [],
    template: { snapshot, binding, revision: 4 },
    dataset: {
      code: 'detail',
      name: '科室工作量明细',
      queryText: 'SELECT 1 AS value',
      fieldSchema: { variables: [], fields: [] },
      timeoutSeconds: 30,
      maxRows: 20_000,
      isEnabled: true,
    },
  } satisfies ReportPackage;

  /** 验证导出包按报表编码请求信封接口，解出 result 后以 2 空格缩进 JSON 落盘且不再携带信封字段。 */
  it('unwraps the envelope and downloads the pure package json by report code', async () => {
    const anchor = { href: '', download: '', click: vi.fn() };
    const createObjectURL = vi.fn().mockReturnValue('blob:package');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('document', { createElement: vi.fn().mockReturnValue(anchor) });
    vi.stubGlobal('URL', { createObjectURL, revokeObjectURL });
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: requestPackage }));

    await reportPackageApi.exportReportPackage('dept-workload');

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/reports/dept-workload/package');
    expect(init).toBeUndefined();
    expect(anchor.download).toBe('dept-workload.report-package.json');
    expect(anchor.href).toBe('blob:package');
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:package');
    // 落盘内容必须是解包后的纯报表包对象（与 JSON.stringify(…, null, 2) 完全一致），不得残留信封。
    const blob = createObjectURL.mock.calls[0][0] as Blob;
    const text = await blob.text();
    expect(text).toBe(JSON.stringify(requestPackage, null, 2));
    const saved = JSON.parse(text) as Partial<ReportPackage>;
    expect(saved.packageKind).toBe('hd-report-center.report-package');
    expect('code' in (JSON.parse(text) as object)).toBe(false);
  });

  /** 验证信封 code 非 200 时把后端 message 抛给页面提示，不产生错误内容文件。 */
  it('surfaces the backend error when the package envelope reports failure', async () => {
    fetchMock.mockResolvedValue(jsonResponse({
      code: 404,
      message: '报表不存在',
      result: null,
    }, 404));

    await expect(reportPackageApi.exportReportPackage('missing'))
      .rejects.toThrow('报表不存在');
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/reports/missing/package',
      undefined,
    );
  });

  /** 验证响应不是有效 JSON 时抛出可读错误，而不是把原始响应当作文件落盘。 */
  it('reports a readable error when the package endpoint returns a non-json body', async () => {
    fetchMock.mockResolvedValue(invalidJsonResponse(200));

    await expect(reportPackageApi.exportReportPackage('dept-workload'))
      .rejects.toThrow('服务器返回内容不是有效的 JSON');
  });

  /** 验证校验请求把整份包作为 package 字段提交并回传冲突探测结果。 */
  it('posts the whole package to the validate endpoint', async () => {
    const result: ReportPackageValidateResult = {
      ok: true,
      errors: [],
      warnings: [{ code: 'PARAM_URL_PREFILL_SUGGESTED', message: '参数 deptId 建议启用 URL 预填' }],
      packageSummary: {
        reportCode: 'dept-workload',
        reportName: '科室工作量统计',
        datasetCode: 'detail',
        parameterCount: 2,
        fieldCount: 8,
        sourceDataSourceType: 'SqlServer',
      },
      collisions: {
        reportCodeExists: false,
        datasetCodeExists: true,
        datasetIdentical: false,
        datasetReferencedByOther: null,
      },
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(reportPackageApi.validateReportPackage(requestPackage)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/report-packages/validate');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ package: requestPackage });
  });

  /** 验证试跑请求携带目标数据源与参数样例值，并返回列、行与耗时。 */
  it('posts package, target data source and parameter values to dry-run', async () => {
    const result: ReportPackageDryRunResult = {
      columns: ['departmentName', 'workload'],
      rows: [
        { departmentName: '内科', workload: 12 },
        { departmentName: '外科', workload: null },
      ],
      elapsedMilliseconds: 86,
      rowCountCapped: false,
      fieldSchemaMatch: true,
      fieldSchemaMismatches: [],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    const request: ReportPackageDryRunRequest = {
      package: requestPackage,
      targetDataSourceCode: 'his_pro',
      parameterValues: { startDate: '2026-11' },
    };
    await expect(reportPackageApi.dryRunReportPackage(request)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/report-packages/dry-run');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
  });

  /** 验证导入请求包含模式、替换数据集与是否试跑标记，并返回动作摘要。 */
  it('posts mode and dataset decision to the import endpoint', async () => {
    const result: ReportPackageImportResult = {
      reportCode: 'dept-workload',
      datasetAction: 'Reused',
      reportAction: 'Created',
      warnings: ['分享保护口令未随包迁移，请在线上重新设置'],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    const request: ReportPackageImportRequest = {
      package: requestPackage,
      targetDataSourceCode: 'his_pro',
      mode: 'Create',
      replaceDataset: false,
      dryRunPerformed: true,
    };
    await expect(reportPackageApi.importReportPackage(request)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/report-packages/import');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
  });
});

describe('dataSyncApi endpoints', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /** 构造一份最小同步任务文档，供列表、读取与保存用例复用。 */
  const taskDocument: SyncTaskDocument = {
    code: 'his_outpatient_sync',
    name: '门诊挂号同步',
    sourceDatasetCode: 'detail',
    targetDataSourceCode: 'ods_db',
    targetTable: 'ods_registration',
    conflictPolicy: 'TruncateFirst',
    scheduleType: 'Daily',
    scheduleTime: '02:30',
    intervalMinutes: 0,
    enabled: true,
    revision: 4,
    lastRunAt: '2026-11-03T02:30:05+08:00',
    mappings: [{
      sourceField: 'cardNo',
      targetField: 'CARD_NO',
      targetType: 'string',
      length: 16,
      nullable: true,
      converters: [{ type: 'trim', args: null }],
    }],
  };

  /** 验证任务列表按固定路径发起 GET 并返回全量文档。 */
  it('lists sync tasks from the collection path', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: [taskDocument] }));

    await expect(dataSyncApi.tasks()).resolves.toEqual([taskDocument]);
    expect(fetchMock).toHaveBeenCalledWith('/api/report-center/poc/data-sync-tasks', undefined);
  });

  /** 验证按编码读取任务走编码段路径。 */
  it('reads one sync task by its encoded code', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: taskDocument }));

    await expect(dataSyncApi.getTask('his_outpatient_sync')).resolves.toEqual(taskDocument);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/data-sync-tasks/his_outpatient_sync',
      undefined,
    );
  });

  /** 验证保存任务以 PUT 提交完整输入（含映射与乐观并发修订号），body 不含密码字段。 */
  it('saves a sync task via PUT with mappings and revision', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: taskDocument }));

    const request: SyncTaskInput = {
      code: 'his_outpatient_sync',
      name: '门诊挂号同步',
      sourceDatasetCode: 'detail',
      targetDataSourceCode: 'ods_db',
      targetTable: 'ods_registration',
      conflictPolicy: 'TruncateFirst',
      scheduleType: 'Daily',
      scheduleTime: '02:30',
      intervalMinutes: 0,
      mappings: taskDocument.mappings,
      enabled: true,
      revision: 3,
    };
    await expect(dataSyncApi.saveTask(request.code, request)).resolves.toEqual(taskDocument);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-tasks/his_outpatient_sync');
    expect(init.method).toBe('PUT');
    expect(init.headers).toEqual({ 'Content-Type': 'application/json' });
    expect(JSON.parse(init.body as string)).toEqual(request);
    expect(String(init.body)).not.toContain('password');
  });

  /** 验证删除任务走 DELETE 并回传删除确认编码。 */
  it('deletes a sync task via DELETE', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: { code: 'his_outpatient_sync' } }));

    await expect(dataSyncApi.deleteTask('his_outpatient_sync'))
      .resolves.toEqual({ code: 'his_outpatient_sync' });

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-tasks/his_outpatient_sync');
    expect(init.method).toBe('DELETE');
  });

  /** 验证映射预览按任务编码 POST 参数字典并返回字段与样例行。 */
  it('posts dataset parameters to the preview-mapping endpoint', async () => {
    const request: DatasetPreviewRequest = { parameters: { deptId: 'D02' } };
    const result = {
      fields: [{ code: 'cardNo', displayName: 'cardNo', dataType: 'string' }],
      rows: [['C001']],
      hasMore: false,
      limit: 50,
      durationMilliseconds: 9,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(dataSyncApi.previewMapping('his_outpatient_sync', request)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-tasks/his_outpatient_sync/preview-mapping');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
  });

  /** 验证试跑请求携带行数上限并返回前后对照样例。 */
  it('posts the row limit to try-run and returns converted samples', async () => {
    const result: DataSyncTryRunResult = {
      ok: true,
      targetColumns: ['CARD_NO'],
      samples: [{ source: { cardNo: ' C001 ' }, target: { CARD_NO: 'C001' } }],
      elapsedMilliseconds: 21,
      rowCountCapped: false,
      failedRows: 0,
      errors: [],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    const request: DataSyncTryRunRequest = { rowLimit: 10 };
    await expect(dataSyncApi.tryRun('his_outpatient_sync', request)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-tasks/his_outpatient_sync/try-run');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
  });

  /** 验证立即执行以 POST 启动并返回初始运行日志。 */
  it('starts a background run via POST and returns the initial run log', async () => {
    const runLog: SyncRunLogDocument = {
      runNo: 'run-1',
      taskCode: 'his_outpatient_sync',
      status: 'Running',
      startedAt: '2026-11-03T10:00:00+08:00',
      finishedAt: null,
      rowsRead: 0,
      rowsWritten: 0,
      rowsFailed: 0,
      message: null,
      failures: [],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: runLog }));

    await expect(dataSyncApi.runTask('his_outpatient_sync')).resolves.toEqual(runLog);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-tasks/his_outpatient_sync/run');
    expect(init.method).toBe('POST');
  });

  /** 验证运行记录按任务编码从 runs 路径读取。 */
  it('loads run logs from the runs path', async () => {
    const runLog: SyncRunLogDocument = {
      runNo: 'run-1',
      taskCode: 'his_outpatient_sync',
      status: 'Completed',
      startedAt: '2026-11-03T10:00:00+08:00',
      finishedAt: '2026-11-03T10:00:12+08:00',
      rowsRead: 320,
      rowsWritten: 318,
      rowsFailed: 2,
      message: null,
      failures: [{ rowIndex: 12, field: 'CARD_NO', reason: '必填字段取值为空。', originalValue: null }],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: [runLog] }));

    await expect(dataSyncApi.taskRuns('his_outpatient_sync')).resolves.toEqual([runLog]);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/data-sync-tasks/his_outpatient_sync/runs',
      undefined,
    );
  });

  /** 验证同步包读取按任务编码从 package 路径返回完整包对象。 */
  it('reads the sync package json by task code', async () => {
    const syncPackage: DataSyncPackage = {
      packageKind: 'hd-report-center.sync-package',
      packageVersion: 1,
      exportedAt: '2026-11-03T10:00:00+08:00',
      taskCode: 'his_outpatient_sync',
      sourceDataset: {
        code: 'detail',
        name: '门诊明细',
        dataSourceCode: 'his_db',
        dataSourceType: 'SqlServer',
        queryText: 'SELECT cardNo FROM registration',
        fieldSchema: {},
        timeoutSeconds: 30,
        maxRows: 20000,
        isEnabled: true,
      },
      targetDataSourceCode: 'ods_db',
      targetDataSourceType: 'SqlServer',
      targetTable: 'ods_registration',
      conflictPolicy: 'TruncateFirst',
      scheduleType: 'Daily',
      scheduleTime: '02:30',
      intervalMinutes: 0,
      mappings: taskDocument.mappings,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: syncPackage }));

    await expect(dataSyncApi.getTaskPackage('his_outpatient_sync')).resolves.toEqual(syncPackage);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/data-sync-tasks/his_outpatient_sync/package',
      undefined,
    );
  });

  /** 验证校验请求把整份包作为 package 字段提交并回传冲突探测结果。 */
  it('posts the whole package to the sync validate endpoint', async () => {
    const result: DataSyncValidateResult = {
      ok: true,
      errors: [],
      warnings: [{ code: 'SYNC_TARGET_TYPE_MISMATCH', message: '目标数据源类型与导出方不一致' }],
      collisions: {
        taskCodeExists: false,
        datasetCodeExists: true,
        datasetIdentical: true,
        datasetReferencedByReports: null,
        targetDataSourceExists: true,
        targetDataSourceTypeMatch: true,
      },
      packageSummary: {
        taskCode: 'his_outpatient_sync',
        sourceDatasetCode: 'detail',
        targetTable: 'ods_registration',
        mappingCount: 1,
        targetDataSourceType: 'SqlServer',
      },
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    const requestPackage: DataSyncPackage = {
      packageKind: 'hd-report-center.sync-package',
      packageVersion: 1,
      exportedAt: '2026-11-03T10:00:00+08:00',
      taskCode: 'his_outpatient_sync',
      sourceDataset: {
        code: 'detail',
        name: '门诊明细',
        dataSourceCode: 'his_db',
        dataSourceType: 'SqlServer',
        queryText: 'SELECT cardNo FROM registration',
        fieldSchema: {},
        timeoutSeconds: 30,
        maxRows: 20000,
        isEnabled: true,
      },
      targetDataSourceCode: 'ods_db',
      targetDataSourceType: 'SqlServer',
      targetTable: 'ods_registration',
      conflictPolicy: 'TruncateFirst',
      scheduleType: 'Daily',
      scheduleTime: '02:30',
      intervalMinutes: 0,
      mappings: taskDocument.mappings,
    };
    await expect(dataSyncApi.validatePackage(requestPackage)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-packages/validate');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ package: requestPackage });
  });

  /** 验证包试跑请求携带包与目标数据源并返回样例结论。 */
  it('posts package and target data source to dry-run', async () => {
    const result: DataSyncTryRunResult = {
      ok: true,
      targetColumns: ['CARD_NO'],
      samples: [{ source: { cardNo: 'C001' }, target: { CARD_NO: 'C001' } }],
      elapsedMilliseconds: 33,
      rowCountCapped: false,
      failedRows: 0,
      errors: [],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    const request = {
      package: {
        packageKind: 'hd-report-center.sync-package',
        packageVersion: 1,
        exportedAt: '2026-11-03T10:00:00+08:00',
        taskCode: 'his_outpatient_sync',
        sourceDataset: {
          code: 'detail',
          name: '门诊明细',
          dataSourceCode: 'his_db',
          dataSourceType: 'SqlServer',
          queryText: 'SELECT cardNo FROM registration',
          fieldSchema: {},
          timeoutSeconds: 30,
          maxRows: 20000,
          isEnabled: true,
        },
        targetDataSourceCode: 'ods_db',
        targetDataSourceType: 'SqlServer',
        targetTable: 'ods_registration',
        conflictPolicy: 'TruncateFirst',
        scheduleType: 'Daily',
        scheduleTime: '02:30',
        intervalMinutes: 0,
        mappings: taskDocument.mappings,
      } satisfies DataSyncPackage,
      targetDataSourceCode: 'ods_db',
    };
    await expect(dataSyncApi.dryRunPackage(request)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-packages/dry-run');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
  });

  /** 验证导入请求包含模式与是否试跑标记，并返回任务级动作摘要。 */
  it('posts mode and dry-run flag to the sync import endpoint', async () => {
    const result: DataSyncImportResult = {
      taskCode: 'his_outpatient_sync',
      datasetAction: 'Created',
      taskAction: 'Created',
      warnings: [],
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    const request: DataSyncImportRequest = {
      package: {
        packageKind: 'hd-report-center.sync-package',
        packageVersion: 1,
        exportedAt: '2026-11-03T10:00:00+08:00',
        taskCode: 'his_outpatient_sync',
        sourceDataset: {
          code: 'detail',
          name: '门诊明细',
          dataSourceCode: 'his_db',
          dataSourceType: 'SqlServer',
          queryText: 'SELECT cardNo FROM registration',
          fieldSchema: {},
          timeoutSeconds: 30,
          maxRows: 20000,
          isEnabled: true,
        },
        targetDataSourceCode: 'ods_db',
        targetDataSourceType: 'SqlServer',
        targetTable: 'ods_registration',
        conflictPolicy: 'TruncateFirst',
        scheduleType: 'Daily',
        scheduleTime: '02:30',
        intervalMinutes: 0,
        mappings: taskDocument.mappings,
      },
      targetDataSourceCode: 'ods_db',
      mode: 'Replace',
      dryRunPerformed: true,
    };
    await expect(dataSyncApi.importPackage(request)).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-packages/import');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual(request);
    expect(String(init.body)).not.toContain('password');
  });

  /** 验证表清单端点按数据源编码拼接查询参数并发起 GET。 */
  it('lists tables of a data source via the metadata endpoint', async () => {
    fetchMock.mockResolvedValue(jsonResponse({
      code: 200,
      result: { dataSourceCode: 'his_db', tables: ['RC_VISIT', 'RC_ORDER'] },
    }));

    await expect(dataSyncApi.listTables('his_db')).resolves.toEqual({
      dataSourceCode: 'his_db',
      tables: ['RC_VISIT', 'RC_ORDER'],
    });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/data-sync-tasks/metadata/tables?dataSourceCode=his_db',
      undefined,
    );
  });

  /** 验证列清单端点同时携带数据源编码与表名（表名做 URL 编码），列对象含可空注释 description。 */
  it('lists columns of a table via the metadata endpoint', async () => {
    fetchMock.mockResolvedValue(jsonResponse({
      code: 200,
      result: {
        dataSourceCode: 'ods_db',
        table: 'ods_registration',
        columns: [{ name: 'CARD_NO', dataType: 'nvarchar', nullable: true, length: 16, description: '卡号' }],
      },
    }));

    await expect(dataSyncApi.listTableColumns('ods_db', 'ods_registration')).resolves.toEqual({
      dataSourceCode: 'ods_db',
      table: 'ods_registration',
      columns: [{ name: 'CARD_NO', dataType: 'nvarchar', nullable: true, length: 16, description: '卡号' }],
    });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/report-center/poc/data-sync-tasks/metadata/columns?dataSourceCode=ods_db&table=ods_registration',
      undefined,
    );
  });

  /** 验证源取数预览以 POST 提交“数据源 + SQL + 可选源表名”，字段携带可空注释 description。 */
  it('previews source columns via the preview-source endpoint', async () => {
    const result = {
      fields: [{ code: 'cardNo', displayName: 'cardNo', dataType: 'string', description: '卡号' }],
      rows: [['A-001']] as Array<Array<string | number | boolean | null>>,
      hasMore: false,
      limit: 50,
      durationMilliseconds: 12,
    };
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result }));

    await expect(dataSyncApi.previewSource({
      dataSourceCode: 'his_db',
      queryText: 'SELECT * FROM [RC_VISIT]',
      sourceTable: 'RC_VISIT',
    })).resolves.toEqual(result);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-tasks/preview-source');
    expect(init.method).toBe('POST');
    expect(init.headers).toEqual({ 'Content-Type': 'application/json' });
    expect(JSON.parse(init.body as string)).toEqual({
      dataSourceCode: 'his_db',
      queryText: 'SELECT * FROM [RC_VISIT]',
      sourceTable: 'RC_VISIT',
    });
  });

  /** 验证保存任务可携带新契约的 source 节（服务端据此自动维护源数据集）。 */
  it('saves a sync task with the source node for auto dataset upsert', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ code: 200, result: taskDocument }));

    const request: SyncTaskInput = {
      code: 'his_outpatient_sync',
      name: '门诊挂号同步',
      targetDataSourceCode: 'ods_db',
      targetTable: 'ods_registration',
      conflictPolicy: 'TruncateFirst',
      scheduleType: 'Manual',
      scheduleTime: null,
      intervalMinutes: 0,
      mappings: taskDocument.mappings,
      enabled: true,
      revision: 0,
      source: {
        dataSourceCode: 'his_db',
        queryText: 'SELECT * FROM [RC_VISIT]',
      },
    };
    await expect(dataSyncApi.saveTask(request.code, request)).resolves.toEqual(taskDocument);

    const [path, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe('/api/report-center/poc/data-sync-tasks/his_outpatient_sync');
    expect(init.method).toBe('PUT');
    const body = JSON.parse(init.body as string) as SyncTaskInput;
    expect(body.source).toEqual({ dataSourceCode: 'his_db', queryText: 'SELECT * FROM [RC_VISIT]' });
  });

  /** 验证后端 404 任务不存在的业务错误把信封 message 抛给页面提示。 */
  it('surfaces the backend not-found message for missing tasks', async () => {
    fetchMock.mockResolvedValue(jsonResponse({
      code: 404,
      message: 'SYNC_TASK_NOT_FOUND: 同步任务不存在',
      result: null,
    }, 404));

    await expect(dataSyncApi.getTask('missing')).rejects.toThrow('SYNC_TASK_NOT_FOUND');
  });
});
