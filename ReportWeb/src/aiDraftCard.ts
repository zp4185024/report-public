import type { AiChatDraft, AiDraftStatus, ReportPackageValidateResult } from './types';

/** 草稿卡片上的参数清单行；required 标记来自参数定义。 */
export interface AiDraftParameterRow {
  code: string;
  displayName: string;
  dataType: string;
  required: boolean;
}

/** 草稿卡片上的列清单行；来自包数据集字段契约。 */
export interface AiDraftFieldRow {
  code: string;
  displayName: string;
  dataType: string;
}

/**
 * 状态徽标的中文文案；让用户不认识英文状态也能判断下一步。
 * @param status - 草稿状态。
 * @returns 徽标文本。
 */
export function aiDraftStatusLabel(status: AiDraftStatus): string {
  return status === 'validated'
    ? '校验通过'
    : status === 'dryRunOk'
      ? '试跑通过'
      : status === 'failed'
        ? '提交失败，等待 AI 修正'
        : '已提交待确认';
}

/**
 * 状态徽标的样式类名（绿=试跑通过可导入，蓝=校验通过，黄=待确认，红=失败）：
 * 把“校验通过”与“试跑通过”拆成两档语义色，让用户一眼分清当前草稿走到哪一步。
 * @param status - 草稿状态。
 * @returns CSS 类名。
 */
export function aiDraftBadgeClass(status: AiDraftStatus): string {
  return status === 'dryRunOk'
    ? 'ai-draft-badge ok'
    : status === 'validated'
      ? 'ai-draft-badge info'
      : status === 'failed'
        ? 'ai-draft-badge error'
        : 'ai-draft-badge pending';
}

/**
 * 判定草稿当前是否允许导入：只有全链校验通过（validated/dryRunOk）的包才能走导入链路。
 * @param status - 草稿状态。
 * @returns 是否允许导入。
 */
export function canImportDraft(status: AiDraftStatus): boolean {
  return status === 'validated' || status === 'dryRunOk';
}

/**
 * 从草稿包提取列清单（保持包内字段顺序），供卡片展示将生成的报表列。
 * @param draft - 当前草稿。
 * @returns 列清单行；无包时为空。
 */
export function collectDraftFields(draft: AiChatDraft): AiDraftFieldRow[] {
  return (draft.package?.dataset.fieldSchema.fields ?? []).map((field) => ({
    code: field.code,
    displayName: field.displayName,
    dataType: field.dataType,
  }));
}

/**
 * 从草稿包提取参数清单（保持包内参数顺序），必填参数在卡片上标注。
 * @param draft - 当前草稿。
 * @returns 参数清单行；无包时为空。
 */
export function collectDraftParameters(draft: AiChatDraft): AiDraftParameterRow[] {
  return (draft.package?.parameters ?? []).map((parameter) => ({
    code: parameter.code,
    displayName: parameter.displayName,
    dataType: parameter.dataType,
    required: parameter.isRequired,
  }));
}

/**
 * 汇总校验报告为可展示文本；ok 时给出通过提示，否则逐条列出错误。
 * @param validateResult - 最近一次校验报告。
 * @returns 展示文本。
 */
export function formatDraftValidateResult(validateResult: ReportPackageValidateResult | null): string {
  if (validateResult === null) {
    return '尚未校验。';
  }
  if (validateResult.ok) {
    return `服务端校验通过${validateResult.warnings.length > 0 ? `（${validateResult.warnings.length} 条警告）` : ''}。`;
  }
  return `服务端校验未通过（${validateResult.errors.length} 项）：${validateResult.errors
    .map((issue) => issue.message)
    .join('；')}`;
}

/**
 * 生成试跑结果的可展示文本：行数、耗时与字段契约比对结论。
 * @param result - 试跑结果。
 * @returns 展示文本。
 */
export function formatDraftDryRunResult(result: {
  rows: unknown[];
  elapsedMilliseconds: number;
  fieldSchemaMatch: boolean;
  fieldSchemaMismatches: string[];
}): string {
  const base = `试跑返回 ${result.rows.length} 行（${result.elapsedMilliseconds} ms）。`;
  return result.fieldSchemaMatch
    ? `${base}字段契约与实际列一致。`
    : `${base}字段契约存在差异：${result.fieldSchemaMismatches.join('；')}`;
}

/**
 * 生成导入成功提示：告知用户已落库并刷新模板列表（导入走现有报表包链路，不经 /ai 接口）。
 * @param reportCode - 导入的报表编码。
 * @param datasetAction - 服务端执行的数据集动作（如 Reused/Created/Replaced）。
 * @returns 提示文本。
 */
export function formatDraftImportSuccess(reportCode: string, datasetAction: string): string {
  return `报表「${reportCode}」已导入（数据集：${datasetAction}）。请刷新模板列表查看，并可在编辑器中继续调整。`;
}
