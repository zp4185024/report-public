import { describe, expect, it } from 'vitest';
import {
  alignDatasetVariables,
  importExternalSourceFields,
} from './reportParameterSources';
import type { ExternalParameterSourceDefinition, ParameterDefinition } from './types';

const formParameter: ParameterDefinition = {
  code: 'hisOrganizeName',
  displayName: '医院名称',
  controlType: 'text',
  sourceType: 'Form',
  defaultValue: null,
  visible: true,
  dataType: 'string',
  defaultExpression: null,
  fixedValue: null,
  readOnly: false,
  required: false,
  allowUrlPrefill: true,
  urlKey: 'organName',
  options: [],
  sourceCode: null,
  sourceFieldCode: null,
};

const his: ExternalParameterSourceDefinition = {
  code: 'his',
  name: 'HIS 登录信息',
  enabled: true,
  contextKeys: ['token'],
  fields: [
    { code: 'hisOrganizeName', displayName: '医院名称', dataType: 'string', responsePath: 'content.organName' },
    { code: 'hisDepartmentName', displayName: '科室名称', dataType: 'string', responsePath: 'content.departmentName' },
    { code: 'hisUserName', displayName: '人员名称', dataType: 'string', responsePath: 'content.userName' },
  ],
};

describe('reportParameterSources', () => {
  it('preserves a template-owned external source while aligning SQL variables', () => {
    const external = importExternalSourceFields([formParameter], his)[0];
    const aligned = alignDatasetVariables([external], [{
      code: 'hisOrganizeName',
      displayName: 'SQL 医院名称',
      dataType: 'string',
      required: true,
      sourceType: 'Form',
    }]);

    expect(aligned[0]).toMatchObject({
      code: 'hisOrganizeName',
      sourceType: 'External',
      sourceCode: 'his',
      sourceFieldCode: 'hisOrganizeName',
      required: true,
    });
  });

  it('imports display fields and maps same-code SQL parameters to the external result', () => {
    const parameters = importExternalSourceFields([formParameter], his);

    expect(parameters).toHaveLength(3);
    expect(parameters.map((item) => item.code)).toEqual([
      'hisOrganizeName',
      'hisDepartmentName',
      'hisUserName',
    ]);
    expect(parameters.every((item) => (
      item.sourceType === 'External' && item.sourceCode === 'his' && !item.visible
    ))).toBe(true);
  });
});
