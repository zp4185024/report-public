import { describe, expect, it } from 'vitest';
import {
  buildOffsetExpression,
  describeOffset,
  evaluateDefaultExpression,
  getDefaultExpressionError,
  getRelativePointPresets,
  matchOffsetExpression,
  matchRelativePointPreset,
  SYNC_WINDOW_PRESETS,
} from './defaultExpressions';

/** 与后端求值器测试同一基准时间：2026-08-18 14:35:20（本地时区，星期二）。 */
const CURRENT = new Date(2026, 7, 18, 14, 35, 20);

describe('evaluateDefaultExpression', () => {
  it('preserves legacy presets exactly', () => {
    expect(evaluateDefaultExpression('today()', 'date', CURRENT)).toBe('2026-08-18');
    expect(evaluateDefaultExpression('yesterday()', 'date', CURRENT)).toBe('2026-08-17');
    expect(evaluateDefaultExpression('startOfMonth(today())', 'date', CURRENT)).toBe('2026-08-01');
    expect(evaluateDefaultExpression('endOfDay(yesterday())', 'datetime', CURRENT)).toBe('2026-08-17 23:59:59');
  });

  it('computes rolling windows with offsets and nesting', () => {
    expect(evaluateDefaultExpression('startOfMonth(addMonths(today(),-1))', 'date', CURRENT)).toBe('2026-07-01');
    expect(evaluateDefaultExpression('endOfMonth(addMonths(today(),-1))', 'date', CURRENT)).toBe('2026-07-31');
    expect(evaluateDefaultExpression('addMonths(today(),-1)', 'month', CURRENT)).toBe('2026-07');
    expect(evaluateDefaultExpression('addYears(today(),-1)', 'year', CURRENT)).toBe('2025');
    expect(evaluateDefaultExpression('addDays(today(),-7)', 'date', CURRENT)).toBe('2026-08-11');
    expect(evaluateDefaultExpression('endOfYear(addYears(today(),-1))', 'date', CURRENT)).toBe('2025-12-31');
    expect(evaluateDefaultExpression('startOfDay(addDays(today(),-7))', 'datetime', CURRENT)).toBe('2026-08-11 00:00:00');
  });

  it('evaluates week functions with Monday as week start', () => {
    // 2026-08-18 是周二：本周周一 08-17、周日 08-23；上周一 08-10、上周日 08-16。
    expect(evaluateDefaultExpression('startOfWeek(today())', 'date', CURRENT)).toBe('2026-08-17');
    expect(evaluateDefaultExpression('endOfWeek(today())', 'date', CURRENT)).toBe('2026-08-23');
    expect(evaluateDefaultExpression('startOfWeek(addDays(today(),-7))', 'date', CURRENT)).toBe('2026-08-10');
    expect(evaluateDefaultExpression('endOfWeek(addDays(today(),-7))', 'date', CURRENT)).toBe('2026-08-16');
  });

  it('is insensitive to case and inner whitespace', () => {
    expect(evaluateDefaultExpression('StartOfMonth( AddMonths( Today() , -1 ) )', 'date', CURRENT)).toBe('2026-07-01');
  });

  it('returns null for blank or malformed expressions', () => {
    expect(evaluateDefaultExpression(null, 'date', CURRENT)).toBeNull();
    expect(evaluateDefaultExpression('   ', 'date', CURRENT)).toBeNull();
    expect(evaluateDefaultExpression('now', 'date', CURRENT)).toBeNull();
    expect(evaluateDefaultExpression('foo()', 'date', CURRENT)).toBeNull();
    expect(evaluateDefaultExpression('adddays(today())', 'date', CURRENT)).toBeNull();
    expect(evaluateDefaultExpression('today()today()', 'date', CURRENT)).toBeNull();
  });

  it('every shipped window preset evaluates to a stable start/end pair', () => {
    for (const preset of SYNC_WINDOW_PRESETS) {
      expect(evaluateDefaultExpression(preset.startExpression, 'date', CURRENT)).toBeTruthy();
      expect(evaluateDefaultExpression(preset.endExpression, 'date', CURRENT)).toBeTruthy();
    }
    // 每月同步上月：起点 7-1、终点 7-31，验证预设语义。
    const monthly = SYNC_WINDOW_PRESETS.find((preset) => preset.label === '每月同步上月');
    expect(evaluateDefaultExpression(monthly?.startExpression ?? null, 'date', CURRENT)).toBe('2026-07-01');
    expect(evaluateDefaultExpression(monthly?.endExpression ?? null, 'date', CURRENT)).toBe('2026-07-31');
  });
});

