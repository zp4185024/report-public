import { LocaleType, type IWorkbookData } from '@univerjs/presets';
import { describe, expect, it } from 'vitest';
import { scanTemplateBindings, TemplateBindingError } from './templateBinding';
import type { ReportDefinition } from './types';

const definition: ReportDefinition = {
  code: 'dept-workload',
  name: '科室工作量统计',
  sheetName: '科室工作量',
  preview: { mode: 'Paged', pageSize: 20, showExactTotal: false },
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
    { code: 'hospitalId', displayName: '医院', controlType: 'text', sourceType: 'Form', defaultValue: null, visible: true, dataType: 'string', defaultExpression: null, fixedValue: null, readOnly: false, required: false, allowUrlPrefill: true, urlKey: 'hospitalId' },
    { code: 'deptId', displayName: '科室', controlType: 'text', sourceType: 'Form', defaultValue: null, visible: true, dataType: 'string', defaultExpression: null, fixedValue: null, readOnly: false, required: false, allowUrlPrefill: true, urlKey: 'deptId' },
    { code: 'startDate', displayName: '开始日期', controlType: 'date', sourceType: 'Form', defaultValue: null, visible: true, dataType: 'date', defaultExpression: 'startOfMonth', fixedValue: null, readOnly: false, required: false, allowUrlPrefill: false, urlKey: null },
    { code: 'endDate', displayName: '结束日期', controlType: 'date', sourceType: 'Form', defaultValue: null, visible: true, dataType: 'date', defaultExpression: 'endOfMonth', fixedValue: null, readOnly: false, required: false, allowUrlPrefill: false, urlKey: null },
  ],
  fields: [
    { code: 'sequence', displayName: '序号', dataType: 'number' },
    { code: 'departmentName', displayName: '科室', dataType: 'string' },
    { code: 'outpatientCount', displayName: '门诊人次', dataType: 'number' },
    { code: 'dischargeCount', displayName: '出院人次', dataType: 'number' },
  ],
};

function workbook(sheets: IWorkbookData['sheets'] = {}): IWorkbookData {
  return {
    id: 'template-workbook',
    name: '模板',
    appVersion: '1.0.0',
    locale: LocaleType.ZH_CN,
    styles: {},
    sheetOrder: Object.keys(sheets),
    sheets,
  };
}

function oneSheet(
  cellData: NonNullable<IWorkbookData['sheets'][string]['cellData']>,
  mergeData: NonNullable<IWorkbookData['sheets'][string]['mergeData']> = [],
): IWorkbookData {
  return workbook({
    'sheet-1': {
      id: 'sheet-1',
      name: '报表模板',
      cellData,
      mergeData,
    },
  });
}

function expectBindingError(
  snapshot: IWorkbookData,
  expected: { sheetId?: string; row?: number; column?: number; cellAddress?: string },
  reportDefinition: ReportDefinition = definition,
): TemplateBindingError {
  try {
    scanTemplateBindings(snapshot, reportDefinition);
  } catch (error) {
    expect(error).toBeInstanceOf(TemplateBindingError);
    const bindingError = error as TemplateBindingError;
    expect(bindingError).toMatchObject(expected);
    if (expected.sheetId) {
      expect(bindingError.message).toContain(expected.sheetId);
    }
    if (expected.cellAddress) {
      expect(bindingError.message).toContain(expected.cellAddress);
    }
    return bindingError;
  }

  throw new Error('预期扫描模板时抛出 TemplateBindingError');
}

