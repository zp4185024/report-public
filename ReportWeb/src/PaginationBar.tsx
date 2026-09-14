import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from 'lucide-react';

interface PaginationBarProps {
  total: number;
  page: number;
  pageSize: number;
  pageSizeOptions?: readonly number[];
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  itemLabel: string;
}

/**
 * 提供列表统一的页码、每页条数和总数操作，避免不同管理页重复实现分页边界。
 * @param props - 分页总数、当前页、页大小和变更回调。
 * @returns 返回可复用的分页工具栏。
 */
export function PaginationBar({
  total,
  page,
  pageSize,
  pageSizeOptions = [15, 30, 60],
  onPageChange,
  onPageSizeChange,
  itemLabel,
}: PaginationBarProps) {
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const currentPage = Math.min(Math.max(page, 1), pageCount);
  const hasItems = total > 0;
  const firstItem = hasItems ? (currentPage - 1) * pageSize + 1 : 0;
  const lastItem = hasItems ? Math.min(currentPage * pageSize, total) : 0;
  /** 将目标页限制在有效范围内，再通知列表页面发起分页查询。 */
  const changePage = (nextPage: number) => {
    onPageChange(Math.min(Math.max(nextPage, 1), pageCount));
  };

  return (
    <div className="pagination-bar" aria-label={`${itemLabel}分页`}>
      <span className="pagination-summary">
        {hasItems ? `${firstItem}-${lastItem} / ${total} 个${itemLabel}` : `共 0 个${itemLabel}`}
      </span>
      <label className="pagination-size">
        <span>每页</span>
        <select
          value={pageSize}
          onChange={(event) => onPageSizeChange(Number(event.target.value))}
          aria-label={`${itemLabel}每页条数`}
        >
          {pageSizeOptions.map((size) => <option key={size} value={size}>{size}</option>)}
        </select>
      </label>
      <span className="pagination-page">第 {currentPage} / {pageCount} 页</span>
      <div className="pagination-actions">
        <button
          type="button"
          className="icon-button"
          title="第一页"
          aria-label="第一页"
          disabled={!hasItems || currentPage === 1}
          onClick={() => changePage(1)}
        ><ChevronsLeft size={16} /></button>
        <button
          type="button"
          className="icon-button"
          title="上一页"
          aria-label="上一页"
          disabled={!hasItems || currentPage === 1}
          onClick={() => changePage(currentPage - 1)}
        ><ChevronLeft size={16} /></button>
        <button
          type="button"
          className="icon-button"
          title="下一页"
          aria-label="下一页"
          disabled={!hasItems || currentPage === pageCount}
          onClick={() => changePage(currentPage + 1)}
        ><ChevronRight size={16} /></button>
        <button
          type="button"
          className="icon-button"
          title="最后一页"
          aria-label="最后一页"
          disabled={!hasItems || currentPage === pageCount}
          onClick={() => changePage(pageCount)}
        ><ChevronsRight size={16} /></button>
      </div>
    </div>
  );
}
