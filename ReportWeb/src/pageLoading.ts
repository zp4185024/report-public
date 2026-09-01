import type { ExportJob } from './types';

export interface PageLoadingState {
  loading: boolean;
  saving: boolean;
  importing: boolean;
  exportingTemplate: boolean;
  exportingData: boolean;
  printing: boolean;
  previewing: boolean;
  cancellingExport: boolean;
  mode: 'design' | 'preview';
  exportJob: ExportJob | null;
}

export interface PageLoadingPresentation {
  title: string;
  detail: string;
}

/**
 * 整理 pageLoading 的 export Progress 数据，并生成可供调用方使用的输出。
 * @param job - job 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function exportProgress(job: ExportJob | null): string {
  if (job === null || (job.status !== 'Pending' && job.status !== 'Running')) {
    return '正在准备并生成下载文件，请勿关闭页面。';
  }

  const total = job.totalRows > 0 ? ` / ${job.totalRows}` : '';
  return `已处理 ${job.writtenRows}${total} 行，请勿关闭页面。`;
}

/** 把互斥的异步操作统一转换为页面级加载状态，重叠状态按用户当前任务显示。 */
export function createPageLoadingPresentation(
  state: PageLoadingState,
): PageLoadingPresentation | null {
  if (state.previewing) {
    return state.mode === 'design'
      ? {
          title: '正在生成数据预览',
          detail: '正在查询数据并渲染工作表，请稍候。',
        }
      : {
          title: '正在查询报表数据',
          detail: '正在使用当前参数刷新工作表，请稍候。',
        };
  }

  // 第一阶段：检查 createPageLoadingPresentation 当前条件，并按匹配分支执行对应处理。
  if (state.exportingData) {
    return state.cancellingExport
      ? {
          title: '正在取消数据导出',
          detail: '正在等待后台任务停止，请稍候。',
        }
      : {
          title: '正在导出报表数据',
          detail: exportProgress(state.exportJob),
        };
  }

  if (state.importing) {
    return {
      title: '正在导入模板',
      detail: '正在解析并加载 XLSX 模板，请稍候。',
    };
  }

  if (state.exportingTemplate) {
    return {
      title: '正在导出模板',
      detail: '正在生成 XLSX 模板文件，请稍候。',
    };
  }

  // 第2阶段：检查 createPageLoadingPresentation 当前条件，并按匹配分支执行对应处理。
  if (state.printing) {
    return {
      title: '正在准备打印',
      detail: '正在整理当前工作表的打印内容，请稍候。',
    };
  }

  if (state.saving) {
    return {
      title: '正在保存模板',
      detail: '正在校验并保存当前模板，请稍候。',
    };
  }

  if (state.loading) {
    return {
      title: '正在加载报表',
      detail: '正在读取报表定义和模板，请稍候。',
    };
  }

  return null;
}
