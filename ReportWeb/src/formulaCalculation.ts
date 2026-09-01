/** Univer 公式 facade 中本功能实际使用的最小接口，隔离第三方版本类型变化。 */
export interface FormulaCalculationController {
  /**
   * 执行 formulaCalculation 的 execute Calculation 核心流程，并保持错误和结果语义稳定。
   */
  executeCalculation(): void;
  /**
   * 响应 formulaCalculation 的 on Calculation Result Applied 事件，并协调相关状态和后续操作。
   * @param timeout - timeout 对应的业务输入；具体类型和空值范围以方法签名为准。
   */
  onCalculationResultApplied(timeout?: number): Promise<void>;
}

/**
 * 触发一次完整公式计算，并等待 Univer 把 Worker 计算结果写回工作簿。
 * 0.25.1 的等待 API 会跟踪最新计算会话，因此应先启动计算，再等待该会话完成。
 */
export async function recalculateFormulas(
  formula: FormulaCalculationController,
  timeoutMs = 10_000,
): Promise<void> {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new Error('公式重算超时时间必须大于 0。');
  }

  try {
    formula.executeCalculation();
    await formula.onCalculationResultApplied(timeoutMs);
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    throw new Error(`Univer 公式重算失败：${detail}`);
  }
}
