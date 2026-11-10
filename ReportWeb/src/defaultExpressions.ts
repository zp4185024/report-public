/**
 * 日期类默认表达式的前端镜像实现与预设目录：与后端
 * HD.ReportCenter.ReportEngine 的 PocDefaultExpressionEvaluator 保持同一套
 * 封闭函数文法（now/today/yesterday/tomorrow、startofday 等区间截断、
 * adddays/addmonths/addyears 整数偏移，支持一层嵌套组合）。
 * 词表两端保持同步：新增函数或预设必须同时改后端求值器和这里，
 * 否则保存可通过但前端预览值与正式执行值会分叉。
 */

/** 参数数据类型维度（语义目录按它过滤展示集合）；date/datetime/month/year 之外按 date 处理。 */
export type RelativePointDataType = 'date' | 'datetime' | 'month' | 'year';

/**
 * 语义时间点目录条目：界面只出现中文标签，表达式仅作存储与求值输入。
 * suitableFor 缺省表示 date/datetime 可见；月/年粒度参数只显示边界与粒度化条目，
 * 避免给 yyyy-MM 参数冒出"7 天前"这类格式化后语义含混的选项。
 */
export interface RelativePointPreset {
  /** 界面展示的中文语义标签。 */
  label: string;
  /** 落库的默认表达式原文（小写规范形态）。 */
  expression: string;
  /** 适用参数类型集合；缺省 = date/datetime。 */
  suitableFor?: ReadonlyArray<RelativePointDataType>;
}

/**
 * 统一语义时间点目录（Grafana/Metabase 式 quick ranges 思路：语义给用户、表达式进存储）。
 * 新增条目时标签必须说清"相对哪个基准、取哪一天"，表达式与后端求值器词表保持同步。
 */
export const RELATIVE_POINT_PRESETS: RelativePointPreset[] = [
  { label: '今天', expression: 'today()' },
  { label: '昨天', expression: 'yesterday()' },
  { label: '明天', expression: 'tomorrow()' },
  { label: '7 天前', expression: 'adddays(today(),-7)' },
  { label: '30 天前', expression: 'adddays(today(),-30)' },
  { label: '本周第一天', expression: 'startofweek(today())' },
  { label: '本周最后一天', expression: 'endofweek(today())' },
  { label: '上周第一天', expression: 'startofweek(adddays(today(),-7))' },
  { label: '上周最后一天', expression: 'endofweek(adddays(today(),-7))' },
  { label: '本月第一天', expression: 'startofmonth(today())', suitableFor: ['date', 'datetime', 'month'] },
  { label: '本月最后一天', expression: 'endofmonth(today())', suitableFor: ['date', 'datetime', 'month'] },
  { label: '上月第一天', expression: 'startofmonth(addmonths(today(),-1))', suitableFor: ['date', 'datetime', 'month'] },
  { label: '上月最后一天', expression: 'endofmonth(addmonths(today(),-1))', suitableFor: ['date', 'datetime', 'month'] },
  { label: '今年第一天', expression: 'startofyear(today())', suitableFor: ['date', 'datetime', 'month', 'year'] },
  { label: '今年最后一天', expression: 'endofyear(today())', suitableFor: ['date', 'datetime', 'month', 'year'] },
  { label: '去年第一天', expression: 'startofyear(addyears(today(),-1))', suitableFor: ['date', 'datetime', 'month', 'year'] },
  { label: '去年最后一天', expression: 'endofyear(addyears(today(),-1))', suitableFor: ['date', 'datetime', 'month', 'year'] },
  { label: '上月', expression: 'addmonths(today(),-1)', suitableFor: ['month'] },
  { label: '本月', expression: 'today()', suitableFor: ['month'] },
  { label: '今年', expression: 'today()', suitableFor: ['year'] },
  { label: '去年', expression: 'addyears(today(),-1)', suitableFor: ['year'] },
  { label: '当前时刻', expression: 'now()', suitableFor: ['datetime'] },
  { label: '今天末尾', expression: 'endofday(today())', suitableFor: ['datetime'] },
  { label: '昨天末尾', expression: 'endofday(yesterday())', suitableFor: ['datetime'] },
];

/** 偏移量单位：day/week/month/year；week 落库时折算为 7 倍天数（求值器无周偏移函数）。 */
export type RelativeOffsetUnit = 'day' | 'week' | 'month' | 'year';

/** 结构化偏移描述：正数表示未来（后），负数表示过去（前）。 */
export interface RelativeOffset {
  amount: number;
  unit: RelativeOffsetUnit;
}

