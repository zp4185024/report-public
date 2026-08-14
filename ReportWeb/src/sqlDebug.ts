import type { ExecuteResult } from './types';

interface DebugParameterRow {
  参数名: string;
  类型?: string;
  值: unknown;
  SQL已绑定?: boolean;
}

/** 开发模式查询诊断：区分统一参数输入、SQL 实际绑定参数和展示 SQL。 */
export function logSqlDebug(
  reportCode: string,
  result: ExecuteResult,
): void {
  if (!import.meta.env.DEV || result.sqlDebug === null || result.sqlDebug === undefined) {
    return;
  }

  const boundParameters: DebugParameterRow[] = result.sqlDebug.parameters
    .map((parameter) => ({
      参数名: parameter.name,
      类型: parameter.dataType,
      值: parameter.value,
    }));
  const boundParameterNames = new Set(result.sqlDebug.parameters
    .map((parameter) => parameter.name.replace(/^@/, '').toLowerCase()));
  const unifiedParameters: DebugParameterRow[] = Object.entries(result.parameters)
    .map(([name, value]) => ({
      参数名: name,
      值: value,
      SQL已绑定: boundParameterNames.has(name.toLowerCase()),
    }));

  console.group(`[报表 SQL] ${reportCode}`);
  console.info('1. 后台统一解析参数（URL、页面和外部接口参数汇总后）');
  console.table(unifiedParameters);
  console.info('2. 数据库实际执行的参数化 SQL');
  console.info(result.sqlDebug.parameterizedSql);
  console.info('3. SQL 实际绑定参数');
  console.table(boundParameters);
  console.info('4. 参数替换后的展示 SQL（仅用于核对，不会提交数据库执行）');
  console.info(result.sqlDebug.displaySql);
  console.groupEnd();
}
