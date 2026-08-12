import { describe, expect, it } from 'vitest';
import type { IWorkbookData } from '@univerjs/presets';
import {
  normalizeUniverRuntimeNumber,
  normalizeWorkbookSnapshot,
} from './workbookSnapshot';

function workbookWithRuntimeMergeCoordinates(): IWorkbookData {
  return {
    id: 'runtime-workbook',
    name: '运行时模板',
    sheetOrder: ['report-sheet'],
    sheets: {
      'report-sheet': {
        id: 'report-sheet',
        name: '报表',
        rowCount: 10,
        columnCount: 6,
        cellData: {},
        mergeData: [
          {
            startRow: '0',
            endRow: '0',
            startColumn: '0',
            endColumn: '5',
          },
          {
            startRow: 1,
            endRow: 1,
            startColumn: 0,
            endColumn: 1,
          },
        ],
      },
    },
  } as unknown as IWorkbookData;
}

function workbookWithRuntimeLayoutNumbers(): IWorkbookData {
  return {
    id: 'runtime-layout-workbook',
    rev: '2',
    name: '运行时布局模板',
    sheetOrder: ['report-sheet'],
    sheets: {
      'report-sheet': {
        id: 'report-sheet',
        name: '报表',
        hidden: '0',
        rowCount: '50',
        columnCount: '12',
        zoomRatio: '1',
        scrollTop: '0',
        scrollLeft: '0',
        defaultColumnWidth: '100',
        defaultRowHeight: '26',
        freeze: {
          xSplit: '0',
          ySplit: '5',
          startRow: '5',
          startColumn: '0',
        },
        rowData: {
          0: { h: '42', ah: '42', ia: '1', hd: '0' },
        },
        columnData: {
          0: { w: '70', hd: '0' },
        },
        rowHeader: { width: '46', hidden: '0' },
        columnHeader: { height: '24', hidden: '0' },
        showGridlines: '1',
        rightToLeft: '0',
        cellData: {
          0: { 0: { v: '001' } },
        },
        mergeData: [],
      },
    },
  } as unknown as IWorkbookData;
}

describe('normalizeWorkbookSnapshot', () => {
  it('converts Univer runtime merge coordinates to numbers without mutating the saved snapshot', () => {
    const snapshot = workbookWithRuntimeMergeCoordinates();

    const normalized = normalizeWorkbookSnapshot(snapshot);

    expect(normalized.sheets['report-sheet']?.mergeData).toEqual([
      { startRow: 0, endRow: 0, startColumn: 0, endColumn: 5 },
      { startRow: 1, endRow: 1, startColumn: 0, endColumn: 1 },
    ]);
    expect((snapshot.sheets['report-sheet']?.mergeData?.[0] as unknown as { startRow: unknown }).startRow)
      .toBe('0');
  });

  it('converts Univer runtime numeric strings while preserving real numbers', () => {
    expect(normalizeUniverRuntimeNumber('026', '明细模板行高度')).toBe(26);
    expect(normalizeUniverRuntimeNumber(12.5, '明细模板行高度')).toBe(12.5);
    expect(() => normalizeUniverRuntimeNumber('', '明细模板行高度'))
      .toThrow('明细模板行高度');
  });

  it('normalizes worksheet layout numbers without converting numeric-looking cell text', () => {
    const snapshot = workbookWithRuntimeLayoutNumbers();

    const normalized = normalizeWorkbookSnapshot(snapshot);
    const sheet = normalized.sheets['report-sheet'];

    expect(normalized.rev).toBe(2);
    expect(sheet).toMatchObject({
      hidden: 0,
      rowCount: 50,
      columnCount: 12,
      zoomRatio: 1,
      scrollTop: 0,
      scrollLeft: 0,
      defaultColumnWidth: 100,
      defaultRowHeight: 26,
      freeze: { xSplit: 0, ySplit: 5, startRow: 5, startColumn: 0 },
      rowData: { 0: { h: 42, ah: 42, ia: 1, hd: 0 } },
      columnData: { 0: { w: 70, hd: 0 } },
      rowHeader: { width: 46, hidden: 0 },
      columnHeader: { height: 24, hidden: 0 },
      showGridlines: 1,
      rightToLeft: 0,
    });
    expect(sheet?.cellData?.[0]?.[0]?.v).toBe('001');
    expect((snapshot.sheets['report-sheet']?.freeze as unknown as { ySplit: unknown }).ySplit)
      .toBe('5');
  });

  it('preserves Univer no-freeze sentinel values after cancelling frozen panes', () => {
    const snapshot = workbookWithRuntimeLayoutNumbers();
    snapshot.sheets['report-sheet']!.freeze = {
      xSplit: '0',
      ySplit: 0,
      startRow: '-1',
      startColumn: -1,
    } as unknown as NonNullable<typeof snapshot.sheets['report-sheet']>['freeze'];

    const normalized = normalizeWorkbookSnapshot(snapshot);

    expect(normalized.sheets['report-sheet']?.freeze).toEqual({
      xSplit: 0,
      ySplit: 0,
      startRow: -1,
      startColumn: -1,
    });
  });

  it('rejects negative freeze values other than the Univer no-freeze sentinel', () => {
    const snapshot = workbookWithRuntimeLayoutNumbers();
    snapshot.sheets['report-sheet']!.freeze = {
      xSplit: 0,
      ySplit: 0,
      startRow: -2,
      startColumn: -1,
    };

    expect(() => normalizeWorkbookSnapshot(snapshot))
      .toThrow("工作表 'report-sheet' 的冻结设置.startRow必须是 -1 或有效的非负整数。");
  });

  it('clears formula error caches without changing formulas or valid cached values', () => {
    const snapshot = workbookWithRuntimeLayoutNumbers();
    const sheet = snapshot.sheets['report-sheet'];
    sheet.cellData = {
      5: {
        4: { f: '=C6+D6', v: '#VALUE!', t: 1 },
        5: { f: '=AVERAGE(C6:D6)', v: '#DIV/0!', t: 1 },
        6: { f: '="正常"', v: '正常', t: 1 },
      },
    };

    const normalized = normalizeWorkbookSnapshot(snapshot);
    const cells = normalized.sheets['report-sheet']?.cellData?.[5];

    expect(cells?.[4]).toMatchObject({ f: '=C6+D6', v: 0, t: 2 });
    expect(cells?.[5]).toMatchObject({ f: '=AVERAGE(C6:D6)', v: 0, t: 2 });
    expect(cells?.[6]).toMatchObject({ f: '="正常"', v: '正常', t: 1 });
    expect(sheet.cellData?.[5]?.[4]?.v).toBe('#VALUE!');
  });
});