/** 偏移单位与中文标签的对照，供构造器下拉与动态标签使用。 */
const OFFSET_UNIT_LABELS: ReadonlyArray<{ unit: RelativeOffsetUnit; label: string; daysPerUnit: number }> = [
  { unit: 'day', label: '天', daysPerUnit: 1 },
  { unit: 'week', label: '周', daysPerUnit: 7 },
  { unit: 'month', label: '个月', daysPerUnit: 0 },
  { unit: 'year', label: '年', daysPerUnit: 0 },
];

/**
 * 把结构化偏移量生成为表达式：天/月/年直接映射 adddays/addmonths/addyears，
 * 周折算为 7 倍天数；正数向未来、负数向过去。
 * @param offset - 结构化偏移描述。
 * @returns 可落库的表达式原文。
 */
export function buildOffsetExpression(offset: RelativeOffset): string {
  const amount = Number.isFinite(offset.amount) ? Math.trunc(offset.amount) : 0;
  if (offset.unit === 'week') {
    return `adddays(today(),${amount * 7})`;
  }
  const functionName = offset.unit === 'day' ? 'adddays' : offset.unit === 'month' ? 'addmonths' : 'addyears';
  return `${functionName}(today(),${amount})`;
}

/**
 * 解析偏移形态表达式（adddays/addmonths/addyears(today(),整数)），供存量配置回显为
 * "N 天/个月/年前"动态标签；周偏移落库时已折算为天数，回显按天展示。
 * @param expression - 待解析的表达式原文。
 * @returns 偏移描述；不是偏移形态时返回 null。
 */
export function matchOffsetExpression(expression: string | null): RelativeOffset | null {
  if (!expression) return null;
  const normalized = expression.replace(/\s+/gu, '').toLowerCase();
  const match = normalized.match(/^(adddays|addmonths|addyears)\(today\(\),(-?\d+)\)$/u);
  if (!match) return null;
  const unit: RelativeOffsetUnit = match[1] === 'adddays' ? 'day' : match[1] === 'addmonths' ? 'month' : 'year';
  return { amount: Number(match[2]), unit };
}

/**
 * 把偏移量描述为中文动态标签（如"7 天前""3 个月后"），与目录静态标签同一口径。
 * @param offset - 结构化偏移描述。
 * @returns 面向用户的中文标签。
 */
export function describeOffset(offset: RelativeOffset): string {
  const unitLabel = OFFSET_UNIT_LABELS.find((item) => item.unit === offset.unit)?.label ?? offset.unit;
  return offset.amount < 0 ? `${Math.abs(offset.amount)} ${unitLabel}前` : `${offset.amount} ${unitLabel}后`;
}

/**
 * 按参数数据类型过滤语义目录：date/datetime 显示全部适用条目，
 * month/year 只显示边界与粒度化条目（今年/去年/上月等）。
 * @param dataType - 参数数据类型或控件类型（date/datetime/month/year，其他按 date 处理）。
 * @returns 该类型可选的语义条目列表。
 */
export function getRelativePointPresets(dataType: string): RelativePointPreset[] {
  const normalized = dataType.trim().toLowerCase();
  const key: RelativePointDataType =
    normalized === 'datetime' || normalized === 'month' || normalized === 'year'
      ? (normalized as RelativePointDataType)
      : 'date';
  return RELATIVE_POINT_PRESETS.filter(
    (preset) => preset.suitableFor?.includes(key) ?? (key === 'date' || key === 'datetime'),
  );
}

/**
 * 把已存表达式回显为语义条目：与过滤后的目录逐条精确比对（大小写与空白不敏感）。
 * @param expression - 已存的默认表达式原文。
 * @param dataType - 参数数据类型，决定在哪个子集里匹配。
 * @returns 命中的目录条目；未命中（手写组合/老词表）返回 null，调用方落"高级表达式"形态。
 */
export function matchRelativePointPreset(
  expression: string | null,
  dataType: string,
): RelativePointPreset | null {
  if (!expression || !expression.trim()) return null;
  const normalized = expression.replace(/\s+/gu, '').toLowerCase();
  return (
    getRelativePointPresets(dataType).find((preset) => preset.expression.toLowerCase() === normalized) ?? null
  );
}

/**
 * 按参数控件类型取语义目录（兼容旧调用名）：datetime 控件含时刻类条目，
 * month/year 控件只显示粒度化边界，date 与未知类型按天粒度处理。
 * @param controlType - 参数控件类型。
 * @returns 该控件可选的语义条目列表。
 */
