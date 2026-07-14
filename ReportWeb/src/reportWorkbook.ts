import {
  BooleanNumber,
  HorizontalAlign,
  LocaleType,
  VerticalAlign,
  WrapStrategy,
  type ICellData,
  type IWorkbookData,
} from '@univerjs/presets';
import type { ExecuteResult, ReportDefinition } from './types';

const workbookId = 'report-validation-workbook';
const sheetId = 'report-sheet';
const dataStartRow = 6;

export function buildWorkbookData(
  definition: ReportDefinition,
  result: ExecuteResult,
): IWorkbookData {
  const cellData: Record<number, Record<number, ICellData>> = {};
  const setCell = (row: number, column: number, cell: ICellData) => {
    cellData[row] ??= {};
    cellData[row][column] = cell;
  };

  setCell(0, 0, { v: definition.name, s: 'title' });
  setCell(1, 0, { v: `医院：${result.parameters.hospitalId}`, s: 'meta' });
  setCell(1, 2, { v: `科室：${result.parameters.deptId}`, s: 'meta' });
  setCell(1, 4, { v: `人员：${result.parameters.personId}`, s: 'meta' });
  setCell(
    2,
    0,
    {
      v: `统计期间：${result.parameters.startDate} 至 ${result.parameters.endDate}`,
      s: 'meta',
    },
  );

  definition.columns.forEach((column, columnIndex) => {
    setCell(4, columnIndex, { v: column.header, s: 'header' });
  });

  result.rows.forEach((row, rowIndex) => {
    const sheetRow = dataStartRow + rowIndex;
    const matrixRow = sheetRow - 1;
    setCell(matrixRow, 0, { v: row.sequence, s: 'center' });
    setCell(matrixRow, 1, { v: row.departmentName });
    setCell(matrixRow, 2, { v: row.outpatientCount, s: 'number' });
    setCell(matrixRow, 3, { v: row.dischargeCount, s: 'number' });
    setCell(matrixRow, 4, {
      f: formulaFor(definition, 'totalWorkload', sheetRow, '=C{row}+D{row}'),
      s: 'formula',
    });
    setCell(matrixRow, 5, {
      f: formulaFor(definition, 'averageWorkload', sheetRow, '=AVERAGE(C{row}:D{row})'),
      s: 'formula',
    });
  });

  return {
    id: workbookId,
    rev: 1,
    name: definition.name,
    appVersion: '1.0.0',
    locale: LocaleType.ZH_CN,
    styles: {
      title: {
        ff: 'Microsoft YaHei',
        fs: 16,
        bl: BooleanNumber.TRUE,
        ht: HorizontalAlign.CENTER,
        vt: VerticalAlign.MIDDLE,
      },
      meta: {
        ff: 'Microsoft YaHei',
        fs: 10,
        cl: { rgb: '#344054' },
        vt: VerticalAlign.MIDDLE,
      },
      header: {
        ff: 'Microsoft YaHei',
        fs: 10,
        bl: BooleanNumber.TRUE,
        bg: { rgb: '#e9eef5' },
        cl: { rgb: '#18212b' },
        ht: HorizontalAlign.CENTER,
        vt: VerticalAlign.MIDDLE,
        tb: WrapStrategy.WRAP,
      },
      center: {
        ht: HorizontalAlign.CENTER,
        vt: VerticalAlign.MIDDLE,
      },
      number: {
        ht: HorizontalAlign.RIGHT,
        vt: VerticalAlign.MIDDLE,
        n: { pattern: '#,##0' },
      },
      formula: {
        bg: { rgb: '#eef8f1' },
        ht: HorizontalAlign.RIGHT,
        vt: VerticalAlign.MIDDLE,
        n: { pattern: '#,##0.00' },
      },
    },
    sheetOrder: [sheetId],
    sheets: {
      [sheetId]: {
        id: sheetId,
        name: definition.sheetName,
        tabColor: '#2563eb',
        hidden: BooleanNumber.FALSE,
        freeze: {
          xSplit: 0,
          ySplit: 5,
          startRow: 5,
          startColumn: 0,
        },
        rowCount: Math.max(50, dataStartRow + definition.preview.pageSize + 5),
        columnCount: Math.max(12, definition.columns.length),
        zoomRatio: 1,
        scrollTop: 0,
        scrollLeft: 0,
        defaultColumnWidth: 100,
        defaultRowHeight: 26,
        mergeData: [
          { startRow: 0, endRow: 0, startColumn: 0, endColumn: 5 },
          { startRow: 1, endRow: 1, startColumn: 0, endColumn: 1 },
          { startRow: 1, endRow: 1, startColumn: 2, endColumn: 3 },
          { startRow: 1, endRow: 1, startColumn: 4, endColumn: 5 },
          { startRow: 2, endRow: 2, startColumn: 0, endColumn: 5 },
        ],
        cellData,
        rowData: {
          0: { h: 42 },
          1: { h: 30 },
          2: { h: 30 },
          3: { h: 10 },
          4: { h: 34 },
        },
        columnData: Object.fromEntries(
          definition.columns.map((column, index) => [index, { w: column.width }]),
        ),
        rowHeader: { width: 46, hidden: BooleanNumber.FALSE },
        columnHeader: { height: 24, hidden: BooleanNumber.FALSE },
        showGridlines: BooleanNumber.TRUE,
        rightToLeft: BooleanNumber.FALSE,
      },
    },
  };
}

export function workbookDataToMatrix(workbook: IWorkbookData): ICellData[][] {
  const sheet = workbook.sheets[sheetId];
  const cellData = sheet.cellData ?? {};
  const maxRow = Math.max(0, ...Object.keys(cellData).map(Number));
  const matrix: ICellData[][] = [];

  for (let row = 0; row <= maxRow; row++) {
    matrix[row] = Array.from({ length: 6 }, (_, column) => cellData[row]?.[column] ?? {});
  }

  return matrix;
}

export const reportWorkbookIds = { workbookId, sheetId } as const;

function formulaFor(
  definition: ReportDefinition,
  columnCode: string,
  row: number,
  fallback: string,
): string {
  const template = definition.columns.find((column) => column.code === columnCode)?.formulaTemplate;
  return (template || fallback).replaceAll('{row}', String(row));
}
