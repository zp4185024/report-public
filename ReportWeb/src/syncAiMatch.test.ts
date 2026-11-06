import { describe, expect, it } from 'vitest';
import { applyAiSyncMatchSuggestions } from './syncTaskDesign';
import type { SyncColumnMapping } from './types';
import type { SyncSourceColumn, SyncTargetColumn } from './syncTaskDesign';

/**
 * 构造源字段列：与数据集字段契约同形状（名称/类型代码/可选注释），
 * 注释会进入 AI 匹配提示词，供语义匹配与同名基线共同使用。
 * @param definitions (名称, 类型, 注释) 序列。
 * @returns 源字段列。
 */
function sources(definitions: [string, string, string?][]): SyncSourceColumn[] {
  return definitions.map(([name, dataType, description]) => ({
    name,
    dataType,
    description: description ?? null,
  }));
}

/**
 * 构造目标列集合。
 * @param definitions (名称, 类型, 可空, 长度, 注释) 序列。
 * @returns 目标列集合。
 */
function targets(definitions: [string, string, boolean, number | null, string?][]): SyncTargetColumn[] {
  return definitions.map(([name, targetType, nullable, length, description]) => ({
    name,
    targetType,
    nullable,
    length,
    description: description ?? null,
  }));
}

/**
 * 构造一条 AI 建议。
 * @param overrides - 需要覆盖的字段。
 * @returns 完整建议对象。
 */
function suggestion(overrides: {
  sourceField: string;
  targetField: string;
  targetType?: string;
  confidence?: 'high' | 'mid' | 'low';
  reason?: string;
  needsManual?: boolean;
  converters?: { type: string; args: Record<string, string | null> | null }[];
}) {
  return {
    sourceField: overrides.sourceField,
    targetField: overrides.targetField,
    targetType: overrides.targetType ?? 'string',
    length: null,
    nullable: true,
    converters: overrides.converters ?? [],
    confidence: overrides.confidence ?? 'high',
    reason: overrides.reason ?? '建议依据',
    needsManual: overrides.needsManual ?? false,
  };
}