export function getPresetsForControl(controlType: string): RelativePointPreset[] {
  return getRelativePointPresets(controlType);
}

/** 数据同步取数窗口预设：一次填充起止两个变量的滚动表达式。 */
export interface SyncWindowPreset {
  /** 界面展示的中文标签。 */
  label: string;
  /** 窗口起点（startDate 类变量）的表达式。 */
  startExpression: string;
  /** 窗口终点（endDate 类变量）的表达式。 */
  endExpression: string;
  /** 一句话说明窗口语义，展示在按钮提示里。 */
  hint: string;
}

/** 数据同步窗口预设：每天/每月/每年/滚动 N 天四类常用节奏。 */
export const SYNC_WINDOW_PRESETS: SyncWindowPreset[] = [
  {
    label: '每天同步昨天',
    startExpression: 'yesterday()',
    endExpression: 'yesterday()',
    hint: '每天运行取前一天整天的数据',
  },
  {
    label: '每月同步上月',
    startExpression: 'startofmonth(addmonths(today(),-1))',
    endExpression: 'endofmonth(addmonths(today(),-1))',
    hint: '每月运行取上一个自然月的整月数据',
  },
  {
    label: '每年同步去年',
    startExpression: 'startofyear(addyears(today(),-1))',
    endExpression: 'endofyear(addyears(today(),-1))',
    hint: '每年运行取上一个自然年的整年数据',
  },
  {
    label: '滚动最近 7 天',
    startExpression: 'adddays(today(),-7)',
    endExpression: 'today()',
    hint: '每次运行取最近 7 天（含今天）的数据',
  },
  {
    label: '滚动最近 30 天',
    startExpression: 'adddays(today(),-30)',
    endExpression: 'today()',
    hint: '每次运行取最近 30 天（含今天）的数据',
  },
];

/**
 * 按后端 PocDefaultExpressionEvaluator 的封闭文法现算默认表达式。
 * 语义与后端一致：大小写与内部空白不敏感；求值基于本地时间；
 * date/month/year 分别格式化为 yyyy-MM-dd/yyyy-MM/yyyy，其余按 yyyy-MM-dd HH:mm:ss。
 * @param expression - 表达式原文，如 startOfMonth(addMonths(today(),-1))。
 * @param dataType - 参数数据类型，决定最终格式化。
 * @param now - 计算基准时间，测试可传固定值。
 * @returns 格式化后的默认值文本；表达式为空或非法时返回 null（交由必填/固定值兜底）。
 */
export function evaluateDefaultExpression(
  expression: string | null,
  dataType: string,
  now: Date,
): string | null {
  if (!expression || !expression.trim()) return null;
  try {
    const value = parseExpression(expression, now);
    return formatByDataType(value, dataType);
  } catch {
    // 非法表达式不抛出：调用方（导入向导）按 null 回落必填校验，编辑界面用 getDefaultExpressionError 提示原因。
    return null;
  }
}

/**
 * 校验默认表达式并返回中文错误说明，供编辑界面在保存前给出行内提示。
 * 空白表达式视为合法（允许不配置默认值）。
 * @param expression - 待校验的表达式原文。
 * @returns 合法或空白返回 null；非法返回面向用户的错误描述。
 */
export function getDefaultExpressionError(expression: string | null): string | null {
  if (!expression || !expression.trim()) return null;
  try {
    parseExpression(expression, new Date());
    return null;
  } catch (error) {
    return error instanceof Error ? error.message : '默认表达式无效。';
  }
}

/**
 * 取当天零点的本地时间副本，作为日期表达式计算的统一基准。
 * @param value - 任意时间点。
 * @returns 当天 00:00:00.000 的 Date 对象。
 */
function startOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate());
}

/**
 * 把时间回退到所在周的周一零点（周按中国习惯以周一为一周之始），
 * 与后端 PocDefaultExpressionEvaluator.StartOfWeek 同口径：getDay() 周日=0、周一=1，
 * 统一换算为"距周一的天数"（周一 0、周日 6）后回退。
 * @param value - 待截断的时间。
 * @returns 所在周周一 00:00:00.000 的 Date 对象。
 */
function startOfWeek(value: Date): Date {
  return addUnit(startOfDay(value), 'd', -((value.getDay() + 6) % 7));
}

/**
 * 按整数偏移构造新日期（日/月/年三类偏移共用），不修改入参。
 * @param value - 基准日期。
 * @param unit - 偏移单位：d 为天，m 为月，y 为年。
 * @param amount - 偏移量，可为负。
 * @returns 偏移后的新 Date 对象。
 */
