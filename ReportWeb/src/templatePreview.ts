import type { IWorkbookData } from '@univerjs/presets';
import { MAX_TEMPLATE_COORDINATE } from './templateBinding';
import { workloadRowFieldCodes } from './types';
import type {
  ExecuteResult,
  ResolvedParameters,
  TemplateBinding,
  WorkloadRowFieldCode,
} from './types';

const tokenRegex = /\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9]*)[ \t\r\n\f\v]*\}\}/g;
const detailTokenRegex = /^\{\{[ \t\r\n\f\v]*detail\.([A-Za-z][A-Za-z0-9]*)[ \t\r\n\f\v]*\}\}$/;
const resolvedParameterCodes = [
  'hospitalId',
  'deptId',
  'personId',
  'startDate',
  'endDate',
] as const satisfies readonly (keyof ResolvedParameters)[];
const resolvedParameterCodeSet: ReadonlySet<string> = new Set(resolvedParameterCodes);
const workloadRowFieldCodeSet: ReadonlySet<string> = new Set(workloadRowFieldCodes);

type JsonObject = Record<string, unknown>;

interface ErrorLocation {
  sheetId?: string;
  row?: number;
  column?: number;
}

interface ParameterCellGroup {
  sheetId: string;
  row: number;
  column: number;
  codes: Set<keyof ResolvedParameters>;
}

interface ValidatedParameterBinding {
  code: string;
  sheetId: string;
  row: number;
  column: number;
}

interface ValidatedDetailField {
  code: string;
  column: number;
}

interface ValidatedDetailBinding {
  datasetCode: 'detail';
  sheetId: string;
  templateRow: number;
  fields: ValidatedDetailField[];
}

interface ValidatedTemplateBinding {
  version: 1;
  parameterBindings: ValidatedParameterBinding[];
  detailBinding: ValidatedDetailBinding | null;
}

interface ValidatedExecuteResult {
  parameters: ResolvedParameters;
  rows: unknown[];
}

interface ValidatedSnapshot {
  sheets: JsonObject;
}

export interface CellWrite {
  sheetId: string;
  row: number;
  column: number;
  value: string | number | boolean | null;
}

export interface DetailRowPlan {
  row: number;
  values: Array<{ column: number; value: string | number | null }>;
}

export interface TemplatePreviewPlan {
  parameterWrites: CellWrite[];
  detail: null | {
    sheetId: string;
    templateRow: number;
    insertCount: number;
    rows: DetailRowPlan[];
  };
}

export class TemplatePreviewError extends Error {
  sheetId?: string;
  row?: number;
  column?: number;
  cellAddress?: string;

  constructor(message: string, location: ErrorLocation = {}) {
    const { sheetId, row, column } = location;
    const cellAddress = isCoordinate(row) && isCoordinate(column)
      ? toCellAddress(row, column)
      : undefined;
    const context = [
      sheetId !== undefined ? `工作表 '${sheetId}'` : undefined,
      cellAddress !== undefined ? `单元格 ${cellAddress}` : undefined,
    ].filter(Boolean).join('，');

    super(context ? `${message}（${context}）` : message);
    this.name = 'TemplatePreviewError';
    this.sheetId = sheetId;
    this.row = row;
    this.column = column;
    this.cellAddress = cellAddress;
  }
}

export function createTemplatePreviewPlan(
  snapshot: IWorkbookData,
  binding: TemplateBinding,
  result: ExecuteResult,
): TemplatePreviewPlan {
  const validatedSnapshot = validateSnapshot(snapshot as unknown);
  const validatedBinding = validateTemplateBinding(binding as unknown);
  const validatedResult = validateExecuteResult(result as unknown);

  return {
    parameterWrites: createParameterWrites(validatedSnapshot, validatedBinding, validatedResult),
    detail: createDetailPlan(validatedSnapshot, validatedBinding, validatedResult),
  };
}

