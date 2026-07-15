import { LocaleType, type IWorkbookData } from '@univerjs/presets';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { reportApi } from './api';
import type {
  ExecuteRequest,
  ExportJob,
  SaveTemplateRequest,
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
};

const executeRequest: ExecuteRequest = {
  page: 3,
  hospitalId: 'H01',
  deptId: 'D02',
  personId: 'P03',
  startDate: '2026-07-01',
  endDate: '2026-07-14',
};

const fetchMock = vi.fn();

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: vi.fn().mockResolvedValue(body),
  } as unknown as Response;
}

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
});
