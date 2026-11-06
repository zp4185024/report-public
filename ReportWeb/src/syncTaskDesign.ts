/**
 * 纯函数，不依赖组件。
 * 数据同步任务设计纯函数模块：负责“表格+下拉”交互下的映射构建、同名自动匹配、
 * 方言取数 SQL 生成、目标列元数据转换、映射完整性与转换链参数校验、内置转换器目录
 * （含中文名与参数默认值）、试跑样例按映射顺序的配对列派生，以及同步包上传文本的本地结构预检。
 * 所有结论以纯数据返回，页面组件只负责展示与提交。
 */
import cronstrue from 'cronstrue/i18n';
import { CronExpressionParser } from 'cron-parser';
import type {
  DataSyncSampleRow,
  DataRowValue,
  DataSourceDbType,
  SyncColumnMetadata,
  SyncColumnMapping,
  SyncConverterStep,
} from './types';
import {
  DATA_SYNC_PACKAGE_KIND,
  DATA_SYNC_PACKAGE_SUPPORTED_VERSION,
  type DataSyncPackage,
} from './types';
import { unwrapEnvelopeResult } from './reportPackageWizard';

/** 映射表格中的源字段行模型；name 与源取数列名一致，dataType 用于类型徽标展示，description 为可选列注释。 */
export interface SyncSourceColumn {
  name: string;
  dataType: string;
  description?: string | null;
}

/** 映射表格的目标列模型；targetType/length/nullable 决定目标表 DDL 与终值校验，description 为可选列注释。 */
export interface SyncTargetColumn {
  name: string;
  targetType: string;
  length: number | null;
  nullable: boolean;
  description?: string | null;
}

/** 转换器参数表单字段描述；control 决定行内参数编辑器的输入控件形态。 */
export interface SyncConverterFieldDescriptor {
  key: string;
  label: string;
  hint: string;
  control: 'text' | 'number' | 'select' | 'textarea';
  options?: ReadonlyArray<{ value: string; label: string }>;
  required: boolean;
}

/** 内置转换器目录项；与后端 PocDataSyncConverter.SupportedConverters 白名单一一对应。 */
export interface SyncConverterDescriptor {
  type: string;
  label: string;
  hint: string;
  fields: SyncConverterFieldDescriptor[];
  defaultArgs: Record<string, string>;
}

/** 目标列类型下拉选项与中文名；与后端 TargetTypes 白名单保持一致。 */
export const TARGET_TYPE_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'string', label: '文本' },
  { value: 'number', label: '整数' },
  { value: 'decimal', label: '小数' },
  { value: 'datetime', label: '日期时间' },
  { value: 'bool', label: '布尔' },
];

/** 冲突策略选项；与后端 NormalizeConflictPolicy 的白名单保持一致。 */
export const CONFLICT_POLICY_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'TruncateFirst', label: '先清空目标表再写入' },
  { value: 'Append', label: '追加写入' },
];

/** 调度类型选项；与后端 ScheduleTypes 白名单保持一致。 */
export const SCHEDULE_TYPE_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'Manual', label: '手动执行' },
  { value: 'Daily', label: '每天定时' },
  { value: 'Interval', label: '固定间隔' },
  { value: 'Cron', label: 'Cron 表达式' },
];

/** Cron 表达式的口径提示文案，与后端校验错误文案保持同一说法（标准 5 段）。 */
export const CRON_EXPRESSION_HINT = '标准 5 段：分 时 日 月 周（支持天/周域的 ?），不支持含秒的 6 段写法。';

/** Cron 表达式本地预览降级提示：解析不了时如实告知“以服务端校验为准”，不阻塞输入。 */
export const CRON_PREVIEW_UNAVAILABLE_HINT = '暂无法本地解析，以服务端校验为准。';

/** Cron 表达式预览结果：description 为中文释义，nextRuns 为未来执行时间（本地时区格式化文本），两者都可为空；unavailableReason 非空表示走了降级分支。 */
export interface CronExpressionPreview {
  description: string | null;
  nextRuns: string[];
  unavailableReason: string | null;
}

/**
 * 把日期格式化为 `yyyy-MM-dd HH:mm` 的本地时间文本。
 * @param date - 待格式化的日期。
 * @returns 定长文本（月份/日期/时/分补零）。
 */
function formatCronRunTime(date: Date): string {
  /**
   * 把数值补齐为两位数字文本，用于日期时间的月/日/时/分段。
   * @param value - 待补零的非负整数。
   * @returns 至少两位的十进制文本。
   */
  const pad = (value: number): string => value.toString().padStart(2, '0');
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}

/**
 * 预览 Cron 表达式：cronstrue 生成中文释义、cron-parser 计算未来 3 次执行时间（本地时区）。
 * 与后端口径一致只按标准 5 段预览：段数不符或任一库解析失败都进入降级分支——
 * 只返回 CRON_PREVIEW_UNAVAILABLE_HINT，不抛异常、不阻塞输入；后端是唯一裁决者。
 * @param expression - 用户输入的 Cron 表达式原文。
 * @param now - 计算未来执行的基准时间，缺省取当前时间（测试可注入固定值）。
 * @returns 预览结果；空白输入返回全空对象（由调用方的必填校验负责报错）。
 */
