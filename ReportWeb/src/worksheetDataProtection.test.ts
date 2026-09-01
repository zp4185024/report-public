import { describe, expect, it, vi } from 'vitest';
import {
  configureWorksheetDataProtection,
  setWorksheetProtectionShadowVisible,
} from './worksheetDataProtection';

describe('预览工作表数据保护', () => {
  it('预览隐藏保护斜纹，设计模式恢复保护提示', () => {
    const setProtectedRangeShadowStrategy = vi.fn();

    setWorksheetProtectionShadowVisible({ setProtectedRangeShadowStrategy }, false);
    setWorksheetProtectionShadowVisible({ setProtectedRangeShadowStrategy }, true);

    expect(setProtectedRangeShadowStrategy.mock.calls).toEqual([
      ['none'],
      ['always'],
    ]);
  });

  it('保持格式可编辑，只关闭数据写入和直接取消保护', async () => {
    const calls: string[] = [];
    const permission = {
      /**
       * 验证 is Protected 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      isProtected: () => false,
      protect: vi.fn(async () => {
        calls.push('protect');
      }),
      setEditable: vi.fn(async () => {
        calls.push('editable');
      }),
      setPoint: vi.fn(async (point: string, allowed: boolean) => {
        calls.push(`${point}:${allowed}`);
      }),
    };

    const created = await configureWorksheetDataProtection(
      permission,
      true,
      {
        deleteProtection: 'delete-protection',
        filter: 'filter',
        setCellValue: 'set-cell-value',
      },
      '报表预览数据保护',
    );

    expect(created).toBe(true);
    expect(calls).toEqual([
      'protect',
      'editable',
      'filter:true',
      'set-cell-value:false',
      'delete-protection:false',
    ]);
  });

  it('解除数据保护时恢复工作表编辑权限', async () => {
    const permission = {
      /**
       * 验证 is Protected 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      isProtected: () => true,
      protect: vi.fn(async () => undefined),
      setEditable: vi.fn(async () => undefined),
      setPoint: vi.fn(async () => undefined),
    };

    const created = await configureWorksheetDataProtection(
      permission,
      false,
      {
        deleteProtection: 'delete-protection',
        filter: 'filter',
        setCellValue: 'set-cell-value',
      },
      '报表预览数据保护',
    );

    expect(created).toBe(false);
    expect(permission.protect).not.toHaveBeenCalled();
    expect(permission.setEditable).toHaveBeenCalledOnce();
    expect(permission.setPoint).not.toHaveBeenCalled();
  });
});
