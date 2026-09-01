import type {
  DataRowValue,
  DatasetFieldDefinition,
  ExecuteRequest,
  ExecuteResult,
  FieldPresentationDefinition,
  ParameterDefinition,
  ReportDefinition,
  WorkloadRow,
} from './types';

export const localFilterSourceType = 'LocalFilter';

/**
 * 将 localDataQuery 中的 normalized Code 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizedCode(value: string): string {
  return value.trim().toUpperCase();
}

/**
 * 按 request Value 规则筛选 localDataQuery 数据，并返回满足条件的结果。
 * @param parameters - parameters 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param code - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function requestValue(
  parameters: Readonly<Record<string, string | null>>,
  code: string,
): string | null {
  const matchingEntry = Object.entries(parameters).find(
    ([parameterCode]) => normalizedCode(parameterCode) === normalizedCode(code),
  );
  return matchingEntry?.[1]?.trim() || null;
}

/**
 * 查询或读取 localDataQuery 所需的 read Multi Select Values 数据，并按调用约定返回结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function readMultiSelectValues(value: string | null | undefined): string[] {
  if (value === null || value === undefined || !value.trim()) return [];
  try {
    const parsed = JSON.parse(value) as unknown;
    if (Array.isArray(parsed)) {
      return parsed
        .filter((item): item is string => typeof item === 'string')
        .map((item) => item.trim())
        .filter(Boolean);
    }
  } catch {
    // 兼容旧的逗号分隔 URL 参数。
  }
  return value.split(',').map((item) => item.trim()).filter(Boolean);
}

/**
 * 将 localDataQuery 中的 serialize Multi Select Values 输入转换为调用方需要的稳定格式。
 * @param values - values 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function serializeMultiSelectValues(values: readonly string[]): string | null {
  const normalized = values.map((item) => item.trim()).filter(Boolean);
  return normalized.length === 0 ? null : JSON.stringify(normalized);
}

/**
 * 负责 localDataQuery 的 comparable Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function comparableValue(value: DataRowValue): string {
  return value === null ? '' : String(value).trim().toUpperCase();
}

/**
 * 将 localDataQuery 中的 mapped Label 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @param mappings - mappings 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function mappedLabel(
  value: DataRowValue,
  mappings: readonly { value: string; label: string }[],
): DataRowValue {
  const mapping = mappings.find((item) => comparableValue(item.value) === comparableValue(value));
  return mapping?.label ?? value;
}

/**
 * 按 row Entry 规则筛选 localDataQuery 数据，并返回满足条件的结果。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @param code - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function rowEntry(row: WorkloadRow, code: string): [string, DataRowValue] | undefined {
  return Object.entries(row).find(([rowCode]) => normalizedCode(rowCode) === normalizedCode(code));
}

/**
 * 负责 localDataQuery 的 display Row 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @param presentations - presentations 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function displayRow(
  row: WorkloadRow,
  presentations: readonly FieldPresentationDefinition[],
): WorkloadRow {
  let displayed = row;
  for (const presentation of presentations) {
    const entry = rowEntry(displayed, presentation.code);
    if (entry === undefined) continue;
    const nextValue = mappedLabel(entry[1], presentation.valueMappings);
    if (nextValue !== entry[1]) {
      displayed = { ...displayed, [entry[0]]: nextValue };
    }
  }
  return displayed;
}

/**
 * 校验 localDataQuery 的 is Local Filter Source 条件，并返回可供后续流程判断的结果。
 * @param sourceType - sourceType 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function isLocalFilterSource(sourceType: string): boolean {
  return sourceType.toLowerCase() === localFilterSourceType.toLowerCase();
}

/** 本地筛选是编辑器概念；持久化时继续使用后端已支持的 Form 类型。 */
export function toPersistedLocalFilterParameter(
  parameter: ParameterDefinition,
): ParameterDefinition {
  return isLocalFilterSource(parameter.sourceType)
    ? { ...parameter, sourceType: 'Form', readOnly: false }
    : { ...parameter };
}

/** 参数编码与返回字段编码一致时，视为前端拿到完整结果后的本地精确筛选。 */
export function isLocalFilterParameter(
  parameter: Pick<ParameterDefinition, 'code' | 'sourceType'>,
  fields: readonly Pick<DatasetFieldDefinition, 'code'>[],
): boolean {
  const sourceType = parameter.sourceType.toLowerCase();
  return (sourceType === 'form' || isLocalFilterSource(sourceType)) && fields.some(
    (field) => normalizedCode(field.code) === normalizedCode(parameter.code),
  );
}

/**
 * 构造并发送 localDataQuery 的 parameter Request Values 接口请求，校验调用参数并返回类型化响应。
 * @param parameter - parameter 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param request - 本次操作使用的输入数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function parameterRequestValues(parameter: ParameterDefinition, request: ExecuteRequest): string[] {
  const value = requestValue(request.parameters, parameter.code);
  return parameter.controlType.toLowerCase() === 'multiselect'
    ? readMultiSelectValues(value)
    : value === null ? [] : [value];
}

/**
 * 按 display Parameter Value 规则筛选 localDataQuery 数据，并返回满足条件的结果。
 * @param parameter - parameter 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function displayParameterValue(parameter: ParameterDefinition, value: string): string {
  const values = parameter.controlType.toLowerCase() === 'multiselect'
    ? readMultiSelectValues(value)
    : [value];
  return values.map((item) => {
    const option = (parameter.options ?? []).find(
      (candidate) => comparableValue(candidate.value) === comparableValue(item),
    );
    return option?.label ?? item;
  }).join('、');
}

/**
 * SQL 结果保持原始值用于比较；筛选完成后再按独立字段显示映射转换。
 * 分页结果不是完整集合，禁止伪装成全量本地筛选。
 */
export function applyLocalDataQuery(
  result: ExecuteResult,
  definition: ReportDefinition,
  request: ExecuteRequest,
): ExecuteResult {
  const filters = definition.parameters
    .filter((parameter) => isLocalFilterParameter(parameter, definition.fields))
    .map((parameter) => ({
      code: parameter.code,
      values: parameterRequestValues(parameter, request),
    }))
    .filter((filter) => filter.values.length > 0);

  if (filters.length > 0 && definition.preview.mode !== 'Full') {
    throw new Error('本地筛选仅支持整表预览，请把预览模式改为“整表预览”。');
  }

  const filteredRows = filters.length === 0
    ? result.rows
    : result.rows.filter((row) => filters.every((filter) => (
        filter.values.some((value) => (
          comparableValue(rowEntry(row, filter.code)?.[1] ?? null) === comparableValue(value)
        ))
      )));
  const parameters = Object.fromEntries(Object.entries(result.parameters).map(([code, value]) => [
    code,
    (() => {
      const parameter = definition.parameters.find(
        (item) => normalizedCode(item.code) === normalizedCode(code),
      );
      return parameter === undefined ? value : displayParameterValue(parameter, value);
    })(),
  ]));
  const rows = filteredRows.map((row) => displayRow(row, definition.fieldPresentations ?? []));

  return {
    ...result,
    pageSize: definition.preview.mode === 'Full' ? rows.length : result.pageSize,
    hasMore: definition.preview.mode === 'Full' ? false : result.hasMore,
    parameters,
    rows,
  };
}
