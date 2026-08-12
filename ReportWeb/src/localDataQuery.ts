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

function normalizedCode(value: string): string {
  return value.trim().toUpperCase();
}

function requestValue(
  parameters: Readonly<Record<string, string | null>>,
  code: string,
): string | null {
  const matchingEntry = Object.entries(parameters).find(
    ([parameterCode]) => normalizedCode(parameterCode) === normalizedCode(code),
  );
  return matchingEntry?.[1]?.trim() || null;
}

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

export function serializeMultiSelectValues(values: readonly string[]): string | null {
  const normalized = values.map((item) => item.trim()).filter(Boolean);
  return normalized.length === 0 ? null : JSON.stringify(normalized);
}

function comparableValue(value: DataRowValue): string {
  return value === null ? '' : String(value).trim().toUpperCase();
}

function mappedLabel(
  value: DataRowValue,
  mappings: readonly { value: string; label: string }[],
): DataRowValue {
  const mapping = mappings.find((item) => comparableValue(item.value) === comparableValue(value));
  return mapping?.label ?? value;
}

function rowEntry(row: WorkloadRow, code: string): [string, DataRowValue] | undefined {
  return Object.entries(row).find(([rowCode]) => normalizedCode(rowCode) === normalizedCode(code));
}

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

function parameterRequestValues(parameter: ParameterDefinition, request: ExecuteRequest): string[] {
  const value = requestValue(request.parameters, parameter.code);
  return parameter.controlType.toLowerCase() === 'multiselect'
    ? readMultiSelectValues(value)
    : value === null ? [] : [value];
}

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
