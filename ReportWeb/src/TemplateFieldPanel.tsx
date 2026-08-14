import { Braces, Columns3, Link2, PlugZap, Settings2 } from 'lucide-react';
import {
  parameterSourceCategory,
  type ParameterSourceCategory,
} from './externalParameterContext';
import type { ParameterDefinition, ReportDefinition } from './types';

/** 字段面板不直接操作 Univer，只把 token 和绑定种类交给宿主，保持 UI 与工作簿 API 解耦。 */
interface TemplateFieldPanelProps {
  definition: ReportDefinition;
  disabled: boolean;
  onInsert: (token: string, kind: 'parameter' | 'detail') => void;
}

interface TemplateParameterGroup {
  category: ParameterSourceCategory;
  label: string;
  icon: typeof Braces;
  parameters: ParameterDefinition[];
}

const parameterGroupDefinitions: ReadonlyArray<Omit<TemplateParameterGroup, 'parameters'>> = [
  { category: 'form', label: '页面参数', icon: Braces },
  { category: 'url', label: 'URL 参数', icon: Link2 },
  { category: 'external', label: '接口参数', icon: PlugZap },
  { category: 'system', label: '系统参数', icon: Settings2 },
];

/** visible 只控制查询区；模板可使用每一个已经定义并最终进入 parameters 的参数。 */
export function templateParameterGroups(
  parameters: readonly ParameterDefinition[],
): TemplateParameterGroup[] {
  return parameterGroupDefinitions
    .map((group) => ({
      ...group,
      parameters: parameters.filter(
        (parameter) => parameterSourceCategory(parameter.sourceType) === group.category,
      ),
    }))
    .filter((group) => group.parameters.length > 0);
}

/**
 * 根据后台报表定义生成可插入字段。
 * 数据集只提供查询返回字段；公式由设计人员直接写在模板行中并随行复制。
 */
export function TemplateFieldPanel({
  definition,
  disabled,
  onInsert,
}: TemplateFieldPanelProps) {
  const parameterGroups = templateParameterGroups(definition.parameters);
  const detailFields = definition.fields;

  return (
    <aside className="field-panel" aria-label="模板字段">
      {parameterGroups.map((group) => {
        const Icon = group.icon;
        return (
          <div className="field-group" key={group.category}>
            <div className="field-group-title">
              <Icon size={15} />
              {group.label}
            </div>
            <div className="field-list">
              {group.parameters.map((parameter) => {
                const token = `{{params.${parameter.code}}}`;
                return (
                  <button
                    className={`field-button ${group.category}`}
                    type="button"
                    key={parameter.code}
                    title={`${parameter.displayName} · ${token}`}
                    disabled={disabled}
                    onClick={() => onInsert(token, 'parameter')}
                  >
                    {parameter.displayName}
                  </button>
                );
              })}
            </div>
          </div>
        );
      })}

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
                {field.displayName}
              </button>
            );
          })}
        </div>
      </div>
    </aside>
  );
}
