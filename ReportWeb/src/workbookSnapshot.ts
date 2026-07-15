import type { IWorkbookData } from '@univerjs/presets';
import { MAX_TEMPLATE_COORDINATE } from './templateBinding';

/**
 * Univer 0.25.1 的部分 facade 方法在运行时会返回数字字符串，和 TypeScript 声明不一致。
 * 所有快照离开 Univer 边界前在这里归一化，避免前端扫描器和 .NET 后端拒绝本应合法的模板。
 */
const mergeCoordinateFields = [
  'startRow',
  'endRow',
  'startColumn',
  'endColumn',
] as const;

type MergeCoordinateField = typeof mergeCoordinateFields[number];
type JsonObject = Record<string, unknown>;

export function normalizeWorkbookSnapshot(snapshot: IWorkbookData): IWorkbookData {
  // 仅复制 sheets/含 mergeData 的工作表/合并范围，保留其它大块快照对象引用，避免一次无必要的深拷贝。
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

/** 把 facade 返回的普通数字或数字字符串统一为有限 number；错误信息由调用方提供业务位置。 */
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
  // 坐标只接受非负整数字符串；小数、符号和空字符串不能被 Number() 宽松转换后混入快照。
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
