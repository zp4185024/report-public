import { externalParameterContext } from './externalParameterContext';
import { readReportQueryParameters } from './reportShare';
import type { ExecuteRequest, ReportDefinition } from './types';

/**
 * 负责 reportRequest 的 url Value 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param parameter - parameter 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param query - query 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function urlValue(
  parameter: ReportDefinition['parameters'][number],
  query: Readonly<Record<string, string>>,
): string | null {
  return query[parameter.urlKey || parameter.code] ?? null;
}

/**
 * 构建 reportRequest 所需的 create Initial Report Request 内容，并保证生成结果符合调用约定。
 * @param definition - 描述字段、参数或业务约束的定义对象。
 * @param sharedParameters - sharedParameters 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param search - search 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function createInitialReportRequest(
  definition?: ReportDefinition,
  sharedParameters?: Readonly<Record<string, string>>,
  search = window.location.search,
): ExecuteRequest {
  const query = readReportQueryParameters(search);
  const parameters: Record<string, string | null> = {};
  for (const parameter of definition?.parameters ?? []) {
    const readsUrl = parameter.sourceType.toLowerCase() === 'url' || parameter.allowUrlPrefill;
    const supplied = sharedParameters === undefined
      ? readsUrl ? urlValue(parameter, query) : null
      : sharedParameters[parameter.code] ?? (readsUrl ? urlValue(parameter, query) : null);
    parameters[parameter.code] = supplied ?? parameter.defaultValue ?? parameter.fixedValue;
  }

  return {
    page: 1,
    parameters,
    context: externalParameterContext(definition?.externalSources, search),
    sqlDebug: import.meta.env.DEV,
  };
}

/** 查询前刷新真正由 URL 管理的参数；页面输入值只在初始化时允许 URL 预填。 */
export function refreshReportRequestFromUrl(
  request: ExecuteRequest,
  definition: ReportDefinition,
  search = window.location.search,
): ExecuteRequest {
  const query = readReportQueryParameters(search);
  const parameters = { ...request.parameters };
  for (const parameter of definition.parameters) {
    if (parameter.sourceType.toLowerCase() !== 'url') continue;
    parameters[parameter.code] = urlValue(parameter, query)
      ?? parameter.defaultValue
      ?? parameter.fixedValue;
  }

  return {
    ...request,
    parameters,
    context: externalParameterContext(definition.externalSources, search),
  };
}

/** 返回需要用户填写但当前仍为空的参数；外部参数由后端可信来源解析，不在此处拦截。 */
export function findMissingRequiredParameterCodes(
  definition: ReportDefinition,
  parameters: Readonly<Record<string, string | null>>,
): string[] {
  return definition.parameters
    .filter((parameter) => (
      parameter.required &&
      parameter.sourceType.toLowerCase() !== 'external' &&
      !(parameters[parameter.code]?.trim())
    ))
    .map((parameter) => parameter.code);
}
