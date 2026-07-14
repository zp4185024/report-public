import {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
} from 'react';
import type { CSSProperties } from 'react';
import UniverPresetSheetsCoreZhCN from '@univerjs/preset-sheets-core/locales/zh-CN';
import {
  AUTO_FILL_APPLY_TYPE,
  UniverSheetsCorePreset,
} from '@univerjs/preset-sheets-core';
import '@univerjs/preset-sheets-core/lib/index.css';
import { createUniver, LocaleType, mergeLocales } from '@univerjs/presets';
import type { IWorkbookData } from '@univerjs/presets';
import type { TemplatePreviewPlan } from './templatePreview';
import UniverWorker from './univer.worker?worker';

export type TemplateEditorMode = 'design' | 'preview';

export interface UniverTemplateHandle {
  saveDraft(): IWorkbookData;
  loadDesign(snapshot: IWorkbookData): void;
  insertBinding(token: string, kind: 'parameter' | 'detail'): void;
  renderPreview(snapshot: IWorkbookData, plan: TemplatePreviewPlan): Promise<number>;
}

export interface UniverTemplateHostProps {
  initialSnapshot: IWorkbookData;
  mode: TemplateEditorMode;
  onChanged: () => void;
  onRendered: (duration: number) => void;
  onError: (error: Error) => void;
}

type UniverRuntime = ReturnType<typeof createUniver>;
type UniverWorkbook = ReturnType<UniverRuntime['univerAPI']['createWorkbook']>;
type UniverWorksheet = NonNullable<ReturnType<UniverWorkbook['getSheetBySheetId']>>;
type UniverRange = ReturnType<UniverWorksheet['getRange']>;
type CommandSubscription = ReturnType<UniverWorkbook['onCommandExecuted']>;

const hostStyle: CSSProperties = {
  width: '100%',
  height: '100%',
};

function normalizeError(value: unknown): Error {
  if (value instanceof Error) {
    return value;
  }
  if (typeof value === 'string' && value.length > 0) {
    return new Error(value);
  }
  return new Error('Univer 操作失败，未返回可诊断的错误信息。');
}

function operationError(message: string, cause: unknown): Error {
  const detail = normalizeError(cause).message;
  return new Error(detail.length > 0 ? `${message}：${detail}` : message);
}

function requireWorksheet(
  workbook: UniverWorkbook,
  sheetId: string,
  purpose: string,
): UniverWorksheet {
  let worksheet: UniverWorksheet | null;
  try {
    worksheet = workbook.getSheetBySheetId(sheetId);
  } catch (error) {
    throw operationError(`Univer 无法获取${purpose}工作表 '${sheetId}'`, error);
  }
  if (worksheet === null) {
    throw new Error(`Univer 找不到${purpose}工作表 '${sheetId}'。`);
  }
  return worksheet;
}

function requireRange(
  worksheet: UniverWorksheet,
  sheetId: string,
  row: number,
  column: number,
  numRows: number,
  numColumns: number,
  purpose: string,
): UniverRange {
  let range: UniverRange | null;
  try {
    range = worksheet.getRange(row, column, numRows, numColumns);
  } catch (error) {
    throw operationError(
      `Univer 无法获取${purpose}范围（工作表 '${sheetId}'，行 ${row + 1}，列 ${column + 1}）`,
      error,
    );
  }
  if (range === null) {
    throw new Error(
      `Univer 找不到${purpose}范围（工作表 '${sheetId}'，行 ${row + 1}，列 ${column + 1}）。`,
    );
  }
  return range;
}

function setRangeValue(
  range: UniverRange,
  value: string | number | boolean | null,
): void {
  range.setValue(value === null ? { v: null } : value);
}

function waitForTwoAnimationFrames(): Promise<void> {
  return new Promise((resolve) => {
    requestAnimationFrame(() => {
      requestAnimationFrame(() => resolve());
    });
  });
}

export const UniverTemplateHost = forwardRef<
  UniverTemplateHandle,
  UniverTemplateHostProps
