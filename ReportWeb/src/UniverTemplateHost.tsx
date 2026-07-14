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

interface TemplateOperation {
  generation: number;
  releaseInternal: () => void;
}

const operationCancelledMessage = '操作已被新的模板操作取消。';

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
  const internalOperationDepthRef = useRef(0);
  const operationGenerationRef = useRef(0);
  const activeTemplateOperationRef = useRef<TemplateOperation | null>(null);
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

  const acquireInternalOperation = useCallback(() => {
    internalOperationDepthRef.current += 1;
    let released = false;
    return () => {
      if (released) {
        return;
      }
      released = true;
      internalOperationDepthRef.current = Math.max(0, internalOperationDepthRef.current - 1);
    };
  }, []);

  const beginTemplateOperation = useCallback((): TemplateOperation => {
    activeTemplateOperationRef.current?.releaseInternal();
    const operation: TemplateOperation = {
      generation: operationGenerationRef.current + 1,
      releaseInternal: acquireInternalOperation(),
    };
    operationGenerationRef.current = operation.generation;
    activeTemplateOperationRef.current = operation;
    return operation;
  }, [acquireInternalOperation]);

  const finishTemplateOperation = useCallback((operation: TemplateOperation) => {
    operation.releaseInternal();
    if (activeTemplateOperationRef.current === operation) {
      activeTemplateOperationRef.current = null;
    }
  }, []);

  const isCurrentGeneration = useCallback(
    (generation: number) => operationGenerationRef.current === generation,
    [],
  );

  const assertCurrentGeneration = useCallback((generation: number) => {
    if (operationGenerationRef.current !== generation) {
      throw new Error(operationCancelledMessage);
    }
  }, []);

  const replaceWorkbook = useCallback((
    snapshot: IWorkbookData,
    editable: boolean,
    generation: number,
  ) => {
    assertCurrentGeneration(generation);
    const runtime = runtimeRef.current;
    if (runtime === null) {
      throw new Error('Univer 编辑器尚未初始化，无法切换工作簿。');
    }

    const previousSubscription = commandSubscriptionRef.current;
    try {
      previousSubscription?.dispose();
    } catch (error) {
      throw operationError('Univer 释放工作簿命令订阅失败', error);
    }
    if (commandSubscriptionRef.current === previousSubscription) {
      commandSubscriptionRef.current = null;
    }

    let activeWorkbook: UniverWorkbook | null;
    try {
      activeWorkbook = runtime.univerAPI.getActiveWorkbook();
    } catch (error) {
      throw operationError('Univer 读取当前工作簿失败', error);
    }
    if (activeWorkbook !== null) {
      let activeWorkbookId: string;
      try {
        activeWorkbookId = activeWorkbook.getId();
      } catch (error) {
        throw operationError('Univer 读取当前工作簿标识失败', error);
      }

      let disposed: boolean;
      try {
        disposed = runtime.univerAPI.disposeUnit(activeWorkbookId);
      } catch (error) {
        throw operationError(`Univer 释放当前工作簿 '${activeWorkbookId}' 失败`, error);
      }
      if (!disposed) {
        throw new Error(`Univer 无法释放当前工作簿 '${activeWorkbookId}'。`);
      }
    }

    let clonedSnapshot: IWorkbookData;
    try {
      clonedSnapshot = structuredClone(snapshot);
    } catch (error) {
      throw operationError('Univer 克隆工作簿快照失败', error);
    }

    let workbook: UniverWorkbook;
    try {
      workbook = runtime.univerAPI.createWorkbook(clonedSnapshot);
    } catch (error) {
      throw operationError('Univer 创建工作簿失败', error);
    }

    const disposeCreatedWorkbook = () => {
      try {
        runtime.univerAPI.disposeUnit(workbook.getId());
      } catch {
        // Keep the facade error that made workbook setup fail.
      }
    };

    try {
      workbook.setEditable(editable);
    } catch (error) {
      disposeCreatedWorkbook();
      throw operationError('Univer 设置工作簿编辑状态失败', error);
    }
    let commandSubscription: CommandSubscription;
    try {
      commandSubscription = workbook.onCommandExecuted(() => {
        if (modeRef.current === 'design' && internalOperationDepthRef.current === 0) {
          onChangedRef.current();
        }
      });
    } catch (error) {
      disposeCreatedWorkbook();
      throw operationError('Univer 订阅工作簿命令失败', error);
    }
    if (!isCurrentGeneration(generation)) {
      try {
        commandSubscription.dispose();
      } catch {
        // The cancellation error is more useful than a subscription cleanup failure.
      }
      if (commandSubscriptionRef.current === commandSubscription) {
        commandSubscriptionRef.current = null;
      }
      disposeCreatedWorkbook();
      throw new Error(operationCancelledMessage);
    }
    commandSubscriptionRef.current = commandSubscription;
    return workbook;
  }, [assertCurrentGeneration, isCurrentGeneration]);

  useEffect(() => {
    const container = containerRef.current;
    if (container === null) {
      return;
    }

    let worker: Worker | null = null;
    let runtime: UniverRuntime | null = null;
    let initializationOperation: TemplateOperation | null = null;
    let resizeObserver: ResizeObserver | null = null;
    let mutationObserver: MutationObserver | null = null;
    let resizeFrame: number | null = null;
    const syncWorkbenchHeight = () => {
      const workbench = container.firstElementChild as HTMLElement | null;
      if (workbench !== null) {
        workbench.style.height = `${container.clientHeight}px`;
      }
    };

    try {
      worker = new UniverWorker();
      runtime = createUniver({
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
      initializationOperation = beginTemplateOperation();
      replaceWorkbook(
        initialSnapshotRef.current,
        modeRef.current === 'design',
        initializationOperation.generation,
      );
      finishTemplateOperation(initializationOperation);
      initializationOperation = null;

      resizeObserver = new ResizeObserver(syncWorkbenchHeight);
      mutationObserver = new MutationObserver(syncWorkbenchHeight);
      resizeObserver.observe(container);
      mutationObserver.observe(container, { childList: true });
      syncWorkbenchHeight();
      resizeFrame = requestAnimationFrame(syncWorkbenchHeight);
    } catch (error) {
      if (initializationOperation !== null) {
        finishTemplateOperation(initializationOperation);
      }
      resizeObserver?.disconnect();
      mutationObserver?.disconnect();
      if (resizeFrame !== null) {
        cancelAnimationFrame(resizeFrame);
      }
      try {
        commandSubscriptionRef.current?.dispose();
      } catch {
        // Initialization reports one normalized error below.
      }
      commandSubscriptionRef.current = null;
      runtimeRef.current = null;
      workerRef.current = null;
      try {
        runtime?.univer.dispose();
      } catch {
        // Initialization reports one normalized error below.
      }
      try {
        worker?.terminate();
      } catch {
        // Initialization reports one normalized error below.
      }
      notifyError(operationError('Univer 初始化失败', error));
      return;
    }

    return () => {
      operationGenerationRef.current += 1;
      activeTemplateOperationRef.current?.releaseInternal();
      activeTemplateOperationRef.current = null;
      internalOperationDepthRef.current = 0;
      resizeObserver?.disconnect();
      mutationObserver?.disconnect();
      if (resizeFrame !== null) {
        cancelAnimationFrame(resizeFrame);
      }
      try {
        commandSubscriptionRef.current?.dispose();
      } catch {
        // The component is already unmounting.
      }
      commandSubscriptionRef.current = null;
      runtimeRef.current = null;
      workerRef.current = null;
      try {
        runtime?.univer.dispose();
      } finally {
        worker?.terminate();
      }
    };
  }, [
    beginTemplateOperation,
    finishTemplateOperation,
    notifyError,
    replaceWorkbook,
  ]);

  useImperativeHandle(ref, () => ({
    saveDraft() {
      try {
        let workbook: UniverWorkbook | null | undefined;
        try {
          workbook = runtimeRef.current?.univerAPI.getActiveWorkbook();
        } catch (error) {
          throw operationError('Univer 获取待保存工作簿失败', error);
        }
        if (workbook == null) {
          throw new Error('Univer 当前没有可保存的工作簿。');
        }
        try {
          return workbook.save();
        } catch (error) {
          throw operationError('Univer 序列化模板草稿失败', error);
        }
      } catch (error) {
        const contextual = operationError('Univer 保存模板草稿失败', error);
        notifyError(contextual);
        throw contextual;
      }
    },

    loadDesign(snapshot) {
      const startedAt = performance.now();
      const operation = beginTemplateOperation();
      try {
        replaceWorkbook(snapshot, true, operation.generation);
      } catch (error) {
        if (!isCurrentGeneration(operation.generation)) {
          throw new Error(operationCancelledMessage);
        }
        const contextual = operationError('Univer 加载设计模板失败', error);
        notifyError(contextual);
        throw contextual;
      } finally {
        finishTemplateOperation(operation);
      }

      void waitForTwoAnimationFrames().then(() => {
        if (!isCurrentGeneration(operation.generation)) {
          return;
        }
        try {
          onRenderedRef.current(performance.now() - startedAt);
        } catch (error) {
          notifyError(operationError('Univer 报告设计模板渲染耗时失败', error));
        }
      });
    },

    insertBinding(token, kind) {
      try {
        if (token.trim().length === 0) {
          throw new Error('绑定 token 不能为空。');
        }

        let workbook: UniverWorkbook | null | undefined;
        try {
          workbook = runtimeRef.current?.univerAPI.getActiveWorkbook();
        } catch (error) {
          throw operationError('Univer 获取绑定目标工作簿失败', error);
        }
        if (workbook == null) {
          throw new Error('Univer 当前没有可编辑的工作簿。');
        }

        let activeCell: NonNullable<ReturnType<UniverWorkbook['getActiveCell']>> | null;
        try {
          activeCell = workbook.getActiveCell();
        } catch (error) {
          throw operationError('Univer 获取当前活动单元格失败', error);
        }
        if (activeCell === null) {
          throw new Error('请先在工作表中选择一个单元格。');
        }

        let formula: string;
        try {
          formula = activeCell.getFormula();
        } catch (error) {
          throw operationError('Univer 读取活动单元格公式失败', error);
        }
        if (formula.length > 0) {
          throw new Error('不能在公式单元格中插入绑定 token。');
        }

        let nextValue = token;
        if (kind === 'parameter') {
          let currentValue: ReturnType<typeof activeCell.getValue>;
          try {
            currentValue = activeCell.getValue(true);
          } catch (error) {
            throw operationError('Univer 读取活动单元格值失败', error);
          }
          if (currentValue != null && typeof currentValue !== 'string') {
            throw new Error('参数绑定只能插入空单元格或文本单元格。');
          }
          nextValue = `${currentValue ?? ''}${token}`;
        }

        const releaseInternal = acquireInternalOperation();
        try {
          try {
            activeCell.setValue(nextValue);
          } catch (error) {
            throw operationError('Univer 写入绑定 token 失败', error);
          }
        } finally {
          releaseInternal();
        }
        onChangedRef.current();
      } catch (error) {
        const contextual = operationError('Univer 插入绑定 token 失败', error);
        notifyError(contextual);
        throw contextual;
      }
    },

    async renderPreview(snapshot, plan) {
      const startedAt = performance.now();
      const operation = beginTemplateOperation();
      let previewWorkbook: UniverWorkbook | null = null;
      try {
        previewWorkbook = replaceWorkbook(snapshot, true, operation.generation);

        for (const write of plan.parameterWrites) {
          const worksheet = requireWorksheet(previewWorkbook, write.sheetId, '参数写入目标');
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
          const worksheet = requireWorksheet(previewWorkbook, detail.sheetId, '明细模板');
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
            assertCurrentGeneration(operation.generation);
            try {
              filled = await sourceRange.autoFill(targetRange, AUTO_FILL_APPLY_TYPE.COPY);
            } catch (error) {
              throw operationError('Univer 复制明细模板行失败', error);
            }
            assertCurrentGeneration(operation.generation);
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

        assertCurrentGeneration(operation.generation);
        try {
          previewWorkbook.setEditable(false);
        } catch (error) {
          throw operationError('Univer 将预览工作簿切换为只读失败', error);
        }
        assertCurrentGeneration(operation.generation);
        await waitForTwoAnimationFrames();
        assertCurrentGeneration(operation.generation);
        const duration = performance.now() - startedAt;
        try {
          onRenderedRef.current(duration);
        } catch (error) {
          throw operationError('Univer 报告模板预览渲染耗时失败', error);
        }
        assertCurrentGeneration(operation.generation);
        return duration;
      } catch (error) {
        if (!isCurrentGeneration(operation.generation)) {
          throw new Error(operationCancelledMessage);
        }

        let contextual = operationError('Univer 渲染模板预览失败', error);
        if (previewWorkbook !== null) {
          try {
            previewWorkbook.setEditable(false);
          } catch (readonlyError) {
            const readonlyContext = operationError(
              'Univer 在预览失败后将工作簿切换为只读失败',
              readonlyError,
            );
            contextual = new Error(`${contextual.message}；${readonlyContext.message}`);
          }
        }
        notifyError(contextual);
        throw contextual;
      } finally {
        finishTemplateOperation(operation);
      }
    },
  }), [
    acquireInternalOperation,
    assertCurrentGeneration,
    beginTemplateOperation,
    finishTemplateOperation,
    isCurrentGeneration,
    notifyError,
    replaceWorkbook,
  ]);

  return (
    <div
      ref={containerRef}
      className="univer-template-host"
      data-testid="univer-template-host"
      style={hostStyle}
    />
  );
});
