import type { ParameterDefinition } from './types';

type ParameterShape = Pick<ParameterDefinition, 'controlType' | 'dataType'>;

export type ParameterInputType = 'date' | 'datetime-local' | 'month' | 'number' | 'text';

export interface ParameterInputAttributes {
  type: ParameterInputType;
  min?: string;
  max?: string;
  step?: number;
}

export function isTemporalControlType(controlType: string): boolean {
  return ['date', 'datetime', 'month', 'year'].includes(controlType.toLowerCase());
}

export function defaultDataTypeForControl(controlType: string, currentDataType: string): string {
  switch (controlType.toLowerCase()) {
    case 'date':
      return 'date';
    case 'datetime':
      return 'datetime';
    case 'month':
    case 'year':
    case 'multiselect':
      return 'string';
    default:
      return currentDataType;
  }
}

export function parameterInputAttributes(parameter: ParameterShape): ParameterInputAttributes {
  switch (parameter.controlType.toLowerCase()) {
    case 'date':
      return { type: 'date' };
    case 'datetime':
      return { type: 'datetime-local', step: 1 };
    case 'month':
      return { type: 'month' };
    case 'year':
      return { type: 'number', min: '1', max: '9999', step: 1 };
    case 'number':
      return { type: 'number' };
    default:
      if (parameter.dataType.toLowerCase() === 'date') return { type: 'date' };
      if (parameter.dataType.toLowerCase() === 'datetime') return { type: 'datetime-local', step: 1 };
      if (['int', 'long', 'decimal', 'number'].includes(parameter.dataType.toLowerCase())) {
        return { type: 'number' };
      }
      return { type: 'text' };
  }
}

export function parameterInputValue(parameter: ParameterShape, value: string): string {
  if (parameterInputAttributes(parameter).type !== 'datetime-local') return value;
  return value.replace(' ', 'T');
}