export function previewCronExpression(expression: string, now: Date = new Date()): CronExpressionPreview {
  const trimmed = expression.trim();
  if (!trimmed) {
    return { description: null, nextRuns: [], unavailableReason: null };
  }
  // 阶段：口径检查。5 段之外（含 6 段带秒）直接降级，避免前端算出后端必然拒绝的“未来执行时间”。
  if (trimmed.split(/\s+/).length !== 5) {
    return { description: null, nextRuns: [], unavailableReason: CRON_PREVIEW_UNAVAILABLE_HINT };
  }

  // 阶段：中文释义。cronstrue 对不认识的写法会抛异常，按降级处理不展示释义。
  let description: string | null = null;
  try {
    description = cronstrue.toString(trimmed, { locale: 'zh_CN' }) || null;
  } catch {
    description = null;
  }

  // 阶段：未来执行时间。cron-parser 算不出的表达式同样降级，保留释义（若有）供参考。
  const nextRuns: string[] = [];
  try {
    const iterator = CronExpressionParser.parse(trimmed, { currentDate: now });
    for (let index = 0; index < 3; index += 1) {
      nextRuns.push(formatCronRunTime(iterator.next().toDate()));
    }
  } catch {
    return { description, nextRuns: [], unavailableReason: CRON_PREVIEW_UNAVAILABLE_HINT };
  }
  return { description, nextRuns, unavailableReason: null };
}

/**
 * 校验设计器里 Cron 调度的本地必填口径：Cron 类型必须填写表达式（语法细节以后端解析为准）。
 * @param scheduleType - 调度类型代码。
 * @param scheduleCron - 用户输入的 Cron 表达式。
 * @returns 面向设计器的中文错误信息；合法返回 null。
 */
export function validateCronScheduleInput(scheduleType: string, scheduleCron: string): string | null {
  if (scheduleType === 'Cron' && !scheduleCron.trim()) {
    return `Cron 调度必须填写表达式（${CRON_EXPRESSION_HINT}）。`;
  }
  return null;
}

/**
 * 按调度类型裁剪保存输入的 Cron 表达式：仅 Cron 类型携带去空白后的原文，其余类型一律置 null，
 * 与后端“非 Cron 类型 ScheduleCron 置空”的裁剪规则一一对应。
 * @param scheduleType - 调度类型代码。
 * @param scheduleCron - 用户输入的 Cron 表达式。
 * @returns 保存输入应提交的表达式（或 null）。
 */
export function scheduleCronForSave(scheduleType: string, scheduleCron: string): string | null {
  return scheduleType === 'Cron' && scheduleCron.trim() ? scheduleCron.trim() : null;
}

/**
 * 渲染任务级调度摘要文本：手动任务显示“手动执行”，每天定时显示时刻，间隔显示分钟数，
 * Cron 显示表达式原文（列表页不本地解析，非法表达式由保存/导入时的服务端校验拦截）。
 * @param scheduleType - 调度类型代码。
 * @param scheduleTime - 每天定时的时间（HH:mm），可空。
 * @param intervalMinutes - 固定间隔的分钟数。
 * @param scheduleCron - Cron 表达式原文，可空。
 * @returns 面向列表展示的调度摘要。
 */
export function scheduleSummary(
  scheduleType: string,
  scheduleTime: string | null,
  intervalMinutes: number,
  scheduleCron?: string | null,
): string {
  if (scheduleType === 'Daily') return `每天 ${scheduleTime || '--:--'}`;
  if (scheduleType === 'Interval') return `每 ${intervalMinutes} 分钟`;
  if (scheduleType === 'Cron') return scheduleCron?.trim() || scheduleTypeLabel(scheduleType);
  return scheduleTypeLabel(scheduleType);
}

/**
 * 内置转换器目录：中文标签、参数表单描述与默认参数，保存前校验和
 * 映射表格行内参数编辑器共用这一份，避免页面另写一份参数表导致前后端规则漂移。
 */