function createParameterWrites(
  snapshot: ValidatedSnapshot,
  binding: ValidatedTemplateBinding,
  result: ValidatedExecuteResult,
): CellWrite[] {
  const groups = new Map<string, ParameterCellGroup>();

  for (const parameterBinding of binding.parameterBindings) {
    const location = {
      sheetId: parameterBinding.sheetId,
      row: parameterBinding.row,
      column: parameterBinding.column,
    };
    ensureCoordinate(parameterBinding.row, '参数绑定行坐标无效。', location);
    ensureCoordinate(parameterBinding.column, '参数绑定列坐标无效。', location);
    const code = ensureParameterCode(parameterBinding.code, result.parameters, location);
    const groupKey = JSON.stringify([
      parameterBinding.sheetId,
      parameterBinding.row,
      parameterBinding.column,
    ]);
    const existingGroup = groups.get(groupKey);
    if (existingGroup !== undefined) {
      existingGroup.codes.add(code);
      continue;
    }

    groups.set(groupKey, {
      sheetId: parameterBinding.sheetId,
      row: parameterBinding.row,
      column: parameterBinding.column,
      codes: new Set([code]),
    });
  }

  return Array.from(groups.values())
    .sort(compareParameterCells)
    .map((group) => {
      const originalValue = getParameterCellValue(snapshot, group);
      const value = replaceParameterTokens(originalValue, group, result.parameters);

      return {
        sheetId: group.sheetId,
        row: group.row,
        column: group.column,
        value,
      };
    });
}

function replaceParameterTokens(
  originalValue: string,
  group: ParameterCellGroup,
  parameters: ResolvedParameters,
): string {
  const location = {
    sheetId: group.sheetId,
    row: group.row,
    column: group.column,
  };
  const matchedCodes = new Set<keyof ResolvedParameters>();

  for (const match of originalValue.matchAll(tokenRegex)) {
    const matchIndex = match.index;
    const matchEnd = matchIndex + match[0].length;
    if (originalValue[matchIndex - 1] === '{' || originalValue[matchEnd] === '}') {
      throw new TemplatePreviewError('参数绑定单元格包含格式错误的 token。', location);
    }

    const scope = match[1];
    const typedCode = match[2] as keyof ResolvedParameters;
    if (scope !== 'params') {
      throw new TemplatePreviewError('参数绑定单元格不能包含 detail token。', location);
    }
    if (!group.codes.has(typedCode)) {
      throw new TemplatePreviewError(`参数 token '${match[2]}' 缺少对应绑定。`, location);
    }
    matchedCodes.add(typedCode);
  }

  const missingCode = Array.from(group.codes).find((code) => !matchedCodes.has(code));
  if (missingCode !== undefined) {
    throw new TemplatePreviewError(
      `参数绑定单元格缺少 params token '${missingCode}'。`,
      location,
    );
  }

  const value = originalValue.replace(
    tokenRegex,
    (_token, _scope: string, code: string) => parameters[code as keyof ResolvedParameters],
  );
  if (value.includes('{{') || value.includes('}}')) {
    throw new TemplatePreviewError('参数绑定单元格替换后仍包含残留 token。', location);
  }
  return value;
}

function createDetailPlan(
  snapshot: ValidatedSnapshot,
  binding: ValidatedTemplateBinding,
  result: ValidatedExecuteResult,
): TemplatePreviewPlan['detail'] {
  const detailBinding = binding.detailBinding;
  if (detailBinding === null) {
    return null;
  }

  const sheetId = detailBinding.sheetId;
  const templateRow = detailBinding.templateRow;
  ensureSheetExists(snapshot, sheetId, {
    sheetId,
    row: templateRow,
    column: 0,
  });

  const fields = detailBinding.fields.map((field): { code: WorkloadRowFieldCode; column: number } => {
    const column = field.column;
    const location = {
      sheetId,
      row: templateRow,
      column,
    };
    if (!workloadRowFieldCodeSet.has(field.code)) {
      throw new TemplatePreviewError(`未知的 detail code '${field.code}'。`, location);
    }
    ensureDetailTemplateCell(snapshot, sheetId, templateRow, column, field.code);

    return {
      code: field.code as WorkloadRowFieldCode,
      column,
    };
  }).sort(
    (left, right) => left.column - right.column || compareOrdinal(left.code, right.code),
  );

  const lastTargetRow = templateRow + Math.max(0, result.rows.length - 1);
  ensureCoordinate(lastTargetRow, '明细结果最后目标行坐标无效。', {
    sheetId,
    row: lastTargetRow,
    column: fields[0]?.column ?? 0,
  });

  const rows = createDetailRows(sheetId, templateRow, fields, result.rows);

  return {
    sheetId,
    templateRow,
    insertCount: Math.max(0, result.rows.length - 1),
    rows,
  };
}

