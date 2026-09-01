import { useEffect, useState, type FormEvent } from 'react';
import { LoaderCircle, LockKeyhole, X } from 'lucide-react';

interface UnlockProtectionDialogProps {
  open: boolean;
  busy: boolean;
  error: string | null;
  onSubmit: (password: string) => void;
  onClose: () => void;
}

/**
 * 初始化 UnlockProtectionDialog 的 Unlock Protection Dialog 监听流程，并在状态变化时同步执行相关操作。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function UnlockProtectionDialog({
  open,
  busy,
  error,
  onSubmit,
  onClose,
}: UnlockProtectionDialogProps) {
  const [password, setPassword] = useState('');

  useEffect(() => {
    if (!open) setPassword('');
  }, [open]);

  // 第一阶段：根据 UnlockProtectionDialog 当前分支转换输入数据，并保存该阶段产生的中间结果。
  useEffect(() => {
    if (!open) return undefined;
    /**
     * 结束 UnlockProtectionDialog 的 close On Escape 交互并清理临时状态。
     * @param event - 触发当前处理流程的界面或业务事件。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    // 第一阶段：构造 UnlockProtectionDialog 当前阶段需要的中间数据和执行参数。
    /**
     * 结束 UnlockProtectionDialog 的 close On Escape 交互并清理临时状态。
     * @param event - 触发当前处理流程的界面或业务事件。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onClose();
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => window.removeEventListener('keydown', closeOnEscape);
  }, [busy, onClose, open]);

  if (!open) return null;

  /**
   * 校验当前输入并提交保存请求，成功后同步 UnlockProtectionDialog 的界面状态。
   * @param event - 触发当前处理流程的界面或业务事件。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    // 第3阶段：检查 UnlockProtectionDialog 当前条件，并按匹配分支执行对应处理。
    if (password.length > 0 && !busy) onSubmit(password);
  };

  // 第2阶段：整理 UnlockProtectionDialog 已计算的数据，并按方法契约返回最终结果。
  // 第2阶段：整理 UnlockProtectionDialog 已计算的数据，并按方法契约返回最终结果。
  return (
    <div className="unlock-backdrop" role="presentation">
      <form
        className="unlock-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="unlock-title"
        onSubmit={submit}
      >
        <header>
          <div className="unlock-title">
            <LockKeyhole size={18} />
            <h2 id="unlock-title">解除数据保护</h2>
          </div>
          <button
            type="button"
            className="icon-button"
            title="关闭"
            aria-label="关闭解除保护对话框"
            disabled={busy}
            onClick={onClose}
          >
            <X size={17} />
          </button>
        </header>
        <label>
          <span>密码</span>
          <input
            autoFocus
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
        </label>
        {error !== null && <p className="unlock-error" role="alert">{error}</p>}
        <footer>
          <button type="button" className="button" disabled={busy} onClick={onClose}>取消</button>
          <button type="submit" className="button button-primary" disabled={busy || password.length === 0}>
            {busy && <LoaderCircle className="spin" size={16} />}
            解除保护
          </button>
        </footer>
      </form>
    </div>
  );
}
