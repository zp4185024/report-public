import {
  REPORT_PACKAGE_KIND,
  REPORT_PACKAGE_SUPPORTED_VERSION,
  type ReportPackage,
  type ReportPackageParameter,
} from './types';
import { evaluateDefaultExpression as evaluateSharedExpression } from './defaultExpressions';

/** 上传解析结果：要么得到可用的包对象（来自信封解包时带标记），要么得到可直接展示的中文错误。 */
export type ReportPackageParseResult =
  | { package: ReportPackage; unwrappedFromEnvelope?: true }
  | { error: string };

/**
 * 目标数据源记忆键：记录线上导入员上次选择的目标数据源编码，
 * 下次进入向导时自动带出，减少非专业人员重复选择出错的机会。
 */
export const REPORT_PACKAGE_TARGET_DATASOURCE_KEY = 'hd-report-center.report-package-import.targetDataSourceCode';

/**
 * 判断顶层 JSON 是否为后端 Furion 统一接口信封，并解出内层包对象。
 * 旧导出端把接口响应直接存盘时，文件会变成 { code, type, message, result } 包装；
 * 这里只在顶层没有 packageKind、code 是数字且 result 是普通对象时才视为信封，
 * 其余情况返回 null 交由调用方按常规缺失标识报错，避免误解包恰好叫 result 的业务字段。
 * @param parsed - JSON.parse 得到的顶层对象，调用方已保证非 null 且非数组。
 * @returns 信封内的 result 对象；不是信封形态时返回 null。
 */
export function unwrapEnvelopeResult(parsed: object): Record<string, unknown> | null {
  const envelope = parsed as { packageKind?: unknown; code?: unknown; result?: unknown };
  if (envelope.packageKind != null) return null;
  if (typeof envelope.code !== 'number') return null;
  const result = envelope.result;
  if (typeof result !== 'object' || result === null || Array.isArray(result)) return null;
  return result as Record<string, unknown>;
}

/**
 * 解析上传的报表包文本并完成本地结构预检（packageKind / packageVersion / 必需节），
 * 把明显不是报表包的文件挡在调用服务端校验之前。顶层没有 packageKind 但
 * result 内的 packageKind 匹配时，视为接口信封包装并自动解包，版本等后续校验
 * 一律以解包后的对象为准，成功时附 unwrappedFromEnvelope 标记供页面提示升级导出端。
 * @param text - 上传文件的完整文本内容。
 * @returns 结构合法时返回包对象（解包场景额外带标记），否则返回面向操作员的中文错误。
 */
export function parseReportPackageText(text: string): ReportPackageParseResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { error: '文件内容不是有效的 JSON，无法读取报表包。' };
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { error: '文件内容不是报表包 JSON 对象。' };
  }

  // 阶段：识别包体。顶层标识不匹配时先尝试解开接口信封包装，兼容旧导出端直存响应的文件。
  let candidate = parsed as Partial<ReportPackage>;
  let unwrappedFromEnvelope = false;
  if (candidate.packageKind !== REPORT_PACKAGE_KIND) {
    const inner = unwrapEnvelopeResult(parsed);
    if (inner !== null && inner.packageKind === REPORT_PACKAGE_KIND) {
      candidate = inner as Partial<ReportPackage>;
      unwrappedFromEnvelope = true;
    } else {
      const actualKind = inner !== null && typeof inner.packageKind === 'string'
        ? inner.packageKind
        : String(candidate.packageKind);
      return { error: `文件类型标识不匹配：期望 ${REPORT_PACKAGE_KIND}，实际为 ${actualKind}。` };
    }
  }

  // 阶段：结构预检。版本号与必需节的检查始终以解包后的对象为准。
  if (typeof candidate.packageVersion !== 'number' || !Number.isFinite(candidate.packageVersion)) {
    return { error: '报表包缺少有效的 packageVersion 版本号。' };
  }
  if (candidate.packageVersion > REPORT_PACKAGE_SUPPORTED_VERSION) {
    return { error: `报表包版本 ${candidate.packageVersion} 高于当前支持的版本 ${REPORT_PACKAGE_SUPPORTED_VERSION}，请先升级报表中心。` };
  }
  if (!candidate.report || !candidate.dataset || !candidate.template) {
    return { error: '报表包缺少必需的 report / dataset / template 节，文件可能已损坏。' };
  }

  // 阶段：回传结果。信封解包场景附上标记，供页面提示操作员升级导出端后重新导出。
  return unwrappedFromEnvelope
    ? { package: candidate as ReportPackage, unwrappedFromEnvelope: true }
    : { package: candidate as ReportPackage };
}

