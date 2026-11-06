import { describe, expect, it } from 'vitest';
import {
  buildDefaultTableMappings,
  buildInitialMappings,
  buildTableSelectSql,
  buildTryRunPairColumns,
  collectTryRunKeys,
  converterLabel,
  converterSummary,
  createConverterStep,
  CRON_PREVIEW_UNAVAILABLE_HINT,
  defaultTargetColumn,
  formatColumnWithDescription,
  conflictPolicyLabel,
  parseSyncPackageText,
  previewCronExpression,
  scheduleCronForSave,
  scheduleSummary,
  scheduleTypeLabel,
  SCHEDULE_TYPE_OPTIONS,
  suggestTargetFieldName,
  targetColumnsFromMappings,
  targetColumnsFromMetadata,
  targetTypeLabel,
  targetDbTypeToType,
  tryRunCellDisplay,
  TRY_RUN_CELL_MAX_CHARS,
  validateCronScheduleInput,
  validateSyncMappings,
  type SyncPackageParseResult,
} from './syncTaskDesign';
import type { DataRowValue, DataSyncPackage, DataSyncSampleRow, SyncColumnMapping, SyncConverterStep } from './types';

/**
 * 按组件渲染语义读取样例值：键为 null 时返回 undefined，配合 tryRunCellDisplay 即“—”占位。
 * @param values - 样例行的源/目标值映射。
 * @param key - 配对列的源字段键或目标列键，可为 null。
 * @returns 取到的值；键缺失或为 null 时为 undefined。
 */
function valueAt(values: Record<string, DataRowValue>, key: string | null): DataRowValue | undefined {
  return key === null ? undefined : values[key];
}

/**
 * 从解析结果中取出错误分支的文本；解析成功时直接断言失败，保持用例意图明确。
 * @param parsed - parseSyncPackageText 的返回值。
 * @returns 错误分支携带的中文提示。
 */
function parseError(parsed: SyncPackageParseResult): string {
  if (!('error' in parsed)) throw new Error('非法同步包应解析失败');
  return parsed.error;
}

/**
 * 构造源列集合：三个字段覆盖大小写差异场景，供预匹配与默认映射用例复用。
 * @returns 固定的源列列表。
 */
function sampleSourceColumns() {
  return [
    { name: 'cardNo', dataType: 'string' },
    { name: 'visitDate', dataType: 'datetime' },
    { name: 'deptCode', dataType: 'string' },
  ];
}

/**
 * 构造目标列集合：CardNo/VisitDate/DeptCode 与源列同名（仅大小写不同），供预匹配用例复用。
 * @returns 固定的目标列列表。
 */
function sampleTargetColumns() {
  return [
    { name: 'CardNo', targetType: 'string', length: 16, nullable: true },
    { name: 'VisitDate', targetType: 'datetime', length: null, nullable: false },
    { name: 'DeptCode', targetType: 'string', length: null, nullable: true },
  ];
}

/**
 * 构造一个最小映射，减少各用例重复字段的书写量。
 * @param overrides - 覆盖默认值的映射字段。
 * @returns 合并后的映射对象。
 */
function mapping(overrides: Partial<SyncColumnMapping>): SyncColumnMapping {
  return {
    sourceField: 'cardNo',
    targetField: 'CARD_NO',
    targetType: 'string',
    length: null,
    nullable: true,
    converters: [],
    ...overrides,
  };
}

