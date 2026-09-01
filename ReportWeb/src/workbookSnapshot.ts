import type { IWorkbookData } from '@univerjs/presets';
import { MAX_TEMPLATE_COORDINATE } from './templateBinding';

/**
 * Univer 0.25.1 的部分 facade 方法在运行时会返回数字字符串，和 TypeScript 声明不一致。
 * 所有快照进入或离开 Univer 边界时都在这里归一化，避免布局损坏或后端拒绝本应合法的模板。
 */
const mergeCoordinateFields = [
  'startRow',
  'endRow',
  'startColumn',
  'endColumn',
] as const;

// Univer 会把设计态占位符参与计算后的 Excel 错误写进公式缓存；模板保存时只保留公式正文，
// 错误缓存重置为数值 0，预览阶段填入真实数据后再显式计算。
const excelFormulaErrors = new Set([
  '#NULL!',
  '#DIV/0!',
  '#VALUE!',
  '#REF!',
  '#NAME?',
  '#NUM!',
  '#N/A',
  '#GETTING_DATA',
  '#SPILL!',
  '#CALC!',
  '#FIELD!',
  '#BLOCKED!',
  '#UNKNOWN!',
  '#CONNECT!',
  '#BUSY!',
  '#PYTHON!',
]);

const worksheetNumberRules = {
  hidden: 'booleanNumber',
  rowCount: 'positiveInteger',
  columnCount: 'positiveInteger',
  zoomRatio: 'positiveNumber',
  scrollTop: 'nonNegativeNumber',
  scrollLeft: 'nonNegativeNumber',
  defaultColumnWidth: 'nonNegativeNumber',
  defaultRowHeight: 'nonNegativeNumber',
  showGridlines: 'booleanNumber',
  rightToLeft: 'booleanNumber',
} as const;

const freezeNumberRules = {
  xSplit: 'coordinate',
  ySplit: 'coordinate',
  startRow: 'freezeStart',
  startColumn: 'freezeStart',
} as const;

const rowNumberRules = {
  h: 'nonNegativeNumber',
  ah: 'nonNegativeNumber',
  ia: 'booleanNumber',
  hd: 'booleanNumber',
} as const;

const columnNumberRules = {
  w: 'nonNegativeNumber',
  hd: 'booleanNumber',
} as const;

const rowHeaderNumberRules = {
  width: 'nonNegativeNumber',
  hidden: 'booleanNumber',
} as const;

const columnHeaderNumberRules = {
  height: 'nonNegativeNumber',
  hidden: 'booleanNumber',
} as const;

type MergeCoordinateField = typeof mergeCoordinateFields[number];
type JsonObject = Record<string, unknown>;
type NumberRule =
  | 'booleanNumber'
  | 'coordinate'
  | 'freezeStart'
  | 'nonNegativeInteger'
  | 'positiveInteger'
  | 'nonNegativeNumber'
  | 'positiveNumber';
type NumberRules = Readonly<Record<string, NumberRule>>;

/**
 * 将 workbookSnapshot 中的 normalize Workbook Snapshot 输入转换为调用方需要的稳定格式。
 * @param snapshot - 包含工作簿结构、单元格和样式信息的快照数据。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function normalizeWorkbookSnapshot(snapshot: IWorkbookData): IWorkbookData {
  const source = snapshot as unknown as JsonObject;
  const rev = source.rev === undefined
    ? undefined
    : normalizeNumber(source.rev, '工作簿修订号 rev', 'nonNegativeInteger');
  const styles = source.styles === undefined
    ? undefined
    : normalizeTextOverflowStyles(source.styles);

  // 仅复制包含布局数值的对象；cellData 等业务数据保持原引用，宿主随后 structuredClone 时再完整隔离。
  const sheets = Object.fromEntries(
    Object.entries(snapshot.sheets).map(([sheetId, sheet]) => [
      sheetId,
      normalizeWorksheet(sheet as unknown as JsonObject, sheetId),
    ]),
  );

  return {
    ...snapshot,
    ...(rev === undefined ? {} : { rev }),
    ...(styles === undefined ? {} : { styles }),
    sheets,
  } as IWorkbookData;
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

/**
 * 将未指定的文本溢出策略统一为允许向空白相邻单元格延伸，兼容历史模板的显示语义。
 * @param value - 工作簿全局样式表，样式值可能来自数据库或 Univer 运行时快照。
 * @returns 返回复制后的样式表；显式裁剪或换行策略保持不变。
 */
function normalizeTextOverflowStyles(value: unknown): unknown {
  if (!isJsonObject(value)) {
    return value;
  }

  return Object.fromEntries(
    Object.entries(value).map(([styleId, style]) => {
      if (!isJsonObject(style)) {
        return [styleId, style];
      }

      const wrapStrategy = style.tb === undefined || style.tb === null
        ? 0
        : typeof style.tb === 'string' && style.tb.trim() !== ''
          ? Number(style.tb)
          : style.tb;
      return wrapStrategy === 0
        ? [styleId, { ...style, tb: 1 }]
        : [styleId, style];
    }),
  );
}

