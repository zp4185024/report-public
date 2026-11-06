import { LocaleType } from '@univerjs/presets';
import { describe, expect, it } from 'vitest';
import {
  buildInitialParameterValues,
  evaluateDefaultExpression,
  initialParameterValue,
  missingRequiredParameterCodes,
  parseReportPackageText,
} from './reportPackageWizard';
import type { ReportPackage } from './types';

/** 构造一份字段齐备的最小报表包，作为各用例的公共输入。 */
function samplePackage(): ReportPackage {
  return {
    packageKind: 'hd-report-center.report-package',
    packageVersion: 1,
    exportedAt: '2026-11-02T10:00:00+08:00',
    sourceDataSourceCode: 'report_center',
    sourceDataSourceType: 'SqlServer',
    // 阶段：报表节。预览与导出设置取常规默认值，覆盖 Full 模式的完整字段。
    report: {
      code: 'dept-workload',
      name: '科室工作量统计',
      sheetName: '科室工作量',
      datasetCode: 'detail',
      previewSettings: { mode: 'Full', pageSize: 20, showExactTotal: false },
      exportSettings: {
        defaultMode: 'Normal',
        normalMaxRows: 20_000,
        normalMaxCells: 500_000,
        normalTimeoutSeconds: 60,
        largeExportEnabled: true,
        rowsPerSheet: 1_000_000,
        fileRetentionHours: 24,
      },
      fieldPresentations: [],
      isEnabled: true,
    },
    // 阶段：参数节。一个必填月份表达式参数加一个可选下拉参数，覆盖默认值的两类来源。
    parameters: [
      {
        code: 'startDate',
        displayName: '开始月份',
        dataType: 'month',
        controlType: 'month',
        sourceType: 'Form',
        sourceCode: null,
        sourceFieldCode: null,
        urlKey: null,
        defaultExpression: 'startofmonth(today())',
        fixedValue: null,
        options: null,
        isVisible: true,
        isReadOnly: false,
        isRequired: true,
        allowUrlPrefill: false,
        displayOrder: 1,
      },
      {
        code: 'deptCode',
        displayName: '科室',
        dataType: 'string',
        controlType: 'select',
        sourceType: 'Form',
        sourceCode: null,
        sourceFieldCode: null,
        urlKey: null,
        defaultExpression: null,
        fixedValue: null,
        options: [
          { value: 'D01', label: '内科' },
          { value: 'D02', label: '外科' },
        ],
        isVisible: true,
        isReadOnly: false,
        isRequired: false,
        allowUrlPrefill: true,
        displayOrder: 2,
      },
    ],
    // 阶段：模板节。空快照与空绑定即可满足前端解析，后端绑定校验由服务端用例负责。
    template: {
      snapshot: {
        id: 'workbook',
        name: '模板',
        appVersion: '1.0.0',
        locale: LocaleType.ZH_CN,
        styles: {},
        sheetOrder: [],
        sheets: {},
      },
      binding: { version: 1, parameterBindings: [], detailBinding: null },
      revision: 4,
    },
    dataset: {
      code: 'detail',
      name: '科室工作量明细',
      queryText: 'SELECT 1 AS value',
      fieldSchema: { variables: [], fields: [{ code: 'value', displayName: '数值', dataType: 'int' }] },
      timeoutSeconds: 30,
      maxRows: 20_000,
      isEnabled: true,
    },
  };
}

