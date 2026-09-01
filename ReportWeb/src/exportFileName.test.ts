import { describe, expect, it } from 'vitest';
import {
  defaultExportFileName,
  normalizeExportFileName,
  validateExportFileName,
} from './exportFileName';

describe('导出文件名', () => {
  /** 验证模板和数据导出会从报表名称生成可区分的默认文件名。 */
  it('generates defaults for template and data exports', () => {
    expect(defaultExportFileName('科室工作量统计', 'template')).toBe('科室工作量统计_模板.xlsx');
    expect(defaultExportFileName('科室工作量统计', 'data')).toBe('科室工作量统计_数据.xlsx');
  });

  /** 验证非法名称会被阻止，避免浏览器将名称解释为路径或特殊文件。 */
  it('rejects empty and invalid names', () => {
    expect(validateExportFileName('')).not.toBe('');
    expect(validateExportFileName('报表/汇总')).not.toBe('');
    expect(validateExportFileName('报表.xlsx')).toBe('');
  });

  /** 验证手工输入名称会统一补全 xlsx 扩展名并清理非法字符。 */
  it('normalizes a custom name to xlsx', () => {
    expect(normalizeExportFileName(' 月度汇总 ', '报表.xlsx')).toBe('月度汇总.xlsx');
    expect(normalizeExportFileName('月度/汇总', '报表.xlsx')).toBe('月度_汇总.xlsx');
    expect(normalizeExportFileName('', '报表.xlsx')).toBe('报表.xlsx');
  });
});
