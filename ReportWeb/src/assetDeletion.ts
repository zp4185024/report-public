/**
 * 删除交互的纯函数集合：构造确认文案和挑选删除后继续编辑的替代资源。
 * 与组件解耦便于单元测试，页面只负责状态和网络请求。
 */

/** 可删除的受管资源最小形状：显示名称 + 唯一编码。 */
export interface DeletableResource {
  code: string;
  name: string;
}

/**
 * 构造删除确认弹窗的提示文案，带上资源类型、名称和编码并声明操作不可恢复。
 * @param label - 资源类型的中文名称，如「数据源」「数据集」。
 * @param resource - 待删除资源的名称和编码。
 * @returns 供 window.confirm 展示的完整提示文案。
 */
export function confirmDeleteMessage(label: string, resource: DeletableResource): string {
  return `确认删除${label}「${resource.name}」（${resource.code}）？删除后不可恢复。`;
}

/**
 * 从既有列表里挑选删除后继续编辑的替代资源：优先取被删项的后一个，否则回退前一个。
 * @param items - 删除前列表页持有的资源集合，顺序保持展示顺序。
 * @param deletedCode - 刚刚删除成功的资源编码。
 * @returns 建议选中的替代资源；列表被删空时返回 null，由调用方进入新增草稿。
 */
export function pickResourceAfterDeletion<T extends DeletableResource>(
  items: readonly T[],
  deletedCode: string,
): T | null {
  const index = items.findIndex((item) => item.code === deletedCode);
  if (index < 0) {
    return items[0] ?? null;
  }
  return items[index + 1] ?? items[index - 1] ?? null;
}

/**
 * 把后端删除接口抛出的错误转换成展示给管理员的文案；冲突信息由后端拼好引用清单。
 * @param exception - 删除请求抛出的异常或未知值。
 * @param label - 资源类型的中文名称，用于兜底文案。
 * @returns 可直接放进错误提示的消息文本。
 */
export function deleteErrorMessage(exception: unknown, label: string): string {
  return exception instanceof Error ? exception.message : `${label}删除失败。`;
}
