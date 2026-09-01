import type { ParameterDefinition } from './types';

type ParameterShape = Pick<ParameterDefinition, 'controlType' | 'dataType'>;

export type ParameterInputType = 'date' | 'datetime-local' | 'month' | 'number' | 'text';

export interface ParameterInputAttributes {
  type: ParameterInputType;
  min?: string;
  max?: string;
  step?: number;
}

/**
 * 校验 parameterInput 的 is Temporal Control Type 条件，并返回可供后续流程判断的结果。
 * @param controlType - controlType 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function isTemporalControlType(controlType: string): boolean {
  return ['date', 'datetime', 'month', 'year'].includes(controlType.toLowerCase());
}

/**
 * 创建并返回 parameterInput 的 default Data Type For Control 初始数据，避免不同调用方共享可变对象。
 * @param controlType - controlType 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param currentDataType - currentDataType 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 创建并返回 parameterInput 的 parameter Input Attributes 配置对象，集中提供相关状态和操作入口。
 * @param parameter - parameter 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 负责 parameterInput 的 parameter Input Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param parameter - parameter 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function parameterInputValue(parameter: ParameterShape, value: string): string {
  if (parameterInputAttributes(parameter).type !== 'datetime-local') return value;
  return value.replace(' ', 'T');
}
