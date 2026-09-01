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

/**
 * 遍历 externalParameterContext 数据并执行 external Parameter Template Keys 转换，生成后续流程需要的结果。
 * @param source - 本次转换或写入使用的数据对象。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 负责 externalParameterContext 的 current Url Parameter Values 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param keys - keys 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param search - search 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 按 external Parameter Destinations 规则筛选 externalParameterContext 数据，并返回满足条件的结果。
 * @param source - 本次转换或写入使用的数据对象。
 * @param key - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 遍历 externalParameterContext 数据并执行 external Parameter Document Context 转换，生成后续流程需要的结果。
 * @param sources - sources 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param search - search 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

export type ParameterSourceCategory = 'form' | 'url' | 'system' | 'external';

/**
 * 负责 externalParameterContext 的 parameter Source Category 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param sourceType - sourceType 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function parameterSourceCategory(sourceType: string): ParameterSourceCategory {
  const normalized = sourceType.toLowerCase();
  if (normalized === 'external') return 'external';
  if (normalized === 'url') return 'url';
  if (normalized === 'form' || normalized === 'localfilter') return 'form';
  return 'system';
}
