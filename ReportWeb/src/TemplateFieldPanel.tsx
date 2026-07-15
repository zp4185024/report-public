import { Braces, Columns3 } from 'lucide-react';
import type { ReportDefinition } from './types';

/** 字段面板不直接操作 Univer，只把 token 和绑定种类交给宿主，保持 UI 与工作簿 API 解耦。 */
interface TemplateFieldPanelProps {
  definition: ReportDefinition;
  disabled: boolean;
  onInsert: (token: string, kind: 'parameter' | 'detail') => void;
}

/**
 * 根据后台报表定义生成可插入字段。
 * 公式列被排除在明细字段之外，因为公式应由设计人员写在模板行中并随行复制。
 */
export function TemplateFieldPanel({
  definition,
  disabled,
  onInsert,
}: TemplateFieldPanelProps) {
  const parameters = definition.parameters.filter((parameter) => parameter.visible);
  const detailFields = definition.columns.filter((column) => column.dataType !== 'formula');

  return (
    <aside className="field-panel" aria-label="模板字段">
      <div className="field-group">
        <div className="field-group-title">
          <Braces size={15} />
          参数
        </div>
        <div className="field-list">
          {parameters.map((parameter) => {
            const token = `{{params.${parameter.code}}}`;
            return (
              <button
                className="field-button"
                type="button"
                key={parameter.code}
                title={token}
                disabled={disabled}
                onClick={() => onInsert(token, 'parameter')}
              >
                {parameter.displayName}
              </button>
            );
          })}
        </div>
      </div>

      <div className="field-group">
        <div className="field-group-title">
          <Columns3 size={15} />
          明细
        </div>
        <div className="field-list">
          {detailFields.map((field) => {
            const token = `{{detail.${field.code}}}`;
            return (
              <button
                className="field-button"
                type="button"
                key={field.code}
                title={token}
                disabled={disabled}
                onClick={() => onInsert(token, 'detail')}
              >
                {field.header}
              </button>
            );
          })}
        </div>
      </div>
    </aside>
  );
}