export const CONVERTER_CATALOG: ReadonlyArray<SyncConverterDescriptor> = [
  { type: 'trim', label: '去空白', hint: '去除文本两端的空白字符', fields: [], defaultArgs: {} },
  { type: 'toNumber', label: '转数字', hint: '解析为数字，整数优先；解析失败该行记为失败', fields: [], defaultArgs: {} },
  {
    type: 'toDecimal',
    label: '转小数',
    hint: '解析为小数并按 scale 四舍五入',
    fields: [{ key: 'scale', label: '小数位 scale', hint: '0 到 28 的整数，留空默认 2', control: 'number', required: false }],
    defaultArgs: { scale: '2' },
  },
  {
    type: 'toDate',
    label: '转日期',
    hint: '按指定格式解析日期时间',
    fields: [{ key: 'format', label: '日期格式 format', hint: '如 yyyy-MM-dd、yyyyMMdd，留空默认 yyyy-MM-dd', control: 'text', required: false }],
    defaultArgs: { format: 'yyyy-MM-dd' },
  },
  { type: 'toString', label: '转文本', hint: '把任意值统一转为文本', fields: [], defaultArgs: {} },
  { type: 'emptyToNull', label: '空白转 NULL', hint: '空白文本归一为 NULL 写库', fields: [], defaultArgs: {} },
  {
    type: 'nullToDefault',
    label: '空值给默认',
    hint: '把空值替换为指定默认文本',
    fields: [{ key: 'value', label: '默认值 value', hint: '空值时写入的固定文本', control: 'text', required: true }],
    defaultArgs: { value: '' },
  },
  {
    type: 'required',
    label: '必填校验',
    hint: '空值按策略处理：记失败行或填默认值',
    fields: [
      {
        key: 'strategy',
        label: '失败策略 strategy',
        hint: 'failRow=记失败行；useDefault=填默认值',
        control: 'select',
        options: [{ value: 'failRow', label: '记失败行（failRow）' }, { value: 'useDefault', label: '填默认值（useDefault）' }],
        required: false,
      },
      { key: 'defaultValue', label: '默认值 defaultValue', hint: '策略为 useDefault 时必填', control: 'text', required: false },
    ],
    defaultArgs: { strategy: 'failRow', defaultValue: '' },
  },
  {
    type: 'truncate',
    label: '截断长度',
    hint: '把文本截断到指定长度，防止超长写库失败',
    fields: [{ key: 'length', label: '最大长度 length', hint: '正整数，超出部分被截断', control: 'number', required: true }],
    defaultArgs: { length: '50' },
  },
  {
    type: 'dictionary',
    label: '字典映射',
    hint: '按键值对照表翻译源值；未命中该行记为失败',
    fields: [{ key: 'items', label: '映射表 items', hint: 'JSON 对象文本，如 {"1":"门诊","2":"住院"}', control: 'textarea', required: true }],
    defaultArgs: { items: '' },
  },
  {
    type: 'expression',
    label: '表达式',
    hint: '受限表达式求值，入参变量名为 value',
    fields: [{ key: 'expression', label: '表达式 expression', hint: '以 value 引用原始值，如 value == null ? "" : value.Trim()', control: 'textarea', required: true }],
    defaultArgs: { expression: '' },
  },
];

/** 目标字段下拉里“该源字段不同步”的特殊选项值；与真实目标列名冲突不可能发生（列名非空）。 */
export const SKIP_TARGET_OPTION = '__skip__';

/**
 * 按数据源方言生成“选表模式”的取数 SQL：单条只读 SELECT，标识符按方言引用。
 * 表名取自元数据接口返回的原样大小写，引用后可安全包含大小写敏感或保留字表名。
 * @param dbType - 数据源方言（SqlServer/Oracle/MySql/PostgreSQL）。
 * @param table - 元数据返回的表名。
 * @returns 合法的单条 SELECT 语句文本；未知方言退化为不引用的原样表名。
 */
export function buildTableSelectSql(dbType: DataSourceDbType | string, table: string): string {
  const name = table.trim();
  if (!name) return '';
  const normalized = String(dbType).trim();
  switch (normalized) {
    case 'SqlServer':
      return `SELECT * FROM [${name}]`;
    case 'MySql':
      return `SELECT * FROM \`${name}\``;
    case 'Oracle':
    case 'PostgreSQL':
      return `SELECT * FROM "${name}"`;
    default:
      return `SELECT * FROM ${name}`;
  }
}

/**
 * 把数据库列类型文本收敛为目标列类型代码（string/number/decimal/datetime/bool）。
 * 顺序先时间、再布尔、再整数/小数，避免 datetime2/bigint 等复合词被误判；未知类型按文本处理。
 * @param dbType - 元数据接口返回的数据库类型文本。
 * @returns 目标列类型代码，保证始终落在 TARGET_TYPE_OPTIONS 白名单内。
 */
export function targetDbTypeToType(dbType: string): string {
  const text = (dbType ?? '').toLowerCase();
  if (/(datetime|timestamp|\bdate\b|\btime\b|year)/.test(text)) return 'datetime';
  if (/(bool|bit|boolean)/.test(text)) return 'bool';
  if (/(int|byte|short|long|serial)/.test(text)) return 'number';
  if (/(decimal|numeric|float|double|real|money|number)/.test(text)) return 'decimal';
  return 'string';
}

/**
 * 把元数据列清单转换为映射表格的目标列集合：类型按数据库类型文本推断，
 * 可空、长度与列注释原样携带，供目标字段下拉、同名自动匹配、类型列与注释展示。
 * @param columns - metadata/columns 接口返回的列清单。
 * @returns 目标列模型列表。
 */
export function targetColumnsFromMetadata(columns: readonly SyncColumnMetadata[]): SyncTargetColumn[] {
  return columns.map((column) => ({
    name: column.name,
    targetType: targetDbTypeToType(column.dataType),
    length: column.length,
    nullable: column.nullable,
    description: column.description ?? null,
  }));
}

/**
 * 格式化列注释展示文本：`列名 · 注释`（注释存在时），无注释只返回列名。
 * 目标字段下拉选项、title 提示等“名字带含义”的场景共用同一拼接规则。
 * @param name - 列名。
 * @param description - 列注释，可空。
 * @returns 下拉/提示用展示文本。
 */
export function formatColumnWithDescription(name: string, description?: string | null): string {
  const trimmed = description?.trim();
  return trimmed ? `${name} · ${trimmed}` : name;
}

/**
 * 从源字段声明推断新表模式的目标列默认类型：预览推断的 dataType 已是四个通用代码，
 * 直接沿用；未知值退化为文本，保证目标 DDL 始终合法。
 * @param dataType - 源字段的数据类型代码（string/number/bool/datetime 或数据集声明）。
 * @returns 目标列类型代码。
 */
export function inferTargetTypeFromSource(dataType: string): string {
  return TARGET_TYPE_OPTIONS.some((item) => item.value === dataType) ? dataType : 'string';
}

