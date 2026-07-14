import type { IWorkbookData } from '@univerjs/presets';
import type {
  DetailBinding,
  DetailFieldBinding,
  ParameterBinding,
  ReportDefinition,
  TemplateBinding,
  WorkloadRow,
} from './types';

const tokenRegex = /\{\{\s*(params|detail)\.([A-Za-z][A-Za-z0-9]*)\s*\}\}/g;
const maximumCoordinate = 2_147_483_647;

interface ErrorLocation {
  sheetId?: string;
  row?: number;
  column?: number;
}

interface DetailLocation {
  sheetId: string;
  row: number;
}

type JsonObject = Record<string, unknown>;

export class TemplateBindingError extends Error {
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
      sheetId ? `工作表 '${sheetId}'` : undefined,
      cellAddress ? `单元格 ${cellAddress}` : undefined,
    ].filter(Boolean).join('，');

    super(context ? `${message}（${context}）` : message);
    this.name = 'TemplateBindingError';
    this.sheetId = sheetId;
    this.row = row;
    this.column = column;
    this.cellAddress = cellAddress;
  }
}

export function scanTemplateBindings(
  snapshot: IWorkbookData,
  definition: ReportDefinition,
): TemplateBinding {
  const parameterCodes = new Set(definition.parameters.map((parameter) => parameter.code));
  const detailCodes = new Set(
    definition.columns
      .filter((column) => column.dataType !== 'formula')
      .map((column) => column.code),
  );
  const parameterBindings: ParameterBinding[] = [];
  const detailFields: DetailFieldBinding[] = [];
  let detailLocation: DetailLocation | null = null;

  if (!isJsonObject(snapshot)) {
    throw new TemplateBindingError('模板快照必须是对象。');
  }

  const sheets = snapshot.sheets;
  if (sheets === undefined || sheets === null) {
    return emptyBinding();
  }
  if (!isJsonObject(sheets)) {
    throw new TemplateBindingError('工作表集合 sheets 必须是对象。');
  }

  for (const [sheetId, sheetValue] of Object.entries(sheets)) {
    if (!isJsonObject(sheetValue)) {
      throw new TemplateBindingError('工作表节点必须是对象。', { sheetId });
    }

    const cellData = sheetValue.cellData;
    if (cellData === undefined || cellData === null) {
      continue;
    }
    if (!isJsonObject(cellData)) {
      throw new TemplateBindingError('cellData 必须是对象。', { sheetId });
    }

    for (const [rowKey, rowValue] of Object.entries(cellData)) {
      const row = parseCoordinateKey(rowKey, '行', { sheetId });
      if (!isJsonObject(rowValue)) {
        throw new TemplateBindingError('行节点必须是对象。', { sheetId, row });
      }

      for (const [columnKey, cellValue] of Object.entries(rowValue)) {
        const column = parseCoordinateKey(columnKey, '列', { sheetId, row });
        const location = { sheetId, row, column };
        if (!isJsonObject(cellValue)) {
          throw new TemplateBindingError('单元格节点必须是对象。', location);
        }

        if (typeof cellValue.v !== 'string') {
          continue;
        }

        const value = cellValue.v;
        const matches = Array.from(value.matchAll(tokenRegex));
        for (const match of matches) {
          const matchIndex = match.index;
          if (matchIndex === undefined) {
            throw new TemplateBindingError('无法定位模板 token。', location);
          }
          const matchEnd = matchIndex + match[0].length;
          if (value[matchIndex - 1] === '{' || value[matchEnd] === '}') {
            throw new TemplateBindingError('模板 token 格式错误。', location);
          }

          const scope = match[1];
          const code = match[2];
          if (scope === 'params') {
            ensureKnownCode(parameterCodes, scope, code, location);
            parameterBindings.push({ code, sheetId, row, column });
            continue;
          }

          if (value.trim() !== match[0]) {
            throw new TemplateBindingError('明细 token 必须独占整个单元格。', location);
          }
          ensureKnownCode(detailCodes, scope, code, location);
          if (
            detailLocation !== null &&
            (detailLocation.sheetId !== sheetId || detailLocation.row !== row)
          ) {
            throw new TemplateBindingError('所有明细 token 必须位于同一个工作表行。', location);
          }

          detailLocation ??= { sheetId, row };
          detailFields.push({ code: code as keyof WorkloadRow, column });
        }

        const remainingValue = value.replace(tokenRegex, '');
        if (remainingValue.includes('{{') || remainingValue.includes('}}')) {
          throw new TemplateBindingError('模板 token 格式错误。', location);
        }
      }
    }
  }

  parameterBindings.sort(
    (left, right) =>
      compareOrdinal(left.sheetId, right.sheetId) ||
      left.row - right.row ||
      left.column - right.column ||
      compareOrdinal(left.code, right.code),
  );

  let detailBinding: DetailBinding | null = null;
  if (detailLocation !== null) {
    ensureDetailRowIsNotMerged(sheets, detailLocation, detailFields[0]?.column ?? 0);
    detailFields.sort(
      (left, right) => left.column - right.column || compareOrdinal(left.code, right.code),
    );
    detailBinding = {
      datasetCode: 'detail',
      sheetId: detailLocation.sheetId,
      templateRow: detailLocation.row,
      fields: detailFields,
    };
  }

  return {
    version: 1,
    parameterBindings,
    detailBinding,
  };
}