describe('syncTaskDesign initial mappings', () => {
  /** 验证同名（忽略大小写）源列与目标列生成初始映射，且沿用目标列的类型声明。 */
  it('pre-matches source columns to same-named target columns case-insensitively', () => {
    const mappings = buildInitialMappings(sampleSourceColumns(), sampleTargetColumns());

    expect(mappings).toHaveLength(3);
    expect(mappings[0]).toEqual({
      sourceField: 'cardNo',
      targetField: 'CardNo',
      targetType: 'string',
      length: 16,
      nullable: true,
      converters: [],
    });
    expect(mappings[1]).toEqual({
      sourceField: 'visitDate',
      targetField: 'VisitDate',
      targetType: 'datetime',
      length: null,
      nullable: false,
      converters: [],
    });
    expect(mappings[2].targetField).toBe('DeptCode');
  });

  /** 验证已有映射保留在前，其占用的源列与目标列不会再被预匹配。 */
  it('keeps existing mappings and skips sources and targets they already occupy', () => {
    const existing = [mapping({
      sourceField: 'deptCode',
      targetField: 'cardno',
      targetType: 'string',
      length: null,
      nullable: true,
    })];
    const mappings = buildInitialMappings(sampleSourceColumns(), sampleTargetColumns(), existing);

    // cardno（CardNo）已被 deptCode 占用，cardNo 不再重复映射；visitDate 仍可同名匹配。
    expect(mappings).toHaveLength(2);
    expect(mappings[0]).toEqual(existing[0]);
    expect(mappings[1].sourceField).toBe('visitDate');
    expect(mappings[1].targetField).toBe('VisitDate');
  });

  /** 验证没有同名目标列的源列不会被强行映射（保留“(不同步)”语义）。 */
  it('leaves source columns without a same-named target unmapped', () => {
    const mappings = buildInitialMappings(
      [{ name: 'remark', dataType: 'string' }],
      sampleTargetColumns(),
    );
    expect(mappings).toHaveLength(0);
  });

  /** 验证目标列集合为空（未选目标表）时不生成任何映射。 */
  it('matches nothing when the target column list is empty', () => {
    expect(buildInitialMappings(sampleSourceColumns(), [])).toHaveLength(0);
  });
});

describe('syncTaskDesign table select sql', () => {
  /** 验证四种方言的标识符引用：SqlServer 中括号、MySql 反引号、Oracle/PG 双引号。 */
  it('generates dialect quoted SELECT for each supported database', () => {
    expect(buildTableSelectSql('SqlServer', 'RcVisit')).toBe('SELECT * FROM [RcVisit]');
    expect(buildTableSelectSql('MySql', 'rc_visit')).toBe('SELECT * FROM `rc_visit`');
    expect(buildTableSelectSql('Oracle', 'RC_VISIT')).toBe('SELECT * FROM "RC_VISIT"');
    expect(buildTableSelectSql('PostgreSQL', 'rc_visit')).toBe('SELECT * FROM "rc_visit"');
  });

  /** 验证未知方言退化为不引用的原样表名，空表名返回空串。 */
  it('falls back to unquoted name for unknown dialect and empty table', () => {
    expect(buildTableSelectSql('SQLite', 'rc_visit')).toBe('SELECT * FROM rc_visit');
    expect(buildTableSelectSql('Oracle', '  ')).toBe('');
  });
});

describe('syncTaskDesign target metadata mapping', () => {
  /** 验证数据库列类型文本收敛到目标类型白名单：复合词按时间/布尔/整数/小数优先。 */
  it('maps database column types to target type whitelist', () => {
    expect(targetDbTypeToType('datetime2')).toBe('datetime');
    expect(targetDbTypeToType('TIMESTAMP')).toBe('datetime');
    expect(targetDbTypeToType('bit')).toBe('bool');
    expect(targetDbTypeToType('bigint')).toBe('number');
    expect(targetDbTypeToType('NUMBER')).toBe('decimal');
    expect(targetDbTypeToType('nvarchar')).toBe('string');
    expect(targetDbTypeToType('')).toBe('string');
  });

  /** 验证元数据列清单转换为映射表格的目标列集合（类型推断 + 可空/长度/注释透传）。 */
  it('builds target columns from metadata columns', () => {
    expect(targetColumnsFromMetadata([
      { name: 'CARD_NO', dataType: 'nvarchar', nullable: true, length: 16, description: '卡号' },
      { name: 'VISIT_DATE', dataType: 'datetime', nullable: false, length: null, description: null },
    ])).toEqual([
      { name: 'CARD_NO', targetType: 'string', length: 16, nullable: true, description: '卡号' },
      { name: 'VISIT_DATE', targetType: 'datetime', length: null, nullable: false, description: null },
    ]);
  });

  /** 验证列注释展示文本拼接：有注释拼“列名 · 注释”，无注释（null/空白）只显示列名。 */
  it('formats column labels with optional description', () => {
    expect(formatColumnWithDescription('CARD_NO', '卡号')).toBe('CARD_NO · 卡号');
    expect(formatColumnWithDescription('CARD_NO', null)).toBe('CARD_NO');
    expect(formatColumnWithDescription('CARD_NO', '   ')).toBe('CARD_NO');
    expect(formatColumnWithDescription('CARD_NO', ' 卡号 ')).toBe('CARD_NO · 卡号');
  });

  /** 验证新建表模式的默认映射：1:1 同名、类型按源推断、转换链为空（直通）。 */
  it('builds one-to-one default mappings for create-table mode', () => {
    const mappings = buildDefaultTableMappings(sampleSourceColumns());
    expect(mappings).toHaveLength(3);
    expect(mappings[0]).toEqual({
      sourceField: 'cardNo',
      targetField: 'cardNo',
      targetType: 'string',
      length: null,
      nullable: true,
      converters: [],
    });
    expect(mappings[1].targetType).toBe('datetime');
  });
});

