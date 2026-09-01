import { describe, expect, it } from 'vitest';
import { recalculateFormulas } from './formulaCalculation';

describe('recalculateFormulas', () => {
  it('starts calculation and waits for the latest applied result', async () => {
    const calls: string[] = [];
    const formula = {
      /**
       * 验证 execute Calculation 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      executeCalculation: () => {
        calls.push('execute');
      },
      /**
       * 验证 on Calculation Result Applied 场景下的预期行为，并防止相关逻辑回归。
       * @param timeout - timeout 对应的业务输入；具体类型和空值范围以方法签名为准。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      onCalculationResultApplied: async (timeout?: number) => {
        calls.push(`wait:${timeout}`);
      },
    };

    await recalculateFormulas(formula, 100);

    expect(calls).toEqual(['execute', 'wait:100']);
  });

  it('surfaces calculation wait failures with a Chinese context', async () => {
    const formula = {
      /**
       * 验证 execute Calculation 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      executeCalculation: () => undefined,
      /**
       * 验证 on Calculation Result Applied 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      onCalculationResultApplied: async () => {
        throw new Error('timeout');
      },
    };

    await expect(recalculateFormulas(formula, 100)).rejects.toThrow(
      'Univer 公式重算失败：timeout',
    );
  });
});