/**
 * 把目标列类型代码转换为中文名，未知类型原样返回以便发现问题。
 * @param value - 目标列类型代码（string/number/decimal/datetime/bool）。
 * @returns 面向设计器的中文类型名。
 */
export function targetTypeLabel(value: string): string {
  return TARGET_TYPE_OPTIONS.find((item) => item.value === value)?.label ?? value;
}

/**
 * 把冲突策略代码转换为中文名，未知策略原样返回以便发现问题。
 * @param value - 冲突策略代码（TruncateFirst/Append）。
 * @returns 面向设计器的中文策略名。
 */
export function conflictPolicyLabel(value: string): string {
  return CONFLICT_POLICY_OPTIONS.find((item) => item.value === value)?.label ?? value;
}

/**
 * 把调度类型代码转换为中文名，未知类型原样返回以便发现问题。
 * @param value - 调度类型代码（Manual/Daily/Interval/Cron）。
 * @returns 面向设计器的中文调度名。
 */
export function scheduleTypeLabel(value: string): string {
  return SCHEDULE_TYPE_OPTIONS.find((item) => item.value === value)?.label ?? value;
}

/**
 * 查找内置转换器目录项；未收录的类型返回 null，由校验层报错。
 * @param type - 转换器类型代码。
 * @returns 目录项或 null。
 */
export function findConverterDescriptor(type: string): SyncConverterDescriptor | null {
  return CONVERTER_CATALOG.find((item) => item.type === type) ?? null;
}

/**
 * 读取转换器中文名，未知类型原样返回以便发现问题。
 * @param type - 转换器类型代码。
 * @returns 面向设计器的中文转换器名。
 */
export function converterLabel(type: string): string {
  return findConverterDescriptor(type)?.label ?? type;
}

/**
 * 按目录默认参数构造一个转换器步骤，供映射表格“添加转换器”直接入链。
 * @param type - 转换器类型代码，须在 CONVERTER_CATALOG 内。
 * @returns 带默认参数的转换器步骤；未知类型返回空参数步骤由保存校验拦截。
 */
export function createConverterStep(type: string): SyncConverterStep {
  const descriptor = findConverterDescriptor(type);
  return { type, args: { ...descriptor?.defaultArgs } };
}

/**
 * 生成转换器 chip 的摘要文本：名称 + 非空参数的“键=值”列表（单值截断），无参数时只显示名称。
 * @param step - 转换器步骤。
 * @returns 面向 chip 展示的单行摘要。
 */
export function converterSummary(step: SyncConverterStep): string {
  const descriptor = findConverterDescriptor(step.type);
  const label = converterLabel(step.type);
  const args = step.args ?? {};
  const parts: string[] = [];
  for (const field of descriptor?.fields ?? []) {
    const value = (args[field.key] ?? '')?.toString().trim() ?? '';
    if (!value) continue;
    const display = value.length > 16 ? `${value.slice(0, 16)}…` : value;
    parts.push(`${field.key}=${display}`);
  }
  return parts.length > 0 ? `${label}（${parts.join('，')}）` : label;
}

/**
 * 从映射列表派生目标列声明集合：同名（忽略大小写）目标列只保留首个声明，
 * 顺序与映射顺序一致，供建表模式的目标 DDL 推导和保存前完整性校验。
 * @param mappings - 当前映射列表。
 * @returns 去重后的目标列模型列表。
 */
export function targetColumnsFromMappings(
  mappings: readonly SyncColumnMapping[],
): SyncTargetColumn[] {
  const columns: SyncTargetColumn[] = [];
  const seen = new Set<string>();
  for (const mapping of mappings) {
    const key = mapping.targetField.trim().toLowerCase();
    if (!key || seen.has(key)) continue;
    seen.add(key);
    columns.push({
      name: mapping.targetField,
      targetType: mapping.targetType,
      length: mapping.length,
      nullable: mapping.nullable,
    });
  }
  return columns;
}

/**
 * 为新映射建议目标列名：默认沿用源字段名，与现有目标列撞名（忽略大小写）时追加序号。
 * @param sourceField - 源字段名。
 * @param existing - 现有目标列集合。
 * @returns 不与现有目标列重名的目标列名。
 */
export function suggestTargetFieldName(
  sourceField: string,
  existing: readonly SyncTargetColumn[],
): string {
  const taken = new Set(existing.map((column) => column.name.trim().toLowerCase()));
  if (!taken.has(sourceField.trim().toLowerCase())) return sourceField;
  let index = 2;
  while (taken.has(`${sourceField}_${index}`.toLowerCase())) index += 1;
  return `${sourceField}_${index}`;
}

/**
 * 构造新目标列的默认声明：文本类型、不限长度、允许空，设计器可在行内编辑器里再调整。
 * @param name - 目标列名。
 * @returns 默认目标列模型。
 */
export function defaultTargetColumn(name: string): SyncTargetColumn {
  return { name, targetType: 'string', length: null, nullable: true };
}

/**
 * 按源字段声明构造“新建表模式”的默认映射：每列 1:1 同名映射（目标列名=源字段名），
 * 目标类型按源类型推断、允许空、不挂转换链（直通）。设计器可再逐行改目标名或标记不同步。
 * @param sourceColumns - 源字段列。
 * @returns 可直接写回草稿的映射列表。
 */
