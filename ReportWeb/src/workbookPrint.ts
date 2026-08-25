export type PrintOrientation = 'portrait' | 'landscape';

export interface PrintableBorder {
  color?: string;
  style: 'solid' | 'dashed' | 'dotted' | 'double';
  widthPt: number;
}

export interface PrintableCellStyle {
  background?: string;
  bold?: boolean;
  borders?: Partial<Record<'top' | 'right' | 'bottom' | 'left', PrintableBorder>>;
  color?: string;
  fontFamily?: string;
  fontSizePt?: number;
  horizontal?: string;
  italic?: boolean;
  strikethrough?: boolean;
  underline?: boolean;
  vertical?: string;
  wrap?: boolean;
}

export interface PrintableRichTextStyleData {
  bl?: unknown;
  cl?: { rgb?: string | null } | null;
  ff?: unknown;
  fs?: unknown;
  it?: unknown;
  st?: { s?: unknown } | null;
  ul?: { s?: unknown } | null;
}

/** Univer 富文本样式可能来自旧模板字符串值，这里统一转换为打印 CSS 可用类型。 */
export function applyPrintableRichTextStyle(
  base: PrintableCellStyle,
  richTextStyle?: PrintableRichTextStyleData | null,
): PrintableCellStyle {
  if (richTextStyle == null) return base;
  return {
    ...base,
    bold: readBooleanFlag(richTextStyle.bl) ?? base.bold,
    color: richTextStyle.cl?.rgb ?? base.color,
    fontFamily: readNonEmptyString(richTextStyle.ff) ?? base.fontFamily,
    fontSizePt: readFiniteNumber(richTextStyle.fs) ?? base.fontSizePt,
    italic: readBooleanFlag(richTextStyle.it) ?? base.italic,
    strikethrough: readBooleanFlag(richTextStyle.st?.s) ?? base.strikethrough,
    underline: readBooleanFlag(richTextStyle.ul?.s) ?? base.underline,
  };
}

export interface PrintableMerge {
  startRow: number;
  endRow: number;
  startColumn: number;
  endColumn: number;
}

export interface PrintableSheet {
  columnWidths: number[];
  /** 导出/打印统一使用的报表名称；未提供时回退到工作表名称。 */
  documentTitle?: string;
  merges: PrintableMerge[];
  name: string;
  rowHeights: number[];
  /** 工作表视图网格线；关闭时打印不应人为补充浅灰单元格边框。 */
  showGridlines?: boolean;
  startColumn: number;
  startRow: number;
  styles: PrintableCellStyle[][];
  values: string[][];
}

export interface PrintableSheetDocument {
  html: string;
  orientation: PrintOrientation;
  scale: number;
}

const landscapeWidthThreshold = 760;
const a4WidthMm: Record<PrintOrientation, number> = {
  portrait: 210,
  landscape: 297,
};
const cssPixelsPerMm = 96 / 25.4;
const pageMarginMm = 8;
const printFrameCleanupDelay = 60_000;

/**
 * Luck-Report 的浏览器打印流程会把完整报表 HTML 写入隔离 iframe，再应用 @page 后调用浏览器打印。
 * 这里保留同一流程，但由 Univer 公共 facade 生成可审计的 HTML，不加载 Luckysheet 私有渲染器。
 * 来源：references/luck-report（Apache-2.0）。
 */
export function buildPrintableSheetDocument(sheet: PrintableSheet): PrintableSheetDocument {
  const rowCount = sheet.values.length;
  const columnCount = Math.max(
    sheet.columnWidths.length,
    ...sheet.values.map((row) => row.length),
    0,
  );
  if (rowCount < 1 || columnCount < 1) {
    throw new Error('当前工作表没有可打印内容。');
  }
  if (!sheet.values.some((row) => row.some((value) => value.length > 0))) {
    throw new Error('当前工作表没有可打印内容。');
  }

  const columnWidths = Array.from({ length: columnCount }, (_, index) => (
    finiteDimension(sheet.columnWidths[index], 88, 4, 2_000)
  ));
  const totalWidth = columnWidths.reduce((sum, width) => sum + width, 0);
  const orientation: PrintOrientation = totalWidth > landscapeWidthThreshold
    ? 'landscape'
    : 'portrait';
  const printableWidth = (a4WidthMm[orientation] - pageMarginMm * 2) * cssPixelsPerMm;
  const scale = Math.min(1, printableWidth / totalWidth);
  const mergeIndex = indexMerges(sheet, rowCount, columnCount);
  const rows: string[] = [];

  for (let rowIndex = 0; rowIndex < rowCount; rowIndex += 1) {
    const cells: string[] = [];
    for (let columnIndex = 0; columnIndex < columnCount; columnIndex += 1) {
      const coordinate = coordinateKey(rowIndex, columnIndex);
      if (mergeIndex.covered.has(coordinate)) continue;

      const merge = mergeIndex.starts.get(coordinate);
      const rowspan = merge === undefined ? 1 : merge.endRow - merge.startRow + 1;
      const colspan = merge === undefined ? 1 : merge.endColumn - merge.startColumn + 1;
      const value = sheet.values[rowIndex]?.[columnIndex] ?? '';
      const style = printableCellCss(sheet.styles[rowIndex]?.[columnIndex]);
      const attributes = [
        rowspan > 1 ? ` rowspan="${rowspan}"` : '',
        colspan > 1 ? ` colspan="${colspan}"` : '',
        style.length > 0 ? ` style="${escapeHtml(style)}"` : '',
      ].join('');
      cells.push(`<td${attributes}><div>${escapeHtml(value).replace(/\r?\n/g, '<br>')}</div></td>`);
    }
    const height = finiteDimension(sheet.rowHeights[rowIndex], 24, 0, 1_000);
    const hidden = height === 0 ? ' class="hd-report-print-hidden-row"' : '';
    rows.push(`<tr${hidden} style="height:${height}px">${cells.join('')}</tr>`);
  }

  const columns = columnWidths
    .map((width) => `<col style="width:${width}px">`)
    .join('');
  const title = escapeHtml(sheet.documentTitle?.trim() || sheet.name || '报表');
  const printCss = buildPrintCss(
    orientation,
    totalWidth,
    scale,
    sheet.showGridlines !== false,
  );
  const html = [
    '<!doctype html>',
    '<html lang="zh-CN">',
    '<head>',
    '<meta charset="UTF-8">',
    `<title>${title}</title>`,
    `<style>${printCss}</style>`,
    '</head>',
    '<body>',
    `<main class="hd-report-print" aria-label="${title}">`,
    '<table>',
    `<colgroup>${columns}</colgroup>`,
    `<tbody>${rows.join('')}</tbody>`,
    '</table>',
    '</main>',
    '</body>',
    '</html>',
  ].join('');

  return { html, orientation, scale };
}