/**
 * 按后端 PocDefaultExpressionEvaluator 的封闭文法计算默认值，
 * 让导入向导在浏览器内为试跑表单提供与正式报表一致的日期默认值。
 * 文法实现收敛在 defaultExpressions.ts 共享模块（后端求值器的唯一前端镜像），
 * 本函数只保留向导侧的签名适配：空白或非法表达式返回 null 交必填校验拦截。
 * @param expression - 包参数声明的默认值表达式原文，如 startofmonth(addmonths(today(),-1))。
 * @param dataType - 参数数据类型，决定最终格式化（date/month/year/其他）。
 * @param now - 用于计算的时间基准，便于测试传入固定值。
 * @returns 可直接填入输入框的字符串；表达式为空返回 null，非法表达式返回 null。
 */
export function evaluateDefaultExpression(
  expression: string | null,
  dataType: string,
  now: Date,
): string | null {
  return evaluateSharedExpression(expression, dataType, now);
}

/**
 * 计算单个包参数在试跑表单里的初始值：固定值优先，其次求值默认表达式，
 * 再次取选项首项，最后为空字符串交由必填校验拦截。
 * @param parameter - 包内参数定义。
 * @param now - 默认表达式的计算基准时间。
 * @returns 填入表单的初始字符串值。
 */
export function initialParameterValue(
  parameter: ReportPackageParameter,
  now: Date,
): string {
  if (parameter.fixedValue !== null && parameter.fixedValue.trim() !== '') {
    return parameter.fixedValue;
  }
  const expressionValue = evaluateDefaultExpression(
    parameter.defaultExpression,
    parameter.dataType,
    now,
  );
  if (expressionValue !== null) return expressionValue;
  return parameter.options?.[0]?.value ?? '';
}

/**
 * 为整份包生成试跑参数初始值字典；隐藏参数也参与取值，保证 SQL 变量完整。
 * @param requestPackage - 解析后的报表包。
 * @param now - 默认表达式的计算基准时间。
 * @returns 以参数编码为键的初始值字典。
 */
export function buildInitialParameterValues(
  requestPackage: ReportPackage,
  now: Date,
): Record<string, string> {
  const values: Record<string, string> = {};
  for (const parameter of requestPackage.parameters ?? []) {
    values[parameter.code] = initialParameterValue(parameter, now);
  }
  return values;
}

/**
 * 找出已标记必填但当前值为空的参数编码，用于阻止带着缺口执行试跑。
 * @param requestPackage - 解析后的报表包。
 * @param values - 当前表单取值字典。
 * @returns 仍缺值的必填参数编码列表。
 */
export function missingRequiredParameterCodes(
  requestPackage: ReportPackage,
  values: Record<string, string>,
): string[] {
  return (requestPackage.parameters ?? [])
    .filter((parameter) => parameter.isRequired && !(values[parameter.code] ?? '').trim())
    .map((parameter) => parameter.code);
}

/**
 * 读取上次导入时记住的目标数据源编码；LocalStorage 不可用或值缺失时返回 null。
 * @returns 上次选择的数据源编码，或 null 表示没有记忆值。
 */
export function recallTargetDataSourceCode(): string | null {
  try {
    return window.localStorage.getItem(REPORT_PACKAGE_TARGET_DATASOURCE_KEY);
  } catch {
    return null;
  }
}

/**
 * 记住本次选择的目标数据源编码，供下次进入向导自动带出。
 * @param code - 本次选定的数据源编码；空值表示清除记忆。
 */
export function rememberTargetDataSourceCode(code: string): void {
  try {
    if (code.trim()) window.localStorage.setItem(REPORT_PACKAGE_TARGET_DATASOURCE_KEY, code.trim());
    else window.localStorage.removeItem(REPORT_PACKAGE_TARGET_DATASOURCE_KEY);
  } catch {
    // 隐私模式等场景写入失败时静默降级为不记忆。
  }
}
