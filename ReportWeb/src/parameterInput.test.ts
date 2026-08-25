import { describe, expect, it } from 'vitest';
import {
  defaultDataTypeForControl,
  parameterInputAttributes,
  parameterInputValue,
} from './parameterInput';

describe('parameterInput', () => {
  it.each([
    ['date', 'string', 'date'],
    ['datetime', 'string', 'datetime-local'],
    ['month', 'string', 'month'],
    ['year', 'string', 'number'],
  ])('maps %s control to its native input', (controlType, dataType, expected) => {
    expect(parameterInputAttributes({ controlType, dataType }).type).toBe(expected);
  });

  it('normalizes backend datetime defaults for datetime-local inputs', () => {
    expect(parameterInputValue(
      { controlType: 'datetime', dataType: 'datetime' },
      '2026-08-24 10:30:00',
    )).toBe('2026-08-24T10:30:00');
  });

  it('limits the year control to a four-digit calendar year', () => {
    expect(parameterInputAttributes({ controlType: 'year', dataType: 'string' })).toEqual({
      type: 'number',
      min: '1',
      max: '9999',
      step: 1,
    });
  });

  it.each([
    ['date', 'date'],
    ['datetime', 'datetime'],
    ['month', 'string'],
    ['year', 'string'],
  ])('selecting %s chooses compatible data type %s', (controlType, expected) => {
    expect(defaultDataTypeForControl(controlType, 'decimal')).toBe(expected);
  });
});
