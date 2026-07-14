import type { IWorkbookData } from '@univerjs/presets';
import { workloadRowFieldCodes } from './types';
import type {
  ExecuteResult,
  ResolvedParameters,
  TemplateBinding,
  WorkloadRowFieldCode,
} from './types';

const tokenRegex = /\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9]*)[ \t\r\n\f\v]*\}\}/g;
const maximumCoordinate = 2_147_483_647;
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
  return {
    parameterWrites: createParameterWrites(snapshot, binding, result),
    detail: createDetailPlan(snapshot, binding, result),
  };
}

function createParameterWrites(
  snapshot: IWorkbookData,
  binding: TemplateBinding,
  result: ExecuteResult,
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
      const value = originalValue.replace(
        tokenRegex,
        (token, scope: string, code: string) => {
          if (scope !== 'params' || !group.codes.has(code as keyof ResolvedParameters)) {
            return token;
          }
          return result.parameters[code as keyof ResolvedParameters];
        },
      );

      return {
        sheetId: group.sheetId,
        row: group.row,
        column: group.column,
        value,
      };
    });
}

function createDetailPlan(
  snapshot: IWorkbookData,
  binding: TemplateBinding,
  result: ExecuteResult,
): TemplatePreviewPlan['detail'] {
  const detailBinding: unknown = binding.detailBinding;
  if (detailBinding === null) {
    return null;
  }
  if (!isJsonObject(detailBinding)) {
    throw new TemplatePreviewError('明细绑定必须是对象。');
  }

  const sheetId = detailBinding.sheetId;
  const templateRow = detailBinding.templateRow;
  const baseLocation = {
    sheetId: typeof sheetId === 'string' ? sheetId : undefined,
    row: typeof templateRow === 'number' ? templateRow : undefined,
    column: 0,
  };
  if (detailBinding.datasetCode !== 'detail') {
    throw new TemplatePreviewError("明细绑定 datasetCode 必须为 'detail'。", baseLocation);
  }
  if (typeof sheetId !== 'string') {
    throw new TemplatePreviewError('明细绑定 sheetId 无效。', baseLocation);
  }
  ensureCoordinate(templateRow, '明细模板行坐标无效。', {
    sheetId,
    row: typeof templateRow === 'number' ? templateRow : undefined,
    column: 0,
  });
  ensureSheetExists(snapshot, sheetId, {
    sheetId,
    row: templateRow,
    column: 0,
  });

  if (!Array.isArray(detailBinding.fields)) {
    throw new TemplatePreviewError('明细字段绑定必须是数组。', {
      sheetId,
      row: templateRow,
      column: 0,
    });
  }

  const fields = detailBinding.fields.map((field): { code: WorkloadRowFieldCode; column: number } => {
    if (!isJsonObject(field)) {
      throw new TemplatePreviewError('明细字段绑定必须是对象。', {
        sheetId,
        row: templateRow,
        column: 0,
      });
    }

    const column = field.column;
    const location = {
      sheetId,
      row: templateRow,
      column: typeof column === 'number' ? column : undefined,
    };
    if (typeof field.code !== 'string' || !workloadRowFieldCodeSet.has(field.code)) {
      throw new TemplatePreviewError(`未知的 detail code '${String(field.code)}'。`, location);
    }
    ensureCoordinate(column, '明细字段列坐标无效。', location);

    return {
      code: field.code as WorkloadRowFieldCode,
      column,
    };
  }).sort(
    (left, right) => left.column - right.column || compareOrdinal(left.code, right.code),
  );

  const rows: DetailRowPlan[] = result.rows.length === 0
    ? [{
        row: templateRow,
        values: fields.map(({ column }) => ({ column, value: null })),
      }]
    : result.rows.map((workloadRow, index) => ({
        row: templateRow + index,
        values: fields.map(({ code, column }) => ({
          column,
          value: workloadRow[code],
        })),
      }));

  return {
    sheetId,
    templateRow,
    insertCount: Math.max(0, result.rows.length - 1),
    rows,
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
  snapshot: IWorkbookData,
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

function ensureSheetExists(
  snapshot: IWorkbookData,
  sheetId: string,
  location: ErrorLocation,
): void {
  getSheet(snapshot, sheetId, location);
}

function getSheet(
  snapshot: IWorkbookData,
  sheetId: string,
  location: ErrorLocation,
): JsonObject {
  const sheets: unknown = snapshot.sheets;
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
    Number.isInteger(value) &&
    value >= 0 &&
    value <= maximumCoordinate;
}

function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
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
