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
});
