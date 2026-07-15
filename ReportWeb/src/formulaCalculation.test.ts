import { describe, expect, it } from 'vitest';
import { recalculateFormulas } from './formulaCalculation';

describe('recalculateFormulas', () => {
  it('starts calculation and waits for the latest applied result', async () => {
    const calls: string[] = [];
    const formula = {
      executeCalculation: () => {
        calls.push('execute');
      },
      onCalculationResultApplied: async (timeout?: number) => {
        calls.push(`wait:${timeout}`);
      },
    };

    await recalculateFormulas(formula, 100);

    expect(calls).toEqual(['execute', 'wait:100']);
  });

  it('surfaces calculation wait failures with a Chinese context', async () => {
    const formula = {
      executeCalculation: () => undefined,
      onCalculationResultApplied: async () => {
        throw new Error('timeout');
      },
    };

    await expect(recalculateFormulas(formula, 100)).rejects.toThrow(
      'Univer 公式重算失败：timeout',
    );
  });
});
