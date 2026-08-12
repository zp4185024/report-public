import { useEffect, useState, type FormEvent } from 'react';
import { LoaderCircle, LockKeyhole, X } from 'lucide-react';

interface UnlockProtectionDialogProps {
  open: boolean;
  busy: boolean;
  error: string | null;
  onSubmit: (password: string) => void;
  onClose: () => void;
}

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

  useEffect(() => {
    if (!open) return undefined;
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onClose();
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => window.removeEventListener('keydown', closeOnEscape);
  }, [busy, onClose, open]);

  if (!open) return null;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (password.length > 0 && !busy) onSubmit(password);
  };

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
