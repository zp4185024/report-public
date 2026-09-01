import { describe, expect, it } from 'vitest';
import { createPreviewToolbarPresentation } from './previewToolbar';
import type { ExecuteResult } from './types';

/**
 * 验证 result 场景下的预期行为，并防止相关逻辑回归。
 * @param page - 控制数据范围或位置的数值。
 * @param rowCount - 控制数据范围或位置的数值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function result(page: number, rowCount: number): ExecuteResult {
  return {
    page,
    pageSize: rowCount,
    hasMore: false,
    parameters: {
      hospitalId: 'H01',
      deptId: 'D02',
      personId: 'P03',
      startDate: '2026-07-01',
      endDate: '2026-07-15',
    },
    rows: Array.from({ length: rowCount }, (_, index) => ({
      sequence: index + 1,
      departmentName: `D02-${index + 1}`,
      outpatientCount: 100 + index,
      dischargeCount: 20 + index,
    })),
  };
}

describe('createPreviewToolbarPresentation', () => {
  it('shows one full row count and hides paging for Full preview', () => {
    expect(createPreviewToolbarPresentation('Full', result(1, 137))).toEqual({
      labels: ['共 137 行'],
      showPager: false,
    });
  });

  it('shows the current page and enables paging for Paged preview', () => {
    expect(createPreviewToolbarPresentation('Paged', result(2, 20))).toEqual({
      labels: ['第 2 页', '20 行'],
      showPager: true,
    });
  });
});
