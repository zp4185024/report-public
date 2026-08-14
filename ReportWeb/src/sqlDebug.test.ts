import { afterEach, describe, expect, it, vi } from 'vitest';
import { logSqlDebug } from './sqlDebug';
import type { ExecuteResult } from './types';

describe('logSqlDebug', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('logs unified URL values, bound parameters and both SQL forms', () => {
    const group = vi.spyOn(console, 'group').mockImplementation(() => undefined);
    const info = vi.spyOn(console, 'info').mockImplementation(() => undefined);
    const table = vi.spyOn(console, 'table').mockImplementation(() => undefined);
    const groupEnd = vi.spyOn(console, 'groupEnd').mockImplementation(() => undefined);
    const result: ExecuteResult = {
      page: 1,
      pageSize: 20,
      hasMore: false,
      parameters: {
        hisDepartmentId: 'D03',
        hisDepartmentName: '方便门诊',
      },
      rows: [],
      sqlDebug: {
        parameterizedSql: 'select * from REG_TRADE where DEPARTMENT_ID = @hisDepartmentId',
        displaySql: "select * from REG_TRADE where DEPARTMENT_ID = 'D03'",
        parameters: [{ name: '@hisDepartmentId', dataType: 'String', value: 'D03' }],
      },
    };

    logSqlDebug('test2', result);

    expect(group).toHaveBeenCalledWith('[报表 SQL] test2');
    expect(table).toHaveBeenNthCalledWith(1, [
      { 参数名: 'hisDepartmentId', 值: 'D03', SQL已绑定: true },
      { 参数名: 'hisDepartmentName', 值: '方便门诊', SQL已绑定: false },
    ]);
    expect(table).toHaveBeenNthCalledWith(2, [
      { 参数名: '@hisDepartmentId', 类型: 'String', 值: 'D03' },
    ]);
    expect(info).toHaveBeenCalledWith(result.sqlDebug?.parameterizedSql);
    expect(info).toHaveBeenCalledWith(result.sqlDebug?.displaySql);
    expect(groupEnd).toHaveBeenCalledOnce();
  });

  it('does not log when the backend omitted development diagnostics', () => {
    const group = vi.spyOn(console, 'group').mockImplementation(() => undefined);

    logSqlDebug('test2', {
      page: 1,
      pageSize: 20,
      hasMore: false,
      parameters: {},
      rows: [],
    });

    expect(group).not.toHaveBeenCalled();
  });
});
