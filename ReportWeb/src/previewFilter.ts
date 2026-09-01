import type { IRange } from '@univerjs/presets';
import type { TemplatePreviewPlan } from './templatePreview';

export interface PreviewFilterTarget {
  sheetId: string;
  range: IRange;
}

/**
 * 明细 token 所在行是第一条数据，紧邻的上一行才是模板表头。
 * 筛选范围由预览计划推导，避免被批量写入后遗留的活动数据选区带偏一行。
 */
export function createPreviewFilterTarget(
  plan: TemplatePreviewPlan,
): PreviewFilterTarget | null {
  const detail = plan.detail;
  if (
    detail === null
    || detail.templateRow < 1
    || detail.rows.length === 0
    || detail.rows[0].values.length === 0
  ) {
    return null;
  }

  const columns = detail.rows[0].values.map(({ column }) => column);
  return {
    sheetId: detail.sheetId,
    range: {
      startRow: detail.templateRow - 1,
      endRow: detail.templateRow + detail.rows.length - 1,
      startColumn: Math.min(...columns),
      endColumn: Math.max(...columns),
    },
  };
}

/**
 * 校验 previewFilter 的 is Same Filter Range 条件，并返回可供后续流程判断的结果。
 * @param left - left 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param right - right 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function isSameFilterRange(left: IRange, right: IRange): boolean {
  return left.startRow === right.startRow
    && left.endRow === right.endRow
    && left.startColumn === right.startColumn
    && left.endColumn === right.endColumn;
}

/**
 * 预览完成后宿主会把活动单元格收回到推导出的表头左上角。
 * 只有这个默认锚点才允许用完整明细范围修正原生智能筛选结果；用户主动选择的范围必须保留。
 */
export function isPreviewFilterAnchor(
  selection: IRange | null,
  expectedRange: IRange,
): boolean {
  return selection !== null
    && selection.startRow === selection.endRow
    && selection.startColumn === selection.endColumn
    && selection.startRow === expectedRange.startRow
    && selection.startColumn === expectedRange.startColumn;
}

/**
 * 负责 previewFilter 的 should Repair Preview Filter Range 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param selection - 需要读取、修改或校验的单元格范围。
 * @param actualRange - 需要读取、修改或校验的单元格范围。
 * @param expectedRange - 需要读取、修改或校验的单元格范围。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function shouldRepairPreviewFilterRange(
  selection: IRange | null,
  actualRange: IRange,
  expectedRange: IRange,
): boolean {
  return isPreviewFilterAnchor(selection, expectedRange)
    && !isSameFilterRange(actualRange, expectedRange);
}

/**
 * 将 previewFilter 的 column Name 输入转换为稳定格式，保留后续计算或展示需要的信息。
 * @param column - 参与映射、显示或数据写入的字段集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function columnName(column: number): string {
  let remaining = column + 1;
  let result = '';
  while (remaining > 0) {
    const index = (remaining - 1) % 26;
    result = String.fromCharCode(65 + index) + result;
    remaining = Math.floor((remaining - 1) / 26);
  }
  return result;
}

/** 把 Univer 的零基范围转成便于用户核对的 Excel A1 表示法。 */
export function formatFilterRange(range: IRange): string {
  const start = `${columnName(range.startColumn)}${range.startRow + 1}`;
  const end = `${columnName(range.endColumn)}${range.endRow + 1}`;
  return start === end ? start : `${start}:${end}`;
}