describe('syncTaskDesign target columns', () => {
  /** 验证目标列可从映射派生且忽略大小写去重。 */
  it('derives unique target columns from mappings in order', () => {
    const columns = targetColumnsFromMappings([
      mapping({ targetField: 'CARD_NO', targetType: 'string', length: 16 }),
      mapping({ sourceField: 'visitDate', targetField: 'card_no', targetType: 'string', length: 16 }),
      mapping({ sourceField: 'deptCode', targetField: 'DEPT_CODE', targetType: 'string', length: null }),
    ]);

    expect(columns).toEqual([
      { name: 'CARD_NO', targetType: 'string', length: 16, nullable: true },
      { name: 'DEPT_CODE', targetType: 'string', length: null, nullable: true },
    ]);
  });

  /** 验证新映射的目标列名建议会跳过现有目标列并追加序号。 */
  it('suggests a deduplicated target field name', () => {
    expect(suggestTargetFieldName('Remark', [])).toBe('Remark');
    expect(suggestTargetFieldName('CardNo', sampleTargetColumns())).toBe('CardNo_2');
    expect(suggestTargetFieldName('cardno', [
      ...sampleTargetColumns(),
      { name: 'CardNo_2', targetType: 'string', length: null, nullable: true },
    ])).toBe('cardno_3');
  });

  /** 验证默认目标列声明为文本、不限长、允许空。 */
  it('creates the default target column declaration', () => {
    expect(defaultTargetColumn('cardNo')).toEqual({
      name: 'cardNo',
      targetType: 'string',
      length: null,
      nullable: true,
    });
  });
});