function addUnit(value: Date, unit: string, amount: number): Date {
  if (unit === 'd') {
    return new Date(value.getFullYear(), value.getMonth(), value.getDate() + amount);
  }
  if (unit === 'm') {
    return new Date(value.getFullYear(), value.getMonth() + amount, value.getDate());
  }
  return new Date(value.getFullYear() + amount, value.getMonth(), value.getDate());
}

/**
 * 解析并求值表达式：剔除全部空白并小写后按函数文法递归下降解析，
 * 任何文法错误抛出带中文说明的 Error（求值入口捕获后按 null 处理）。
 * @param expression - 表达式原文。
 * @param now - 计算基准时间。
 * @returns 求值结果。
 */
function parseExpression(expression: string, now: Date): Date {
  const text = expression.replace(/\s+/gu, '').toLowerCase();
  const parser = createParser(text, now);
  const value = parser.parseExpression();
  parser.expectEnd();
  return value;
}

/**
 * 创建一个基于闭包状态的解析器：函数表封闭（基础时刻、区间截断、日/月/年偏移），
 * 不存在任意表达式执行面；错误消息携带原始表达式便于定位。
 * @param text - 已剔除空白并小写的规范化表达式。
 * @param now - 计算基准时间。
 * @returns 含 parseExpression/expectEnd 两个方法的解析器对象。
 */