export function buildDefaultTableMappings(
  sourceColumns: readonly SyncSourceColumn[],
): SyncColumnMapping[] {
  // 阶段：逐列生成 1:1 映射。目标名直接沿用源名（预览列名本身已去重），类型按源声明推断。
  return sourceColumns
    .filter((column) => column.name.trim())
    .map((column) => ({
      sourceField: column.name,
      targetField: column.name,
      targetType: inferTargetTypeFromSource(column.dataType),
      length: null,
      nullable: true,
      converters: [],
    }));
}

/**
 * 同名自动预匹配（表格交互）：源列名与目标列名忽略大小写比对，为每对同名且尚未建立
 * 映射的组合生成初始映射（沿用目标列元数据推断的类型/长度/可空），已有映射原样保留在前。
 * 目标列集合为空（未选目标表）时不做任何匹配。
 * @param sourceColumns - 源字段列。
 * @param targetColumns - 目标列集合（现有表模式来自元数据；新建表模式为空）。
 * @param existingMappings - 已保存或已编辑的映射，默认为空。
 * @returns 可直接写回草稿的映射列表。
 */
export function buildInitialMappings(
  sourceColumns: readonly SyncSourceColumn[],
  targetColumns: readonly SyncTargetColumn[],
  existingMappings: readonly SyncColumnMapping[] = [],
): SyncColumnMapping[] {
  const result = existingMappings.map((mapping) => ({ ...mapping }));
  const mappedSources = new Set(result.map((item) => item.sourceField.trim().toLowerCase()));
  const mappedTargets = new Set(result.map((item) => item.targetField.trim().toLowerCase()));
  // 阶段：逐个源列尝试同名匹配；仅当目标列同名且未被其它映射占用时生成新映射。
  for (const column of sourceColumns) {
    const sourceKey = column.name.trim().toLowerCase();
    if (!sourceKey || mappedSources.has(sourceKey)) continue;
    const matched = targetColumns.find((target) => {
      const targetKey = target.name.trim().toLowerCase();
      return targetKey === sourceKey && !mappedTargets.has(targetKey);
    });
    if (!matched) continue;
    result.push({
      sourceField: column.name,
      targetField: matched.name,
      targetType: matched.targetType,
      length: matched.length,
      nullable: matched.nullable,
      converters: [],
    });
    mappedSources.add(sourceKey);
    mappedTargets.add(matched.name.trim().toLowerCase());
  }
  return result;
}

/**
 * 校验映射设计并返回中文错误列表。表格交互允许源列“不同步”、也允许现有表的
 * 部分列不被写入，因此不再把未映射源列/未写入目标列当错误；覆盖源引用、目标列
 * 形状（空/超长/重复/类型白名单/负长度）与转换链参数问题，列表为空表示可提交保存。
 * @param sourceColumns - 源字段列。
 * @param targetColumns - 目标列集合（保留签名用于派生提示与未来扩展，当前不据此报错）。
 * @param mappings - 当前映射列表。
 * @returns 面向设计器的中文错误列表，顺序与检查项一致。
 */
export function validateSyncMappings(
  sourceColumns: readonly SyncSourceColumn[],
  targetColumns: readonly SyncTargetColumn[],
  mappings: readonly SyncColumnMapping[],
): string[] {
  const errors: string[] = [];
  const sourceNames = new Set(sourceColumns.map((column) => column.name.trim().toLowerCase()));
  const mappedSourceKeys = new Set<string>();
  const mappedTargetKeys = new Set<string>();
  // 阶段一：逐条映射检查形状、重复与源引用。“不同步”的源列不在 mappings 里，天然跳过。
  for (const mapping of mappings) {
    const sourceKey = mapping.sourceField.trim().toLowerCase();
    const targetKey = mapping.targetField.trim().toLowerCase();
    if (!mapping.sourceField.trim()) errors.push('存在未选择源字段的映射。');
    else if (sourceColumns.length > 0 && !sourceNames.has(sourceKey)) {
      errors.push(`映射引用了源数据集中不存在的字段 ${mapping.sourceField}。`);
    }
    if (!targetKey) errors.push('目标列名不能为空。');
    else if (targetKey.length > 128) errors.push(`目标列 ${mapping.targetField} 名称超过 128 个字符。`);
    else if (mappedTargetKeys.has(targetKey)) errors.push(`目标列 ${mapping.targetField} 被多条映射重复写入。`);
    if (sourceKey && mappedSourceKeys.has(sourceKey)) errors.push(`源列 ${mapping.sourceField} 被映射到多个目标列。`);
    if (!TARGET_TYPE_OPTIONS.some((item) => item.value === mapping.targetType)) {
      errors.push(`目标列 ${mapping.targetField || '(未命名)'} 的类型 ${mapping.targetType || '(空)'} 不在支持范围内。`);
    }
    if (mapping.length !== null && mapping.length < 0) {
      errors.push(`目标列 ${mapping.targetField} 的长度不能为负数。`);
    }
    mappedSourceKeys.add(sourceKey);
    mappedTargetKeys.add(targetKey);
  }
  // 阶段二：转换链参数校验，与后端编译期规则一一对应，先把参数问题挡在保存之前。
  for (const mapping of mappings) {
    for (const message of validateConverterChain(mapping)) {
      errors.push(`目标列 ${mapping.targetField || '(未命名)'}：${message}`);
    }
  }
  return errors;
}