describe('syncTaskDesign validation', () => {
  /** 验证完整且参数齐备的设计不产生任何错误；转换链为空按“直通”语义合法。 */
  it('accepts a fully mapped design without converter issues', () => {
    const mappings = buildInitialMappings(sampleSourceColumns(), sampleTargetColumns());
    const errors = validateSyncMappings(
      sampleSourceColumns(),
      targetColumnsFromMappings(mappings),
      mappings,
    );
    expect(errors).toEqual([]);
  });

  /** 验证“(不同步)”是合法状态：未映射的源列与未写入的目标列都不再报错。 */
  it('allows skipped source columns and partially written targets', () => {
    const mappings = [mapping({ sourceField: 'cardNo', targetField: 'CARD_NO' })];
    const errors = validateSyncMappings(
      sampleSourceColumns(),
      sampleTargetColumns(),
      mappings,
    );
    expect(errors).toEqual([]);
  });

  /** 验证重复映射：同一目标列被写两次、同一源列连到两个目标列。 */
  it('reports duplicated target writes and fan-out sources', () => {
    const mappings = [
      mapping({ sourceField: 'cardNo', targetField: 'CARD_NO' }),
      mapping({ sourceField: 'visitDate', targetField: 'card_no' }),
      mapping({ sourceField: 'cardNo', targetField: 'DEPT_CODE' }),
    ];
    const errors = validateSyncMappings(
      sampleSourceColumns(),
      targetColumnsFromMappings(mappings),
      mappings,
    );

    expect(errors).toContain('目标列 card_no 被多条映射重复写入。');
    expect(errors).toContain('源列 cardNo 被映射到多个目标列。');
  });

  /** 验证映射引用源数据集中不存在的字段时被拒绝。 */
  it('reports mappings referencing unknown source fields', () => {
    const mappings = [mapping({ sourceField: 'ghostField', targetField: 'CARD_NO' })];
    const errors = validateSyncMappings(
      sampleSourceColumns(),
      targetColumnsFromMappings(mappings),
      mappings,
    );
    expect(errors).toContain('映射引用了源数据集中不存在的字段 ghostField。');
  });

  /** 验证目标列形状：类型不在白名单、长度为负、名称超长。 */
  it('reports invalid target column shapes', () => {
    const mappings = [
      mapping({ targetField: 'CARD_NO', targetType: 'money', length: -1 }),
      mapping({ sourceField: 'visitDate', targetField: 'X'.repeat(129) }),
    ];
    const errors = validateSyncMappings(
      sampleSourceColumns(),
      targetColumnsFromMappings(mappings),
      mappings,
    );

    expect(errors).toContain('目标列 CARD_NO 的类型 money 不在支持范围内。');
    expect(errors).toContain('目标列 CARD_NO 的长度不能为负数。');
    expect(errors.some((item) => item.includes('名称超过 128 个字符'))).toBe(true);
  });

  /** 验证转换链参数校验覆盖各转换器的必填参数与数值范围。 */
  it('validates converter chain arguments against backend compile rules', () => {
    const mappings = [mapping({
      converters: [
        { type: 'nullToDefault', args: { value: '' } },
        { type: 'truncate', args: { length: '0' } },
        { type: 'toDecimal', args: { scale: '33' } },
        { type: 'dictionary', args: { items: '[1,2]' } },
        { type: 'expression', args: { expression: '' } },
        { type: 'required', args: { strategy: 'useDefault', defaultValue: '' } },
        { type: 'unknownX', args: null },
      ],
    })];
    const errors = validateSyncMappings(
      sampleSourceColumns(),
      targetColumnsFromMappings(mappings),
      mappings,
    );

    expect(errors).toContain('目标列 CARD_NO：第 1 个转换器（空值给默认）缺少必填参数 value。');
    expect(errors).toContain('目标列 CARD_NO：第 2 个转换器（截断长度）的 length 必须是正整数。');
    expect(errors).toContain('目标列 CARD_NO：第 3 个转换器（转小数）的 scale 必须是 0 到 28 的整数。');
    expect(errors).toContain('目标列 CARD_NO：第 4 个转换器（字典映射）的 items 必须是 JSON 对象文本，如 {"1":"门诊"}。');
    expect(errors).toContain('目标列 CARD_NO：第 5 个转换器（表达式）缺少必填参数 expression。');
    expect(errors).toContain('目标列 CARD_NO：第 6 个转换器（必填校验）采用 useDefault 策略时必须提供 defaultValue。');
    expect(errors.some((item) => item.includes('不是受支持的内置转换器'))).toBe(true);
  });
});

describe('syncTaskDesign labels and converter defaults', () => {
  /** 验证目标类型、冲突策略与调度的中文名映射表。 */
  it('maps type, conflict policy and schedule codes to Chinese labels', () => {
    expect(targetTypeLabel('string')).toBe('文本');
    expect(targetTypeLabel('datetime')).toBe('日期时间');
    expect(targetTypeLabel('xml')).toBe('xml');
    expect(conflictPolicyLabel('TruncateFirst')).toBe('先清空目标表再写入');
    expect(conflictPolicyLabel('Append')).toBe('追加写入');
    expect(scheduleTypeLabel('Manual')).toBe('手动执行');
    expect(scheduleTypeLabel('Daily')).toBe('每天定时');
    expect(scheduleTypeLabel('Interval')).toBe('固定间隔');
    expect(converterLabel('dictionary')).toBe('字典映射');
  });

  /** 验证按目录创建转换器步骤时携带默认参数。 */
  it('creates converter steps with catalog default arguments', () => {
    expect(createConverterStep('toDecimal').args).toEqual({ scale: '2' });
    expect(createConverterStep('toDate').args).toEqual({ format: 'yyyy-MM-dd' });
    expect(createConverterStep('trim').args).toEqual({});
    expect(createConverterStep('trim').type).toBe('trim');
  });

  /** 验证转换器 chip 摘要：无参数只显示名称，有参数展示“键=值”并截断长值。 */
  it('summarizes converter chips with trimmed argument values', () => {
    expect(converterSummary({ type: 'trim', args: null })).toBe('去空白');
    const toDecimal: SyncConverterStep = { type: 'toDecimal', args: { scale: '2' } };
    expect(converterSummary(toDecimal)).toBe('转小数（scale=2）');
    const dictionary: SyncConverterStep = {
      type: 'dictionary',
      args: { items: '{"1":"门诊","2":"住院","3":"体检"}' },
    };
    const summary = converterSummary(dictionary);
    // 超长值按 16 字符截断并以省略号收尾，chip 摘要保持单行可读。
    expect(summary.startsWith('字典映射（items=')).toBe(true);
    expect(summary.endsWith('…）')).toBe(true);
    expect(summary.length).toBeLessThan('字典映射（items=）'.length + 20);
  });
});

