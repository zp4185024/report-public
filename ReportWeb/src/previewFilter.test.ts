import { describe, expect, it } from 'vitest';
import type { TemplatePreviewPlan } from './templatePreview';
import {
  createPreviewFilterTarget,
  formatFilterRange,
  isPreviewFilterAnchor,
  isSameFilterRange,
  shouldRepairPreviewFilterRange,
} from './previewFilter';

/**
 * 验证 plan 场景下的预期行为，并防止相关逻辑回归。
 * @param detail - detail 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function plan(detail: TemplatePreviewPlan['detail']): TemplatePreviewPlan {
  return { parameterWrites: [], detail };
}

describe('createPreviewFilterTarget', () => {
  it('uses the row above the first detail row as the filter header', () => {
    const target = createPreviewFilterTarget(plan({
      sheetId: 'report-sheet',
      templateRow: 4,
      insertCount: 2,
      rows: [
        { row: 4, values: [{ column: 2, value: 'a' }, { column: 6, value: 'b' }] },
        { row: 5, values: [{ column: 2, value: 'c' }, { column: 6, value: 'd' }] },
        { row: 6, values: [{ column: 2, value: 'e' }, { column: 6, value: 'f' }] },
      ],
    }));

    expect(target).toEqual({
      sheetId: 'report-sheet',
      range: {
        startRow: 3,
        endRow: 6,
        startColumn: 2,
        endColumn: 6,
      },
    });
  });

  it('falls back to the current selection when no separate header can be inferred', () => {
    expect(createPreviewFilterTarget(plan(null))).toBeNull();
    expect(createPreviewFilterTarget(plan({
      sheetId: 'report-sheet',
      templateRow: 0,
      insertCount: 0,
      rows: [{ row: 0, values: [{ column: 0, value: 'value' }] }],
    }))).toBeNull();
  });
});

describe('isSameFilterRange', () => {
  it('compares all four range boundaries', () => {
    const range = { startRow: 3, endRow: 6, startColumn: 2, endColumn: 6 };
    expect(isSameFilterRange(range, { ...range })).toBe(true);
    expect(isSameFilterRange(range, { ...range, startRow: 4 })).toBe(false);
  });
});

describe('preview filter selection', () => {
  const expected = { startRow: 3, endRow: 2865, startColumn: 0, endColumn: 13 };

  it('recognizes only the default header anchor', () => {
    expect(isPreviewFilterAnchor({
      startRow: 3,
      endRow: 3,
      startColumn: 0,
      endColumn: 0,
    }, expected)).toBe(true);
    expect(isPreviewFilterAnchor({
      startRow: 9,
      endRow: 9,
      startColumn: 7,
      endColumn: 7,
    }, expected)).toBe(false);
  });

  it('repairs a mismatched native range only for the default anchor', () => {
    const anchor = { startRow: 3, endRow: 3, startColumn: 0, endColumn: 0 };
    const userCell = { startRow: 9, endRow: 9, startColumn: 7, endColumn: 7 };
    const mismatched = { ...expected, startRow: 4 };
    expect(shouldRepairPreviewFilterRange(anchor, mismatched, expected)).toBe(true);
    expect(shouldRepairPreviewFilterRange(userCell, mismatched, expected)).toBe(false);
    expect(shouldRepairPreviewFilterRange(anchor, expected, expected)).toBe(false);
  });

  it('formats the applied range for user feedback', () => {
    expect(formatFilterRange(expected)).toBe('A4:N2866');
    expect(formatFilterRange({
      startRow: 0,
      endRow: 0,
      startColumn: 26,
      endColumn: 26,
    })).toBe('AA1');
  });
});