>(function UniverTemplateHost(
  { initialSnapshot, mode, onChanged, onRendered, onError },
  ref,
) {
  const containerRef = useRef<HTMLDivElement>(null);
  const runtimeRef = useRef<UniverRuntime | null>(null);
  const workerRef = useRef<Worker | null>(null);
  const commandSubscriptionRef = useRef<CommandSubscription | null>(null);
  const internalOperationRef = useRef(false);
  const initialSnapshotRef = useRef(initialSnapshot);
  const modeRef = useRef(mode);
  const onChangedRef = useRef(onChanged);
  const onRenderedRef = useRef(onRendered);
  const onErrorRef = useRef(onError);

  modeRef.current = mode;
  onChangedRef.current = onChanged;
  onRenderedRef.current = onRendered;
  onErrorRef.current = onError;

  const notifyError = useCallback((error: Error) => {
    try {
      onErrorRef.current(error);
    } catch {
      // Consumer callback failures must not hide the original Univer error.
    }
  }, []);

  const replaceWorkbook = useCallback((snapshot: IWorkbookData, editable: boolean) => {
    const runtime = runtimeRef.current;
    if (runtime === null) {
      throw new Error('Univer 编辑器尚未初始化，无法切换工作簿。');
    }

    commandSubscriptionRef.current?.dispose();
    commandSubscriptionRef.current = null;

    const activeWorkbook = runtime.univerAPI.getActiveWorkbook();
    if (activeWorkbook !== null) {
      const disposed = runtime.univerAPI.disposeUnit(activeWorkbook.getId());
      if (!disposed) {
        throw new Error(`Univer 无法释放当前工作簿 '${activeWorkbook.getId()}'。`);
      }
    }

    const workbook = runtime.univerAPI.createWorkbook(structuredClone(snapshot));
    workbook.setEditable(editable);
    commandSubscriptionRef.current = workbook.onCommandExecuted(() => {
      if (modeRef.current === 'design' && !internalOperationRef.current) {
        onChangedRef.current();
      }
    });
    return workbook;
  }, []);

  useEffect(() => {
    const container = containerRef.current;
    if (container === null) {
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
          container,
          workerURL: worker,
          header: true,
          toolbar: true,
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
    internalOperationRef.current = true;
    try {
      replaceWorkbook(initialSnapshotRef.current, modeRef.current === 'design');
    } catch (error) {
      notifyError(normalizeError(error));
    } finally {
      internalOperationRef.current = false;
    }

    const syncWorkbenchHeight = () => {
      const workbench = container.firstElementChild as HTMLElement | null;
      if (workbench !== null) {
        workbench.style.height = `${container.clientHeight}px`;
      }
    };
    const resizeObserver = new ResizeObserver(syncWorkbenchHeight);
    const mutationObserver = new MutationObserver(syncWorkbenchHeight);
    resizeObserver.observe(container);
    mutationObserver.observe(container, { childList: true });
    syncWorkbenchHeight();
    requestAnimationFrame(syncWorkbenchHeight);

    return () => {
      resizeObserver.disconnect();
      mutationObserver.disconnect();
      commandSubscriptionRef.current?.dispose();
      commandSubscriptionRef.current = null;
      runtimeRef.current = null;
      workerRef.current = null;
      runtime.univer.dispose();
      worker.terminate();
    };
  }, [notifyError, replaceWorkbook]);

  useImperativeHandle(ref, () => ({
    saveDraft() {
      const workbook = runtimeRef.current?.univerAPI.getActiveWorkbook();
      if (workbook == null) {
        throw new Error('Univer 当前没有可保存的工作簿。');
      }
      return workbook.save();
    },

    loadDesign(snapshot) {
      const startedAt = performance.now();
      internalOperationRef.current = true;
      try {
        replaceWorkbook(snapshot, true);
      } catch (error) {
        internalOperationRef.current = false;
        const normalized = normalizeError(error);
        notifyError(normalized);
        throw normalized;
      }

      void waitForTwoAnimationFrames().then(() => {
        internalOperationRef.current = false;
        try {
          onRenderedRef.current(performance.now() - startedAt);
        } catch (error) {
          notifyError(normalizeError(error));
        }
      });
    },

    insertBinding(token, kind) {
      if (token.trim().length === 0) {
        throw new Error('绑定 token 不能为空。');
      }

      const workbook = runtimeRef.current?.univerAPI.getActiveWorkbook();
      if (workbook == null) {
        throw new Error('Univer 当前没有可编辑的工作簿。');
      }
      const activeCell = workbook.getActiveCell();
      if (activeCell === null) {
        throw new Error('请先在工作表中选择一个单元格。');
      }

      const formula = activeCell.getFormula();
      if (formula.length > 0) {
        throw new Error('不能在公式单元格中插入绑定 token。');
      }

      let nextValue = token;
      if (kind === 'parameter') {
        const currentValue = activeCell.getValue(true);
        if (currentValue != null && typeof currentValue !== 'string') {
          throw new Error('参数绑定只能插入空单元格或文本单元格。');
        }
        nextValue = `${currentValue ?? ''}${token}`;
      }

      const previousInternalState = internalOperationRef.current;
      internalOperationRef.current = true;
      try {
        activeCell.setValue(nextValue);
      } catch (error) {
        throw operationError('Univer 写入绑定 token 失败', error);
      } finally {
        internalOperationRef.current = previousInternalState;
      }
      onChangedRef.current();
    },

    async renderPreview(snapshot, plan) {
      const startedAt = performance.now();
      internalOperationRef.current = true;
      try {
        const workbook = replaceWorkbook(snapshot, true);

        for (const write of plan.parameterWrites) {
          const worksheet = requireWorksheet(workbook, write.sheetId, '参数写入目标');
          const range = requireRange(
            worksheet,
            write.sheetId,
            write.row,
            write.column,
            1,
            1,
            '参数写入目标',
          );
          try {
            setRangeValue(range, write.value);
          } catch (error) {
            throw operationError(
              `Univer 写入参数失败（工作表 '${write.sheetId}'，行 ${write.row + 1}，列 ${write.column + 1}）`,
              error,
            );
          }
        }

        if (plan.detail !== null) {
          const detail = plan.detail;
          const worksheet = requireWorksheet(workbook, detail.sheetId, '明细模板');
          const rowCount = detail.rows.length;
          if (rowCount < 1) {
            throw new Error('明细预览计划至少需要一行目标数据。');
          }

          let sourceRowHeight: number;
          try {
            sourceRowHeight = worksheet.getRowHeight(detail.templateRow);
          } catch (error) {
            throw operationError(
              `Univer 无法读取明细模板行高度（工作表 '${detail.sheetId}'，行 ${detail.templateRow + 1}）`,
              error,
            );
          }
          if (!Number.isFinite(sourceRowHeight) || sourceRowHeight < 0) {
            throw new Error(
              `Univer 返回了无效的明细模板行高度（工作表 '${detail.sheetId}'，行 ${detail.templateRow + 1}）。`,
            );
          }

          if (detail.insertCount > 0) {
            let insertResult: UniverWorksheet;
            try {
              insertResult = worksheet.insertRows(detail.templateRow + 1, detail.insertCount);
            } catch (error) {
              throw operationError(
                `Univer 插入明细行失败（工作表 '${detail.sheetId}'，起始行 ${detail.templateRow + 2}，数量 ${detail.insertCount}）`,
                error,
              );
            }
            if (insertResult == null) {
              throw new Error(
                `Univer 未能插入明细行（工作表 '${detail.sheetId}'，起始行 ${detail.templateRow + 2}，数量 ${detail.insertCount}）。`,
              );
            }
          }

          let maxColumns: number;
          try {
            maxColumns = worksheet.getMaxColumns();
          } catch (error) {
            throw operationError(`Univer 无法读取明细模板列数（工作表 '${detail.sheetId}'）`, error);
          }
          if (!Number.isSafeInteger(maxColumns) || maxColumns < 1) {
            throw new Error(`Univer 返回了无效的明细模板列数（工作表 '${detail.sheetId}'）。`);
          }

          const sourceRange = requireRange(
            worksheet,
            detail.sheetId,
            detail.templateRow,
            0,
            1,
            maxColumns,
            '明细模板行',
          );
          if (rowCount > 1) {
            const targetRange = requireRange(
              worksheet,
              detail.sheetId,
              detail.templateRow,
              0,
              rowCount,
              maxColumns,
              '明细模板复制目标',
            );
            let filled: boolean;
            try {
              filled = await sourceRange.autoFill(targetRange, AUTO_FILL_APPLY_TYPE.COPY);
            } catch (error) {
              throw operationError('Univer 复制明细模板行失败', error);
            }
            if (!filled) {
              throw new Error('Univer 未能复制明细模板行');
            }
          }

          try {
            worksheet.setRowHeights(detail.templateRow, rowCount, sourceRowHeight);
          } catch (error) {
            throw operationError(
              `Univer 设置明细行高度失败（工作表 '${detail.sheetId}'，起始行 ${detail.templateRow + 1}，数量 ${rowCount}）`,
              error,
            );
          }

          for (const row of detail.rows) {
            for (const value of row.values) {
              const range = requireRange(
                worksheet,
                detail.sheetId,
                row.row,
                value.column,
                1,
                1,
                '明细写入目标',
              );
              try {
                setRangeValue(range, value.value);
              } catch (error) {
                throw operationError(
                  `Univer 写入明细失败（工作表 '${detail.sheetId}'，行 ${row.row + 1}，列 ${value.column + 1}）`,
                  error,
                );
              }
            }
          }
        }

        workbook.setEditable(false);
        await waitForTwoAnimationFrames();
        const duration = performance.now() - startedAt;
        onRenderedRef.current(duration);
        return duration;
      } catch (error) {
        const workbook = runtimeRef.current?.univerAPI.getActiveWorkbook();
        try {
          workbook?.setEditable(false);
        } catch {
          // Preserve the original preview error if switching to readonly also fails.
        }
        const normalized = normalizeError(error);
        notifyError(normalized);
        throw normalized;
      } finally {
        internalOperationRef.current = false;
      }
    },
  }), [notifyError, replaceWorkbook]);

  return (
    <div
      ref={containerRef}
      className="univer-template-host"
      data-testid="univer-template-host"
      style={hostStyle}
    />
  );
});