describe('syncTaskDesign package parsing', () => {
  /**
   * 构造一份最小同步包 JSON 文本，供各解析用例复用。
   * @param overrides - 覆盖默认值的顶层字段。
   * @returns 序列化后的同步包文本。
   */
  function packageText(overrides: Record<string, unknown> = {}): string {
    const base: Record<string, unknown> = {
      packageKind: 'hd-report-center.sync-package',
      packageVersion: 1,
      exportedAt: '2026-11-03T10:00:00+08:00',
      taskCode: 'his_outpatient_sync',
      sourceDataset: {
        code: 'detail',
        name: '门诊明细',
        dataSourceCode: 'his_db',
        dataSourceType: 'SqlServer',
        queryText: 'SELECT cardNo FROM registration',
        fieldSchema: {},
        timeoutSeconds: 30,
        maxRows: 20000,
        isEnabled: true,
      },
      targetDataSourceCode: 'target_db',
      targetDataSourceType: 'SqlServer',
      targetTable: 'ods_registration',
      conflictPolicy: 'TruncateFirst',
      scheduleType: 'Manual',
      scheduleTime: null,
      intervalMinutes: 0,
      mappings: [],
    };
    return JSON.stringify({ ...base, ...overrides });
  }

  /** 验证合法同步包文本解析为包对象。 */
  it('parses a valid sync package text', () => {
    const parsed = parseSyncPackageText(packageText());
    if (!('package' in parsed)) throw new Error('合法同步包应解析成功');
    expect(parsed.package.taskCode).toBe('his_outpatient_sync');
  });

  /** 验证非 JSON、类型不匹配、版本过高与缺节文本各自返回中文错误。 */
  it('rejects invalid json, wrong kind, newer version and missing sections', () => {
    expect(parseError(parseSyncPackageText('not-json'))).toContain('不是有效的 JSON');
    expect(parseError(parseSyncPackageText('[]'))).toContain('不是同步包 JSON 对象');
    expect(parseError(parseSyncPackageText(packageText({ packageKind: 'other' })))).toContain('文件类型标识不匹配');
    expect(parseError(parseSyncPackageText(packageText({ packageVersion: 'x' })))).toContain('packageVersion');
    expect(parseError(parseSyncPackageText(packageText({ packageVersion: 2 })))).toContain('高于当前支持的版本');
    expect(parseError(parseSyncPackageText(packageText({ sourceDataset: null })))).toContain('缺少必需的');
  });

  /** 验证解析出的包对象可直接赋给 DataSyncPackage 契约类型（编译期镜像检查）。 */
  it('keeps the parsed package assignable to the DataSyncPackage contract', () => {
    const parsed = parseSyncPackageText(packageText({
      mappings: [
        { sourceField: 'cardNo', targetField: 'CARD_NO', targetType: 'string', length: 16, nullable: true, converters: [] },
      ],
    }));
    if (!('package' in parsed)) throw new Error('合法同步包应解析成功');
    const requestPackage: DataSyncPackage = parsed.package;
    expect(requestPackage.mappings[0].targetField).toBe('CARD_NO');
  });

  /** 验证旧导出端直存接口响应产生的信封包装同步包会被自动解包并带标记，供页面提示升级导出端。 */
  it('unwraps an envelope-wrapped sync package and flags it', () => {
    const parsed = parseSyncPackageText(JSON.stringify({
      code: 200,
      type: 'success',
      message: '',
      result: JSON.parse(packageText()),
    }));
    if (!('package' in parsed)) throw new Error('信封同步包应自动解包成功');
    expect(parsed.unwrappedFromEnvelope).toBe(true);
    expect(parsed.package.taskCode).toBe('his_outpatient_sync');
    expect(parsed.package.packageKind).toBe('hd-report-center.sync-package');
  });

  /** 验证信封 result 内类型标识不匹配的同步包仍按原格式报错，并指出内层实际标识。 */
  it('still rejects an envelope whose inner sync package kind mismatches', () => {
    const parsed = parseSyncPackageText(JSON.stringify({
      code: 200,
      type: 'success',
      message: '',
      result: JSON.parse(packageText({ packageKind: 'other' })),
    }));
    expect(parseError(parsed)).toContain('文件类型标识不匹配');
    expect(parseError(parsed)).toContain('other');
  });
});

