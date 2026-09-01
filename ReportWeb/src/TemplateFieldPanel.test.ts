import { describe, expect, it } from 'vitest';
import { templateParameterGroups } from './TemplateFieldPanel';
import type { ParameterDefinition } from './types';

/**
 * 验证 parameter 场景下的预期行为，并防止相关逻辑回归。
 * @param overrides - overrides 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function parameter(overrides: Partial<ParameterDefinition>): ParameterDefinition {
  return {
    code: 'value',
    displayName: '参数',
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
    ...overrides,
  };
}

describe('templateParameterGroups', () => {
  it('keeps every defined hidden URL parameter available without field-specific rules', () => {
    const groups = templateParameterGroups([
      parameter({ code: 'sex', displayName: '性别' }),
      parameter({
        code: 'callerValue',
        displayName: '调用方参数',
        sourceType: 'Url',
        visible: false,
        readOnly: true,
        allowUrlPrefill: true,
        urlKey: 'caller_key',
      }),
      parameter({ code: 'hisUserName', sourceType: 'External', visible: false }),
      parameter({ code: 'reportDate', sourceType: 'System', visible: false }),
    ]);

    expect(groups.map((group) => group.category)).toEqual([
      'form',
      'url',
      'external',
      'system',
    ]);
    expect(groups.find((group) => group.category === 'url')?.parameters[0]).toMatchObject({
      code: 'callerValue',
      displayName: '调用方参数',
      urlKey: 'caller_key',
    });
  });
});
