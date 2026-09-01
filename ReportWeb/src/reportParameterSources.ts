import type {
  DatasetVariableDefinition,
  ExternalParameterFieldDefinition,
  ExternalParameterSourceDefinition,
  ParameterDefinition,
} from './types';

/**
 * 构建 reportParameterSources 所需的 create Parameter 内容，并保证生成结果符合调用约定。
 * @param index - 控制数据范围或位置的数值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 校验 reportParameterSources 的 is External 条件，并返回可供后续流程判断的结果。
 * @param sourceType - sourceType 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function isExternal(sourceType: string | undefined): boolean {
  return sourceType?.toLowerCase() === 'external';
}

/**
 * 按 align Dataset Variables 规则筛选 reportParameterSources 数据，并返回满足条件的结果。
 * @param parameters - parameters 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param variables - variables 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function alignDatasetVariables(
  parameters: readonly ParameterDefinition[],
  variables: readonly DatasetVariableDefinition[],
): ParameterDefinition[] {
  const variableCodes = new Set(variables.map((variable) => variable.code.toLowerCase()));
  const mapped = variables.map((variable, index) => {
    const current = parameters.find(
      (parameter) => parameter.code.toLowerCase() === variable.code.toLowerCase(),
    );
    const datasetOwnsExternalBinding = isExternal(variable.sourceType);
    const sourceType = datasetOwnsExternalBinding
      ? 'External'
      : current?.sourceType ?? variable.sourceType ?? 'Form';
    const external = isExternal(sourceType);
    return {
      ...(current ?? createParameter(index)),
      code: variable.code,
      displayName: current?.displayName || variable.displayName,
      dataType: variable.dataType,
      required: variable.required || current?.required || false,
      sourceType,
      sourceCode: datasetOwnsExternalBinding
        ? variable.sourceCode ?? null
        : current?.sourceCode ?? null,
      sourceFieldCode: datasetOwnsExternalBinding
        ? variable.sourceFieldCode ?? null
        : current?.sourceFieldCode ?? null,
      readOnly: external || current?.readOnly || false,
      visible: external ? false : current?.visible ?? true,
      allowUrlPrefill: external ? false : current?.allowUrlPrefill ?? false,
      urlKey: external ? null : current?.urlKey ?? null,
    };
  });
  return [
    ...mapped,
    ...parameters
      .filter((parameter) => !variableCodes.has(parameter.code.toLowerCase()))
      .map((parameter) => ({ ...parameter })),
  ];
}

/**
 * 创建并返回 reportParameterSources 的 bind External Field 配置对象，集中提供相关状态和操作入口。
 * @param parameter - parameter 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param source - 本次转换或写入使用的数据对象。
 * @param field - 参与映射、显示或数据写入的字段集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function bindExternalField(
  parameter: ParameterDefinition,
  source: ExternalParameterSourceDefinition,
  field: ExternalParameterFieldDefinition,
): ParameterDefinition {
  return {
    ...parameter,
    sourceType: 'External',
    sourceCode: source.code,
    sourceFieldCode: field.code,
    dataType: field.dataType,
    readOnly: true,
    visible: false,
    allowUrlPrefill: false,
    urlKey: null,
    defaultExpression: null,
    fixedValue: null,
    controlType: 'text',
    options: [],
  };
}

/**
 * 接收并校验 reportParameterSources 的 import External Source Fields 输入，随后写入目标状态。
 * @param parameters - parameters 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param source - 本次转换或写入使用的数据对象。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function importExternalSourceFields(
  parameters: readonly ParameterDefinition[],
  source: ExternalParameterSourceDefinition,
): ParameterDefinition[] {
  const fieldsByCode = new Map(
    source.fields.map((field) => [field.code.toLowerCase(), field]),
  );
  const existingCodes = new Set(parameters.map((parameter) => parameter.code.toLowerCase()));
  const mapped = parameters.map((parameter) => {
    const field = fieldsByCode.get(parameter.code.toLowerCase());
    return field !== undefined && field.dataType.toLowerCase() === parameter.dataType.toLowerCase()
      ? bindExternalField(parameter, source, field)
      : { ...parameter };
  });
  const additions = source.fields
    .filter((field) => !existingCodes.has(field.code.toLowerCase()))
    .map((field, index) => bindExternalField({
      ...createParameter(parameters.length + index),
      code: field.code,
      displayName: field.displayName,
      dataType: field.dataType,
      required: false,
    }, source, field));
  return [...mapped, ...additions];
}
