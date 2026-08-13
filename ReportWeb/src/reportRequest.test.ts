import { describe, expect, it } from 'vitest';
import {
  createInitialReportRequest,
  refreshReportRequestFromUrl,
} from './reportRequest';
import type { ParameterDefinition, ReportDefinition } from './types';

function parameter(overrides: Partial<ParameterDefinition>): ParameterDefinition {
  return {
    code: 'value',
    displayName: '参数',
    controlType: 'text',
    sourceType: 'Form',
    defaultValue: null,
    visible: true,
    dataType: 'string',
    defaultExpression: null,
    fixedValue: null,
    readOnly: false,
    required: false,
    allowUrlPrefill: false,
    urlKey: null,
    options: [],
    sourceCode: null,
    sourceFieldCode: null,
    ...overrides,
  };
}

const definition = {
  code: 'test2',
  name: '测试报表',
  sheetName: 'test',
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
  parameters: [
    parameter({ code: 'departmentId', sourceType: 'Url', urlKey: 'departmentId' }),
    parameter({ code: 'departmentName', sourceType: 'Url', urlKey: 'departmentName' }),
    parameter({ code: 'sex', allowUrlPrefill: true, urlKey: 'sex' }),
    parameter({ code: 'userName', sourceType: 'External' }),
  ],
  externalSources: [{
    code: 'identity',
    name: '登录身份',
    enabled: true,
    fields: [],
    contextKeys: ['token'],
  }],
  fields: [],
  fieldPresentations: [],
  shareProtection: { enabled: false, passwordConfigured: false },
} satisfies ReportDefinition;

describe('reportRequest', () => {
  it('builds one parameter object from URL, page input defaults and external context', () => {
    const request = createInitialReportRequest(
      definition,
      undefined,
      '?departmentId=D01&departmentName=%E8%B4%A2%E5%8A%A1%E7%A7%91&sex=F&token=old',
    );

    expect(request.parameters).toMatchObject({
      departmentId: 'D01',
      departmentName: '财务科',
      sex: 'F',
    });
    expect(request.context).toEqual({ token: 'old' });
  });

  it('re-reads URL-owned values for every query without overwriting page inputs', () => {
    const initial = createInitialReportRequest(
      definition,
      undefined,
      '?departmentId=D01&departmentName=%E8%B4%A2%E5%8A%A1%E7%A7%91&sex=F&token=old',
    );
    initial.parameters.sex = 'M';

    const refreshed = refreshReportRequestFromUrl(
      initial,
      definition,
      '?departmentId=D03&departmentName=%E6%96%B9%E4%BE%BF%E9%97%A8%E8%AF%8A&sex=F&token=new',
    );

    expect(refreshed.parameters).toMatchObject({
      departmentId: 'D03',
      departmentName: '方便门诊',
      sex: 'M',
    });
    expect(refreshed.context).toEqual({ token: 'new' });
  });
});
