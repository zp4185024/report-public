import { useEffect, useState, type FormEvent } from 'react';
import { FileDown, LoaderCircle, X } from 'lucide-react';
import {
  normalizeExportFileName,
  validateExportFileName,
  type ExportFileKind,
} from './exportFileName';

export interface ExportNameDialogProps {
  open: boolean;
  kind: ExportFileKind | null;
  defaultName: string;
  busy?: boolean;
  onCancel: () => void;
  onConfirm: (fileName: string) => void;
}

/**
 * 提交导出文件名弹窗，先让用户确认名称再触发实际下载流程。
 * @param props - 弹窗开关、导出类型、默认名称以及确认回调。
 * @returns 文件名编辑弹窗或空内容。
 */
export function ExportNameDialog({
  open,
  kind,
  defaultName,
  busy = false,
  onCancel,
  onConfirm,
}: ExportNameDialogProps) {
  // 第一阶段：根据弹窗打开状态重置默认名称和上一次校验提示。
  const [fileName, setFileName] = useState(defaultName);
  const [validationError, setValidationError] = useState('');

  useEffect(() => {
    // 第二阶段：弹窗每次打开时恢复当前报表对应的默认导出名称。
    if (!open) return;
    setFileName(defaultName);
    setValidationError('');
  }, [defaultName, open]);

  useEffect(() => {
    // 第三阶段：锁定背景滚动并注册 Escape 关闭监听，卸载时完整恢复页面状态。
    if (!open) return undefined;
    const previousOverflow = document.documentElement.style.overflow;
    document.documentElement.style.overflow = 'hidden';
    /** 处理键盘 Escape，允许用户在未导出前关闭命名弹窗。 */
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onCancel();
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => {
      window.removeEventListener('keydown', closeOnEscape);
      document.documentElement.style.overflow = previousOverflow;
    };
  }, [busy, onCancel, open]);

  if (!open || kind === null) return null;

  /** 校验名称并把带扩展名的最终值交给页面导出流程。 */
  const submit = (event: FormEvent<HTMLFormElement>) => {
    // 第四阶段：校验用户输入并规范化扩展名后交给父页面启动下载。
    event.preventDefault();
    const error = validateExportFileName(fileName);
    if (error) {
      setValidationError(error);
      return;
    }
    onConfirm(normalizeExportFileName(fileName, defaultName));
  };

  const title = kind === 'template' ? '导出模板' : '导出数据';

  return (
    <div className="export-name-backdrop" role="presentation">
      <form className="export-name-dialog" role="dialog" aria-modal="true" aria-labelledby="export-name-title" onSubmit={submit}>
        <header className="export-name-header">
          <div>
            <h2 id="export-name-title"><FileDown size={18} />{title}</h2>
            <p>确认文件名后开始下载。</p>
          </div>
          <button
            type="button"
            className="icon-button"
            title="关闭"
            aria-label="关闭导出命名窗口"
            disabled={busy}
            onClick={onCancel}
          >
            <X size={18} />
          </button>
        </header>
        <div className="export-name-body">
          <label className="export-name-field">
            <span>文件名</span>
            <input
              autoFocus
              maxLength={128}
              value={fileName}
              aria-invalid={validationError ? true : undefined}
              onChange={(event) => {
                setFileName(event.target.value);
                if (validationError) setValidationError('');
              }}
            />
            <small>文件将以 `.xlsx` 格式下载。</small>
          </label>
          {validationError && <div className="export-name-error" role="alert">{validationError}</div>}
        </div>
        <footer className="export-name-footer">
          <button type="button" className="button" disabled={busy} onClick={onCancel}>取消</button>
          <button type="submit" className="button button-primary" disabled={busy}>
            {busy ? <LoaderCircle className="spin" size={16} /> : <FileDown size={16} />}
            开始导出
          </button>
        </footer>
      </form>
    </div>
  );
}
