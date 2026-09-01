import { describe, expect, it } from 'vitest';
import {
  applyPrintableRichTextStyle,
  buildPrintableSheetDocument,
  type PrintableSheet,
} from './workbookPrint';

/**
 * 验证 printable Sheet 场景下的预期行为，并防止相关逻辑回归。
 * @param overrides - overrides 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function printableSheet(overrides: Partial<PrintableSheet> = {}): PrintableSheet {
  return {
    columnWidths: [120, 80],
    merges: [],
    name: '门诊日报',
    rowHeights: [30, 24],
    startColumn: 0,
    startRow: 0,
    styles: [[], []],
    values: [
      ['标题', ''],
      ['科室', '方便门诊'],
    ],
    ...overrides,
  };
}

describe('工作表浏览器打印', () => {
  it('把旧模板富文本中的字符串字号和加粗标记转换为打印样式', () => {
    const style = applyPrintableRichTextStyle(
      { bold: false, fontFamily: 'Microsoft YaHei', fontSizePt: 11 },
      { bl: '1', ff: 'Arial', fs: '16' },
    );

    expect(style.bold).toBe(true);
    expect(style.fontFamily).toBe('Arial');
    expect(style.fontSizePt).toBe(16);
  });

  it('生成带 A4 纸张规则、合并单元格和安全文本的完整打印文档', () => {
    const result = buildPrintableSheetDocument(printableSheet({
      merges: [{ startRow: 0, endRow: 0, startColumn: 0, endColumn: 1 }],
      values: [
        ['门诊 <日报>', ''],
        ['科室', '方便门诊\n一楼'],
      ],
    }));

    expect(result.orientation).toBe('portrait');
    expect(result.html).toContain('@page { size: A4 portrait; margin: 8mm; }');
    expect(result.html).toContain('<td colspan="2"');
    expect(result.html).toContain('门诊 &lt;日报&gt;');
    expect(result.html).toContain('方便门诊<br>一楼');
    expect(result.html).not.toContain('<td></td><td></td>');
  });

  it('打印文档标题优先使用报表名称，而不是工作表名称', () => {
    const result = buildPrintableSheetDocument(printableSheet({
      documentTitle: '医保-锦州市城镇职工医疗保险费用申报表-异地（JNPF迁移）',
    }));

    expect(result.html).toContain('<title>医保-锦州市城镇职工医疗保险费用申报表-异地（JNPF迁移）</title>');
    expect(result.html).not.toContain('<title>门诊日报</title>');
  });

  it('保留可打印的字体、颜色、对齐、换行和边框样式', () => {
    const result = buildPrintableSheetDocument(printableSheet({
      styles: [[{
        background: '#e2e8f0',
        bold: true,
        borders: {
          bottom: { color: '#0f172a', style: 'double', widthPt: 2 },
        },
        color: '#1d4ed8',
        fontFamily: 'Microsoft YaHei',
        fontSizePt: 14,
        horizontal: 'center',
        italic: true,
        underline: true,
        vertical: 'top',
        wrap: true,
      }]],
      values: [['样式', '测试']],
      rowHeights: [32],
    }));

    expect(result.html).toContain('background-color:#e2e8f0');
    expect(result.html).toContain('color:#1d4ed8');
    expect(result.html).toContain('font-family:Microsoft YaHei');
    expect(result.html).toContain('font-weight:700');
    expect(result.html).toContain('font-style:italic');
    expect(result.html).toContain('text-decoration:underline');
    expect(result.html).toContain('text-align:center');
    expect(result.html).toContain('vertical-align:top');
    expect(result.html).toContain('white-space:pre-wrap');
    expect(result.html).toContain('border-bottom:2pt double #0f172a');
  });

  it('关闭工作表网格线后不为打印单元格补充浅灰边框', () => {
    const result = buildPrintableSheetDocument(printableSheet({
      showGridlines: false,
      styles: [[{
        borders: {
          bottom: { color: '#0f172a', style: 'solid', widthPt: 1 },
        },
      }]],
      values: [['无网格线', '打印']],
    }));

    expect(result.html).toContain('border: 0;');
    expect(result.html).not.toContain('border: 0.5pt solid #d1d5db;');
    expect(result.html).toContain('border-bottom:1pt solid #0f172a');
  });

  it('宽表自动使用横向打印，非法合并范围不会吞掉单元格', () => {
    const result = buildPrintableSheetDocument(printableSheet({
      columnWidths: [500, 400],
      merges: [{ startRow: 0, endRow: 8, startColumn: 0, endColumn: 1 }],
      values: [['A', 'B']],
      rowHeights: [24],
    }));

    expect(result.orientation).toBe('landscape');
    expect(result.html).toContain('@page { size: A4 landscape; margin: 8mm; }');
    expect(result.scale).toBeCloseTo(1, 1);
    expect(result.html).toContain('<col style="width:500px">');
    expect(result.html.match(/<td/g)).toHaveLength(2);
  });

  it('超宽表格按原始列宽整体缩放，避免字体不缩放导致内容被裁切', () => {
    const result = buildPrintableSheetDocument(printableSheet({
      columnWidths: [600, 600, 600],
      values: [['字段一', '字段二', '字段三']],
      rowHeights: [24],
    }));

    expect(result.orientation).toBe('landscape');
    expect(result.scale).toBeGreaterThan(0.5);
    expect(result.scale).toBeLessThan(0.7);
    expect(result.html).toContain('width:1800px; zoom:');
    expect(result.html).toContain('<col style="width:600px">');
    expect(result.html).not.toContain('<col style="width:33.3333%">');
  });

  it('打印时让地址和连续长文本自动换行，不被单元格裁切', () => {
    const result = buildPrintableSheetDocument(printableSheet({
      styles: [[{ wrap: false }, { wrap: false }]],
      values: [['辽宁省锦州市凌河区长地址', '210703196206152020']],
      rowHeights: [24],
    }));

    expect(result.html).toContain('white-space:pre');
    expect(result.html).toContain('white-space: pre-wrap !important');
    expect(result.html).toContain('overflow-wrap: anywhere');
    expect(result.html).toContain('overflow: visible');
  });

  it('空工作表不会打开打印窗口', () => {
    expect(() => buildPrintableSheetDocument(printableSheet({
      values: [['', '']],
      rowHeights: [24],
    }))).toThrow('当前工作表没有可打印内容');
  });
});
