import { LocaleType, type IWorkbookData } from '@univerjs/presets';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { reportApi } from './api';
import type { SaveTemplateRequest, TemplateBinding, TemplateDocument } from './types';

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

const fetchMock = vi.fn();

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: vi.fn().mockResolvedValue(body),
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
});
