import { describe, expect, it } from 'vitest';
import {
  applyLocalDataQuery,
  isLocalFilterParameter,
  isLocalFilterSource,
  localFilterSourceType,
  serializeMultiSelectValues,
  toPersistedLocalFilterParameter,
} from './localDataQuery';
import type {
  ExecuteRequest,
  ExecuteResult,
  ParameterDefinition,
  ReportDefinition,
} from './types';

const choiceParameter: ParameterDefinition = {
  code: 'GENDER_CODE',
  displayName: '人员类别',
  controlType: 'select',
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
  options: [
    { value: 'M', label: '男性条件' },
    { value: 'F', label: '女性条件' },
  ],
};

function definition(mode: 'Full' | 'Paged' = 'Full'): ReportDefinition {
  return {
    code: 'test2',
    name: 'test2',
    sheetName: 'test',
    preview: { mode, pageSize: 20, showExactTotal: false },
    export: {
      defaultMode: 'Normal',
      normalMaxRows: 20_000,
      normalMaxCells: 500_000,
      normalTimeoutSeconds: 60,
      largeExportEnabled: true,
      rowsPerSheet: 1_000_000,
      fileRetentionHours: 24,
    },
    parameters: [choiceParameter],
    fields: [
      { code: 'VISIT_ID', displayName: '就诊号', dataType: 'number' },
      { code: 'GENDER_CODE', displayName: '人员类别', dataType: 'string' },
    ],
    fieldPresentations: [{
      code: 'GENDER_CODE',
      valueMappings: [
        { value: 'M', label: '男' },
        { value: 'F', label: '女' },
      ],
    }],
  };
}

function result(): ExecuteResult {
  return {
    page: 1,
    pageSize: 3,
    hasMore: false,
    parameters: { GENDER_CODE: 'F' },
    rows: [
      { VISIT_ID: 1, GENDER_CODE: 'F' },
      { VISIT_ID: 2, GENDER_CODE: 'M' },
      { VISIT_ID: 3, GENDER_CODE: 'F' },
    ],
  };
}

function request(value: string | null): ExecuteRequest {
  return { page: 1, parameters: { GENDER_CODE: value } };
}

describe('local data query', () => {
  it('recognizes a form parameter whose code matches a returned field', () => {
    expect(isLocalFilterParameter(choiceParameter, definition().fields)).toBe(true);
    expect(isLocalFilterParameter(
      { ...choiceParameter, sourceType: localFilterSourceType },
      definition().fields,
    )).toBe(true);
    expect(isLocalFilterSource(localFilterSourceType)).toBe(true);
    expect(toPersistedLocalFilterParameter({
      ...choiceParameter,
      sourceType: localFilterSourceType,
    })).toMatchObject({ sourceType: 'Form', readOnly: false });
    expect(isLocalFilterParameter(
      { ...choiceParameter, code: 'hisOrganizeId' },
      definition().fields,
    )).toBe(false);
  });

  it('keeps parameter option labels separate from returned-field display mappings', () => {
    const source = result();

    const filtered = applyLocalDataQuery(source, definition(), request('F'));

    expect(filtered.rows).toEqual([
      { VISIT_ID: 1, GENDER_CODE: '女' },
      { VISIT_ID: 3, GENDER_CODE: '女' },
    ]);
    expect(filtered.pageSize).toBe(2);
    expect(filtered.parameters.GENDER_CODE).toBe('女性条件');
    expect(source.rows[0].GENDER_CODE).toBe('F');
  });

  it('applies returned-field display mappings even when no local filter is active', () => {
    expect(applyLocalDataQuery(result(), definition(), request(null)).rows).toEqual([
      { VISIT_ID: 1, GENDER_CODE: '女' },
      { VISIT_ID: 2, GENDER_CODE: '男' },
      { VISIT_ID: 3, GENDER_CODE: '女' },
    ]);
  });

  it('supports a multi-select local filter without changing the field mapping', () => {
    const multiDefinition = definition();
    multiDefinition.parameters[0] = {
      ...choiceParameter,
      controlType: 'multiSelect',
    };
    const multiValue = serializeMultiSelectValues(['M', 'F']);
    const multiResult = result();
    multiResult.parameters.GENDER_CODE = multiValue ?? '';

    const filtered = applyLocalDataQuery(
      multiResult,
      multiDefinition,
      request(multiValue),
    );

    expect(filtered.rows).toHaveLength(3);
    expect(filtered.parameters.GENDER_CODE).toBe('男性条件、女性条件');
  });

  it('rejects active local filtering for an incomplete paged result', () => {
    expect(() => applyLocalDataQuery(result(), definition('Paged'), request('M')))
      .toThrow('本地筛选仅支持整表预览');
  });
});