/**
 * 校验单个映射的转换链参数：只覆盖后端编译期就会拒绝的规则
 * （未知转换器、必填参数缺失、数值范围、字典 JSON 形态），返回该映射的错误列表。
 * @param mapping - 待校验的字段映射。
 * @returns 该映射转换链的中文错误列表。
 */
function validateConverterChain(mapping: SyncColumnMapping): string[] {
  const errors: string[] = [];
  // 阶段：逐个转换步骤比对目录白名单与必填参数，缺少参数时给出可定位的字段与参数名。
  mapping.converters.forEach((step, index) => {
    const position = `第 ${index + 1} 个转换器`;
    const descriptor = findConverterDescriptor(step.type);
    if (!descriptor) {
      errors.push(`${position} ${step.type || '(空)'} 不是受支持的内置转换器。`);
      return;
    }
    const args = step.args ?? {};
    /**
     * 读取转换步骤的某个参数文本并去除首尾空白；缺失参数归一为空串便于必填判断。
     * @param key - 参数键。
     * @returns 参数文本，缺省为空串。
     */
    const argText = (key: string) => (args[key] ?? '')?.toString().trim() ?? '';
    if (descriptor.type === 'nullToDefault' && !argText('value')) {
      errors.push(`${position}（${descriptor.label}）缺少必填参数 value。`);
    }
    if (descriptor.type === 'required') {
      const strategy = argText('strategy') || 'failRow';
      if (strategy !== 'failRow' && strategy !== 'useDefault') {
        errors.push(`${position}（${descriptor.label}）的 strategy 只支持 failRow 或 useDefault。`);
      } else if (strategy === 'useDefault' && !argText('defaultValue')) {
        errors.push(`${position}（${descriptor.label}）采用 useDefault 策略时必须提供 defaultValue。`);
      }
    }
    if (descriptor.type === 'truncate') {
      const length = Number(argText('length'));
      if (!argText('length') || !Number.isInteger(length) || length <= 0) {
        errors.push(`${position}（${descriptor.label}）的 length 必须是正整数。`);
      }
    }
    if (descriptor.type === 'toDecimal') {
      const scaleText = argText('scale');
      if (scaleText) {
        const scale = Number(scaleText);
        if (!Number.isInteger(scale) || scale < 0 || scale > 28) {
          errors.push(`${position}（${descriptor.label}）的 scale 必须是 0 到 28 的整数。`);
        }
      }
    }
    if (descriptor.type === 'dictionary') {
      const items = argText('items');
      if (!items) {
        errors.push(`${position}（${descriptor.label}）缺少必填参数 items。`);
      } else {
        // 字典表必须是 JSON 对象文本，数组或标量在后端反序列化阶段会被拒绝。
        let parsed: unknown;
        try {
          parsed = JSON.parse(items);
        } catch {
          parsed = undefined;
        }
        if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
          errors.push(`${position}（${descriptor.label}）的 items 必须是 JSON 对象文本，如 {"1":"门诊"}。`);
        }
      }
    }
    if (descriptor.type === 'expression' && !argText('expression')) {
      errors.push(`${position}（${descriptor.label}）缺少必填参数 expression。`);
    }
  });
  return errors;
}

/** 同步包上传文本的解析结果：要么得到可用的包对象（来自信封解包时带标记），要么得到可直接展示的中文错误。 */
export type SyncPackageParseResult =
  | { package: DataSyncPackage; unwrappedFromEnvelope?: true }
  | { error: string };

/**
 * 解析上传的同步包文本并完成本地结构预检（packageKind / packageVersion / 必需节），
 * 把明显不是同步包的文件挡在调用服务端校验之前。顶层没有 packageKind 但
 * result 内的 packageKind 匹配时，视为接口信封包装并自动解包，版本等后续校验
 * 一律以解包后的对象为准，成功时附 unwrappedFromEnvelope 标记供页面提示升级导出端。
 * @param text - 上传文件的完整文本内容。
 * @returns 结构合法时返回包对象（解包场景额外带标记），否则返回面向操作员的中文错误。
 */
export function parseSyncPackageText(text: string): SyncPackageParseResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { error: '文件内容不是有效的 JSON，无法读取同步包。' };
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { error: '文件内容不是同步包 JSON 对象。' };
  }

  // 阶段：识别包体。顶层标识不匹配时先尝试解开接口信封包装，兼容旧导出端直存响应的文件。
  let candidate = parsed as Partial<DataSyncPackage>;
  let unwrappedFromEnvelope = false;
  if (candidate.packageKind !== DATA_SYNC_PACKAGE_KIND) {
    const inner = unwrapEnvelopeResult(parsed);
    if (inner !== null && inner.packageKind === DATA_SYNC_PACKAGE_KIND) {
      candidate = inner as Partial<DataSyncPackage>;
      unwrappedFromEnvelope = true;
    } else {
      const actualKind = inner !== null && typeof inner.packageKind === 'string'
        ? inner.packageKind
        : String(candidate.packageKind);
      return { error: `文件类型标识不匹配：期望 ${DATA_SYNC_PACKAGE_KIND}，实际为 ${actualKind}。` };
    }
  }

  // 阶段：结构预检。版本号与必需节的检查始终以解包后的对象为准。
  if (typeof candidate.packageVersion !== 'number' || !Number.isFinite(candidate.packageVersion)) {
    return { error: '同步包缺少有效的 packageVersion 版本号。' };
  }
  if (candidate.packageVersion > DATA_SYNC_PACKAGE_SUPPORTED_VERSION) {
    return { error: `同步包版本 ${candidate.packageVersion} 高于当前支持的版本 ${DATA_SYNC_PACKAGE_SUPPORTED_VERSION}，请先升级报表中心。` };
  }
  if (!candidate.sourceDataset || !Array.isArray(candidate.mappings) || !candidate.taskCode || !candidate.targetTable) {
    return { error: '同步包缺少必需的 taskCode / sourceDataset / mappings / targetTable 节，文件可能已损坏。' };
  }

  // 阶段：回传结果。信封解包场景附上标记，供页面提示操作员升级导出端后重新导出。
  return unwrappedFromEnvelope
    ? { package: candidate as DataSyncPackage, unwrappedFromEnvelope: true }
    : { package: candidate as DataSyncPackage };
}

