import { describe, expect, it, vi } from 'vitest';
import {
  prepareSavedRevisionForExport,
  selectSnapshotForSave,
  waitForLargeExportJob,
} from './dataExportFlow';
import type { ExportJob } from './types';

describe('data export save gate', () => {
  it('uses a clean saved revision without confirming or saving', async () => {
    const confirmSave = vi.fn();
    const save = vi.fn();

    const revision = await prepareSavedRevisionForExport({
      revision: 3,
      dirty: false,
      confirmSave,
      save,
    });

    expect(revision).toBe(3);
    expect(confirmSave).not.toHaveBeenCalled();
    expect(save).not.toHaveBeenCalled();
  });

  it('confirms and saves a dirty template before returning the new revision', async () => {
    const calls: string[] = [];

    const revision = await prepareSavedRevisionForExport({
      revision: 3,
      dirty: true,
      /**
       * 验证 confirm Save 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      confirmSave: () => {
        calls.push('confirm');
        return true;
      },
      /**
       * 验证 save 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      save: async () => {
        calls.push('save');
        return 4;
      },
    });
    calls.push(`export:revision-${revision}`);

    expect(calls).toEqual(['confirm', 'save', 'export:revision-4']);
  });

  it('returns null without saving when the user rejects confirmation', async () => {
    const save = vi.fn();

    const revision = await prepareSavedRevisionForExport({
      revision: 0,
      dirty: false,
      /**
       * 验证 confirm Save 场景下的预期行为，并防止相关逻辑回归。
       * @returns 返回值与当前方法声明的类型及空值约束保持一致。
       */
      confirmSave: () => false,
      save,
    });

    expect(revision).toBeNull();
    expect(save).not.toHaveBeenCalled();
  });

  it('uses the retained design draft while exporting from preview', () => {
    const designSnapshot = { id: 'design-draft' };
    const captureEditor = vi.fn(() => ({ id: 'expanded-preview' }));

    const selected = selectSnapshotForSave('preview', captureEditor, designSnapshot);

    expect(selected).toBe(designSnapshot);
    expect(captureEditor).not.toHaveBeenCalled();
  });
});

describe('large data export polling', () => {
  it('polls pending jobs and resolves only after completion', async () => {
    const pending = createJob('Pending', 0);
    const running = createJob('Running', 70);
    const completed = createJob('Completed', 137);
    const getJob = vi.fn()
      .mockResolvedValueOnce(running)
      .mockResolvedValueOnce(completed);
    const updates: ExportJob[] = [];

    const result = await waitForLargeExportJob(
      pending,
      getJob,
      async () => undefined,
      (job) => updates.push(job),
    );

    expect(result).toBe(completed);
    expect(getJob).toHaveBeenCalledTimes(2);
    expect(updates).toEqual([pending, running, completed]);
  });

  it('surfaces a failed background job', async () => {
    const failed = { ...createJob('Failed', 20), error: '数据库查询失败' };

    await expect(waitForLargeExportJob(
      failed,
      vi.fn(),
      async () => undefined,
      vi.fn(),
    )).rejects.toThrow('数据库查询失败');
  });
});

/**
 * 验证 create Job 场景下的预期行为，并防止相关逻辑回归。
 * @param status - status 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param writtenRows - 需要遍历、筛选或写入的业务数据集合。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function createJob(status: ExportJob['status'], writtenRows: number): ExportJob {
  return {
    jobNo: 'job-1',
    status,
    totalRows: 137,
    writtenRows,
    error: null,
    createdAt: '2026-07-15T10:00:00+08:00',
    completedAt: status === 'Completed' ? '2026-07-15T10:00:01+08:00' : null,
  };
}
