import { Plus, Trash2, X } from 'lucide-react';
import type { ValueOption } from './types';

interface ValueOptionsEditorProps {
  title: string;
  options: readonly ValueOption[];
  onChange: (options: ValueOption[]) => void;
  onClose: () => void;
}

export function ValueOptionsEditor({
  title,
  options,
  onChange,
  onClose,
}: ValueOptionsEditorProps) {
  const update = (index: number, patch: Partial<ValueOption>) => {
    const next = options.map((option, optionIndex) => (
      optionIndex === index ? { ...option, ...patch } : { ...option }
    ));
    onChange(next);
  };

  return (
    <div className="value-options-backdrop" role="presentation">
      <section className="value-options-dialog" role="dialog" aria-modal="true" aria-label={title}>
        <header>
          <h3>{title}</h3>
          <button type="button" className="icon-button" title="关闭选项维护" aria-label="关闭选项维护" onClick={onClose}>
            <X size={17} />
          </button>
        </header>
        <div className="value-options-body">
          <div className="value-options-head"><span>原始值</span><span>显示文字</span><span /></div>
          {options.map((option, index) => (
            <div className="value-option-row" key={index}>
              <input aria-label={`原始值 ${index + 1}`} value={option.value} onChange={(event) => update(index, { value: event.target.value })} />
              <input aria-label={`显示文字 ${index + 1}`} value={option.label} onChange={(event) => update(index, { label: event.target.value })} />
              <button type="button" className="table-icon danger" title="删除选项" aria-label={`删除选项 ${index + 1}`} onClick={() => onChange(options.filter((_, optionIndex) => optionIndex !== index).map((item) => ({ ...item })))}>
                <Trash2 size={15} />
              </button>
            </div>
          ))}
          <button type="button" className="button value-option-add" onClick={() => onChange([...options.map((item) => ({ ...item })), { value: '', label: '' }])}>
            <Plus size={15} />新增选项
          </button>
        </div>
        <footer><button type="button" className="button button-primary" onClick={onClose}>完成</button></footer>
      </section>
    </div>
  );
}
