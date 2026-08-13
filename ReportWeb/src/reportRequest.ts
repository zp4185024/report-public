import { externalParameterContext } from './externalParameterContext';
import { readReportQueryParameters } from './reportShare';
import type { ExecuteRequest, ReportDefinition } from './types';

function urlValue(
  parameter: ReportDefinition['parameters'][number],
  query: Readonly<Record<string, string>>,
): string | null {
  return query[parameter.urlKey || parameter.code] ?? null;
}

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
