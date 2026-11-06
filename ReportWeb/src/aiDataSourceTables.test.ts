import { describe, expect, it } from 'vitest';
import {
  AiTableListRenderLimit,
  aiDataSourceTableSubtitle,
  filterAiDataSourceTables,
  sliceAiTableRenderList,
} from './aiDataSourceTables';
import type { AiDataSourceTable } from './types';

/**
 * 构造测试用表清单条目。
 * @param name - 名称。
 * @param kind - 类型（table/view）。
 * @param comment - 注释。
 * @returns 清单条目。
 */
function table(name: string, kind: 'table' | 'view' = 'table', comment: string | null = null): AiDataSourceTable {
  return { name, kind, comment };
}

describe('filterAiDataSourceTables', () => {
  it('keeps the whole list for a blank query', () => {
    const tables = [table('PatientInfo'), table('V_PatientFee', 'view', '患者费用视图')];

    expect(filterAiDataSourceTables(tables, '   ')).toBe(tables);
  });

  it('matches name and comment case-insensitively', () => {
    const tables = [
      table('PatientInfo', 'table', '患者信息表'),
      table('V_FeeDetail', 'view', '费用明细视图'),
      table('DeptDict', 'table', '科室字典'),
    ];

    const byName = filterAiDataSourceTables(tables, 'patient');
    const byComment = filterAiDataSourceTables(tables, '费用');

    expect(byName.map((item) => item.name)).toEqual(['PatientInfo']);
    expect(byComment.map((item) => item.name)).toEqual(['V_FeeDetail']);
  });

  it('matches view keyword against entries regardless of kind', () => {
    const tables = [table('V_FeeDetail', 'view'), table('PatientInfo')];

    expect(filterAiDataSourceTables(tables, 'V_').map((item) => item.name)).toEqual(['V_FeeDetail']);
  });
});

describe('sliceAiTableRenderList', () => {
  it('returns everything when under the render limit', () => {
    const tables = Array.from({ length: 10 }, (_, index) => table(`T${index}`));

    const rendered = sliceAiTableRenderList(tables);

    expect(rendered.items).toHaveLength(10);
    expect(rendered.hiddenCount).toBe(0);
  });

  it('caps rendering at 200 entries and reports the hidden count', () => {
    const tables = Array.from({ length: 431 }, (_, index) => table(`Table${index}`));

    const rendered = sliceAiTableRenderList(tables);

    expect(AiTableListRenderLimit).toBe(200);
    expect(rendered.items).toHaveLength(200);
    expect(rendered.hiddenCount).toBe(231);
    expect(rendered.items[0].name).toBe('Table0');
  });
});

describe('aiDataSourceTableSubtitle', () => {
  it('prefers the database comment as the row subtitle', () => {
    expect(aiDataSourceTableSubtitle(table('PatientInfo', 'table', '患者信息表'))).toBe('患者信息表');
  });

  it('falls back to the kind label when no comment exists', () => {
    expect(aiDataSourceTableSubtitle(table('V_FeeDetail', 'view'))).toBe('视图');
    expect(aiDataSourceTableSubtitle(table('DeptDict'))).toBe('数据表');
  });
});
