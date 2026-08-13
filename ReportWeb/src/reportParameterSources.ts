import type {
  DatasetVariableDefinition,
  ExternalParameterFieldDefinition,
  ExternalParameterSourceDefinition,
  ParameterDefinition,
} from './types';

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

function isExternal(sourceType: string | undefined): boolean {
  return sourceType?.toLowerCase() === 'external';
}

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