function createParser(text: string, now: Date): {
  parseExpression: () => Date;
  expectEnd: () => void;
} {
  // 阶段一：解析状态与词法基础——位置游标、统一错误收敛、字符查看与期望字符消费。
  let position = 0;

  /**
   * 抛出带原始表达式的文法错误，供各解析步骤统一收敛消息格式。
   * @param detail - 具体文法错误描述。
   */
  const fail = (detail: string): never => {
    throw new Error(`不支持默认表达式 '${text}'：${detail}`);
  };

  /**
   * 只读查看当前位置字符；输入耗尽时返回右括号，使参数缺失统一走括号校验报错。
   * @returns 当前位置字符或 ')'。
   */
  const peek = (): string => (position < text.length ? text[position] : ')');

  /**
   * 消费下一个字符，不匹配则抛文法错误（含原始表达式便于定位）。
   * @param character - 期望出现的字符。
   */
  const expect = (character: string): void => {
    if (position >= text.length || text[position] !== character) {
      fail(character === '(' ? '函数名后缺少左括号。' : `此处应为 '${character}'。`);
    }
    position += 1;
  };

  // 阶段二：记号读取——小写标识符（函数名）与整数常量（含负号）的词法提取。
  /**
   * 读取一个小写字母开头的标识符（函数名）。
   * @returns 标识符文本。
   */
  const readIdentifier = (): string => {
    if (position >= text.length || !/[a-z]/.test(text[position])) {
      fail('此处应为函数名。');
    }
    const start = position;
    while (position < text.length && /[a-z0-9_]/.test(text[position])) {
      position += 1;
    }
    return text.slice(start, position);
  };

  /**
   * 读取一个整数常量（允许负号），供偏移函数的第二个参数使用。
   * @param name - 当前函数名，用于错误消息定位。
   * @returns 解析出的整数偏移量。
   */
  const readInteger = (name: string): number => {
    const start = position;
    if (position < text.length && text[position] === '-') {
      position += 1;
    }
    while (position < text.length && /[0-9]/.test(text[position])) {
      position += 1;
    }
    const token = text.slice(start, position);
    if (!token || token === '-' || !/^-?[0-9]+$/.test(token) || !Number.isSafeInteger(Number(token))) {
      fail(`函数 '${name}' 的偏移量必须是整数常量。`);
    }
    return Number(token);
  };

  // 阶段三：参数形态解析——单时间参数与“时间参数, 整数偏移”两种白名单参数组合。
  /**
   * 解析恰好一个时间型参数（区间截断函数）；多余的参数会在外层括号校验处暴露。
   * @param name - 当前函数名，用于错误消息定位。
   * @returns 参数子表达式的求值结果。
   */
  const parseArgument = (name: string): Date => {
    if (peek() === ')') {
      fail(`函数 '${name}' 需要一个时间参数。`);
    }
    return parseExpression();
  };

  /**
   * 解析“时间参数, 整数偏移”两个参数（日/月/年偏移函数）。
   * @param name - 当前函数名，用于错误消息定位。
   * @returns 基准时间与整数偏移量。
   */
  const parseOffsetArguments = (name: string): { base: Date; offset: number } => {
    if (peek() === ')') {
      fail(`函数 '${name}' 需要时间参数和整数偏移两个参数。`);
    }
    const base = parseExpression();
    expect(',');
    return { base, offset: readInteger(name) };
  };

  // 阶段四：函数分发求值——递归下降的核心循环，嵌套参数在分支内递归进入本函数。
  /**
   * 递归下降解析并求值一个表达式（函数调用）；按封闭函数表分发，嵌套参数递归进入本函数。
   * @returns 该子表达式基于基准时间算出的时间值。
   */
  const parseExpression = (): Date => {
    // 阶段四（续）：读函数名与左括号，随后按封闭白名单分发到各函数的参数形态。
    const name = readIdentifier();
    expect('(');
    let value: Date;
    switch (name) {
      case 'now':
        value = now;
        break;
      case 'today':
        value = startOfDay(now);
        break;
      case 'yesterday':
        value = addUnit(startOfDay(now), 'd', -1);
        break;
      case 'tomorrow':
        value = addUnit(startOfDay(now), 'd', 1);
        break;
      case 'startofday':
        value = startOfDay(parseArgument(name));
        break;
      case 'endofday':
        value = new Date(addUnit(startOfDay(parseArgument(name)), 'd', 1).getTime() - 1);
        break;
      case 'startofmonth': {
        const argument = parseArgument(name);
        value = new Date(argument.getFullYear(), argument.getMonth(), 1);
        break;
      }
      case 'endofmonth': {
        const argument = parseArgument(name);
        value = new Date(new Date(argument.getFullYear(), argument.getMonth() + 1, 1).getTime() - 1);
        break;
      }
      case 'startofweek': {
        const argument = parseArgument(name);
        value = startOfWeek(argument);
        break;
      }
      case 'endofweek': {
        const argument = parseArgument(name);
        value = new Date(addUnit(startOfWeek(argument), 'd', 7).getTime() - 1);
        break;
      }
      case 'startofyear':
        value = new Date(parseArgument(name).getFullYear(), 0, 1);
        break;
      case 'endofyear': {
        const argument = parseArgument(name);
        value = new Date(new Date(argument.getFullYear() + 1, 0, 1).getTime() - 1);
        break;
      }
      case 'adddays':
      case 'addmonths':
      case 'addyears': {
        const { base, offset } = parseOffsetArguments(name);
        value = addUnit(base, name === 'adddays' ? 'd' : name === 'addmonths' ? 'm' : 'y', offset);
        break;
      }
      default:
        fail(`不支持的函数 '${name}'。`);
    }
    // 阶段二：闭合右括号；参数多余/缺失在此处统一暴露为文法错误。
    expect(')');
    return value;
  };

  // 阶段五：对外出口——顶层解析入口与“输入必须耗尽”的完整性校验。
  return {
    /**
     * 顶层解析入口：从当前位置解析并求值一个完整表达式。
     * @returns 表达式的求值结果。
     */
    parseExpression: () => parseExpression(),
    /**
     * 顶层解析结束后要求输入耗尽，拒绝“today()today()”之类的多余尾部内容。
     */
    expectEnd: () => {
      if (position < text.length) {
        fail(`表达式末尾存在多余内容 '${text.slice(position)}'。`);
      }
    },
  };
}

/**
 * 把 0-99 的数字格式化为至少两位的十进制文本。
 * @param value - 待格式化的数字。
 * @returns 两位字符的数字文本。
 */
function pad2(value: number): string {
  return String(value).padStart(2, '0');
}

/**
 * 按参数数据类型格式化求值结果，口径与后端一致：
 * date→yyyy-MM-dd，month→yyyy-MM，year→yyyy，其余→yyyy-MM-dd HH:mm:ss。
 * @param value - 求值结果。
 * @param dataType - 参数数据类型。
 * @returns 格式化后的文本。
 */
function formatByDataType(value: Date, dataType: string): string {
  switch (dataType.trim().toLowerCase()) {
    case 'date':
      return `${value.getFullYear()}-${pad2(value.getMonth() + 1)}-${pad2(value.getDate())}`;
    case 'month':
      return `${value.getFullYear()}-${pad2(value.getMonth() + 1)}`;
    case 'year':
      return String(value.getFullYear());
    default:
      return `${value.getFullYear()}-${pad2(value.getMonth() + 1)}-${pad2(value.getDate())} ` +
        `${pad2(value.getHours())}:${pad2(value.getMinutes())}:${pad2(value.getSeconds())}`;
  }
}