export async function printPrintableSheet(sheet: PrintableSheet): Promise<PrintOrientation> {
  const printable = buildPrintableSheetDocument(sheet);
  const iframe = document.createElement('iframe');
  iframe.title = '报表打印';
  iframe.setAttribute('aria-hidden', 'true');
  Object.assign(iframe.style, {
    border: '0',
    height: '1px',
    left: '-10000px',
    position: 'fixed',
    top: '0',
    width: '1px',
  });
  document.body.append(iframe);

  const frameWindow = iframe.contentWindow;
  const frameDocument = iframe.contentDocument;
  if (frameWindow === null || frameDocument === null) {
    iframe.remove();
    throw new Error('浏览器未能创建打印页面。');
  }

  let cleanupTimer = 0;
  const cleanup = () => {
    window.clearTimeout(cleanupTimer);
    iframe.remove();
  };

  try {
    frameDocument.open();
    frameDocument.write(printable.html);
    frameDocument.close();
    await waitForPrintResources(frameDocument);

    frameWindow.addEventListener('afterprint', cleanup, { once: true });
    cleanupTimer = window.setTimeout(cleanup, printFrameCleanupDelay);
    frameWindow.focus();
    frameWindow.print();
    return printable.orientation;
  } catch (error) {
    cleanup();
    throw error;
  }
}

function buildPrintCss(
  orientation: PrintOrientation,
  sourceWidth: number,
  scale: number,
  showGridlines: boolean,
): string {
  const defaultCellBorder = showGridlines ? '0.5pt solid #d1d5db' : '0';
  return `
    @page { size: A4 ${orientation}; margin: ${pageMarginMm}mm; }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; background: #fff; color: #111827; }
    body { font-family: Arial, "Microsoft YaHei", sans-serif; }
    .hd-report-print { width:${sourceWidth}px; zoom:${scale.toFixed(6)}; }
    .hd-report-print table { width: 100%; border-collapse: collapse; table-layout: fixed; }
    .hd-report-print tr { break-inside: avoid; page-break-inside: avoid; }
    .hd-report-print td {
      min-width: 0;
      padding: 2px 4px;
      overflow: hidden;
      border: ${defaultCellBorder};
      background-clip: padding-box;
      vertical-align: middle;
    }
    .hd-report-print td > div { min-width: 0; overflow: hidden; text-overflow: clip; }
    .hd-report-print-hidden-row { display: none; }
    @media print {
      html, body { width: auto; }
      .hd-report-print { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
      .hd-report-print td { overflow: visible; }
      .hd-report-print td > div {
        white-space: pre-wrap !important;
        overflow-wrap: anywhere;
        overflow: visible;
      }
    }
  `;
}

