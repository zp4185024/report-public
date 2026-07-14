import { useEffect, useMemo, useRef } from 'react';
import UniverPresetSheetsCoreZhCN from '@univerjs/preset-sheets-core/locales/zh-CN';
import { UniverSheetsCorePreset } from '@univerjs/preset-sheets-core';
import '@univerjs/preset-sheets-core/lib/index.css';
import { createUniver, LocaleType, mergeLocales } from '@univerjs/presets';
import { buildWorkbookData, workbookDataToMatrix } from './reportWorkbook';
import type { ExecuteResult, ReportDefinition } from './types';
import UniverWorker from './univer.worker?worker';

interface ReportSheetProps {
  definition: ReportDefinition;
  result: ExecuteResult;
  onRendered: (duration: number) => void;
}

type UniverRuntime = ReturnType<typeof createUniver>;

export function ReportSheet({ definition, result, onRendered }: ReportSheetProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const runtimeRef = useRef<UniverRuntime | null>(null);
  const workerRef = useRef<Worker | null>(null);
  const workbookData = useMemo(
    () => buildWorkbookData(definition, result),
    [definition, result],
  );

  useEffect(() => {
    if (!containerRef.current) {
      return;
    }

    const worker = new UniverWorker();
    const runtime = createUniver({
      locale: LocaleType.ZH_CN,
      locales: {
        [LocaleType.ZH_CN]: mergeLocales(UniverPresetSheetsCoreZhCN),
      },
      presets: [
        UniverSheetsCorePreset({
          container: containerRef.current,
          workerURL: worker,
          header: false,
          toolbar: false,
          footer: {
            sheetBar: true,
            statisticBar: false,
            menus: false,
            zoomSlider: true,
          },
          formulaBar: true,
          contextMenu: true,
          disableAutoFocus: true,
        }),
      ],
    });

    workerRef.current = worker;
    runtimeRef.current = runtime;
    runtime.univerAPI.createWorkbook(workbookData);

    return () => {
      runtimeRef.current = null;
      workerRef.current = null;
      runtime.univer.dispose();
      worker.terminate();
    };
  }, []);

  useEffect(() => {
    const runtime = runtimeRef.current;
    const workbook = runtime?.univerAPI.getActiveWorkbook();
    if (!workbook) {
      return;
    }

    const startedAt = performance.now();
    const worksheet = workbook.getActiveSheet();
    const matrix = workbookDataToMatrix(workbookData);
    const targetRows = 5 + definition.preview.pageSize;
    while (matrix.length < targetRows) {
      matrix.push(Array.from({ length: 6 }, () => ({})));
    }

    worksheet.getRange(0, 0, matrix.length, 6).setValues(matrix);
    requestAnimationFrame(() => {
      requestAnimationFrame(() => onRendered(performance.now() - startedAt));
    });
  }, [definition.preview.pageSize, onRendered, workbookData]);

  return <div ref={containerRef} className="report-sheet" data-testid="report-sheet" />;
}