function createDetailRows(
  sheetId: string,
  templateRow: number,
  fields: Array<{ code: WorkloadRowFieldCode; column: number }>,
  resultRows: unknown[],
): DetailRowPlan[] {
  if (resultRows.length === 0) {
    return [{
      row: templateRow,
      values: fields.map(({ column }) => ({ column, value: null })),
    }];
  }

  return resultRows.map((resultRow, index) => {
    const targetRow = templateRow + index;
    const rowLocation = {
      sheetId,
      row: targetRow,
      column: fields[0]?.column ?? 0,
    };
    ensureCoordinate(targetRow, '明细结果目标行坐标无效。', rowLocation);
    if (!isPlainObject(resultRow)) {
      throw new TemplatePreviewError('明细结果行必须是普通对象。', rowLocation);
    }

    return {
      row: targetRow,
      values: fields.map(({ code, column }) => ({
        column,
        value: getDetailFieldValue(resultRow, code, {
          sheetId,
          row: targetRow,
          column,
        }),
      })),
    };
  });
}

function getDetailFieldValue(
  resultRow: JsonObject,
  code: WorkloadRowFieldCode,
  location: ErrorLocation,
): string | number {
  if (!Object.prototype.hasOwnProperty.call(resultRow, code)) {
    throw new TemplatePreviewError(`明细结果行缺少字段 '${code}'。`, location);
  }

  const value = resultRow[code];
  if (code === 'departmentName') {
    if (typeof value !== 'string') {
      throw new TemplatePreviewError(`明细字段 '${code}' 必须是字符串。`, location);
    }
    return value;
  }
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new TemplatePreviewError(`明细字段 '${code}' 必须是有限数字。`, location);
  }
  return value;
}

function validateTemplateBinding(value: unknown): ValidatedTemplateBinding {
  if (!isJsonObject(value)) {
    throw new TemplatePreviewError('模板绑定必须是对象。');
  }
  if (value.version !== 1) {
    throw new TemplatePreviewError('模板绑定 version 必须为 1。');
  }
  if (!Array.isArray(value.parameterBindings)) {
    throw new TemplatePreviewError('parameterBindings 必须是数组。');
  }

  const parameterBindings = value.parameterBindings.map((parameterBinding) => {
    if (!isJsonObject(parameterBinding)) {
      throw new TemplatePreviewError('参数绑定必须是对象。');
    }

    const location = runtimeLocation(parameterBinding);
    if (typeof parameterBinding.sheetId !== 'string') {
      throw new TemplatePreviewError('参数绑定 sheetId 必须是字符串。', location);
    }
    if (typeof parameterBinding.code !== 'string') {
      throw new TemplatePreviewError('参数绑定 code 必须是字符串。', location);
    }
    ensureCoordinate(parameterBinding.row, '参数绑定行坐标无效。', location);
    ensureCoordinate(parameterBinding.column, '参数绑定列坐标无效。', location);

    return {
      code: parameterBinding.code,
      sheetId: parameterBinding.sheetId,
      row: parameterBinding.row,
      column: parameterBinding.column,
    };
  });

  return {
    version: 1,
    parameterBindings,
    detailBinding: validateDetailBinding(value.detailBinding),
  };
}

function validateSnapshot(value: unknown): ValidatedSnapshot {
  if (!isPlainObject(value)) {
    throw new TemplatePreviewError('模板快照必须是普通对象。');
  }
  if (!isPlainObject(value.sheets)) {
    throw new TemplatePreviewError('模板快照 sheets 必须是普通对象。');
  }
  return { sheets: value.sheets };
}