describe('scanTemplateBindings', () => {
  it('scans embedded parameters, multiple parameters, one detail row and canonical ordering', () => {
    const snapshot = oneSheet({
      3: {
        2: { v: '医院：{{params.hospitalId}}' },
        0: { v: '{{params.startDate}} 至 {{params.endDate}}' },
      },
      7: {
        3: { v: '{{detail.outpatientCount}}' },
        1: { v: '  {{ detail.departmentName }}  ' },
        4: { f: '=C8+D8' },
      },
    });

    expect(scanTemplateBindings(snapshot, definition)).toEqual({
      version: 1,
      parameterBindings: [
        { code: 'endDate', sheetId: 'sheet-1', row: 3, column: 0 },
        { code: 'startDate', sheetId: 'sheet-1', row: 3, column: 0 },
        { code: 'hospitalId', sheetId: 'sheet-1', row: 3, column: 2 },
      ],
      detailBinding: {
        datasetCode: 'detail',
        sheetId: 'sheet-1',
        templateRow: 7,
        fields: [
          { code: 'departmentName', column: 1 },
          { code: 'outpatientCount', column: 3 },
        ],
      },
    });
  });

  it('sorts parameters by sheet id before row, column and code', () => {
    const snapshot = workbook({
      'sheet-z': { cellData: { 0: { 0: { v: '{{params.deptId}}' } } } },
      'sheet-a': { cellData: { 5: { 3: { v: '{{params.hospitalId}}' } } } },
    });

    expect(scanTemplateBindings(snapshot, definition).parameterBindings).toEqual([
      { code: 'hospitalId', sheetId: 'sheet-a', row: 5, column: 3 },
      { code: 'deptId', sheetId: 'sheet-z', row: 0, column: 0 },
    ]);
  });

  it('uses ordinal ordering for mixed-case sheet ids and parameter codes', () => {
    const mixedCaseDefinition: ReportDefinition = {
      ...definition,
      parameters: [
        { ...definition.parameters[0], code: 'aCode' },
        { ...definition.parameters[1], code: 'BCode' },
      ],
    };
    const snapshot = workbook({
      a: { cellData: { 0: { 0: { v: '{{params.aCode}}{{params.BCode}}' } } } },
      B: { cellData: { 0: { 0: { v: '{{params.aCode}}' } } } },
    });

    expect(scanTemplateBindings(snapshot, mixedCaseDefinition).parameterBindings).toEqual([
      { code: 'aCode', sheetId: 'B', row: 0, column: 0 },
      { code: 'BCode', sheetId: 'a', row: 0, column: 0 },
      { code: 'aCode', sheetId: 'a', row: 0, column: 0 },
    ]);
  });

  it('returns an empty binding when sheets or cell data are missing or empty', () => {
    const emptyBinding = {
      version: 1,
      parameterBindings: [],
      detailBinding: null,
    };

    expect(scanTemplateBindings(workbook(), definition)).toEqual(emptyBinding);
    expect(scanTemplateBindings(workbook({ 'sheet-1': {} }), definition)).toEqual(emptyBinding);
    expect(scanTemplateBindings(workbook({ 'sheet-1': { cellData: {} } }), definition)).toEqual(emptyBinding);
    expect(scanTemplateBindings({ ...workbook(), sheets: undefined } as unknown as IWorkbookData, definition)).toEqual(emptyBinding);
  });

  it('does not mutate or normalize the input snapshot', () => {
    const snapshot = oneSheet({
      0: { 0: { v: '医院：{{params.hospitalId}}' } },
      4: { 2: { v: '{{detail.dischargeCount}}' } },
    });
    const before = structuredClone(snapshot);

    scanTemplateBindings(snapshot, definition);

    expect(snapshot).toEqual(before);
  });

  it('rejects an unknown parameter and reports an A1 address beyond Z', () => {
    const error = expectBindingError(
      oneSheet({ 1: { 26: { v: '{{params.unknownCode}}' } } }),
      { sheetId: 'sheet-1', row: 1, column: 26, cellAddress: 'AA2' },
    );

    expect(error.message).toContain('unknownCode');
  });

  it('rejects fields that are not declared by the dataset', () => {
    for (const code of ['missingField', 'totalWorkload']) {
      const error = expectBindingError(
        oneSheet({ 2: { 0: { v: `{{detail.${code}}}` } } }),
        { sheetId: 'sheet-1', row: 2, column: 0, cellAddress: 'A3' },
      );
      expect(error.message).toContain(code);
    }
  });

  it('requires a detail token to be the trimmed cell value', () => {
    expectBindingError(
      oneSheet({ 2: { 1: { v: '科室：{{detail.departmentName}}' } } }),
      { sheetId: 'sheet-1', row: 2, column: 1, cellAddress: 'B3' },
    );
  });

  it.each([
    ['space', ' '],
    ['tab', '\t'],
    ['carriage return', '\r'],
    ['line feed', '\n'],
    ['form feed', '\f'],
    ['vertical tab', '\v'],
  ])('accepts ASCII %s inside tokens and around a detail token', (_label, whitespace) => {
    const snapshot = oneSheet({
      0: { 0: { v: `{{${whitespace}params.hospitalId${whitespace}}}` } },
      1: { 0: { v: `${whitespace}{{${whitespace}detail.sequence${whitespace}}}${whitespace}` } },
    });

    expect(scanTemplateBindings(snapshot, definition)).toEqual({
      version: 1,
      parameterBindings: [
        { code: 'hospitalId', sheetId: 'sheet-1', row: 0, column: 0 },
      ],
      detailBinding: {
        datasetCode: 'detail',
        sheetId: 'sheet-1',
        templateRow: 1,
        fields: [{ code: 'sequence', column: 0 }],
      },
    });
  });

  it.each([
    ['U+FEFF inside a token', '\uFEFF', 'inside'],
    ['U+0085 inside a token', '\u0085', 'inside'],
    ['U+FEFF around a detail token', '\uFEFF', 'outside'],
    ['U+0085 around a detail token', '\u0085', 'outside'],
  ])('rejects non-ASCII whitespace: %s', (_label, whitespace, placement) => {
    const value = placement === 'inside'
      ? `{{${whitespace}params.hospitalId}}`
      : `${whitespace}{{detail.sequence}}${whitespace}`;

    expectBindingError(
      oneSheet({ 0: { 0: { v: value } } }),
      { sheetId: 'sheet-1', row: 0, column: 0, cellAddress: 'A1' },
    );
  });

  it('allows non-ASCII whitespace characters in ordinary text without tokens', () => {
    expect(scanTemplateBindings(
      oneSheet({ 0: { 0: { v: '普通\uFEFF文本\u0085内容' } } }),
      definition,
    )).toEqual({
      version: 1,
      parameterBindings: [],
      detailBinding: null,
    });
  });

  it('rejects a detail code outside the configured result fields', () => {
    const error = expectBindingError(
      oneSheet({ 0: { 0: { v: '{{detail.runtimeOnlyField}}' } } }),
      { sheetId: 'sheet-1', row: 0, column: 0, cellAddress: 'A1' },
    );

    expect(error.message).toContain('runtimeOnlyField');
  });

  it('rejects detail tokens from different rows or sheets', () => {
    expectBindingError(
      oneSheet({
        2: { 0: { v: '{{detail.sequence}}' } },
        3: { 1: { v: '{{detail.departmentName}}' } },
      }),
      { sheetId: 'sheet-1', row: 3, column: 1, cellAddress: 'B4' },
    );

    const crossSheetError = expectBindingError(
      workbook({
        'sheet-1': {
          id: 'sheet-1',
          name: '原模板',
          cellData: { 2: { 0: { v: '{{detail.sequence}}' } } },
        },
        'sheet-2': {
          id: 'sheet-2',
          name: '新模板',
          cellData: { 2: { 1: { v: '{{detail.departmentName}}' } } },
        },
      }),
      { sheetId: 'sheet-2', row: 2, column: 1, cellAddress: 'B3' },
    );

    expect(crossSheetError.message).toContain("已有明细区域：工作表 '原模板'（ID: sheet-1）第 3 行");
    expect(crossSheetError.message).toContain("当前冲突工作表：'新模板'（ID: sheet-2）");
    expect(crossSheetError.message).toContain('请先删除旧区域中的明细 token');
  });

  it('rejects any merge range touching the detail row', () => {
    expectBindingError(
      oneSheet(
        { 8: { 2: { v: '{{detail.outpatientCount}}' } } },
        [{ startRow: 7, endRow: 8, startColumn: 0, endColumn: 4 }],
      ),
      { sheetId: 'sheet-1', row: 8, column: 2, cellAddress: 'C9' },
    );
  });

  it.each([
    '{{params.hospitalId',
    'params.hospitalId}}',
    '{{params.hospital-id}}',
    '{{params.hospitalId}}{{',
    '}} {{params.hospitalId}}',
  ])('rejects malformed or residual double braces in %s', (value) => {
    expectBindingError(
      oneSheet({ 0: { 0: { v: value } } }),
      { sheetId: 'sheet-1', row: 0, column: 0, cellAddress: 'A1' },
    );
  });

  it.each([
    '{{{params.hospitalId}}}',
    '{{params.hospitalId}}}',
  ])('rejects an otherwise valid token adjacent to an extra brace in %s', (value) => {
    expectBindingError(
      oneSheet({ 0: { 0: { v: value } } }),
      { sheetId: 'sheet-1', row: 0, column: 0, cellAddress: 'A1' },
    );
  });

  it.each([
    ['row', { x: { 0: { v: '{{params.hospitalId}}' } } }],
    ['negative row', { '-1': { 0: { v: '{{params.hospitalId}}' } } }],
    ['column', { 0: { x: { v: '{{params.hospitalId}}' } } }],
    ['negative column', { 0: { '-1': { v: '{{params.hospitalId}}' } } }],
  ])('rejects a non-numeric or negative %s key', (_label, cellData) => {
    expectBindingError(
      oneSheet(cellData as unknown as NonNullable<IWorkbookData['sheets'][string]['cellData']>),
      { sheetId: 'sheet-1' },
    );
  });

  it.each([
    ['sheets array', { ...workbook(), sheets: [] }, undefined],
    ['null sheet', { ...workbook(), sheets: { 'sheet-1': null } }, 'sheet-1'],
    ['cellData array', workbook({ 'sheet-1': { cellData: [] as unknown as NonNullable<IWorkbookData['sheets'][string]['cellData']> } }), 'sheet-1'],
    ['null row', oneSheet({ 0: null } as unknown as NonNullable<IWorkbookData['sheets'][string]['cellData']>), 'sheet-1'],
    ['array row', oneSheet({ 0: [] } as unknown as NonNullable<IWorkbookData['sheets'][string]['cellData']>), 'sheet-1'],
    ['string cell', oneSheet({ 0: { 0: 'bad' } } as unknown as NonNullable<IWorkbookData['sheets'][string]['cellData']>), 'sheet-1'],
  ])('rejects malformed JSON node shape: %s', (_label, malformedSnapshot, sheetId) => {
    expectBindingError(malformedSnapshot as unknown as IWorkbookData, { sheetId });
  });

  it('allows ordinary single-brace text', () => {
    const snapshot = oneSheet({
      0: { 0: { v: '公式示例：{row}，JSON：{value}' } },
      1: { 0: { v: '医院：{{params.hospitalId}}，保留 {说明}' } },
    });

    expect(scanTemplateBindings(snapshot, definition)).toEqual({
      version: 1,
      parameterBindings: [
        { code: 'hospitalId', sheetId: 'sheet-1', row: 1, column: 0 },
      ],
      detailBinding: null,
    });
  });
});