function emptyBinding(): TemplateBinding {
  return {
    version: 1,
    parameterBindings: [],
    detailBinding: null,
  };
}

function compareOrdinal(left: string, right: string): number {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
}

function ensureKnownCode(
  knownCodes: Set<string>,
  scope: string,
  code: string,
  location: ErrorLocation,
): void {
  if (!knownCodes.has(code)) {
    throw new TemplateBindingError(`未知的 ${scope} code '${code}'。`, location);
  }
}

function ensureDetailRowIsNotMerged(
  sheets: JsonObject,
  detailLocation: DetailLocation,
  detailColumn: number,
): void {
  const sheet = sheets[detailLocation.sheetId];
  if (!isJsonObject(sheet)) {
    throw new TemplateBindingError('工作表节点必须是对象。', detailLocation);
  }

  const mergeData = sheet.mergeData;
  if (mergeData === undefined || mergeData === null) {
    return;
  }

  const errorLocation = {
    ...detailLocation,
    column: detailColumn,
  };
  if (!Array.isArray(mergeData)) {
    throw new TemplateBindingError('mergeData 必须是数组。', errorLocation);
  }

  for (const merge of mergeData) {
    if (!isJsonObject(merge)) {
      throw new TemplateBindingError('mergeData 范围必须是对象。', errorLocation);
    }

    const startRow = parseMergeCoordinate(merge.startRow, errorLocation);
    const endRow = parseMergeCoordinate(merge.endRow, errorLocation);
    if (endRow < startRow) {
      throw new TemplateBindingError('mergeData 行范围无效。', errorLocation);
    }
    if (startRow <= detailLocation.row && detailLocation.row <= endRow) {
      throw new TemplateBindingError('合并单元格不能触碰明细模板行。', errorLocation);
    }
  }
}

function parseMergeCoordinate(value: unknown, location: ErrorLocation): number {
  if (!isCoordinate(value)) {
    throw new TemplateBindingError('mergeData 必须包含有效的行坐标。', location);
  }
  return value;
}

function parseCoordinateKey(
  key: string,
  coordinateName: string,
  location: ErrorLocation,
): number {
  if (!/^\d+$/.test(key)) {
    throw new TemplateBindingError(`${coordinateName}坐标键 '${key}' 无效。`, location);
  }

  const coordinate = Number(key);
  if (!isCoordinate(coordinate)) {
    throw new TemplateBindingError(`${coordinateName}坐标键 '${key}' 无效。`, location);
  }
  return coordinate;
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