function validateDetailBinding(value: unknown): ValidatedDetailBinding | null {
  if (value === null) {
    return null;
  }
  if (!isJsonObject(value)) {
    throw new TemplatePreviewError('明细绑定必须是对象或 null。');
  }

  const baseLocation = runtimeLocation(value, 0);
  if (value.datasetCode !== 'detail') {
    throw new TemplatePreviewError("明细绑定 datasetCode 必须为 'detail'。", baseLocation);
  }
  const sheetId = value.sheetId;
  if (typeof sheetId !== 'string') {
    throw new TemplatePreviewError('明细绑定 sheetId 必须是字符串。', baseLocation);
  }
  const templateRow = value.templateRow;
  ensureCoordinate(templateRow, '明细模板行坐标无效。', baseLocation);
  const rawFields = value.fields;
  if (!Array.isArray(rawFields)) {
    throw new TemplatePreviewError('明细字段绑定必须是数组。', {
      sheetId,
      row: templateRow,
      column: 0,
    });
  }

  const fields = rawFields.map((field) => {
    if (!isJsonObject(field)) {
      throw new TemplatePreviewError('明细字段绑定必须是对象。', {
        sheetId,
        row: templateRow,
        column: 0,
      });
    }

    const location = runtimeLocation(field, undefined, sheetId, templateRow);
    if (typeof field.code !== 'string') {
      throw new TemplatePreviewError('明细字段 code 必须是字符串。', location);
    }
    ensureCoordinate(field.column, '明细字段列坐标无效。', location);
    return {
      code: field.code,
      column: field.column,
    };
  });

  return {
    datasetCode: 'detail',
    sheetId,
    templateRow,
    fields,
  };
}

function validateExecuteResult(value: unknown): ValidatedExecuteResult {
  if (!isJsonObject(value)) {
    throw new TemplatePreviewError('执行结果必须是对象。');
  }
  if (!isJsonObject(value.parameters)) {
    throw new TemplatePreviewError('执行结果 parameters 必须是对象。');
  }
  for (const code of resolvedParameterCodes) {
    if (
      !Object.prototype.hasOwnProperty.call(value.parameters, code) ||
      typeof value.parameters[code] !== 'string'
    ) {
      throw new TemplatePreviewError(`执行结果参数 '${code}' 必须是字符串。`);
    }
  }
  if (!Array.isArray(value.rows)) {
    throw new TemplatePreviewError('执行结果 rows 必须是数组。');
  }

  return {
    parameters: value.parameters as unknown as ResolvedParameters,
    rows: value.rows,
  };
}

function ensureParameterCode(
  code: string,
  parameters: ResolvedParameters,
  location: ErrorLocation,
): keyof ResolvedParameters {
  if (
    !resolvedParameterCodeSet.has(code) ||
    !isJsonObject(parameters) ||
    !Object.prototype.hasOwnProperty.call(parameters, code) ||
    typeof parameters[code] !== 'string'
  ) {
    throw new TemplatePreviewError(`未知的 params code '${code}'。`, location);
  }
  return code as keyof ResolvedParameters;
}

function getParameterCellValue(
  snapshot: ValidatedSnapshot,
  group: ParameterCellGroup,
): string {
  const location = {
    sheetId: group.sheetId,
    row: group.row,
    column: group.column,
  };
  const sheet = getSheet(snapshot, group.sheetId, location);
  if (!isJsonObject(sheet.cellData)) {
    throw new TemplatePreviewError('参数绑定指向的单元格不存在。', location);
  }
  const row = sheet.cellData[String(group.row)];
  if (!isJsonObject(row)) {
    throw new TemplatePreviewError('参数绑定指向的单元格不存在。', location);
  }
  const cell = row[String(group.column)];
  if (!isJsonObject(cell)) {
    throw new TemplatePreviewError('参数绑定指向的单元格不存在。', location);
  }
  if (typeof cell.v !== 'string') {
    throw new TemplatePreviewError('参数绑定单元格的值必须是字符串。', location);
  }
  return cell.v;
}