describe('try-run pair display helpers', () => {
  /** 构造两条样例行：源字段集合漂移（第二条多一个字段），目标列故意缺漏以验证声明回退。 */
  function sampleRows(): DataSyncSampleRow[] {
    return [
      {
        source: { DPNAME: '内科', DCNAME: '张三', NOTE: '初诊' },
        target: { DEPT_NAME: '内科', DOCTOR_NAME: '张三' },
      },
      {
        source: { DPNAME: '外科', DCNAME: '李四', EXTRA: 7 },
        target: { DEPT_NAME: '外科' },
      },
    ];
  }

  /** 验证空值统一渲染为占位符且不标记截断。 */
  it('renders null and undefined cells as a placeholder', () => {
    expect(tryRunCellDisplay(null)).toEqual({ text: '—', truncated: false, title: null });
    expect(tryRunCellDisplay(undefined)).toEqual({ text: '—', truncated: false, title: null });
  });

  /** 验证短文本与数字/布尔原样返回，且不产生悬停完整文本。 */
  it('keeps short values untouched without hover title', () => {
    expect(tryRunCellDisplay('内科')).toEqual({ text: '内科', truncated: false, title: null });
    expect(tryRunCellDisplay(7)).toEqual({ text: '7', truncated: false, title: null });
    expect(tryRunCellDisplay(true)).toEqual({ text: 'true', truncated: false, title: null });
  });

  /** 验证超长文本按上限截断并追加省略号，完整内容进入 title 供悬停查看。 */
  it('truncates long values and exposes the full text as hover title', () => {
    const long = '甲'.repeat(TRY_RUN_CELL_MAX_CHARS + 5);
    const display = tryRunCellDisplay(long);
    expect(display.truncated).toBe(true);
    expect(display.text).toBe(`${'甲'.repeat(TRY_RUN_CELL_MAX_CHARS)}…`);
    expect(display.title).toBe(long);
    const exact = '乙'.repeat(TRY_RUN_CELL_MAX_CHARS);
    expect(tryRunCellDisplay(exact)).toEqual({ text: exact, truncated: false, title: null });
  });

  /** 验证源字段键按首次出现顺序合并去重，兼容第二条样例多出的字段漂移。 */
  it('merges source keys in first-seen order across drifting sample rows', () => {
    const keys = collectTryRunKeys(sampleRows(), []);
    expect(keys.sourceKeys).toEqual(['DPNAME', 'DCNAME', 'NOTE', 'EXTRA']);
  });

  /** 验证目标列键合并样例实际输出，并回退追加接口声明但样例中缺漏的列。 */
  it('falls back to declared target columns missing from samples', () => {
    const keys = collectTryRunKeys(sampleRows(), ['DEPT_NAME', 'DOCTOR_NAME', 'VISIT_NO']);
    expect(keys.targetKeys).toEqual(['DEPT_NAME', 'DOCTOR_NAME', 'VISIT_NO']);
  });

  /** 验证空样例与空声明返回空键集合，供面板渲染空态。 */
  it('returns empty key sets for empty samples', () => {
    expect(collectTryRunKeys([], [])).toEqual({ sourceKeys: [], targetKeys: [] });
  });
});

