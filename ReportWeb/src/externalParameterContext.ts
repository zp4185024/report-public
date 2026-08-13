import type {
  ExternalRequestParameterDefinition,
  ExternalParameterSourceDefinition,
  ExternalParameterSourceDocument,
} from './types';

const requestLocationLabels: Record<ExternalRequestParameterDefinition['location'], string> = {
  Query: '查询参数',
  Body: '请求体',
  Header: '请求头',
};

export function externalParameterTemplateKeys(
  source: Pick<ExternalParameterSourceDocument, 'endpoint' | 'requestBodyTemplate' | 'headersTemplate'> &
    Partial<Pick<ExternalParameterSourceDocument, 'requestParameters'>>,
): string[] {
  const keys = new Set((source.requestParameters ?? []).map((parameter) => parameter.urlKey));
  for (const template of [source.endpoint, source.requestBodyTemplate, source.headersTemplate]) {
    for (const match of template.matchAll(/\{\{([A-Za-z][A-Za-z0-9_]{0,63})\}\}/g)) {
      keys.add(match[1]);
    }
  }
  return [...keys].sort();
}

export function currentUrlParameterValues(
  keys: readonly string[],
  search = window.location.search,
): Record<string, string> {
  const query = new URLSearchParams(search);
  return Object.fromEntries(keys.flatMap((key) => {
    const value = query.get(key);
    return value === null ? [] : [[key, value]];
  }));
}

export function externalParameterDestinations(
  source: Pick<ExternalParameterSourceDocument, 'endpoint' | 'requestBodyTemplate' | 'headersTemplate'> &
    Partial<Pick<ExternalParameterSourceDocument, 'requestParameters'>>,
  key: string,
): string[] {
  const placeholder = `{{${key}}}`;
  const destinations = (source.requestParameters ?? [])
    .filter((parameter) => parameter.urlKey.toLowerCase() === key.toLowerCase())
    .map((parameter) => `${requestLocationLabels[parameter.location]}：${parameter.name}`);
  if (source.endpoint.includes(placeholder)) destinations.push('接口路径模板');
  if (source.requestBodyTemplate.includes(placeholder)) destinations.push('请求体 JSON 模板');
  if (source.headersTemplate.includes(placeholder)) destinations.push('请求头 JSON 模板');
  return [...new Set(destinations)];
}

/** 只挑出启用接口声明过的上下文键，避免把整个地址栏透传给后端或第三方系统。 */
export function externalParameterContext(
  sources: readonly ExternalParameterSourceDefinition[] | null | undefined,
  search = window.location.search,
): Record<string, string | null> {
  const query = new URLSearchParams(search);
  const keys = new Set(
    (sources ?? [])
      .filter((source) => source.enabled)
      .flatMap((source) => source.contextKeys),
  );
  return Object.fromEntries([...keys].map((key) => [key, query.get(key)]));
}

export function externalParameterDocumentContext(
  sources: readonly ExternalParameterSourceDocument[],
  search = window.location.search,
): Record<string, string | null> {
  return externalParameterContext(sources.map((source) => ({
    code: source.code,
    name: source.name,
    enabled: source.enabled,
    fields: source.fields,
    contextKeys: externalParameterTemplateKeys(source),
  })), search);
}

export function parameterSourceCategory(sourceType: string): 'form' | 'system' | 'external' {
  const normalized = sourceType.toLowerCase();
  if (normalized === 'external') return 'external';
  if (normalized === 'form' || normalized === 'url') return 'form';
  return 'system';
}
