import UniverPresetSheetsCoreZhCN from '@univerjs/preset-sheets-core/locales/zh-CN';
import { UniverSheetsCoreWorkerPreset } from '@univerjs/preset-sheets-core/worker';
import UniverPresetSheetsFilterZhCN from '@univerjs/preset-sheets-filter/locales/zh-CN';
import { UniverSheetsFilterWorkerPreset } from '@univerjs/preset-sheets-filter/worker';
import { createUniver, LocaleType, mergeLocales } from '@univerjs/presets';

/**
 * 独立 Worker 入口只注册公式计算所需的 Sheets Core Worker preset。
 * 主线程负责界面和工作簿操作，公式依赖图与计算放到 Worker，避免明细展开后阻塞页面交互。
 */
createUniver({
  locale: LocaleType.ZH_CN,
  locales: {
    [LocaleType.ZH_CN]: mergeLocales(
      UniverPresetSheetsCoreZhCN,
      UniverPresetSheetsFilterZhCN,
    ),
  },
  presets: [
    UniverSheetsCoreWorkerPreset(),
    UniverSheetsFilterWorkerPreset(),
  ],
});
