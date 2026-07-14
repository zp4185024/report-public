import { describe, expect, it } from 'vitest';
import { buildWorkbookData } from './reportWorkbook';
import type { ExecuteResult, ReportDefinition } from './types';

const definition: ReportDefinition = {
  code: 'dept-workload',
  name: '科室工作量统计',
  sheetName: '科室工作量',
  preview: { mode: 'Paged', pageSize: 20, showExactTotal: false },
  parameters: [
    { code: 'hospitalId', displayName: '医院', controlType: 'text', sourceType: 'url', defaultValue: null, visible: true },
    { code: 'deptId', displayName: '科室', controlType: 'text', sourceType: 'url', defaultValue: null, visible: true },
    { code: 'personId', displayName: '人员', controlType: 'text', sourceType: 'url', defaultValue: null, visible: true },
    { code: 'startDate', displayName: '开始日期', controlType: 'date', sourceType: 'default-function', defaultValue: '2026-07-01', visible: true },
    { code: 'endDate', displayName: '结束日期', controlType: 'date', sourceType: 'default-function', defaultValue: '2026-07-14', visible: true },
  ],
  columns: [
    { code: 'sequence', header: '序号', dataType: 'number', width: 70, formulaTemplate: null },
    { code: 'departmentName', header: '科室', dataType: 'text', width: 180, formulaTemplate: null },
    { code: 'outpatientCount', header: '门诊人次', dataType: 'number', width: 110, formulaTemplate: null },
    { code: 'dischargeCount', header: '出院人次', dataType: 'number', width: 110, formulaTemplate: null },
    { code: 'totalWorkload', header: '合计', dataType: 'formula', width: 110, formulaTemplate: '=C{row}+D{row}' },
    { code: 'averageWorkload', header: '平均值', dataType: 'formula', width: 110, formulaTemplate: '=AVERAGE(C{row}:D{row})' },
  ],
};

const result: ExecuteResult = {
  page: 1,
  pageSize: 20,
  hasMore: true,
  parameters: {
    hospitalId: 'H01',
    deptId: 'D02',
    personId: 'P03',
    startDate: '2026-07-01',
    endDate: '2026-07-14',
  },
  rows: Array.from({ length: 20 }, (_, index) => ({
    sequence: index + 1,
    departmentName: `D02-${String(index + 1).padStart(2, '0')}`,
    outpatientCount: 100 + index,
    dischargeCount: 20 + index,
  })),
};

describe('buildWorkbookData', () => {
  it('writes report title, resolved parameters, rows and formulas', () => {
    const workbook = buildWorkbookData(definition, result);
    const sheet = workbook.sheets['report-sheet'];

    expect(sheet.cellData?.[0]?.[0]?.v).toBe('科室工作量统计');
    expect(sheet.cellData?.[1]?.[0]?.v).toBe('医院：H01');
    expect(sheet.cellData?.[1]?.[2]?.v).toBe('科室：D02');
    expect(sheet.cellData?.[1]?.[4]?.v).toBe('人员：P03');
    expect(sheet.cellData?.[5]?.[0]?.v).toBe(1);
    expect(sheet.cellData?.[5]?.[4]?.f).toBe('=C6+D6');
    expect(sheet.cellData?.[5]?.[5]?.f).toBe('=AVERAGE(C6:D6)');
    expect(Object.keys(sheet.cellData ?? {}).filter((row) => Number(row) >= 5)).toHaveLength(20);
  });
});