/**
 * 将 workbookSnapshot 中的 normalize Worksheet 输入转换为调用方需要的稳定格式。
 * @param sheet - 当前读取或修改的工作表对象。
 * @param sheetId - 需要定位的业务记录标识。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizeWorksheet(sheet: JsonObject, sheetId: string): JsonObject {
  const normalized = normalizeNumericFields(
    sheet,
    `工作表 '${sheetId}'`,
    worksheetNumberRules,
  );

  if (sheet.freeze !== undefined) {
    // 第一阶段：更新 normalizeWorksheet 当前分支对应的业务状态，并保持关联数据一致。
    normalized.freeze = normalizeObjectNumericFields(
      sheet.freeze,
      `工作表 '${sheetId}' 的冻结设置`,
      freezeNumberRules,
    );
  }
  if (sheet.rowData !== undefined) {
    normalized.rowData = normalizeIndexedNumericFields(
      sheet.rowData,
      `工作表 '${sheetId}' 的行数据`,
      rowNumberRules,
    );
  }
  if (sheet.columnData !== undefined) {
    normalized.columnData = normalizeIndexedNumericFields(
      sheet.columnData,
      `工作表 '${sheetId}' 的列数据`,
      columnNumberRules,
    );
  }
  if (sheet.rowHeader !== undefined) {
    normalized.rowHeader = normalizeObjectNumericFields(
      sheet.rowHeader,
      `工作表 '${sheetId}' 的行表头`,
      rowHeaderNumberRules,
    );
  }
  if (sheet.columnHeader !== undefined) {
    // 第2阶段：更新 normalizeWorksheet 当前分支对应的业务状态，并保持关联数据一致。
    normalized.columnHeader = normalizeObjectNumericFields(
      sheet.columnHeader,
      `工作表 '${sheetId}' 的列表头`,
      columnHeaderNumberRules,
    );
  }
  if (sheet.cellData !== undefined) {
    normalized.cellData = normalizeFormulaCaches(sheet.cellData);
  }
  if (Array.isArray(sheet.mergeData)) {
    normalized.mergeData = sheet.mergeData.map((range, index) => {
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
  }

  return normalized;
}

/**
 * 将 workbookSnapshot 中的 normalize Formula Caches 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizeFormulaCaches(value: unknown): unknown {
  if (!isJsonObject(value)) {
    return value;
  }

  return Object.fromEntries(
    Object.entries(value).map(([row, rowData]) => {
      if (!isJsonObject(rowData)) {
        return [row, rowData];
      }

      return [row, Object.fromEntries(
        Object.entries(rowData).map(([column, cell]) => {
          if (
            isJsonObject(cell) &&
            typeof cell.f === 'string' &&
            typeof cell.v === 'string' &&
            excelFormulaErrors.has(cell.v.toUpperCase())
          ) {
            return [column, { ...cell, v: 0, t: 2 }];
          }
          return [column, cell];
        }),
      )];
    }),
  );
}

/**
 * 将 workbookSnapshot 中的 normalize Object Numeric Fields 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @param label - label 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param rules - rules 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizeObjectNumericFields(
  value: unknown,
  label: string,
  rules: NumberRules,
): JsonObject {
  if (!isJsonObject(value)) {
    throw new Error(`${label}必须是对象。`);
  }
  return normalizeNumericFields(value, label, rules);
}

/**
 * 将 workbookSnapshot 中的 normalize Indexed Numeric Fields 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @param label - label 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param rules - rules 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizeIndexedNumericFields(
  value: unknown,
  label: string,
  rules: NumberRules,
): JsonObject {
  if (!isJsonObject(value)) {
    throw new Error(`${label}必须是对象。`);
  }

  return Object.fromEntries(
    Object.entries(value).map(([index, item]) => [
      index,
      normalizeObjectNumericFields(item, `${label}[${index}]`, rules),
    ]),
  );
}

/**
 * 将 workbookSnapshot 中的 normalize Numeric Fields 输入转换为调用方需要的稳定格式。
 * @param source - 本次转换或写入使用的数据对象。
 * @param label - label 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param rules - rules 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizeNumericFields(
  source: JsonObject,
  label: string,
  rules: NumberRules,
): JsonObject {
  const normalized = { ...source };
  for (const [field, rule] of Object.entries(rules)) {
    if (source[field] !== undefined) {
      normalized[field] = normalizeNumber(source[field], `${label}.${field}`, rule);
    }
  }
  return normalized;
}

/**
 * 将 workbookSnapshot 中的 normalize Number 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @param label - label 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param rule - rule 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizeNumber(value: unknown, label: string, rule: NumberRule): number {
  const normalized = normalizeUniverRuntimeNumber(value, label);

  if (rule === 'booleanNumber' && normalized !== 0 && normalized !== 1) {
    throw new Error(`${label}必须是 0 或 1。`);
  }
  if (
    (rule === 'coordinate' || rule === 'nonNegativeInteger') &&
    (!Number.isSafeInteger(normalized) || normalized < 0 || normalized > MAX_TEMPLATE_COORDINATE)
  ) {
    throw new Error(`${label}必须是有效的非负整数。`);
  }
  if (
    rule === 'freezeStart' &&
    (!Number.isSafeInteger(normalized) || normalized < -1 || normalized > MAX_TEMPLATE_COORDINATE)
  ) {
    throw new Error(`${label}必须是 -1 或有效的非负整数。`);
  }
  if (rule === 'positiveInteger' && (!Number.isSafeInteger(normalized) || normalized < 1)) {
    throw new Error(`${label}必须是正整数。`);
  }
  if (rule === 'nonNegativeNumber' && normalized < 0) {
    throw new Error(`${label}不能小于 0。`);
  }
  if (rule === 'positiveNumber' && normalized <= 0) {
    throw new Error(`${label}必须大于 0。`);
  }

  return normalized;
}

/**
 * 将 workbookSnapshot 中的 normalize Coordinate 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @param sheetId - 需要定位的业务记录标识。
 * @param mergeIndex - 控制数据范围或位置的数值。
 * @param field - 参与映射、显示或数据写入的字段集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 校验 workbookSnapshot 的 is Merge Coordinate Field 条件，并返回可供后续流程判断的结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function isMergeCoordinateField(value: string): value is MergeCoordinateField {
  return (mergeCoordinateFields as readonly string[]).includes(value);
}

/**
 * 校验 workbookSnapshot 的 is Json Object 条件，并返回可供后续流程判断的结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