describe('报表包上传解析', () => {
  /** 验证结构齐备的包文本能解析出完整对象供后续接口使用。 */
  it('parses a well-formed package text', () => {
    const result = parseReportPackageText(JSON.stringify(samplePackage()));
    expect(result).toEqual({ package: samplePackage() });
  });

  /** 验证非 JSON 文本和错误类型标识会被就地拦下，不发起服务端请求。 */
  it('rejects broken json and wrong package kind locally', () => {
    expect(parseReportPackageText('not-json')).toEqual({ error: expect.stringContaining('JSON') });
    const wrongKind = JSON.stringify({ ...samplePackage(), packageKind: 'other.tool.package' });
    expect(parseReportPackageText(wrongKind)).toEqual({ error: expect.stringContaining('文件类型标识不匹配') });
  });

  /** 验证高于当前支持的包版本会被拒绝，提示先升级而不是静默导入。 */
  it('rejects newer package versions', () => {
    const newer = JSON.stringify({ ...samplePackage(), packageVersion: 2 });
    expect(parseReportPackageText(newer)).toEqual({ error: expect.stringContaining('高于当前支持的版本') });
  });

  /** 验证缺少必需节的包文件给出明确损坏提示。 */
  it('rejects packages missing required sections', () => {
    const incomplete = JSON.stringify({ ...samplePackage(), template: undefined });
    expect(parseReportPackageText(incomplete)).toEqual({ error: expect.stringContaining('缺少必需') });
  });

  /** 验证旧导出端直存接口响应产生的 Furion 信封包装文件会被自动解包并带标记，供页面提示升级导出端。 */
  it('unwraps a Furion envelope-wrapped package and flags it', () => {
    const envelopeText = JSON.stringify({
      code: 200,
      type: 'success',
      message: '',
      result: samplePackage(),
    });
    expect(parseReportPackageText(envelopeText))
      .toEqual({ package: samplePackage(), unwrappedFromEnvelope: true });
  });

  /** 验证信封 result 内类型标识不匹配的文件仍按原格式报错，并指出内层实际标识而不是 undefined。 */
  it('still rejects an envelope whose inner package kind mismatches', () => {
    const envelopeText = JSON.stringify({
      code: 200,
      type: 'success',
      message: '',
      result: { ...samplePackage(), packageKind: 'other.tool.package' },
    });
    expect(parseReportPackageText(envelopeText)).toEqual({
      error: expect.stringContaining(
        '文件类型标识不匹配：期望 hd-report-center.report-package，实际为 other.tool.package',
      ),
    });
  });

  /** 验证顶层已带错误 packageKind 的普通文件不会被误当信封解包，维持原有报错文案。 */
  it('keeps rejecting a plain file whose package kind mismatches', () => {
    const wrongKind = JSON.stringify({ ...samplePackage(), packageKind: 'other.tool.package' });
    expect(parseReportPackageText(wrongKind))
      .toEqual({ error: expect.stringContaining('实际为 other.tool.package') });
  });

  /** 验证顶层无类型标识也无 result 的文件维持 undefined 报错文案，不掩盖真实原因。 */
  it('keeps the undefined-kind error for files without any package content', () => {
    expect(parseReportPackageText(JSON.stringify({ code: 200, type: 'success' })))
      .toEqual({ error: expect.stringContaining('实际为 undefined') });
  });
});

describe('默认表达式求值', () => {
  const now = new Date(2026, 10, 2, 15, 40, 5);

  /** 验证常用日期表达式按后端同一词表和格式输出。 */
  it('evaluates the shared expression vocabulary like the backend', () => {
    expect(evaluateDefaultExpression('today()', 'date', now)).toBe('2026-11-02');
    expect(evaluateDefaultExpression('startofday(now())', 'datetime', now)).toBe('2026-11-02 00:00:00');
    expect(evaluateDefaultExpression('now()', 'datetime', now)).toBe('2026-11-02 15:40:05');
    expect(evaluateDefaultExpression('startofmonth(today())', 'month', now)).toBe('2026-11');
    expect(evaluateDefaultExpression('endofmonth(today())', 'datetime', now)).toBe('2026-11-30 23:59:59');
    expect(evaluateDefaultExpression('startofyear(today())', 'year', now)).toBe('2026');
    expect(evaluateDefaultExpression('endofday(yesterday())', 'datetime', now)).toBe('2026-11-01 23:59:59');
  });

  /** 验证空表达式返回 null、未知表达式返回 null 交由必填校验兜底。 */
  it('returns null for empty or unknown expressions', () => {
    expect(evaluateDefaultExpression(null, 'date', now)).toBeNull();
    expect(evaluateDefaultExpression('  ', 'date', now)).toBeNull();
    expect(evaluateDefaultExpression('nextquarter(today())', 'date', now)).toBeNull();
  });
});

describe('试跑参数初始值', () => {
  const now = new Date(2026, 10, 2, 9, 0, 0);
  const requestPackage = samplePackage();

  /** 验证初始值按固定值、表达式、选项首项的顺序回退。 */
  it('falls back from fixed value to expression to first option', () => {
    expect(initialParameterValue(requestPackage.parameters[0], now)).toBe('2026-11');
    expect(initialParameterValue(requestPackage.parameters[1], now)).toBe('D01');

    const fixed = { ...requestPackage.parameters[1], fixedValue: 'D02', options: null };
    expect(initialParameterValue(fixed, now)).toBe('D02');

    const empty = {
      ...requestPackage.parameters[1],
      fixedValue: null,
      defaultExpression: null,
      options: null,
    };
    expect(initialParameterValue(empty, now)).toBe('');
  });

  /** 验证整包初始值覆盖全部参数并供缺值必填检查复用。 */
  it('builds values for every parameter and reports missing required ones', () => {
    const values = buildInitialParameterValues(requestPackage, now);
    expect(values).toEqual({ startDate: '2026-11', deptCode: 'D01' });
    expect(missingRequiredParameterCodes(requestPackage, values)).toEqual([]);

    const missing = { ...values, startDate: '' };
    expect(missingRequiredParameterCodes(requestPackage, missing)).toEqual(['startDate']);
  });
});
