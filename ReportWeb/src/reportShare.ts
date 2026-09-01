const parameterPrefix = 'p.';

interface CreateReportShareUrlOptions {
  reportCode: string;
  parameters: Readonly<Record<string, string | null>>;
  visibleParameterCodes: readonly string[];
  includeParameters: boolean;
  origin?: string;
}

/**
 * 构建 reportShare 所需的 create Report Share Url 内容，并保证生成结果符合调用约定。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function createReportShareUrl({
  reportCode,
  parameters,
  visibleParameterCodes,
  includeParameters,
  origin = window.location.origin,
}: CreateReportShareUrlOptions): string {
  const url = new URL(`/share/reports/${encodeURIComponent(reportCode)}`, origin);
  if (!includeParameters) return url.toString();

  const fragment = new URLSearchParams();
  for (const code of visibleParameterCodes) {
    const value = parameters[code];
    if (value !== null && value.trim() !== '') {
      fragment.set(`${parameterPrefix}${code}`, value);
    }
  }
  const serialized = fragment.toString();
  if (serialized) url.hash = serialized;
  return url.toString();
}

/**
 * 查询或读取 reportShare 所需的 read Report Share Parameters 数据，并按调用约定返回结果。
 * @param hash - hash 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function readReportShareParameters(hash = window.location.hash): Record<string, string> {
  const result: Record<string, string> = {};
  const fragment = new URLSearchParams(hash.replace(/^#/, ''));
  for (const [key, value] of fragment.entries()) {
    if (key.startsWith(parameterPrefix) && key.length > parameterPrefix.length) {
      result[key.slice(parameterPrefix.length)] = value;
    }
  }
  return result;
}

/**
 * 查询或读取 reportShare 所需的 read Report Query Parameters 数据，并按调用约定返回结果。
 * @param search - search 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function readReportQueryParameters(search = window.location.search): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of new URLSearchParams(search).entries()) {
    result[key] = value;
  }
  return result;
}
