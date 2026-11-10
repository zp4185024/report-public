import { describe, expect, it } from 'vitest';
import {
  aiDraftBadgeClass,
  aiDraftStatusLabel,
  canImportDraft,
  collectDraftFields,
  collectDraftParameters,
  formatDraftDryRunResult,
  formatDraftImportSuccess,
  formatDraftValidateResult,
} from './aiDraftCard';
import type { AiChatDraft, ReportPackage } from './types';

/**
 * 构造最小可用草稿（包结构与后端报表包契约同构，仅需覆盖卡片用到的字段）。
 * @param overrides - 需要覆盖的顶层字段。
 * @returns 完整草稿对象。
 */
function draft(overrides: Partial<AiChatDraft>): AiChatDraft {
  // 第一段：报表包四节按契约最小化构造——卡片只消费 report/parameters/dataset.fieldSchema。
  const base: AiChatDraft = {
    status: 'validated',
    spec: { specVersion: 1 },
    package: {
      packageKind: 'hd-report-center.report-package',
      packageVersion: 1,
      exportedAt: '2026-11-05T00:00:00Z',
      sourceDataSourceCode: 'ai_main',
      sourceDataSourceType: 'PostgreSql',
      report: {
        code: 'fee_summary',
        name: '费用汇总',
        sheetName: '费用汇总',
        datasetCode: 'fee_summary',
        previewSettings: { mode: 'Full', pageSize: 20, showExactTotal: false },
        exportSettings: {
          defaultMode: 'Normal',
          normalMaxRows: 20000,
          normalMaxCells: 500000,
          normalTimeoutSeconds: 60,
          largeExportEnabled: true,
          rowsPerSheet: 1000000,
          fileRetentionHours: 24,
        },
        fieldPresentations: null,
        isEnabled: true,
      },
      parameters: [
        {
          code: 'startDate',
          displayName: '开始日期',
          dataType: 'date',
          controlType: 'date',
          sourceType: 'Form',
          sourceCode: null,
          sourceFieldCode: null,
          urlKey: null,
          defaultExpression: null,
          fixedValue: null,
          options: null,
          isVisible: true,
          isReadOnly: false,
          isRequired: true,
          allowUrlPrefill: true,
          displayOrder: 1,
        },
      ],
      template: {
        snapshot: {} as ReportPackage['template']['snapshot'],
        binding: {
          version: 1,
          parameterBindings: [],
          detailBinding: { datasetCode: 'detail', sheetId: 'report-sheet', templateRow: 3, fields: [] },
        },
        revision: 1,
      },
      dataset: {
        code: 'fee_summary',
        name: '费用汇总数据集',
        queryText: 'SELECT 1',
        fieldSchema: {
          variables: [],
          fields: [
            { code: 'deptName', displayName: '科室', dataType: 'string' },
            { code: 'feeTotal', displayName: '费用合计', dataType: 'number' },
          ],
        },
        timeoutSeconds: 60,
        maxRows: 20000,
        isEnabled: true,
      },
    },
    validateResult: null,
    ...overrides,
  };
  // 第二段：允许用例覆写任意字段（如 status=failed + package=null 的失败态草稿）。
  return base;
}

describe('aiDraftStatusLabel / aiDraftBadgeClass', () => {
  it('maps each status to a readable label and badge class', () => {
    expect(aiDraftStatusLabel('validated')).toBe('校验通过');
    expect(aiDraftStatusLabel('dryRunOk')).toBe('试跑通过');
    expect(aiDraftStatusLabel('submitted')).toBe('已提交待确认');
    expect(aiDraftStatusLabel('failed')).toBe('提交失败，等待 AI 修正');

    expect(aiDraftBadgeClass('validated')).toBe('ai-draft-badge info');
    expect(aiDraftBadgeClass('dryRunOk')).toBe('ai-draft-badge ok');
    expect(aiDraftBadgeClass('submitted')).toBe('ai-draft-badge pending');
    expect(aiDraftBadgeClass('failed')).toBe('ai-draft-badge error');
  });
});