/** 试跑配对单元格展示的最大字符数：超过后截断文本并追加省略号，完整内容改由 title 悬停查看。 */
export const TRY_RUN_CELL_MAX_CHARS = 40;

/** 试跑单元格展示模型：text 为界面渲染文本；truncated 标记是否发生截断；title 为悬停查看的完整文本，未截断时为 null。 */
export interface TryRunCellDisplay {
  text: string;
  truncated: boolean;
  title: string | null;
}

/**
 * 把试跑样例单元格的原始值渲染为展示文本：null/undefined 统一为占位符“—”，
 * 超长文本按 TRY_RUN_CELL_MAX_CHARS 截断并追加省略号，完整内容放入 title 供悬停查看，
 * 让配对表在窄面板与超宽字段下仍保持可读的紧凑布局。
 * @param value - 试跑样例里的源字段值或转换后的目标值。
 * @returns 单元格展示模型（文本、截断标记与悬停完整文本）。
 */
export function tryRunCellDisplay(value: DataRowValue | null | undefined): TryRunCellDisplay {
  if (value === null || value === undefined) {
    return { text: '—', truncated: false, title: null };
  }
  const full = String(value);
  if (full.length <= TRY_RUN_CELL_MAX_CHARS) {
    return { text: full, truncated: false, title: null };
  }
  // 阶段：截断长文本。保留前 TRY_RUN_CELL_MAX_CHARS 个字符并以省略号结尾，完整内容交给 title。
  return { text: `${full.slice(0, TRY_RUN_CELL_MAX_CHARS)}…`, truncated: true, title: full };
}

/** 试跑配对展示的键集合：sourceKeys 用于源行单元格，targetKeys 用于目标行单元格。 */
export interface TryRunKeyCollection {
  sourceKeys: string[];
  targetKeys: string[];
}

/**
 * 按首次出现顺序合并全部样例行的源字段键与目标列键：不同样本行字段集合漂移时保持
 * 最初顺序并去重，避免同一记录对里两行列集合不一致；样例中缺失（如转换失败）的目标列
 * 再回退追加接口声明的 targetColumns，保证配对表的列集合完整。
 * 注意：设计器主路径的配对列已改由 buildTryRunPairColumns 按映射顺序生成（保证转换前后
 * 垂直对齐），本函数仅作为映射为空时的兜底键序来源，保留旧有展示行为。
 * @param samples - 试跑返回的样例行集合。
 * @param declaredTargetColumns - 试跑结果声明的目标列名列表。
 * @returns 去重后的源字段键与目标列键。
 */
export function collectTryRunKeys(
  samples: readonly DataSyncSampleRow[],
  declaredTargetColumns: readonly string[],
): TryRunKeyCollection {
  // 阶段：合并源字段键。逐行收集并按首次出现顺序去重，兼容样本行之间源字段漂移。
  const sourceKeys: string[] = [];
  const seenSource = new Set<string>();
  for (const sample of samples) {
    for (const key of Object.keys(sample.source)) {
      if (!seenSource.has(key)) {
        seenSource.add(key);
        sourceKeys.push(key);
      }
    }
  }
  // 阶段：合并目标列键。同样按首次出现顺序去重，再补齐声明了但样例里没有的目标列。
  const targetKeys: string[] = [];
  const seenTarget = new Set<string>();
  for (const sample of samples) {
    for (const key of Object.keys(sample.target)) {
      if (!seenTarget.has(key)) {
        seenTarget.add(key);
        targetKeys.push(key);
      }
    }
  }
  for (const key of declaredTargetColumns) {
    if (!seenTarget.has(key)) {
      seenTarget.add(key);
      targetKeys.push(key);
    }
  }
  return { sourceKeys, targetKeys };
}

/** 试跑配对列：同一列内 sourceKey 驱动源行单元格、targetKey 驱动目标行单元格，第 i 列两侧对应同一条映射。 */
export interface TryRunPairColumn {
  sourceKey: string | null;
  targetKey: string | null;
}

/** 试跑配对列派生结果：pairs 为列序列；fromMappings=false 表示映射为空走了样例键序兜底，面板需提示对齐不保证。 */
export interface TryRunPairColumns {
  pairs: TryRunPairColumn[];
  fromMappings: boolean;
}

