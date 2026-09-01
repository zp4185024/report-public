import { useEffect } from 'react';
import { Copy, ExternalLink, Share2, X } from 'lucide-react';

export interface SharedParameterSummary {
  code: string;
  label: string;
  value: string;
}

interface ShareReportDialogProps {
  open: boolean;
  reportName: string;
  url: string;
  includeParameters: boolean;
  parameters: readonly SharedParameterSummary[];
  onIncludeParametersChange: (include: boolean) => void;
  onCopy: () => void;
  onClose: () => void;
}

/**
 * 初始化 ShareReportDialog 的 Share Report Dialog 监听流程，并在状态变化时同步执行相关操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function ShareReportDialog({
  open,
  reportName,
  url,
  includeParameters,
  parameters,
  onIncludeParametersChange,
  onCopy,
  onClose,
}: ShareReportDialogProps) {
  useEffect(() => {
    if (!open) return undefined;
    /**
     * 结束 ShareReportDialog 的 close On Escape 交互并清理临时状态。
     * @param event - 触发当前处理流程的界面或业务事件。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', closeOnEscape);
    // 第一阶段：整理 ShareReportDialog 已计算的数据，并按方法契约返回最终结果。
    return () => window.removeEventListener('keydown', closeOnEscape);
  }, [onClose, open]);

  // 第3阶段：检查 ShareReportDialog 当前条件，并按匹配分支执行对应处理。
  if (!open) return null;

  // 第2阶段：遍历 ShareReportDialog 当前数据集合，逐项转换并维护处理结果。
  return (
    <div className="share-dialog-backdrop" role="presentation">
      <section
        className="share-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="share-dialog-title"
      >
        <header className="share-dialog-header">
          <div>
            <h2 id="share-dialog-title"><Share2 size={18} />分享报表</h2>
            <p>{reportName}</p>
          </div>
          <button
            type="button"
            className="icon-button"
            title="关闭分享"
            aria-label="关闭分享"
            onClick={onClose}
          >
            <X size={18} />
          </button>
        </header>

        <div className="share-dialog-body">
          <label className="share-link-field">
            <span>分享链接</span>
            <input
              readOnly
              value={url}
              aria-label="分享链接"
              onFocus={(event) => event.currentTarget.select()}
            />
          </label>

          <label className="share-parameter-toggle">
            <input
              type="checkbox"
              checked={includeParameters}
              onChange={(event) => onIncludeParametersChange(event.target.checked)}
            />
            <span>携带当前查询参数</span>
          </label>

          <div className="share-parameter-summary" aria-label="分享参数">
            <div className="share-parameter-summary-title">
              <strong>参数</strong>
              <span>{includeParameters ? `${parameters.length} 个` : '不携带'}</span>
            </div>
            {includeParameters && parameters.length > 0 ? (
              <dl>
                {parameters.map((parameter) => (
                  <div key={parameter.code}>
                    <dt>{parameter.label}</dt>
                    <dd title={parameter.value}>{parameter.value}</dd>
                  </div>
                ))}
              </dl>
            ) : (
              <p>{includeParameters ? '当前没有已填写参数' : '打开链接后由查看者填写参数'}</p>
            )}
          </div>
        </div>

        <footer className="share-dialog-footer">
          <a className="button" href={url} target="_blank" rel="noreferrer">
            <ExternalLink size={16} />打开分享页
          </a>
          <button type="button" className="button button-primary" onClick={onCopy}>
            <Copy size={16} />复制链接
          </button>
        </footer>
      </section>
    </div>
  );
}
