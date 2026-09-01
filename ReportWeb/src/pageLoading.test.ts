import { describe, expect, it } from 'vitest';
import { createPageLoadingPresentation, type PageLoadingState } from './pageLoading';

/**
 * 验证 state 场景下的预期行为，并防止相关逻辑回归。
 * @param overrides - overrides 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function state(overrides: Partial<PageLoadingState> = {}): PageLoadingState {
  return {
    loading: false,
    saving: false,
    importing: false,
    exportingTemplate: false,
    exportingData: false,
    printing: false,
    previewing: false,
    cancellingExport: false,
    mode: 'preview',
    exportJob: null,
    ...overrides,
  };
}

describe('页面级加载状态', () => {
  it('空闲时不显示遮罩', () => {
    expect(createPageLoadingPresentation(state())).toBeNull();
  });

  it('查询状态优先于内部复用的 loading 状态', () => {
    expect(createPageLoadingPresentation(state({ loading: true, previewing: true }))).toEqual({
      title: '正在查询报表数据',
      detail: '正在使用当前参数刷新工作表，请稍候。',
    });
  });

  it('数据导出状态优先于导出前自动保存状态并显示进度', () => {
    expect(createPageLoadingPresentation(state({
      saving: true,
      exportingData: true,
      exportJob: {
        jobNo: 'job-1',
        status: 'Running',
        totalRows: 200,
        writtenRows: 80,
        error: null,
        createdAt: '2026-08-17T14:00:00+08:00',
        completedAt: null,
      },
    }))).toEqual({
      title: '正在导出报表数据',
      detail: '已处理 80 / 200 行，请勿关闭页面。',
    });
  });

  it('取消导出时显示取消状态', () => {
    expect(createPageLoadingPresentation(state({
      exportingData: true,
      cancellingExport: true,
    }))).toEqual({
      title: '正在取消数据导出',
      detail: '正在等待后台任务停止，请稍候。',
    });
  });
});