/**
 * 按映射顺序派生试跑配对列：第 i 列恒为第 i 条映射（源字段 → 目标列），使源行与目标行的
 * 转换前后值在同一列垂直对齐（如字典转换 14 与“诊疗费”正对比较）。
 * 映射列表为空（防御路径，正常设计不会出现）时回退 collectTryRunKeys 的样例键序，
 * 把两侧键按下标 zip 成配对列并置 fromMappings=false，由面板提示此时对齐无保证。
 * 配对列只声明键，不读取样例值——键在样例中缺失时由单元格按“—”占位渲染。
 * @param mappings - 设计器当前的映射列表。
 * @param samples - 试跑返回的样例行集合（仅兜底分支使用）。
 * @param declaredTargetColumns - 试跑结果声明的目标列名列表（仅兜底分支使用）。
 * @returns 配对列序列与是否按映射生成。
 */
export function buildTryRunPairColumns(
  mappings: readonly SyncColumnMapping[],
  samples: readonly DataSyncSampleRow[],
  declaredTargetColumns: readonly string[],
): TryRunPairColumns {
  // 阶段：映射优先。直接按映射顺序展开，源/目标键一一对应，列数与映射条数一致。
  if (mappings.length > 0) {
    return {
      fromMappings: true,
      pairs: mappings.map((mapping) => ({
        sourceKey: mapping.sourceField,
        targetKey: mapping.targetField,
      })),
    };
  }
  // 阶段：空映射兜底。沿用样例键序合并结果，两侧按下标配对；缺侧补 null，单元格显示占位符。
  const { sourceKeys, targetKeys } = collectTryRunKeys(samples, declaredTargetColumns);
  const columnCount = Math.max(sourceKeys.length, targetKeys.length);
  const pairs: TryRunPairColumn[] = [];
  for (let index = 0; index < columnCount; index += 1) {
    pairs.push({ sourceKey: sourceKeys[index] ?? null, targetKey: targetKeys[index] ?? null });
  }
  return { pairs, fromMappings: false };
}

/** AI 智能匹配的单行置信标注：targetField 小写键 → 置信度与依据（仅供 UI，写回 mappings 时丢弃）。 */
export interface AiSyncMatchMark {
  targetField: string;
  confidence: 'high' | 'mid' | 'low';
  reason: string;
}

/** AI 智能匹配建议的回填结果：新草稿映射 + 逐行置信标注 + 需人工处理的目标列名。 */
export interface AiSyncMatchBackfillResult {
  mappings: SyncColumnMapping[];
  marks: Record<string, AiSyncMatchMark>;
  manualTargets: string[];
}

/**
 * 把 AI 智能匹配建议回填到映射草稿：只填空、不覆盖人工映射（建议与人工冲突时以人工为准），
 * needsManual=true 的建议绝不写入 mappings，只进“需人工处理”清单；建议引用的源/目标名
 * 必须真实存在于两侧清单，防模型幻觉。置信标注按目标列小写键记录，供映射表格逐行展示。
 * @param sourceColumns - 源字段列。
 * @param targetColumns - 目标列集合。
 * @param mappings - 当前草稿映射（人工已填）。
 * @param suggestions - 后端 /ai/sync-match 返回的建议列表。
 * @returns 新草稿映射、逐行置信标注与需人工处理目标列。
 */
export function applyAiSyncMatchSuggestions(
  sourceColumns: readonly SyncSourceColumn[],
  targetColumns: readonly SyncTargetColumn[],
  mappings: readonly SyncColumnMapping[],
  suggestions: readonly {
    sourceField: string;
    targetField: string;
    targetType: string;
    length: number | null;
    nullable: boolean;
    converters: { type: string; args: Record<string, string | null> | null }[];
    confidence: 'high' | 'mid' | 'low';
    reason: string;
    needsManual: boolean;
  }[],
): AiSyncMatchBackfillResult {
  const result = mappings.map((mapping) => ({ ...mapping }));
  const usedSources = new Set(result.map((item) => item.sourceField.trim().toLowerCase()));
  const usedTargets = new Set(result.map((item) => item.targetField.trim().toLowerCase()));
  const sourceNames = new Set(sourceColumns.map((column) => column.name.trim().toLowerCase()));
  const targetTypes = new Map(
    targetColumns.map((column) => [column.name.trim().toLowerCase(), column] as const),
  );
  const marks: Record<string, AiSyncMatchMark> = {};
  const manualTargets: string[] = [];
  // 阶段：先分流“需人工处理”项（不写入映射），再逐条回填可落地建议。
  for (const suggestion of suggestions) {
    const targetKey = suggestion.targetField.trim().toLowerCase();
    if (suggestion.needsManual || !suggestion.sourceField.trim()) {
      if (targetTypes.has(targetKey)) manualTargets.push(suggestion.targetField.trim());
      continue;
    }
    const sourceKey = suggestion.sourceField.trim().toLowerCase();
    if (!sourceNames.has(sourceKey) || !targetTypes.has(targetKey)) continue;
    if (usedSources.has(sourceKey) || usedTargets.has(targetKey)) continue;
    result.push({
      sourceField: suggestion.sourceField.trim(),
      targetField: suggestion.targetField.trim(),
      targetType: suggestion.targetType,
      length: suggestion.length,
      nullable: suggestion.nullable,
      converters: suggestion.converters.map((step) => ({ ...step })),
    });
    usedSources.add(sourceKey);
    usedTargets.add(targetKey);
    marks[targetKey] = {
      targetField: suggestion.targetField.trim(),
      confidence: suggestion.confidence,
      reason: suggestion.reason,
    };
  }
  return { mappings: result, marks, manualTargets };
}
