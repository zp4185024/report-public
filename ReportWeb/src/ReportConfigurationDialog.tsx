import { useEffect, useMemo, useState } from 'react';
import {
  ArrowDown,
  ArrowUp,
  Columns3,
  Database,
  ExternalLink,
  LoaderCircle,
  PlugZap,
  Plus,
  Save,
  SlidersHorizontal,
  Trash2,
  X,
} from 'lucide-react';
import {
  isLocalFilterParameter,
  isLocalFilterSource,
  localFilterSourceType,
  toPersistedLocalFilterParameter,
} from './localDataQuery';
import { ValueOptionsEditor } from './ValueOptionsEditor';
import { StatusToasts } from './StatusToasts';
import {
  alignDatasetVariables,
  bindExternalField,
  importExternalSourceFields,
} from './reportParameterSources';
import type {
  FieldPresentationDefinition,
  ParameterDefinition,
  ReportConfigurationDocument,
  SaveReportConfigurationRequest,
  ValueOption,
} from './types';

type ConfigurationTab = 'basic' | 'parameters' | 'columns';
type ValueEditorTarget =
  | { kind: 'parameter'; index: number }
  | { kind: 'field'; code: string };

interface ReportConfigurationDialogProps {
  open: boolean;
  document: ReportConfigurationDocument | null;
  saveConfiguration: (request: SaveReportConfigurationRequest) => Promise<ReportConfigurationDocument>;
  onClose: () => void;
  onSaved: (document: ReportConfigurationDocument) => void;
}

function toDraft(document: ReportConfigurationDocument): SaveReportConfigurationRequest {
  const dataset = document.datasets.find((item) => item.code === document.datasetCode);
  const parameters = dataset === undefined
    ? document.parameters.map((parameter) => ({ ...parameter }))
    : alignDatasetVariables(document.parameters, dataset.variables);
  const datasetVariableCodes = new Set(
    dataset?.variables.map((variable) => variable.code.toLowerCase()) ?? [],
  );
  return {
    name: document.name,
    sheetName: document.sheetName,
    preview: { ...document.preview },
    export: { ...document.export },
    datasetCode: document.datasetCode,
    parameters: parameters.map((parameter) => (
      dataset !== undefined &&
      !datasetVariableCodes.has(parameter.code.toLowerCase()) &&
      isLocalFilterParameter(parameter, dataset.fields)
        ? { ...parameter, sourceType: localFilterSourceType }
        : parameter
    )),
    revision: document.revision,
    shareProtection: {
      enabled: document.shareProtection?.enabled ?? false,
      newPassword: null,
    },
    fieldPresentations: (document.fieldPresentations ?? []).map((presentation) => ({
      ...presentation,
      valueMappings: presentation.valueMappings.map((mapping) => ({ ...mapping })),
    })),
  };
}

function toSaveRequest(draft: SaveReportConfigurationRequest): SaveReportConfigurationRequest {
  return {
    ...draft,
    parameters: draft.parameters.map((parameter) => toPersistedLocalFilterParameter({
      ...parameter,
      options: (parameter.options ?? []).map((option) => ({
        value: option.value.trim(),
        label: option.label.trim(),
      })),
    })),
    fieldPresentations: (draft.fieldPresentations ?? []).map((presentation) => ({
      ...presentation,
      valueMappings: presentation.valueMappings.map((mapping) => ({
        value: mapping.value.trim(),
        label: mapping.label.trim(),
      })),
    })),
  };
}

function createParameter(index: number): ParameterDefinition {
  return {
    code: `parameter${index + 1}`,
    displayName: `查询参数 ${index + 1}`,
    controlType: 'text',
    sourceType: 'Form',
    defaultValue: null,
    visible: true,
    dataType: 'string',
    defaultExpression: null,
    fixedValue: null,
    readOnly: false,
    required: false,
    allowUrlPrefill: false,
    urlKey: null,
    options: [],
    sourceCode: null,
    sourceFieldCode: null,
  };
}

function isChoiceControl(controlType: string): boolean {
  const normalized = controlType.toLowerCase();
  return normalized === 'select' || normalized === 'multiselect';
}

