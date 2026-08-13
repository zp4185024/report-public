const parameterPrefix = 'p.';

interface CreateReportShareUrlOptions {
  reportCode: string;
  parameters: Readonly<Record<string, string | null>>;
  visibleParameterCodes: readonly string[];
  includeParameters: boolean;
  origin?: string;
}

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

export function readReportQueryParameters(search = window.location.search): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of new URLSearchParams(search).entries()) {
    result[key] = value;
  }
  return result;
}
