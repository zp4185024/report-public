import type { ExecuteResult, PreviewMode } from './types';

/** 预览工具栏只消费模式和查询结果，避免 React 组件自行重复判断 Full/Paged 语义。 */
export interface PreviewToolbarPresentation {
  labels: string[];
  showPager: boolean;
}

/** Full 展示完整行数且不分页；只有报表明确配置 Paged 才展示页码和翻页入口。 */
export function createPreviewToolbarPresentation(
  mode: PreviewMode,
  result: ExecuteResult | null,
): PreviewToolbarPresentation {
  const rowCount = result?.rows.length ?? 0;
  return mode === 'Paged'
    ? {
        labels: [`第 ${result?.page ?? 1} 页`, `${rowCount} 行`],
        showPager: true,
      }
    : {
        labels: [`共 ${rowCount} 行`],
        showPager: false,
      };
}