describe('applyAiSyncMatchSuggestions', () => {
  it('fills blank mappings from suggestions and attaches confidence marks', () => {
    const backfill = applyAiSyncMatchSuggestions(
      sources([
        ['FEE_TYPE', 'string', '费用类别'],
        ['VISIT_DATE', 'datetime', '就诊日期'],
      ]),
      targets([
        ['FEE_CATEGORY', 'string', true, 32, '费用类别'],
        ['visit_date', 'datetime', true, null, '就诊日期'],
      ]),
      [],
      [
        suggestion({ sourceField: 'FEE_TYPE', targetField: 'FEE_CATEGORY', confidence: 'high', reason: '费用类别语义一致' }),
        suggestion({ sourceField: 'VISIT_DATE', targetField: 'visit_date', confidence: 'high', reason: '同名匹配' }),
      ],
    );

    expect(backfill.mappings).toHaveLength(2);
    expect(backfill.mappings[0]).toMatchObject({
      sourceField: 'FEE_TYPE',
      targetField: 'FEE_CATEGORY',
      targetType: 'string',
    });
    expect(backfill.marks['fee_category']).toMatchObject({
      targetField: 'FEE_CATEGORY',
      confidence: 'high',
      reason: '费用类别语义一致',
    });
    expect(backfill.marks['visit_date']).toMatchObject({ confidence: 'high' });
    expect(backfill.manualTargets).toEqual([]);
  });

  it('never overwrites manual mappings when a suggestion conflicts', () => {
    const manual: SyncColumnMapping[] = [
      { sourceField: 'PATIENT_NAME', targetField: 'patient_name', targetType: 'string', length: 64, nullable: true, converters: [] },
    ];
    const backfill = applyAiSyncMatchSuggestions(
      sources([
        ['PATIENT_NAME', 'string', '患者姓名'],
        ['OTHER_SRC', 'string', '其他来源'],
      ]),
      targets([['patient_name', 'string', true, 64, '患者姓名']]),
      manual,
      [suggestion({ sourceField: 'OTHER_SRC', targetField: 'patient_name', confidence: 'high', reason: '名字撞车' })],
    );

    // 人工映射原样保留且只有一条；冲突建议被丢弃、无置信标注。
    expect(backfill.mappings).toHaveLength(1);
    expect(backfill.mappings[0].sourceField).toBe('PATIENT_NAME');
    expect(Object.keys(backfill.marks)).toEqual([]);
  });

  it('excludes needsManual suggestions from mappings and reports manual targets', () => {
    const backfill = applyAiSyncMatchSuggestions(
      sources([['FEE_TYPE', 'string', '费用类别']]),
      targets([
        ['FEE_CATEGORY', 'string', true, 32, '费用类别'],
        ['REMARK', 'string', true, 200, '备注'],
      ]),
      [],
      [
        suggestion({ sourceField: 'FEE_TYPE', targetField: 'FEE_CATEGORY', confidence: 'high', reason: '同名' }),
        suggestion({
          sourceField: '',
          targetField: 'REMARK',
          confidence: 'low',
          reason: '需人工处理：源表与 LLM 均未找到对应字段',
          needsManual: true,
        }),
      ],
    );

    expect(backfill.mappings).toHaveLength(1);
    expect(backfill.manualTargets).toEqual(['REMARK']);
    expect(backfill.marks['remark']).toBeUndefined();
  });

  it('drops suggestions referencing names that do not exist on either side', () => {
    const backfill = applyAiSyncMatchSuggestions(
      sources([['FEE_TYPE', 'string', '费用类别']]),
      targets([['FEE_CATEGORY', 'string', true, 32, '费用类别']]),
      [],
      [
        suggestion({ sourceField: 'GHOST_SRC', targetField: 'FEE_CATEGORY', confidence: 'high', reason: '幻觉字段' }),
        suggestion({ sourceField: 'FEE_TYPE', targetField: 'GHOST_TARGET', confidence: 'high', reason: '幻觉目标' }),
      ],
    );

    expect(backfill.mappings).toEqual([]);
    expect(backfill.manualTargets).toEqual([]);
    expect(Object.keys(backfill.marks)).toEqual([]);
  });

  it('applies suggestions sequentially so a source or target is used at most once', () => {
    const backfill = applyAiSyncMatchSuggestions(
      sources([['FEE_TYPE', 'string', '费用类别']]),
      targets([
        ['FEE_CATEGORY', 'string', true, 32, '费用类别'],
        ['fee_type', 'string', true, 16, '同名'],
      ]),
      [],
      [
        suggestion({ sourceField: 'FEE_TYPE', targetField: 'FEE_CATEGORY', confidence: 'high', reason: '语义' }),
        suggestion({ sourceField: 'FEE_TYPE', targetField: 'fee_type', confidence: 'high', reason: '同名' }),
      ],
    );

    // 首条建议占用 FEE_TYPE 后，第二条因源字段已被占用被丢弃。
    expect(backfill.mappings).toHaveLength(1);
    expect(backfill.mappings[0].targetField).toBe('FEE_CATEGORY');
  });

  it('carries converter chains from suggestions into the backfilled mappings', () => {
    const converters = [{ type: 'trim', args: null }];
    const backfill = applyAiSyncMatchSuggestions(
      sources([['FEE_TYPE', 'string', '费用类别']]),
      targets([['FEE_CATEGORY', 'string', true, 32, '费用类别']]),
      [],
      [suggestion({ sourceField: 'FEE_TYPE', targetField: 'FEE_CATEGORY', converters })],
    );

    expect(backfill.mappings[0].converters).toEqual([{ type: 'trim', args: null }]);
  });
});
