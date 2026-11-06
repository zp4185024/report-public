import { describe, expect, it } from 'vitest';
import {
  confirmDeleteMessage,
  deleteErrorMessage,
  pickResourceAfterDeletion,
} from './assetDeletion';

describe('assetDeletion', () => {
  /** 验证确认文案包含资源类型、名称、编码和不可恢复警告。 */
  it('builds a confirmation message with label, name and code', () => {
    expect(confirmDeleteMessage('数据源', { code: 'his_db', name: 'HIS 库' }))
      .toBe('确认删除数据源「HIS 库」（his_db）？删除后不可恢复。');
  });

  /** 验证删除中间项后优先选中后一项。 */
  it('prefers the item after the deleted one', () => {
    const items = [
      { code: 'a', name: 'A' },
      { code: 'b', name: 'B' },
      { code: 'c', name: 'C' },
    ];

    expect(pickResourceAfterDeletion(items, 'b')).toEqual({ code: 'c', name: 'C' });
  });

  /** 验证删除最后一项时回退到前一项，而不是停留在已删除的选择上。 */
  it('falls back to the previous item when deleting the last one', () => {
    const items = [
      { code: 'a', name: 'A' },
      { code: 'b', name: 'B' },
    ];

    expect(pickResourceAfterDeletion(items, 'b')).toEqual({ code: 'a', name: 'A' });
  });

  /** 验证列表被删空时返回 null，由页面进入新增草稿。 */
  it('returns null when the list becomes empty', () => {
    expect(pickResourceAfterDeletion([{ code: 'a', name: 'A' }], 'a')).toBeNull();
    expect(pickResourceAfterDeletion([], 'a')).toBeNull();
  });

  /** 验证编码不在列表（例如已过期）时退回首项而不是报错。 */
  it('falls back to the first item when the code is unknown', () => {
    const items = [
      { code: 'a', name: 'A' },
      { code: 'b', name: 'B' },
    ];

    expect(pickResourceAfterDeletion(items, 'gone')).toEqual({ code: 'a', name: 'A' });
  });

  /** 验证后端冲突消息原样透出，兜底文案只在非 Error 抛出值时使用。 */
  it('passes through backend conflict messages and falls back for non-errors', () => {
    expect(deleteErrorMessage(new Error('DATASET_IN_USE: 数据集被报表引用'), '数据集'))
      .toBe('DATASET_IN_USE: 数据集被报表引用');
    expect(deleteErrorMessage('boom', '数据集')).toBe('数据集删除失败。');
  });
});
