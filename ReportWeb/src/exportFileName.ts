/** 导出文件类型，用于区分模板草稿和查询数据。 */
export type ExportFileKind = 'template' | 'data';

/**
 * 根据报表名称生成导出文件名初始值，保持模板与数据文件容易区分。
 * @param reportName - 报表展示名称；为空时使用通用报表名称。
 * @param kind - 导出内容类型，决定默认名称中的后缀文字。
 * @returns 带有 `.xlsx` 扩展名的默认文件名。
 */
export function defaultExportFileName(reportName: string | undefined, kind: ExportFileKind): string {
  const safeReportName = sanitizeExportFileName(reportName?.trim() || '报表')
    .replace(/\.xlsx$/iu, '') || '报表';
  const suffix = kind === 'template' ? '模板' : '数据';
  return `${safeReportName}_${suffix}.xlsx`;
}

/**
 * 检查用户输入的文件名，阻止路径分隔符和控制字符进入下载属性。
 * @param value - 待检查的文件名文本。
 * @returns 空字符串表示合法，否则返回面向用户的校验提示。
 */
export function validateExportFileName(value: string): string {
  const trimmed = value.trim();
  if (trimmed.length === 0) return '请输入导出文件名。';
  if (trimmed.length > 128) return '文件名不能超过 128 个字符。';
  if (/[<>:"/\\|?*\u0000-\u001f]/u.test(trimmed)) {
    return '文件名不能包含斜杠、冒号、星号等特殊字符。';
  }
  if (trimmed.endsWith('.') || trimmed.endsWith(' ')) {
    return '文件名不能以空格或句点结尾。';
  }
  return '';
}

/**
 * 规范化文件名并确保下载内容使用 XLSX 扩展名。
 * @param value - 用户输入或调用方提供的文件名。
 * @param fallback - 输入为空时使用的后备文件名。
 * @returns 清理后的 `.xlsx` 文件名。
 */
export function normalizeExportFileName(value: string, fallback: string): string {
  const source = (value.trim() || fallback.trim() || '报表.xlsx')
    .replace(/[<>:"/\\|?*\u0000-\u001f]/gu, '_')
    .replace(/[. ]+$/u, '');
  const withoutExtension = source.replace(/\.xlsx$/iu, '');
  return `${withoutExtension || '报表'}.xlsx`;
}

/**
 * 清理默认名称中的文件系统非法字符，避免报表名称来自配置时破坏下载名。
 * @param value - 报表名称或文件名片段。
 * @returns 可安全放入下载文件名的文本。
 */
function sanitizeExportFileName(value: string): string {
  return value.replace(/[<>:"/\\|?*\u0000-\u001f]/gu, '_').replace(/[. ]+$/u, '') || '报表';
}
