import type { ExportJob } from './types';

export const saveBeforeDataExportMessage =
  '当前模板尚未保存，是否保存后继续导出数据？';

export interface SavedRevisionGate {
  revision: number;
  dirty: boolean;
  /**
   * 负责 dataExportFlow 的 confirm Save 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
   */
  confirmSave(): boolean;
  /**
   * 更新 dataExportFlow 的 save 状态，并同步相关业务数据。
   */
  save(): Promise<number>;
}

/**
 * 数据导出只能引用数据库 revision。dirty 或 revision 0 时先确认并保存，取消时返回 null。
 */
export async function prepareSavedRevisionForExport(
  gate: SavedRevisionGate,
): Promise<number | null> {
  if (!gate.dirty && gate.revision > 0) {
    return gate.revision;
  }
  if (!gate.confirmSave()) {
    return null;
  }

  const savedRevision = await gate.save();
  if (!Number.isSafeInteger(savedRevision) || savedRevision <= 0) {
    throw new Error('模板保存成功后未返回有效 revision。');
  }
  return savedRevision;
}

/** 预览工作簿包含已替换参数和展开行，保存时必须继续使用进入预览前保留的设计草稿。 */
export function selectSnapshotForSave<T>(
  mode: 'design' | 'preview',
  captureDesignSnapshot: () => T,
  retainedDesignSnapshot: T | null,
): T {
  if (mode === 'design') {
    return captureDesignSnapshot();
  }
  if (retainedDesignSnapshot === null) {
    throw new Error('没有可保存的模板设计草稿。');
  }
  return retainedDesignSnapshot;
}

/** 轮询后台任务直到可下载；状态变化通过 onUpdate 交给页面展示进度。 */
export async function waitForLargeExportJob(
  initial: ExportJob,
  getJob: (jobNo: string) => Promise<ExportJob>,
  delay: (milliseconds: number) => Promise<void> = (milliseconds) =>
    new Promise((resolve) => window.setTimeout(resolve, milliseconds)),
  onUpdate: (job: ExportJob) => void = () => undefined,
): Promise<ExportJob> {
  let current = initial;
  while (true) {
    onUpdate(current);
    switch (current.status) {
      case 'Completed':
        return current;
      case 'Failed':
        throw new Error(current.error || '大文件导出失败。');
      case 'Cancelled':
        throw new Error('大文件导出已取消。');
      case 'Pending':
      case 'Running':
        await delay(600);
        current = await getJob(current.jobNo);
        break;
    }
  }
}
