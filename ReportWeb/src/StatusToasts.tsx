import { useEffect, useRef } from 'react';
import { CircleCheck, CircleX, TriangleAlert, X } from 'lucide-react';

type ToastVariant = 'error' | 'success' | 'warning';

interface ToastItemProps {
  message: string;
  variant: ToastVariant;
  onClose: () => void;
  autoCloseMilliseconds?: number;
}

interface StatusToastsProps {
  error?: string | null;
  success?: string | null;
  warning?: string | null;
  onErrorClose?: () => void;
  onSuccessClose?: () => void;
  onWarningClose?: () => void;
  successAutoCloseMilliseconds?: number;
}

/**
 * 负责 StatusToasts 的 Toast Item 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function ToastItem({
  message,
  variant,
  onClose,
  autoCloseMilliseconds,
}: ToastItemProps) {
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  useEffect(() => {
    if (autoCloseMilliseconds === undefined) return undefined;
    const timer = window.setTimeout(() => closeRef.current(), autoCloseMilliseconds);
    return () => window.clearTimeout(timer);
  }, [autoCloseMilliseconds, message]);

  const Icon = variant === 'success'
    ? CircleCheck
    : variant === 'warning'
      ? TriangleAlert
      : CircleX;

  return (
    <div
      className={`status-toast ${variant}`}
      role={variant === 'error' ? 'alert' : 'status'}
      aria-atomic="true"
    >
      <Icon className="status-toast-icon" size={18} aria-hidden="true" />
      <span>{message}</span>
      <button
        type="button"
        className="status-toast-close"
        title="关闭通知"
        aria-label="关闭通知"
        onClick={onClose}
      >
        <X size={16} />
      </button>
    </div>
  );
}

/**
 * 负责 StatusToasts 的 Status Toasts 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function StatusToasts({
  error,
  success,
  warning,
  onErrorClose,
  onSuccessClose,
  onWarningClose,
  successAutoCloseMilliseconds = 6_000,
}: StatusToastsProps) {
  if (!error && !success && !warning) return null;

  return (
    <div className="status-toast-region" aria-label="操作通知">
      {error && onErrorClose && (
        <ToastItem message={error} variant="error" onClose={onErrorClose} />
      )}
      {warning && onWarningClose && (
        <ToastItem message={warning} variant="warning" onClose={onWarningClose} />
      )}
      {success && onSuccessClose && (
        <ToastItem
          message={success}
          variant="success"
          onClose={onSuccessClose}
          autoCloseMilliseconds={successAutoCloseMilliseconds}
        />
      )}
    </div>
  );
}