function printableCellCss(style?: PrintableCellStyle): string {
  if (style === undefined) return '';
  const declarations: string[] = [];
  const background = safeColor(style.background);
  const color = safeColor(style.color);
  const fontFamily = safeFontFamily(style.fontFamily);
  if (background !== undefined) declarations.push(`background-color:${background}`);
  if (color !== undefined) declarations.push(`color:${color}`);
  if (fontFamily !== undefined) declarations.push(`font-family:${fontFamily}`);
  if (Number.isFinite(style.fontSizePt)) {
    declarations.push(`font-size:${Math.min(72, Math.max(6, style.fontSizePt!))}pt`);
  }
  if (style.bold) declarations.push('font-weight:700');
  if (style.italic) declarations.push('font-style:italic');
  const decorations = [
    style.underline ? 'underline' : '',
    style.strikethrough ? 'line-through' : '',
  ].filter(Boolean);
  if (decorations.length > 0) declarations.push(`text-decoration:${decorations.join(' ')}`);
  declarations.push(`text-align:${safeHorizontalAlignment(style.horizontal)}`);
  declarations.push(`vertical-align:${safeVerticalAlignment(style.vertical)}`);
  declarations.push(style.wrap
    ? 'white-space:pre-wrap;overflow-wrap:anywhere'
    : 'white-space:pre');

  for (const edge of ['top', 'right', 'bottom', 'left'] as const) {
    const border = style.borders?.[edge];
    if (border === undefined || !Number.isFinite(border.widthPt)) continue;
    const borderColor = safeColor(border.color) ?? '#111827';
    const width = Math.min(4, Math.max(0.25, border.widthPt));
    declarations.push(`border-${edge}:${width}pt ${border.style} ${borderColor}`);
  }
  return declarations.join(';');
}

function indexMerges(
  sheet: PrintableSheet,
  rowCount: number,
  columnCount: number,
): { covered: Set<string>; starts: Map<string, PrintableMerge> } {
  const covered = new Set<string>();
  const starts = new Map<string, PrintableMerge>();
  const endRow = sheet.startRow + rowCount - 1;
  const endColumn = sheet.startColumn + columnCount - 1;

  for (const source of sheet.merges) {
    if (
      !validCoordinate(source.startRow)
      || !validCoordinate(source.endRow)
      || !validCoordinate(source.startColumn)
      || !validCoordinate(source.endColumn)
      || source.startRow > source.endRow
      || source.startColumn > source.endColumn
      || source.startRow < sheet.startRow
      || source.startColumn < sheet.startColumn
      || source.endRow > endRow
      || source.endColumn > endColumn
    ) {
      continue;
    }

    const merge: PrintableMerge = {
      startRow: source.startRow - sheet.startRow,
      endRow: source.endRow - sheet.startRow,
      startColumn: source.startColumn - sheet.startColumn,
      endColumn: source.endColumn - sheet.startColumn,
    };
    const start = coordinateKey(merge.startRow, merge.startColumn);
    starts.set(start, merge);
    for (let row = merge.startRow; row <= merge.endRow; row += 1) {
      for (let column = merge.startColumn; column <= merge.endColumn; column += 1) {
        const key = coordinateKey(row, column);
        if (key !== start) covered.add(key);
      }
    }
  }
  return { covered, starts };
}

async function waitForPrintResources(frameDocument: Document): Promise<void> {
  const images = Array.from(frameDocument.images);
  await Promise.all(images.map((image) => {
    if (image.complete) return Promise.resolve();
    return new Promise<void>((resolve) => {
      image.addEventListener('load', () => resolve(), { once: true });
      image.addEventListener('error', () => resolve(), { once: true });
    });
  }));
  await frameDocument.fonts?.ready;
}

function safeHorizontalAlignment(value?: string): string {
  const normalized = value?.toLowerCase();
  if (normalized === 'center' || normalized === '2') return 'center';
  if (normalized === 'right' || normalized === '3') return 'right';
  if (normalized === 'justify' || normalized === 'justified' || normalized === '4') return 'justify';
  return 'left';
}

function safeVerticalAlignment(value?: string): string {
  const normalized = value?.toLowerCase();
  if (normalized === 'top' || normalized === '1') return 'top';
  if (normalized === 'bottom' || normalized === '3') return 'bottom';
  return 'middle';
}

function safeColor(value?: string): string | undefined {
  const normalized = value?.trim();
  return normalized !== undefined
    && /^(?:#[0-9a-f]{3,8}|rgba?\([\d.%\s,]+\))$/i.test(normalized)
    ? normalized
    : undefined;
}

function safeFontFamily(value?: string): string | undefined {
  const normalized = value?.trim().replace(/["'`;<>\\]/g, '').slice(0, 80);
  return normalized === undefined || normalized.length === 0 ? undefined : normalized;
}

function finiteDimension(
  value: number | undefined,
  fallback: number,
  minimum: number,
  maximum: number,
): number {
  return Number.isFinite(value)
    ? Math.min(maximum, Math.max(minimum, value!))
    : fallback;
}

function readFiniteNumber(value: unknown): number | undefined {
  if (typeof value === 'number') return Number.isFinite(value) ? value : undefined;
  if (typeof value !== 'string' || value.trim().length === 0) return undefined;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function readBooleanFlag(value: unknown): boolean | undefined {
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return Number.isFinite(value) ? value !== 0 : undefined;
  if (typeof value !== 'string') return undefined;
  const normalized = value.trim().toLowerCase();
  if (normalized === 'true' || normalized === '1') return true;
  if (normalized === 'false' || normalized === '0') return false;
  return undefined;
}

function readNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value : undefined;
}

function validCoordinate(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 0;
}

function coordinateKey(row: number, column: number): string {
  return `${row}:${column}`;
}

function escapeHtml(value: string): string {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}