function ensureDetailTemplateCell(
  snapshot: ValidatedSnapshot,
  sheetId: string,
  rowIndex: number,
  column: number,
  code: string,
): void {
  const location = {
    sheetId,
    row: rowIndex,
    column,
  };
  const sheet = getSheet(snapshot, sheetId, location);
  if (!isJsonObject(sheet.cellData)) {
    throw new TemplatePreviewError('明细绑定指向的单元格不存在。', location);
  }
  const row = sheet.cellData[String(rowIndex)];
  if (!isJsonObject(row)) {
    throw new TemplatePreviewError('明细绑定指向的单元格不存在。', location);
  }
  const cell = row[String(column)];
  if (!isJsonObject(cell)) {
    throw new TemplatePreviewError('明细绑定指向的单元格不存在。', location);
  }
  if (typeof cell.f === 'string' && cell.f.length > 0) {
    throw new TemplatePreviewError('明细绑定不能指向公式单元格。', location);
  }
  if (typeof cell.v !== 'string') {
    throw new TemplatePreviewError('明细绑定单元格的值必须是字符串。', location);
  }

  const match = detailTokenRegex.exec(trimAsciiWhitespace(cell.v));
  if (match === null || match[1] !== code) {
    throw new TemplatePreviewError(`明细绑定单元格必须是 detail token '${code}'。`, location);
  }
}

function ensureSheetExists(
  snapshot: ValidatedSnapshot,
  sheetId: string,
  location: ErrorLocation,
): void {
  getSheet(snapshot, sheetId, location);
}

function getSheet(
  snapshot: ValidatedSnapshot,
  sheetId: string,
  location: ErrorLocation,
): JsonObject {
  const sheets = snapshot.sheets;
  if (
    !isJsonObject(sheets) ||
    !Object.prototype.hasOwnProperty.call(sheets, sheetId) ||
    !isJsonObject(sheets[sheetId])
  ) {
    throw new TemplatePreviewError('绑定指向的工作表不存在。', location);
  }
  return sheets[sheetId];
}

function compareParameterCells(left: ParameterCellGroup, right: ParameterCellGroup): number {
  return compareOrdinal(left.sheetId, right.sheetId) ||
    left.row - right.row ||
    left.column - right.column;
}

function compareOrdinal(left: string, right: string): number {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
}

function ensureCoordinate(
  value: unknown,
  message: string,
  location: ErrorLocation,
): asserts value is number {
  if (!isCoordinate(value)) {
    throw new TemplatePreviewError(message, location);
  }
}

function isCoordinate(value: unknown): value is number {
  return typeof value === 'number' &&
    Number.isSafeInteger(value) &&
    value >= 0 &&
    value <= MAX_TEMPLATE_COORDINATE;
}

function runtimeLocation(
  value: JsonObject,
  defaultColumn?: number,
  defaultSheetId?: string,
  defaultRow?: number,
): ErrorLocation {
  return {
    sheetId: typeof value.sheetId === 'string' ? value.sheetId : defaultSheetId,
    row: typeof value.row === 'number'
      ? value.row
      : typeof value.templateRow === 'number'
        ? value.templateRow
        : defaultRow,
    column: typeof value.column === 'number' ? value.column : defaultColumn,
  };
}

function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isPlainObject(value: unknown): value is JsonObject {
  if (!isJsonObject(value)) {
    return false;
  }
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

function trimAsciiWhitespace(value: string): string {
  return value.replace(/^[ \t\r\n\f\v]+|[ \t\r\n\f\v]+$/g, '');
}

function toCellAddress(row: number, column: number): string {
  let columnNumber = column + 1;
  let letters = '';
  while (columnNumber > 0) {
    letters = String.fromCharCode(65 + ((columnNumber - 1) % 26)) + letters;
    columnNumber = Math.floor((columnNumber - 1) / 26);
  }
  return `${letters}${row + 1}`;
}