function invalidValueOptions(options: readonly ValueOption[]): boolean {
  const values = new Set<string>();
  return options.length === 0 || options.some((option) => {
    const value = option.value.trim().toLowerCase();
    if (!value || !option.label.trim() || values.has(value)) return true;
    values.add(value);
    return false;
  });
}

function controlTypeForDataType(dataType: string): string {
  switch (dataType.toLowerCase()) {
    case 'int':
    case 'long':
    case 'decimal':
    case 'number':
      return 'number';
    case 'date':
    case 'datetime':
      return 'date';
    case 'bool':
    case 'boolean':
      return 'checkbox';
    default:
      return 'text';
  }
}

function moveItem<T>(items: readonly T[], index: number, offset: -1 | 1): T[] {
  const target = index + offset;
  if (target < 0 || target >= items.length) return [...items];
  const next = [...items];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

export function ReportConfigurationDialog({
  open,
  document,
  saveConfiguration,
  onClose,
  onSaved,
}: ReportConfigurationDialogProps) {
  const [tab, setTab] = useState<ConfigurationTab>('basic');
  const [draft, setDraft] = useState<SaveReportConfigurationRequest | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [valueEditorTarget, setValueEditorTarget] = useState<ValueEditorTarget | null>(null);
  const [externalSourceToImport, setExternalSourceToImport] = useState('');

  useEffect(() => {
    if (!open) {
      setDraft(null);
      setTab('basic');
      setError(null);
      setValueEditorTarget(null);
      setExternalSourceToImport('');
      return;
    }
    if (document !== null && draft === null) setDraft(toDraft(document));
  }, [document, draft, open]);

  useEffect(() => {
    if (!open) return undefined;
    const previousOverflow = globalThis.document.documentElement.style.overflow;
    globalThis.document.documentElement.style.overflow = 'hidden';
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !saving) onClose();
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => {
      window.removeEventListener('keydown', closeOnEscape);
      globalThis.document.documentElement.style.overflow = previousOverflow;
    };
  }, [onClose, open, saving]);

  const selectedDataset = useMemo(
    () => document?.datasets.find((dataset) => dataset.code === draft?.datasetCode) ?? null,
    [document?.datasets, draft?.datasetCode],
  );
  const externalSources = document?.externalSources?.filter((source) => source.enabled) ?? [];
  const parameterCodes = useMemo(
    () => new Set(draft?.parameters.map((parameter) => parameter.code.toLowerCase()) ?? []),
    [draft?.parameters],
  );
  const missingParameters = selectedDataset?.variables.filter(
    (variable) => !parameterCodes.has(variable.code.toLowerCase()),
  ) ?? [];
  const mismatchedParameters = selectedDataset?.variables.filter((variable) => {
    const parameter = draft?.parameters.find(
      (item) => item.code.toLowerCase() === variable.code.toLowerCase(),
    );
    return parameter !== undefined && (
      parameter.dataType !== variable.dataType ||
      (variable.required && !parameter.required)
    );
  }) ?? [];
  const datasetVariableCodes = useMemo(
    () => new Set(selectedDataset?.variables.map((variable) => variable.code.toLowerCase()) ?? []),
    [selectedDataset?.variables],
  );

  if (!open) return null;

  const updateParameter = (index: number, patch: Partial<ParameterDefinition>) => {
    setDraft((current) => {
      if (current === null) return current;
      const parameters = [...current.parameters];
      parameters[index] = { ...parameters[index], ...patch };
      return { ...current, parameters };
    });
  };

  const validateDraft = (): string | null => {
    if (draft === null) return '配置尚未加载完成。';
    if (!draft.name.trim() || !draft.sheetName.trim()) return '报表名称和工作表名称不能为空。';
    if (selectedDataset === null) return '请选择一个有效的数据集。';
    if (!selectedDataset.enabled) return '当前数据集已停用，不能用于报表。';
    if (draft.parameters.some((parameter) => !parameter.code.trim())) return '查询参数编码不能为空。';
    if (parameterCodes.size !== draft.parameters.length) return '查询参数编码不能重复。';
    if (draft.parameters.some((parameter) => (
      isLocalFilterSource(parameter.sourceType) &&
      !isLocalFilterParameter(parameter, selectedDataset.fields)
    ))) {
      return '本地筛选必须选择一个返回字段。';
    }
    const invalidExternalParameter = draft.parameters.find((parameter) => {
      if (parameter.sourceType !== 'External') return false;
      const source = externalSources.find((item) => item.code === parameter.sourceCode);
      const field = source?.fields.find((item) => item.code === parameter.sourceFieldCode);
      return source === undefined || field === undefined || field.dataType !== parameter.dataType;
    });
    if (invalidExternalParameter !== undefined) {
      return `参数 '${invalidExternalParameter.displayName}' 需要选择数据类型一致的接口返回字段。`;
    }
    const invalidParameterOptions = draft.parameters.find((parameter) => (
      isChoiceControl(parameter.controlType) && invalidValueOptions(parameter.options ?? [])
    ));
    if (invalidParameterOptions !== undefined) {
      return `参数 '${invalidParameterOptions.displayName}' 的可选项不能为空、重复或缺少显示文字。`;
    }
    const invalidPresentation = (draft.fieldPresentations ?? []).find(
      (presentation) => invalidValueOptions(presentation.valueMappings),
    );
    if (invalidPresentation !== undefined) {
      return `返回字段 '${invalidPresentation.code}' 的显示映射不能为空、重复或缺少显示文字。`;
    }
    if (missingParameters.length > 0) {
      return `尚未配置数据集变量：${missingParameters.map((item) => item.code).join('、')}。`;
    }
    if (mismatchedParameters.length > 0) {
      return `变量类型与数据集不一致：${mismatchedParameters.map((item) => item.code).join('、')}。`;
    }
    const password = draft.shareProtection.newPassword;
    if (password !== null && (password.trim().length === 0 || password.length < 8 || password.length > 128)) {
      return '解除保护密码长度必须为 8 到 128 个字符。';
    }
    if (
      draft.shareProtection.enabled &&
      !(document?.shareProtection?.passwordConfigured ?? false) &&
      password === null
    ) {
      return '开启共享数据保护前必须设置解除保护密码。';
    }
    return null;
  };

  const save = async () => {
    const validationError = validateDraft();
    if (validationError !== null || draft === null) {
      setError(validationError);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const saved = await saveConfiguration(toSaveRequest(draft));
      setDraft(toDraft(saved));
      onSaved(saved);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '报表配置保存失败。');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="configuration-backdrop" role="presentation">
      <section className="configuration-dialog" role="dialog" aria-modal="true" aria-labelledby="configuration-title">
        <header className="configuration-header">
          <div>
            <h2 id="configuration-title">报表配置</h2>
            <p>{document === null ? '正在读取配置' : `${document.reportCode} · 修订号 ${document.revision}`}</p>
          </div>
          <button type="button" className="icon-button" title="关闭报表配置" aria-label="关闭报表配置" disabled={saving} onClick={onClose}>
            <X size={18} />
          </button>
        </header>

        <nav className="configuration-tabs" aria-label="配置类别">
          <button type="button" className={tab === 'basic' ? 'active' : ''} onClick={() => setTab('basic')}>
            <Database size={16} />基础设置
          </button>
          <button type="button" className={tab === 'parameters' ? 'active' : ''} onClick={() => setTab('parameters')}>
            <SlidersHorizontal size={16} />变量取值<span>{draft?.parameters.length ?? 0}</span>
          </button>
          <button type="button" className={tab === 'columns' ? 'active' : ''} onClick={() => setTab('columns')}>
            <Columns3 size={16} />返回字段<span>{selectedDataset?.fields.length ?? 0}</span>
          </button>
        </nav>

        <div className="configuration-body">
          {draft === null ? (
            <div className="configuration-loading"><LoaderCircle className="spin" size={22} />正在读取配置</div>
          ) : tab === 'basic' ? (
            <div className="configuration-source">
              <fieldset>
                <legend>报表信息</legend>
                <div className="configuration-form-grid compact-grid">
                  <label><span>报表名称</span><input value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} /></label>
                  <label><span>工作表名称</span><input value={draft.sheetName} onChange={(event) => setDraft({ ...draft, sheetName: event.target.value })} /></label>
                  <label><span>预览模式</span><select value={draft.preview.mode} onChange={(event) => setDraft({ ...draft, preview: { ...draft.preview, mode: event.target.value as 'Full' | 'Paged' } })}><option value="Full">整表预览</option><option value="Paged">分页预览</option></select></label>
                  <label><span>每页行数</span><input type="number" min="1" max="10000" value={draft.preview.pageSize} onChange={(event) => setDraft({ ...draft, preview: { ...draft.preview, pageSize: Number(event.target.value) } })} /></label>
                </div>
              </fieldset>

              <fieldset className="dataset-reference-fieldset">
                <legend>使用的数据集</legend>
                <div className="dataset-reference-row">
                  <label>
                    <span>数据集</span>
                    <select value={draft.datasetCode} onChange={(event) => {
                      const nextDataset = document?.datasets.find((dataset) => dataset.code === event.target.value);
                      setDraft({
                        ...draft,
                        datasetCode: event.target.value,
                        parameters: nextDataset === undefined
                          ? draft.parameters
                          : alignDatasetVariables(draft.parameters, nextDataset.variables),
                        fieldPresentations: nextDataset === undefined
                          ? draft.fieldPresentations
                          : (draft.fieldPresentations ?? []).filter((presentation) => (
                              nextDataset.fields.some((field) => (
                                field.code.toLowerCase() === presentation.code.toLowerCase()
                              ))
                            )),
                      });
                    }}>
                      {document?.datasets.map((dataset) => (
                        <option key={dataset.code} value={dataset.code} disabled={!dataset.enabled}>
                          {dataset.name}（{dataset.code}）{dataset.enabled ? '' : ' · 已停用'}
                        </option>
                      ))}
                    </select>
                  </label>
                  <button type="button" className="button" onClick={() => window.location.assign('/datasets')}>
                    <ExternalLink size={16} />管理数据集
                  </button>
                </div>
                {selectedDataset !== null && (
                  <div className="dataset-reference-meta">
                    <span>数据源：{selectedDataset.dataSourceCode}</span>
                    <span>返回字段：{selectedDataset.fields.length}</span>
                    <span>输入变量：{selectedDataset.variables.length === 0 ? '无' : selectedDataset.variables.map((variable) => `@${variable.code}`).join('、')}</span>
                  </div>
                )}
                {missingParameters.length > 0 && <p className="inline-warning">尚未配置变量：{missingParameters.map((item) => item.code).join('、')}</p>}
              </fieldset>

              <fieldset>
                <legend>导出策略</legend>
                <div className="configuration-form-grid compact-grid">
                  <label><span>默认导出</span><select value={draft.export.defaultMode} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, defaultMode: event.target.value as 'Normal' | 'Large' } })}><option value="Normal">普通导出</option><option value="Large">大文件导出</option></select></label>
                  <label><span>普通导出最大行数</span><input type="number" min="1" max="1000000" value={draft.export.normalMaxRows} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, normalMaxRows: Number(event.target.value) } })} /></label>
                  <label><span>单工作表最大行数</span><input type="number" min="1" max="1048576" value={draft.export.rowsPerSheet} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, rowsPerSheet: Number(event.target.value) } })} /></label>
                  <label className="toggle-field"><span>大文件导出</span><input type="checkbox" checked={draft.export.largeExportEnabled} onChange={(event) => setDraft({ ...draft, export: { ...draft.export, largeExportEnabled: event.target.checked } })} /></label>
                </div>
              </fieldset>

              <fieldset>
                <legend>共享数据保护</legend>
                <div className="configuration-form-grid compact-grid">
                  <label className="toggle-field">
                    <span>默认保护数据</span>
                    <input
                      type="checkbox"
                      checked={draft.shareProtection.enabled}
                      onChange={(event) => setDraft({
                        ...draft,
                        shareProtection: {
                          ...draft.shareProtection,
                          enabled: event.target.checked,
                        },
                      })}
                    />
                  </label>
                  <label className="protection-password-field">
                    <span>{document?.shareProtection?.passwordConfigured
                      ? '修改解除保护密码'
                      : '解除保护密码'}</span>
                    <input
                      type="password"
                      autoComplete="new-password"
                      minLength={8}
                      maxLength={128}
                      placeholder={document?.shareProtection?.passwordConfigured
                        ? '留空表示不修改'
                        : '至少 8 个字符'}
                      value={draft.shareProtection.newPassword ?? ''}
                      onChange={(event) => setDraft({
                        ...draft,
                        shareProtection: {
                          ...draft.shareProtection,
                          newPassword: event.target.value === '' ? null : event.target.value,
                        },
                      })}
                    />
                  </label>
                </div>
              </fieldset>
            </div>
          ) : tab === 'parameters' ? (
            <div className="configuration-table-section">
              <div className="configuration-section-bar">
                <button type="button" className="button" onClick={() => setDraft({ ...draft, parameters: [...draft.parameters, createParameter(draft.parameters.length)] })}><Plus size={16} />新增模板参数</button>
                <div className="external-parameter-import">
                  <select aria-label="选择要导入的接口" value={externalSourceToImport} onChange={(event) => setExternalSourceToImport(event.target.value)}>
                    <option value="">选择接口</option>
                    {externalSources.map((source) => <option key={source.code} value={source.code}>{source.name}</option>)}
                  </select>
                  <button type="button" className="button" disabled={!externalSourceToImport} onClick={() => {
                    const source = externalSources.find((item) => item.code === externalSourceToImport);
                    if (source !== undefined) {
                      setDraft({ ...draft, parameters: importExternalSourceFields(draft.parameters, source) });
                    }
                  }}><PlugZap size={16} />导入接口字段</button>
                </div>
              </div>
              <div className="configuration-table-scroll">
                <table className="configuration-table parameter-table">
                  <thead><tr><th>顺序</th><th>参数编码</th><th>显示名称</th><th>数据类型</th><th>来源</th><th>控件</th><th>来源配置</th><th>选项</th><th>操作</th></tr></thead>
                  <tbody>
                    {draft.parameters.map((parameter, index) => {
                      const localFilter = isLocalFilterSource(parameter.sourceType);
                      const datasetVariable = selectedDataset?.variables.find(
                        (variable) => variable.code.toLowerCase() === parameter.code.toLowerCase(),
                      );
                      const datasetOwnsExternalBinding = datasetVariable?.sourceType === 'External';
                      const selectedExternalSource = externalSources.find(
                        (source) => source.code === parameter.sourceCode,
                      );
                      const selectableExternalFields = selectedExternalSource?.fields.filter((field) => (
                        datasetVariable === undefined || field.dataType === parameter.dataType
                      )) ?? [];
                      const usedByAnotherParameter = new Set(
                        draft.parameters
                          .filter((_, parameterIndex) => parameterIndex !== index)
                          .map((item) => item.code.toLowerCase()),
                      );
                      return (
                      <tr key={`${index}-${parameter.code}`}>
                        <td><div className="row-order"><button type="button" title="上移" aria-label="上移参数" disabled={index === 0} onClick={() => setDraft({ ...draft, parameters: moveItem(draft.parameters, index, -1) })}><ArrowUp size={14} /></button><button type="button" title="下移" aria-label="下移参数" disabled={index === draft.parameters.length - 1} onClick={() => setDraft({ ...draft, parameters: moveItem(draft.parameters, index, 1) })}><ArrowDown size={14} /></button></div></td>
                        <td>{localFilter ? (
                          <select
                            className="code-input"
                            aria-label={`本地筛选字段 ${index + 1}`}
                            value={parameter.code}
                            onChange={(event) => {
                              const field = selectedDataset?.fields.find(
                                (item) => item.code === event.target.value,
                              );
                              if (field === undefined) {
                                updateParameter(index, {
                                  code: '',
                                  displayName: '',
                                  dataType: 'string',
                                  controlType: 'text',
                                  urlKey: null,
                                });
                                return;
                              }
                              updateParameter(index, {
                                code: field.code,
                                displayName: field.displayName,
                                dataType: field.dataType,
                                controlType: controlTypeForDataType(field.dataType),
                                options: [],
                                defaultExpression: null,
                                fixedValue: null,
                                urlKey: parameter.allowUrlPrefill ? field.code : null,
                              });
                            }}
                          >
                            <option value="">请选择返回字段</option>
                            {selectedDataset?.fields.map((field) => (
                              <option
                                key={field.code}
                                value={field.code}
                                disabled={
                                  usedByAnotherParameter.has(field.code.toLowerCase()) ||
                                  datasetVariableCodes.has(field.code.toLowerCase())
                                }
                              >
                                {field.displayName}（{field.code}）
                              </option>
                            ))}
                          </select>
                        ) : (
                          <input className="code-input" disabled={datasetVariableCodes.has(parameter.code.toLowerCase())} value={parameter.code} onChange={(event) => updateParameter(index, { code: event.target.value })} />
                        )}</td>
                        <td><input value={parameter.displayName} onChange={(event) => updateParameter(index, { displayName: event.target.value })} /></td>
                        <td><select disabled={datasetVariableCodes.has(parameter.code.toLowerCase())} value={parameter.dataType} onChange={(event) => updateParameter(index, { dataType: event.target.value })}><option value="string">文本</option><option value="int">整数</option><option value="long">长整数</option><option value="decimal">小数</option><option value="date">日期</option><option value="datetime">日期时间</option><option value="bool">布尔值</option></select></td>
                        <td><select aria-label={`参数来源 ${index + 1}`} disabled={datasetOwnsExternalBinding} value={parameter.sourceType} onChange={(event) => {
                          const sourceType = event.target.value;
                          const currentField = selectedDataset?.fields.find(
                            (field) => field.code.toLowerCase() === parameter.code.toLowerCase(),
                          );
                          if (isLocalFilterSource(sourceType)) {
                            updateParameter(index, {
                              sourceType,
                              readOnly: false,
                              code: currentField?.code ?? '',
                              displayName: currentField === undefined
                                ? ''
                                : currentField.displayName,
                              dataType: currentField?.dataType ?? 'string',
                              controlType: currentField === undefined
                                ? 'text'
                                : controlTypeForDataType(currentField.dataType),
                              options: [],
                              defaultExpression: null,
                              fixedValue: null,
                              urlKey: currentField !== undefined && parameter.allowUrlPrefill
                                ? currentField.code
                                : null,
                            });
                            return;
                          }
                          if (sourceType === 'External') {
                            const source = externalSources.find((item) => item.fields.some((field) => (
                              datasetVariable === undefined || field.dataType === parameter.dataType
                            )));
                            const field = source?.fields.find((item) => (
                              datasetVariable === undefined || item.dataType === parameter.dataType
                            ));
                            if (source !== undefined && field !== undefined) {
                              updateParameter(index, bindExternalField(parameter, source, field));
                            } else {
                              updateParameter(index, {
                                sourceType: 'External',
                                sourceCode: null,
                                sourceFieldCode: null,
                                readOnly: true,
                                visible: false,
                                allowUrlPrefill: false,
                                urlKey: null,
                              });
                            }
                            return;
                          }
                          if (sourceType === 'Url') {
                            updateParameter(index, {
                              sourceType: 'Url',
                              sourceCode: null,
                              sourceFieldCode: null,
                              readOnly: true,
                              visible: true,
                              allowUrlPrefill: true,
                              urlKey: parameter.urlKey || parameter.code,
                              defaultExpression: null,
                              fixedValue: null,
                              controlType: isChoiceControl(parameter.controlType)
                                ? 'text'
                                : parameter.controlType,
                              options: isChoiceControl(parameter.controlType)
                                ? []
                                : parameter.options,
                            });
                            return;
                          }
                          const fallback = createParameter(index);
                          updateParameter(index, {
                            sourceType,
                            sourceCode: null,
                            sourceFieldCode: null,
                            readOnly: sourceType !== 'Form',
                            code: parameter.code || fallback.code,
                            displayName: parameter.displayName || fallback.displayName,
                            controlType: sourceType !== 'Form' && isChoiceControl(parameter.controlType)
                              ? 'text'
                              : parameter.controlType,
                            options: sourceType !== 'Form' && isChoiceControl(parameter.controlType)
                              ? []
                              : parameter.options,
                          });
                        }}><option value="Form">页面输入</option><option value="Url">URL 参数</option><option value={localFilterSourceType}>本地筛选</option><option value="System">系统默认</option><option value="Fixed">固定值</option><option value="External">外部接口</option></select></td>
                        <td><select value={parameter.controlType} onChange={(event) => {
                          const controlType = event.target.value;
                          updateParameter(index, {
                            controlType,
                            dataType: controlType.toLowerCase() === 'multiselect' ? 'string' : parameter.dataType,
                            options: isChoiceControl(controlType) ? parameter.options ?? [] : [],
                          });
                        }}><option value="text">文本框</option><option value="number">数字框</option><option value="date">日期框</option><option value="checkbox">是/否</option><option value="select">单选</option><option value="multiSelect">多选</option><option value="hidden">隐藏</option></select></td>
                        <td>{parameter.sourceType === 'External' ? (
                          <div className="external-binding-selects">
                            <select aria-label={`接口来源 ${index + 1}`} value={parameter.sourceCode ?? ''} disabled={datasetOwnsExternalBinding} onChange={(event) => {
                              const source = externalSources.find((item) => item.code === event.target.value);
                              const field = source?.fields.find((item) => (
                                datasetVariable === undefined || item.dataType === parameter.dataType
                              ));
                              if (source !== undefined && field !== undefined) {
                                updateParameter(index, bindExternalField(parameter, source, field));
                              }
                            }}>
                              <option value="">选择接口</option>
                              {externalSources.filter((source) => source.fields.some((field) => (
                                datasetVariable === undefined || field.dataType === parameter.dataType
                              ))).map((source) => <option key={source.code} value={source.code}>{source.name}</option>)}
                            </select>
                            <select aria-label={`接口字段 ${index + 1}`} value={parameter.sourceFieldCode ?? ''} disabled={datasetOwnsExternalBinding} onChange={(event) => {
                              const field = selectableExternalFields.find((item) => item.code === event.target.value);
                              if (selectedExternalSource !== undefined && field !== undefined) {
                                updateParameter(index, bindExternalField(parameter, selectedExternalSource, field));
                              }
                            }}>
                              <option value="">选择返回字段</option>
                              {selectableExternalFields.map((field) => <option key={field.code} value={field.code}>{field.displayName}（{field.code}）</option>)}
                            </select>
                          </div>
                        ) : parameter.sourceType === 'Url' ? (
                          <input aria-label={`URL 参数名 ${index + 1}`} value={parameter.urlKey ?? ''} placeholder={parameter.code} onChange={(event) => updateParameter(index, { urlKey: event.target.value || parameter.code })} />
                        ) : isChoiceControl(parameter.controlType) ? (
                          <button type="button" className="button option-maintenance-button" onClick={() => setValueEditorTarget({ kind: 'parameter', index })}>
                            维护选项（{parameter.options?.length ?? 0}）
                          </button>
                        ) : (
                          <input disabled={localFilter} value={(parameter.sourceType === 'Fixed' ? parameter.fixedValue : parameter.defaultExpression) ?? ''} placeholder={localFilter ? '无需配置' : parameter.sourceType === 'Fixed' ? '固定值' : '如 startOfMonth'} onChange={(event) => parameter.sourceType === 'Fixed' ? updateParameter(index, { fixedValue: event.target.value || null }) : updateParameter(index, { defaultExpression: event.target.value || null })} />
                        )}</td>
                        <td><div className="flag-list"><label><input type="checkbox" disabled={selectedDataset?.variables.some((variable) => variable.required && variable.code.toLowerCase() === parameter.code.toLowerCase())} checked={parameter.required} onChange={(event) => updateParameter(index, { required: event.target.checked })} />必填</label><label><input type="checkbox" disabled={parameter.sourceType === 'External'} checked={parameter.visible} onChange={(event) => updateParameter(index, { visible: event.target.checked })} />显示</label><label><input type="checkbox" disabled={parameter.sourceType === 'External' || parameter.sourceType === 'Url'} checked={parameter.allowUrlPrefill} onChange={(event) => updateParameter(index, { allowUrlPrefill: event.target.checked, urlKey: event.target.checked ? parameter.urlKey || parameter.code : null })} />URL 预填</label></div></td>
                        <td>{datasetVariableCodes.has(parameter.code.toLowerCase()) ? <span className="dataset-variable-badge">数据集</span> : <button type="button" className="table-icon danger" title="删除参数" aria-label={`删除参数 ${parameter.displayName}`} onClick={() => setDraft({ ...draft, parameters: draft.parameters.filter((_, itemIndex) => itemIndex !== index) })}><Trash2 size={15} /></button>}</td>
                      </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          ) : (
            <div className="configuration-table-section">
              <div className="configuration-table-scroll">
                <table className="configuration-table column-table">
                  <thead><tr><th>顺序</th><th>字段编码</th><th>业务名称</th><th>数据类型</th><th>显示映射</th></tr></thead>
                  <tbody>{selectedDataset?.fields.map((field, index) => {
                    const mappingCount = (draft.fieldPresentations ?? []).find(
                      (presentation) => presentation.code.toLowerCase() === field.code.toLowerCase(),
                    )?.valueMappings.length ?? 0;
                    return <tr key={field.code}><td>{index + 1}</td><td>{field.code}</td><td>{field.displayName}</td><td>{field.dataType}</td><td><button type="button" className="button option-maintenance-button" onClick={() => setValueEditorTarget({ kind: 'field', code: field.code })}>维护映射（{mappingCount}）</button></td></tr>;
                  })}</tbody>
                </table>
              </div>
            </div>
          )}
        </div>

        <StatusToasts error={error} onErrorClose={() => setError(null)} />
        <footer className="configuration-footer"><div><button type="button" className="button" disabled={saving} onClick={onClose}>取消</button><button type="button" className="button button-primary" disabled={saving || draft === null} onClick={() => void save()}>{saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存配置</button></div></footer>
        {draft !== null && valueEditorTarget !== null && (() => {
          const parameter = valueEditorTarget.kind === 'parameter'
            ? draft.parameters[valueEditorTarget.index]
            : null;
          const presentation = valueEditorTarget.kind === 'field'
            ? (draft.fieldPresentations ?? []).find((item) => (
                item.code.toLowerCase() === valueEditorTarget.code.toLowerCase()
              ))
            : null;
          const editorOptions = parameter?.options ?? presentation?.valueMappings ?? [];
          const title = valueEditorTarget.kind === 'parameter'
            ? `查询参数选项：${parameter?.displayName ?? ''}`
            : `字段显示映射：${valueEditorTarget.code}`;
          return <ValueOptionsEditor
            title={title}
            options={editorOptions}
            onClose={() => setValueEditorTarget(null)}
            onChange={(options) => {
              if (valueEditorTarget.kind === 'parameter') {
                updateParameter(valueEditorTarget.index, { options });
                return;
              }
              setDraft((current) => {
                if (current === null) return current;
                const presentations = [...(current.fieldPresentations ?? [])];
                const targetIndex = presentations.findIndex((item) => (
                  item.code.toLowerCase() === valueEditorTarget.code.toLowerCase()
                ));
                if (options.length === 0) {
                  return {
                    ...current,
                    fieldPresentations: presentations.filter((_, index) => index !== targetIndex),
                  };
                }
                const nextPresentation: FieldPresentationDefinition = {
                  code: valueEditorTarget.code,
                  valueMappings: options,
                };
                if (targetIndex < 0) presentations.push(nextPresentation);
                else presentations[targetIndex] = nextPresentation;
                return { ...current, fieldPresentations: presentations };
              });
            }}
          />;
        })()}
      </section>
    </div>
  );
}
