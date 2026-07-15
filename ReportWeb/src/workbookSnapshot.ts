import type { IWorkbookData } from '@univerjs/presets';
import { MAX_TEMPLATE_COORDINATE } from './templateBinding';

const mergeCoordinateFields = [
  'startRow',
  'endRow',
  'startColumn',
  'endColumn',
] as const;

type MergeCoordinateField = typeof mergeCoordinateFields[number];
type JsonObject = Record<string, unknown>;

export function normalizeWorkbookSnapshot(snapshot: IWorkbookData): IWorkbookData {
  const sheets = Object.fromEntries(
    Object.entries(snapshot.sheets).map(([sheetId, sheet]) => {
      if (!Array.isArray(sheet.mergeData)) {
        return [sheetId, sheet];
      }

      const mergeData = sheet.mergeData.map((range, index) => {
        if (!isJsonObject(range)) {
          throw new Error(`工作表 '${sheetId}' 的第 ${index + 1} 个合并区域无效。`);
        }

        return Object.fromEntries(
          Object.entries(range).map(([key, value]) => {
            if (!isMergeCoordinateField(key)) {
              return [key, value];
            }
            return [key, normalizeCoordinate(value, sheetId, index, key)];
          }),
        );
      });

      return [sheetId, { ...sheet, mergeData }];
    }),
  );

  return { ...snapshot, sheets };
}

export function normalizeUniverRuntimeNumber(value: unknown, label: string): number {
  const normalized = typeof value === 'string' && value.length > 0
    ? Number(value)
    : value;
  if (typeof normalized !== 'number' || !Number.isFinite(normalized)) {
    throw new Error(`${label}不是有效数值。`);
  }
  return normalized;
}

function normalizeCoordinate(
  value: unknown,
  sheetId: string,
  mergeIndex: number,
  field: MergeCoordinateField,
): number {
  const coordinate = typeof value === 'string' && /^\d+$/.test(value)
    ? Number(value)
    : value;

  if (
    typeof coordinate !== 'number' ||
    !Number.isInteger(coordinate) ||
    coordinate < 0 ||
    coordinate > MAX_TEMPLATE_COORDINATE
  ) {
    throw new Error(
      `工作表 '${sheetId}' 的第 ${mergeIndex + 1} 个合并区域坐标 '${field}' 无效。`,
    );
  }

  return coordinate;
}

function isMergeCoordinateField(value: string): value is MergeCoordinateField {
  return (mergeCoordinateFields as readonly string[]).includes(value);
}

function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
