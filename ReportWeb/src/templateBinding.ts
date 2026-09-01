import type { IWorkbookData } from '@univerjs/presets';
import type {
  DetailBinding,
  DetailFieldBinding,
  ParameterBinding,
  ReportDefinition,
  TemplateBinding,
} from './types';

/**
 * 浏览器端模板绑定扫描器。
 * 作用是让设计人员在预览/保存前立即看到精确到单元格的错误；它不是安全边界，后端保存时会独立复扫。
 */
// 字段编码与后端 ValidateCode 保持一致，允许首字母后的下划线，例如 VISIT_ID。
const tokenRegex = /\{\{[ \t\r\n\f\v]*(params|detail)\.([A-Za-z][A-Za-z0-9_]*)[ \t\r\n\f\v]*\}\}/g;
/** 与后端 Int32 坐标范围保持一致，避免浏览器接受后台无法保存的坐标。 */
export const MAX_TEMPLATE_COORDINATE = 2_147_483_647;

interface ErrorLocation {
  sheetId?: string;
  row?: number;
  column?: number;
}

interface DetailLocation {
  sheetId: string;
  sheetName: string;
  row: number;
}

type JsonObject = Record<string, unknown>;

export class TemplateBindingError extends Error {
  sheetId?: string;
  row?: number;
  column?: number;
  cellAddress?: string;

  /**
   * 初始化 templateBinding 实例，保存依赖并建立组件所需的初始状态。
   * @param message - message 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @param location - location 对应的业务输入；具体类型和空值范围以方法签名为准。
   */
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

/**
 * 扫描 Univer 稀疏 cellData，返回稳定排序的规范绑定。
 * 参数 token 可以嵌入文字且同格多个；明细 token 必须独占单元格并集中在同一模板行。
 */
export function scanTemplateBindings(
  snapshot: IWorkbookData,
  definition: ReportDefinition,
): TemplateBinding {
  const parameterCodes = new Set(definition.parameters.map((parameter) => parameter.code));
  const detailCodes = new Set(definition.fields.map((field) => field.code));
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

  // cellData 只保存非空单元格，按对象实际键遍历比按 rowCount/columnCount 扫描整个网格更快。
  for (const [sheetId, sheetValue] of Object.entries(sheets)) {
    if (!isJsonObject(sheetValue)) {
      throw new TemplateBindingError('工作表节点必须是对象。', { sheetId });
    }
    const sheetName = typeof sheetValue.name === 'string' && sheetValue.name.trim().length > 0
      ? sheetValue.name.trim()
      : sheetId;

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
          // 防止正则把三重花括号中间部分误认为合法 token。
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

          // 明细写入必须保留数字类型，禁止把 detail token 与固定文字拼成字符串。
          if (trimAsciiWhitespace(value) !== match[0]) {
            throw new TemplateBindingError('明细 token 必须独占整个单元格。', location);
          }
          ensureKnownCode(detailCodes, scope, code, location);
          // 当前预览执行器通过复制一条模板行展开，所以所有明细字段必须共用工作表和行。
          if (
            detailLocation !== null &&
            (detailLocation.sheetId !== sheetId || detailLocation.row !== row)
          ) {
            throw new TemplateBindingError(
              `已有明细区域：工作表 ${formatSheetLabel(detailLocation.sheetName, detailLocation.sheetId)}` +
              `第 ${detailLocation.row + 1} 行。` +
              `当前冲突工作表：${formatSheetLabel(sheetName, sheetId)}。` +
              '所有明细 token 必须位于同一个工作表行；如需重新建立明细区域，' +
              '请先删除旧区域中的明细 token。',
              location,
            );
          }

          detailLocation ??= { sheetId, sheetName, row };
          detailFields.push({ code, column });
        }

        // 去掉合法 token 后仍有花括号，说明存在拼写错误或未支持的 token 形式。
        const remainingValue = value.replace(tokenRegex, '');
        if (remainingValue.includes('{{') || remainingValue.includes('}}')) {
          throw new TemplateBindingError('模板 token 格式错误。', location);
        }
      }
    }
  }

  // 确定性排序使前端与后端生成的 Binding JSON 可以稳定比较，不受对象插入顺序影响。
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

/**
 * 创建并返回 templateBinding 的 empty Binding 配置对象，集中提供相关状态和操作入口。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function emptyBinding(): TemplateBinding {
  return {
    version: 1,
    parameterBindings: [],
    detailBinding: null,
  };
}

/**
 * 按 compare Ordinal 条件比较、筛选或整理 templateBinding 数据，并返回顺序稳定的结果。
 * @param left - left 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param right - right 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function compareOrdinal(left: string, right: string): number {
  if (left === right) {
    return 0;
  }
  return left < right ? -1 : 1;
}

/**
 * 将 templateBinding 的 trim Ascii Whitespace 输入转换为稳定格式，保留后续计算或展示需要的信息。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function trimAsciiWhitespace(value: string): string {
  return value.replace(/^[ \t\r\n\f\v]+|[ \t\r\n\f\v]+$/g, '');
}

/** 错误提示优先使用可见页签名称，同时保留稳定 sheetId 供日志和快照定位。 */
function formatSheetLabel(sheetName: string, sheetId: string): string {
  return sheetName === sheetId
    ? `'${sheetId}'`
    : `'${sheetName}'（ID: ${sheetId}）`;
}

/**
 * 校验 templateBinding 的 ensure Known Code 条件，并返回可供后续流程判断的结果。
 * @param knownCodes - knownCodes 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param scope - scope 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param code - 需要读取、校验或转换的业务值。
 * @param location - location 对应的业务输入；具体类型和空值范围以方法签名为准。
 */
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

/**
 * 校验 templateBinding 的 ensure Detail Row Is Not Merged 条件，并返回可供后续流程判断的结果。
 * @param sheets - sheets 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param detailLocation - detailLocation 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param detailColumn - 参与映射、显示或数据写入的字段集合。
 */
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

  // 复制/插入明细行时，任何触碰模板行的合并区域都可能改变坐标或让 Univer 拒绝操作。
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

/**
 * 将 templateBinding 中的 parse Merge Coordinate 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @param location - location 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function parseMergeCoordinate(value: unknown, location: ErrorLocation): number {
  if (!isCoordinate(value)) {
    throw new TemplateBindingError('mergeData 必须包含有效的行坐标。', location);
  }
  return value;
}

/**
 * 将 templateBinding 中的 parse Coordinate Key 输入转换为调用方需要的稳定格式。
 * @param key - 需要读取、校验或转换的业务值。
 * @param coordinateName - 需要读取、校验或转换的业务值。
 * @param location - location 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 校验 templateBinding 的 is Coordinate 条件，并返回可供后续流程判断的结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function isCoordinate(value: unknown): value is number {
  return typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= MAX_TEMPLATE_COORDINATE;
}

/**
 * 校验 templateBinding 的 is Json Object 条件，并返回可供后续流程判断的结果。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * 将 templateBinding 的 to Cell Address 输入转换为稳定格式，保留后续计算或展示需要的信息。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @param column - 参与映射、显示或数据写入的字段集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function toCellAddress(row: number, column: number): string {
  // 内部保持零基坐标，错误消息转成设计人员熟悉的 A1 地址。
  let columnNumber = column + 1;
  let letters = '';
  while (columnNumber > 0) {
    letters = String.fromCharCode(65 + ((columnNumber - 1) % 26)) + letters;
    columnNumber = Math.floor((columnNumber - 1) / 26);
  }
  return `${letters}${row + 1}`;
}