describe('try-run pair columns built from mappings', () => {
  /** 构造键序与映射错位的样例行：源/目标键顺序互不相同，验证配对列只认映射顺序而非样例键序。 */
  function misalignedSample(): DataSyncSampleRow {
    return {
      source: { DEPT_CODE: 'N1', NOTE: '初诊', FEECODE: 14 },
      target: { DEPT_NAME: '内科', FEE_CATEGORY_NAME: '诊疗费' },
    };
  }

  /** 构造两条映射：FEECODE→FEE_CATEGORY_NAME 在第一位（对应字典转换 14→诊疗费）。 */
  function feeMappings(): SyncColumnMapping[] {
    return [
      mapping({ sourceField: 'FEECODE', targetField: 'FEE_CATEGORY_NAME' }),
      mapping({ sourceField: 'DEPT_CODE', targetField: 'DEPT_NAME' }),
    ];
  }

  /** 验证配对列按映射顺序生成：第 i 列恒为第 i 条映射的源字段→目标列，与样例键序无关。 */
  it('pairs columns follow mapping order regardless of sample key order', () => {
    const sample = misalignedSample();
    const built = buildTryRunPairColumns(feeMappings(), [sample], ['DEPT_NAME', 'FEE_CATEGORY_NAME']);

    expect(built.fromMappings).toBe(true);
    expect(built.pairs).toEqual([
      { sourceKey: 'FEECODE', targetKey: 'FEE_CATEGORY_NAME' },
      { sourceKey: 'DEPT_CODE', targetKey: 'DEPT_NAME' },
    ]);
  });

  /** 验证源/目标值按映射键取自样例：同一列的两侧值（14 与“诊疗费”）可垂直对照。 */
  it('reads source and target values by mapping keys on the same column', () => {
    const sample = misalignedSample();
    const built = buildTryRunPairColumns(feeMappings(), [sample], []);
    const first = built.pairs[0];

    expect(first.sourceKey).toBe('FEECODE');
    expect(tryRunCellDisplay(valueAt(sample.source, first.sourceKey)).text).toBe('14');
    expect(first.targetKey).toBe('FEE_CATEGORY_NAME');
    expect(tryRunCellDisplay(valueAt(sample.target, first.targetKey)).text).toBe('诊疗费');
  });

  /** 验证映射引用的键在样例中缺失时配对列仍保留该键，单元格经 tryRunCellDisplay 渲染为“—”占位。 */
  it('keeps mapping keys missing from samples and renders the placeholder', () => {
    const sample = misalignedSample();
    const mappings = [
      mapping({ sourceField: 'GHOST_SRC', targetField: 'GHOST_TARGET' }),
      ...feeMappings(),
    ];
    const built = buildTryRunPairColumns(mappings, [sample], ['FEE_CATEGORY_NAME']);

    expect(built.fromMappings).toBe(true);
    expect(built.pairs[0]).toEqual({ sourceKey: 'GHOST_SRC', targetKey: 'GHOST_TARGET' });
    expect(tryRunCellDisplay(valueAt(sample.source, built.pairs[0].sourceKey)).text).toBe('—');
    expect(tryRunCellDisplay(valueAt(sample.target, built.pairs[0].targetKey)).text).toBe('—');
    expect(tryRunCellDisplay(valueAt(sample.source, built.pairs[1].sourceKey)).text).toBe('14');
  });

  /** 验证映射为空时的兜底：回退样例键序按首现顺序 zip 配对（含声明列回退），并置 fromMappings=false 供面板提示。 */
  it('falls back to sample key order zipped in pairs when mappings are empty', () => {
    const samples: DataSyncSampleRow[] = [
      { source: { A: 1, B: 2 }, target: { X: 'x' } },
      { source: { C: 3 }, target: { Y: 'y' } },
    ];
    const built = buildTryRunPairColumns([], samples, ['Z']);

    expect(built.fromMappings).toBe(false);
    expect(built.pairs).toEqual([
      { sourceKey: 'A', targetKey: 'X' },
      { sourceKey: 'B', targetKey: 'Y' },
      { sourceKey: 'C', targetKey: 'Z' },
    ]);
  });

  /** 验证兜底 zip 时两侧键数不等：缺侧补 null，该列单元格经纯函数渲染为“—”占位。 */
  it('pads the shorter fallback side with null keys', () => {
    const samples: DataSyncSampleRow[] = [{ source: { A: 1, B: 2 }, target: { X: 'x' } }];
    const built = buildTryRunPairColumns([], samples, []);
    const second = built.pairs[1];

    expect(built.fromMappings).toBe(false);
    expect(second).toEqual({ sourceKey: 'B', targetKey: null });
    expect(tryRunCellDisplay(valueAt(samples[0].target, second.targetKey)).text).toBe('—');
  });
});

