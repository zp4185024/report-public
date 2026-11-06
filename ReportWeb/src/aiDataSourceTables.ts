import type { AiDataSourceTable } from './types';

/** 数据源表清单滚动区一次渲染的最大条目数；超出部分提示用过滤缩小范围，避免 431 个对象卡顿。 */
export const AiTableListRenderLimit = 200;

/**
 * 按关键词过滤数据源表清单：对名称与注释做大小写不敏感的包含匹配，空关键词返回原清单。
 * @param tables - 后端返回的表/视图清单。
 * @param query - 用户输入的过滤关键词；两侧去空格。
 * @returns 匹配的条目（保持后端顺序：基表在前、视图在后）。
 */
export function filterAiDataSourceTables(
  tables: AiDataSourceTable[],
  query: string,
): AiDataSourceTable[] {
  const keyword = query.trim().toLowerCase();
  if (keyword.length === 0) {
    return tables;
  }
  return tables.filter((table) =>
    table.name.toLowerCase().includes(keyword) ||
    (table.comment ?? '').toLowerCase().includes(keyword));
}

/**
 * 计算滚动区实际渲染的条目：超过渲染上限时只取前 N 条，并统计被隐藏的数量供提示语使用。
 * @param filtered - 已过滤的清单。
 * @param limit - 渲染上限，缺省 AiTableListRenderLimit。
 * @returns 参与渲染的条目与被隐藏的数量。
 */
export function sliceAiTableRenderList(
  filtered: AiDataSourceTable[],
  limit: number = AiTableListRenderLimit,
): { items: AiDataSourceTable[]; hiddenCount: number } {
  if (filtered.length <= limit) {
    return { items: filtered, hiddenCount: 0 };
  }
  return { items: filtered.slice(0, limit), hiddenCount: filtered.length - limit };
}

/**
 * 表清单条目的展示副标题：注释存在时展示注释，否则回退类型标签，保证每行都有说明文字。
 * @param table - 表/视图条目。
 * @returns 副标题文本。
 */
export function aiDataSourceTableSubtitle(table: AiDataSourceTable): string {
  return table.comment || (table.kind === 'view' ? '视图' : '数据表');
}
