import { LocaleType, type IWorkbookData } from '@univerjs/presets';
import { describe, expect, it } from 'vitest';
import {
  createTemplatePreviewPlan,
  TemplatePreviewError,
} from './templatePreview';
import type { ExecuteResult, TemplateBinding } from './types';

function workbookWithSheets(sheets: IWorkbookData['sheets']): IWorkbookData {
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

function workbook(cellData: NonNullable<IWorkbookData['sheets'][string]['cellData']>): IWorkbookData {
  return workbookWithSheets({
    'sheet-1': {
      id: 'sheet-1',
      name: '报表模板',
      cellData,
    },
  });
}

function executeResult(overrides: Partial<ExecuteResult> = {}): ExecuteResult {
  return {
    page: 1,
    pageSize: 20,
    hasMore: false,
    parameters: {
      hospitalId: 'H-001',
      deptId: 'D-002',
      personId: 'P-003',
      startDate: '2026-07-01',
      endDate: '2026-07-14',
    },
    rows: [],
    ...overrides,
  };
}

function detailBinding(
  overrides: Partial<NonNullable<TemplateBinding['detailBinding']>> = {},
): TemplateBinding {
  return {
    version: 1,
    parameterBindings: [],
    detailBinding: {
      datasetCode: 'detail',
      sheetId: 'sheet-1',
      templateRow: 7,
      fields: [
        { code: 'outpatientCount', column: 2 },
        { code: 'sequence', column: 0 },
        { code: 'dischargeCount', column: 3 },
        { code: 'departmentName', column: 1 },
      ],
      ...overrides,
    },
  };
}

function workloadRows(count: number): ExecuteResult['rows'] {
  return Array.from({ length: count }, (_, index) => ({
    sequence: index + 1,
    departmentName: `科室 ${index + 1}`,
    outpatientCount: 100 + index,
    dischargeCount: 10 + index,
  }));
}

function expectPreviewError(
  action: () => unknown,
  expected: {
    sheetId?: string;
    row?: number;
    column?: number;
    cellAddress?: string;
  },
): TemplatePreviewError {
  try {
    action();
  } catch (error) {
    expect(error).toBeInstanceOf(TemplatePreviewError);
    const previewError = error as TemplatePreviewError;
    expect(previewError).toMatchObject(expected);
    if (expected.sheetId !== undefined) {
      expect(previewError.message).toContain(expected.sheetId);
    }
    if (expected.cellAddress !== undefined) {
      expect(previewError.message).toContain(expected.cellAddress);
    }
    return previewError;
  }

  throw new Error('预期创建模板预览计划时抛出 TemplatePreviewError');
}

describe('createTemplatePreviewPlan', () => {
  it('replaces an embedded parameter token while preserving fixed text', () => {
    const snapshot = workbook({
      2: {
        1: { v: '医院：{{\tparams.hospitalId \n}}（已选择）' },
      },
    });
    const binding: TemplateBinding = {
      version: 1,
      parameterBindings: [
        { code: 'hospitalId', sheetId: 'sheet-1', row: 2, column: 1 },
      ],
      detailBinding: null,
    };

    expect(createTemplatePreviewPlan(snapshot, binding, executeResult())).toEqual({
      parameterWrites: [
        { sheetId: 'sheet-1', row: 2, column: 1, value: '医院：H-001（已选择）' },
      ],
      detail: null,
    });
  });

  it('combines different and repeated parameter tokens into one canonical write per cell', () => {
    const snapshot = workbookWithSheets({
      'sheet-Z': {
        id: 'sheet-Z',
        name: 'Z',
        cellData: {
          1: {
            3: {
              v: '{{params.hospitalId}}/{{ params.deptId }}/{{params.hospitalId}}',
            },
          },
        },
      },
      'sheet-a': {
        id: 'sheet-a',
        name: 'A',
        cellData: {
          9: { 0: { v: '截止 {{params.endDate}}' } },
        },
      },
    });
    const binding: TemplateBinding = {
      version: 1,
      parameterBindings: [
        { code: 'hospitalId', sheetId: 'sheet-Z', row: 1, column: 3 },
        { code: 'endDate', sheetId: 'sheet-a', row: 9, column: 0 },
        { code: 'deptId', sheetId: 'sheet-Z', row: 1, column: 3 },
        { code: 'hospitalId', sheetId: 'sheet-Z', row: 1, column: 3 },
      ],
      detailBinding: null,
    };

    expect(createTemplatePreviewPlan(snapshot, binding, executeResult()).parameterWrites).toEqual([
      {
        sheetId: 'sheet-Z',
        row: 1,
        column: 3,
        value: 'H-001/D-002/H-001',
      },
      {
        sheetId: 'sheet-a',
        row: 9,
        column: 0,
        value: '截止 2026-07-14',
      },
    ]);
  });

  it('plans twenty typed detail rows and nineteen inserted rows without formula writes', () => {
    const snapshot = workbook({
      7: {
        0: { v: '{{detail.sequence}}' },
        1: { v: '{{detail.departmentName}}' },
        2: { v: '{{detail.outpatientCount}}' },
        3: { v: '{{detail.dischargeCount}}' },
        4: { f: '=C8+D8' },
      },
    });

    const plan = createTemplatePreviewPlan(
      snapshot,
      detailBinding(),
      executeResult({ rows: workloadRows(20) }),
    );

    expect(plan.detail).toMatchObject({
      sheetId: 'sheet-1',
      templateRow: 7,
      insertCount: 19,
    });
    expect(plan.detail?.rows).toHaveLength(20);
    expect(plan.detail?.rows[0]).toEqual({
      row: 7,
      values: [
        { column: 0, value: 1 },
        { column: 1, value: '科室 1' },
        { column: 2, value: 100 },
        { column: 3, value: 10 },
      ],
    });
    expect(plan.detail?.rows[19]).toEqual({
      row: 26,
      values: [
        { column: 0, value: 20 },
        { column: 1, value: '科室 20' },
        { column: 2, value: 119 },
        { column: 3, value: 29 },
      ],
    });
    expect(plan.detail?.rows.flatMap((row) => row.values).some(({ column }) => column === 4))
      .toBe(false);
  });

  it('uses the template row directly for a single detail result', () => {
    const plan = createTemplatePreviewPlan(
      workbook({ 7: { 0: { v: '{{detail.sequence}}' } } }),
      detailBinding({ fields: [{ code: 'sequence', column: 0 }] }),
      executeResult({ rows: workloadRows(1) }),
    );

    expect(plan.detail).toEqual({
      sheetId: 'sheet-1',
      templateRow: 7,
      insertCount: 0,
      rows: [{ row: 7, values: [{ column: 0, value: 1 }] }],
    });
  });

  it('clears every detail placeholder with null when there are no result rows', () => {
    const plan = createTemplatePreviewPlan(
      workbook({
        7: {
          0: { v: '{{detail.sequence}}' },
          1: { v: '{{detail.departmentName}}' },
        },
      }),
      detailBinding({
        fields: [
          { code: 'sequence', column: 0 },
          { code: 'departmentName', column: 1 },
        ],
      }),
      executeResult({ rows: [] }),
    );

    expect(plan.detail).toEqual({
      sheetId: 'sheet-1',
      templateRow: 7,
      insertCount: 0,
      rows: [{
        row: 7,
        values: [
          { column: 0, value: null },
          { column: 1, value: null },
        ],
      }],
    });
  });

  it('keeps a parameter below the detail row at its original template coordinate', () => {
    const snapshot = workbook({
      4: { 0: { v: '{{detail.sequence}}' } },
      10: { 2: { v: '页脚 {{params.deptId}}' } },
    });
    const binding: TemplateBinding = {
      version: 1,
      parameterBindings: [
        { code: 'deptId', sheetId: 'sheet-1', row: 10, column: 2 },
      ],
      detailBinding: {
        datasetCode: 'detail',
        sheetId: 'sheet-1',
        templateRow: 4,
        fields: [{ code: 'sequence', column: 0 }],
      },
    };

    const plan = createTemplatePreviewPlan(
      snapshot,
      binding,
      executeResult({ rows: workloadRows(2) }),
    );

    expect(plan.parameterWrites).toEqual([
      { sheetId: 'sheet-1', row: 10, column: 2, value: '页脚 D-002' },
    ]);
    expect(plan.detail?.rows.map(({ row }) => row)).toEqual([4, 5]);
    expect(plan.detail?.insertCount).toBe(1);
  });

  it('does not mutate the snapshot, binding or execute result', () => {
    const snapshot = workbook({
      2: { 1: { v: '{{params.hospitalId}}' } },
      7: {
        0: { v: '{{detail.sequence}}' },
        1: { v: '{{detail.departmentName}}' },
      },
    });
    const binding: TemplateBinding = {
      ...detailBinding({
        fields: [
          { code: 'departmentName', column: 1 },
          { code: 'sequence', column: 0 },
        ],
      }),
      parameterBindings: [
        { code: 'hospitalId', sheetId: 'sheet-1', row: 2, column: 1 },
      ],
    };
    const result = executeResult({ rows: workloadRows(2) });
    const snapshotBefore = structuredClone(snapshot);
    const bindingBefore = structuredClone(binding);
    const resultBefore = structuredClone(result);

    createTemplatePreviewPlan(snapshot, binding, result);

    expect(snapshot).toEqual(snapshotBefore);
    expect(binding).toEqual(bindingBefore);
    expect(result).toEqual(resultBefore);
  });

  it.each([
    {
      name: 'missing sheet',
      snapshot: workbookWithSheets({}),
      parameterBinding: { code: 'hospitalId', sheetId: 'missing-sheet', row: 0, column: 0 },
      location: { sheetId: 'missing-sheet', row: 0, column: 0, cellAddress: 'A1' },
    },
    {
      name: 'missing cell',
      snapshot: workbook({}),
      parameterBinding: { code: 'hospitalId', sheetId: 'sheet-1', row: 6, column: 2 },
      location: { sheetId: 'sheet-1', row: 6, column: 2, cellAddress: 'C7' },
    },
    {
      name: 'non-string cell value',
      snapshot: workbook({ 1: { 1: { v: 42 } } }),
      parameterBinding: { code: 'hospitalId', sheetId: 'sheet-1', row: 1, column: 1 },
      location: { sheetId: 'sheet-1', row: 1, column: 1, cellAddress: 'B2' },
    },
    {
      name: 'unknown parameter code',
      snapshot: workbook({ 0: { 0: { v: '{{params.notAllowed}}' } } }),
      parameterBinding: { code: 'notAllowed', sheetId: 'sheet-1', row: 0, column: 0 },
      location: { sheetId: 'sheet-1', row: 0, column: 0, cellAddress: 'A1' },
    },
  ])('rejects an invalid parameter binding: $name', ({ snapshot, parameterBinding, location }) => {
    const binding = {
      version: 1,
      parameterBindings: [parameterBinding],
      detailBinding: null,
    } as unknown as TemplateBinding;

    expectPreviewError(
      () => createTemplatePreviewPlan(snapshot, binding, executeResult()),
      location,
    );
  });

  it.each([
    {
      name: 'unknown field code',
      binding: detailBinding({
        fields: [{ code: 'formulaValue', column: 2 } as never],
      }),
      location: { sheetId: 'sheet-1', row: 7, column: 2, cellAddress: 'C8' },
    },
    {
      name: 'missing sheet',
      binding: detailBinding({ sheetId: 'missing-sheet', templateRow: 4 }),
      location: { sheetId: 'missing-sheet', row: 4, column: 0, cellAddress: 'A5' },
    },
    {
      name: 'negative template row',
      binding: detailBinding({ templateRow: -1 }),
      location: { sheetId: 'sheet-1', row: -1, column: 0 },
    },
  ])('rejects an invalid detail binding: $name', ({ binding, location }) => {
    expectPreviewError(
      () => createTemplatePreviewPlan(workbook({}), binding, executeResult()),
      location,
    );
  });
});