describe('relative point catalog', () => {
  it('shows datetime-only entries only for datetime parameters', () => {
    const datetimePresets = getRelativePointPresets('datetime');
    expect(datetimePresets.some((preset) => preset.label === '当前时刻')).toBe(true);
    expect(datetimePresets.some((preset) => preset.label === '上月第一天')).toBe(true);
    const datePresets = getRelativePointPresets('date');
    expect(datePresets.some((preset) => preset.label === '当前时刻')).toBe(false);
    expect(datePresets.some((preset) => preset.label === '上月第一天')).toBe(true);
  });

  it('shows granular boundaries for month and year parameters', () => {
    const monthPresets = getRelativePointPresets('month');
    expect(monthPresets.some((preset) => preset.label === '上月')).toBe(true);
    expect(monthPresets.some((preset) => preset.label === '去年最后一天')).toBe(true);
    expect(monthPresets.some((preset) => preset.label === '7 天前')).toBe(false);
    expect(monthPresets.some((preset) => preset.label === '当前时刻')).toBe(false);
    const yearPresets = getRelativePointPresets('year');
    expect(yearPresets.some((preset) => preset.label === '去年')).toBe(true);
    expect(yearPresets.some((preset) => preset.label === '上月')).toBe(false);
  });

  it('matches stored expressions back to semantic entries case-insensitively', () => {
    expect(matchRelativePointPreset('startOfMonth(addMonths(today(),-1))', 'date')?.label).toBe('上月第一天');
    expect(matchRelativePointPreset('startofmonth(addmonths(today(),-1))', 'datetime')?.label).toBe('上月第一天');
    expect(matchRelativePointPreset('startoflastmonth()', 'date')).toBeNull();
    expect(matchRelativePointPreset(null, 'date')).toBeNull();
  });
});

describe('offset builder', () => {
  it('builds expressions for each unit with sign direction', () => {
    expect(buildOffsetExpression({ amount: -7, unit: 'day' })).toBe('adddays(today(),-7)');
    expect(buildOffsetExpression({ amount: 2, unit: 'week' })).toBe('adddays(today(),14)');
    expect(buildOffsetExpression({ amount: -3, unit: 'month' })).toBe('addmonths(today(),-3)');
    expect(buildOffsetExpression({ amount: 1, unit: 'year' })).toBe('addyears(today(),1)');
  });

  it('parses offset expressions back for display', () => {
    expect(matchOffsetExpression('addDays( today() , -30 )')).toEqual({ amount: -30, unit: 'day' });
    expect(matchOffsetExpression('startofmonth(today())')).toBeNull();
    expect(describeOffset({ amount: -3, unit: 'month' })).toBe('3 个月前');
    expect(describeOffset({ amount: 7, unit: 'day' })).toBe('7 天后');
  });

  it('roundtrips offset expressions through evaluation', () => {
    const expression = buildOffsetExpression({ amount: -7, unit: 'day' });
    expect(evaluateDefaultExpression(expression, 'date', CURRENT)).toBe('2026-08-11');
  });
});

describe('getDefaultExpressionError', () => {
  it('accepts blank and valid expressions', () => {
    expect(getDefaultExpressionError(null)).toBeNull();
    expect(getDefaultExpressionError('')).toBeNull();
    expect(getDefaultExpressionError('startOfMonth(addMonths(today(),-1))')).toBeNull();
    expect(getDefaultExpressionError('startofweek(adddays(today(),-7))')).toBeNull();
  });

  it('returns a localized error for malformed expressions', () => {
    const error = getDefaultExpressionError('startoflastmonth()');
    expect(error).toContain('startoflastmonth');
  });
});