describe('canImportDraft', () => {
  it('allows import only after validation passes', () => {
    expect(canImportDraft('validated')).toBe(true);
    expect(canImportDraft('dryRunOk')).toBe(true);
    expect(canImportDraft('submitted')).toBe(false);
    expect(canImportDraft('failed')).toBe(false);
  });
});

describe('collectDraftFields / collectDraftParameters', () => {
  it('lists fields and parameters in package order with required marks', () => {
    const current = draft({});

    expect(collectDraftFields(current).map((field) => field.code)).toEqual(['deptName', 'feeTotal']);
    const parameters = collectDraftParameters(current);
    expect(parameters).toHaveLength(1);
    expect(parameters[0]).toMatchObject({ code: 'startDate', displayName: '开始日期', required: true });
  });

  it('returns empty lists when the package failed to compile', () => {
    const failed = draft({ status: 'failed', package: null });

    expect(collectDraftFields(failed)).toEqual([]);
    expect(collectDraftParameters(failed)).toEqual([]);
  });
});

describe('formatDraftValidateResult', () => {
  it('summarizes a passing validation with warning count', () => {
    const text = formatDraftValidateResult({
      ok: true,
      errors: [],
      warnings: [{ code: 'W', message: '提示' }],
      packageSummary: {
        reportCode: 'fee_summary',
        reportName: '费用汇总',
        datasetCode: 'fee_summary',
        parameterCount: 1,
        fieldCount: 2,
        sourceDataSourceType: 'PostgreSql',
      },
      collisions: {
        reportCodeExists: false,
        datasetCodeExists: false,
        datasetIdentical: null,
        datasetReferencedByOther: null,
      },
    });

    expect(text).toContain('校验通过');
    expect(text).toContain('1 条警告');
  });

  it('lists every error message when validation fails', () => {
    const text = formatDraftValidateResult({
      ok: false,
      errors: [
        { code: 'A', message: '参数缺失' },
        { code: 'B', message: '字段重复' },
      ],
      warnings: [],
      packageSummary: {
        reportCode: 'fee_summary',
        reportName: '费用汇总',
        datasetCode: 'fee_summary',
        parameterCount: 1,
        fieldCount: 2,
        sourceDataSourceType: 'PostgreSql',
      },
      collisions: {
        reportCodeExists: false,
        datasetCodeExists: false,
        datasetIdentical: null,
        datasetReferencedByOther: null,
      },
    });

    expect(text).toContain('2 项');
    expect(text).toContain('参数缺失');
    expect(text).toContain('字段重复');
  });

  it('reports not-validated drafts explicitly', () => {
    expect(formatDraftValidateResult(null)).toBe('尚未校验。');
  });
});

describe('formatDraftDryRunResult', () => {
  it('reports row count and schema match', () => {
    const text = formatDraftDryRunResult({
      rows: [1, 2, 3],
      elapsedMilliseconds: 120,
      fieldSchemaMatch: true,
      fieldSchemaMismatches: [],
    });

    expect(text).toContain('3 行');
    expect(text).toContain('120 ms');
    expect(text).toContain('一致');
  });

  it('lists schema mismatches when they exist', () => {
    const text = formatDraftDryRunResult({
      rows: [],
      elapsedMilliseconds: 5,
      fieldSchemaMatch: false,
      fieldSchemaMismatches: ['缺少列 feeTotal'],
    });

    expect(text).toContain('差异');
    expect(text).toContain('缺少列 feeTotal');
  });
});

describe('formatDraftImportSuccess', () => {
  it('tells the user to refresh the report list after import', () => {
    const text = formatDraftImportSuccess('fee_summary', 'Reused');

    expect(text).toContain('fee_summary');
    expect(text).toContain('Reused');
    expect(text).toContain('刷新模板列表');
  });
});