describe('syncTaskDesign cron schedule', () => {
  /** 固定基准时间：让“未来 3 次执行”断言可复现（本地时区，分钟整点后 1 秒避免边界抖动）。 */
  const fixedNow = new Date(2026, 10, 4, 10, 0, 1);

  /** 验证 Cron 释义与未来执行预览：合法 5 段表达式返回中文释义与 3 个本地时区格式化时间。 */
  it('previews description and next three local runs for a valid expression', () => {
    const preview = previewCronExpression('*/2 * * * *', fixedNow);

    expect(preview.description).toBe('每隔 2 分钟');
    expect(preview.nextRuns).toHaveLength(3);
    expect(preview.nextRuns[0]).toBe('2026-11-04 10:02');
    expect(preview.nextRuns[1]).toBe('2026-11-04 10:04');
    expect(preview.unavailableReason).toBeNull();
  });

  /** 验证天/周域 `?` 的合法用例照常预览（与后端口径一致，`?` 受支持）。 */
  it('previews an expression using the day-of-week question mark', () => {
    const preview = previewCronExpression('0 2 * * ?', fixedNow);

    expect(preview.description).not.toBeNull();
    expect(preview.nextRuns[0]).toBe('2026-11-05 02:00');
    expect(preview.unavailableReason).toBeNull();
  });

  /** 验证乱串的降级分支：不抛异常，释义与未来执行为空，返回“以服务端校验为准”提示。 */
  it('degrades gracefully for an unparseable expression', () => {
    const preview = previewCronExpression('bad-cron', fixedNow);

    expect(preview.description).toBeNull();
    expect(preview.nextRuns).toHaveLength(0);
    expect(preview.unavailableReason).toBe(CRON_PREVIEW_UNAVAILABLE_HINT);
  });

  /** 验证 6 段（含秒）表达式的降级分支：与后端口径一致不本地预览，交给服务端裁决。 */
  it('degrades for a six-segment expression with seconds', () => {
    const preview = previewCronExpression('0/30 * * * * *', fixedNow);

    expect(preview.nextRuns).toHaveLength(0);
    expect(preview.unavailableReason).toBe(CRON_PREVIEW_UNAVAILABLE_HINT);
  });

  /** 验证空白输入不触发降级提示（空输入由必填校验负责，预览返回全空结果）。 */
  it('returns an empty preview for blank input', () => {
    expect(previewCronExpression('   ', fixedNow)).toEqual({
      description: null,
      nextRuns: [],
      unavailableReason: null,
    });
  });

  /** 验证保存裁剪：仅 Cron 类型提交去空白后的表达式原文，其余类型一律置 null。 */
  it('keeps the trimmed expression only for the Cron type when saving', () => {
    expect(scheduleCronForSave('Cron', '  */2 * * * *  ')).toBe('*/2 * * * *');
    expect(scheduleCronForSave('Manual', '*/2 * * * *')).toBeNull();
    expect(scheduleCronForSave('Daily', '*/2 * * * *')).toBeNull();
    expect(scheduleCronForSave('Interval', '*/2 * * * *')).toBeNull();
    // Cron 类型但表达式为空白：等价于未填写，同样提交 null（必填校验在保存前拦截）。
    expect(scheduleCronForSave('Cron', '   ')).toBeNull();
  });

  /** 验证本地必填校验：Cron 类型空白表达式报错且文案含标准 5 段口径，其余类型不报错。 */
  it('requires a non-blank expression for the Cron type', () => {
    const error = validateCronScheduleInput('Cron', ' ');
    expect(error).not.toBeNull();
    expect(error).toContain('标准 5 段');
    expect(validateCronScheduleInput('Cron', '*/2 * * * *')).toBeNull();
    expect(validateCronScheduleInput('Manual', '')).toBeNull();
  });

  /** 验证列表调度摘要：Cron 显示表达式原文；缺表达式时回退中文类型名。 */
  it('summarizes Cron schedules with the raw expression', () => {
    expect(scheduleSummary('Cron', null, 0, '0 2 * * ?')).toBe('0 2 * * ?');
    expect(scheduleSummary('Cron', null, 0, '  ')).toBe('Cron 表达式');
    expect(scheduleSummary('Cron', null, 0)).toBe('Cron 表达式');
    // 回归确认：既有 Manual/Daily/Interval 摘要行为不变。
    expect(scheduleSummary('Manual', null, 0)).toBe('手动执行');
    expect(scheduleSummary('Daily', '02:30', 0)).toBe('每天 02:30');
    expect(scheduleSummary('Interval', null, 30)).toBe('每 30 分钟');
  });

  /** 验证调度类型选项文案：Cron 选项在白名单内且与后端 ScheduleTypes 保持一致顺序尾部追加。 */
  it('includes the Cron option in the schedule type options', () => {
    expect(SCHEDULE_TYPE_OPTIONS.map((option) => option.value)).toEqual([
      'Manual',
      'Daily',
      'Interval',
      'Cron',
    ]);
    expect(scheduleTypeLabel('Cron')).toBe('Cron 表达式');
  });
});
