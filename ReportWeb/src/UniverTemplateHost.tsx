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
  CalculationMode,
  UniverSheetsCorePreset,
} from '@univerjs/preset-sheets-core';
import '@univerjs/preset-sheets-core/lib/index.css';
import UniverPresetSheetsFilterZhCN from '@univerjs/preset-sheets-filter/locales/zh-CN';
import {
  RemoveSheetFilterCommand,
  SetSheetFilterRangeCommand,
  SmartToggleSheetsFilterCommand,
  UniverSheetsFilterPreset,
} from '@univerjs/preset-sheets-filter';
import '@univerjs/preset-sheets-filter/lib/index.css';
import { createUniver, LocaleType, mergeLocales } from '@univerjs/presets';
import type { IWorkbookData } from '@univerjs/presets';
import {
  createDetailValueBatches,
  type TemplatePreviewPlan,
} from './templatePreview';
import {
  createPreviewFilterTarget,
  formatFilterRange,
  shouldRepairPreviewFilterRange,
  type PreviewFilterTarget,
} from './previewFilter';
import {
  normalizeUniverRuntimeNumber,
  normalizeWorkbookSnapshot,
} from './workbookSnapshot';
import { isTemplateChangeCommand } from './templateChangeCommand';
import { recalculateFormulas } from './formulaCalculation';
import { createSpreadsheetWarningConfig } from './spreadsheetPresentation';
import {
  applyPrintableRichTextStyle,
  printPrintableSheet,
  type PrintableBorder,
  type PrintableCellStyle,
  type PrintableMerge,
  type PrintableRichTextStyleData,
  type PrintableSheet,
} from './workbookPrint';
import {
  configureWorksheetDataProtection,
  setWorksheetProtectionShadowVisible,
} from './worksheetDataProtection';
import UniverWorker from './univer.worker?worker';

/**
 * React 与 Univer facade 的隔离层。
 * 组件只暴露页面需要的业务动作，并在内部管理 Worker、工作簿和订阅生命周期。
 */
export type TemplateEditorMode = 'design' | 'preview';

/** 页面编排层可以调用的最小命令面，避免 App 直接依赖复杂且版本敏感的 Univer facade。 */
export interface UniverTemplateHandle {
  /** 序列化当前设计工作簿，并修正 Univer 运行时与类型声明不一致的字段。 */
  saveDraft(): IWorkbookData;
  /** 用保留 token 的草稿替换当前工作簿并恢复可编辑状态。 */
  loadDesign(snapshot: IWorkbookData): void;
  /** 向当前活动单元格插入参数或明细 token。 */
  insertBinding(token: string, kind: 'parameter' | 'detail'): void;
  /** 基于草稿副本执行预览计划，完成后只禁止修改数据值。 */
  renderPreview(snapshot: IWorkbookData, plan: TemplatePreviewPlan): Promise<number>;
  /** 把当前预览工作表转换为完整 HTML，并在隔离 iframe 中打开浏览器打印。 */
  printCurrentSheet(): Promise<void>;
  /** 为当前选择的连续数据区域启用或关闭 Excel 式列筛选。 */
  toggleFilter(): Promise<string | null>;
  /** 只切换单元格值写入权限；格式、缩放、查看和导出能力保持可用。 */
  setDataProtected(protectedMode: boolean): Promise<void>;
}

/** 宿主通过回调上报用户修改、渲染耗时和已经补充业务上下文的错误。 */
export interface UniverTemplateHostProps {
  initialSnapshot: IWorkbookData;
  mode: TemplateEditorMode;
  reportName?: string;
  toolsExpanded?: boolean;
  suppressNumericTextWarnings?: boolean;
  onChanged: () => void;
  onRendered: (duration: number) => void;
  onError: (error: Error) => void;
}

type UniverRuntime = ReturnType<typeof createUniver>;
type UniverWorkbook = ReturnType<UniverRuntime['univerAPI']['createWorkbook']>;
type UniverWorksheet = NonNullable<ReturnType<UniverWorkbook['getSheetBySheetId']>>;
type UniverRange = ReturnType<UniverWorksheet['getRange']>;
type CommandSubscription = ReturnType<UniverWorkbook['onCommandExecuted']>;
type UniverTextStyle = NonNullable<ReturnType<UniverRange['getCellStyles']>[number][number]>;

interface UniverRichTextRunData {
  ts?: PrintableRichTextStyleData | null;
}

interface UniverRichTextValue {
  /**
   * 查询或读取 UniverTemplateHost 所需的 get Data 数据，并按调用约定返回结果。
   */
  getData(): { body?: { textRuns?: UniverRichTextRunData[] | null } | null };
}

interface UniverRichTextRange extends UniverRange {
  // Univer 0.25.1 运行时已提供该 beta facade，当前类型声明遗漏了读取方法。
  /**
   * 查询或读取 UniverTemplateHost 所需的 get Rich Text Values 数据，并按调用约定返回结果。
   */
  getRichTextValues(): Array<Array<UniverRichTextValue | null>>;
}

interface UniverPrintableBorderEdge {
  cl: { rgb?: string | null };
  s: number;
}

interface UniverPrintableBorderData {
  b?: UniverPrintableBorderEdge | null;
  l?: UniverPrintableBorderEdge | null;
  r?: UniverPrintableBorderEdge | null;
  t?: UniverPrintableBorderEdge | null;
}

interface TemplateOperation {
  /** 单调递增代次；异步步骤恢复后用它判断本操作是否已被后来的工作簿切换取代。 */
  generation: number;
  /** 结束内部操作抑制，确保程序写入不会被命令订阅误报为用户编辑。 */
  releaseInternal: () => void;
}

const operationCancelledMessage = '操作已被新的模板操作取消。';

const hostStyle: CSSProperties = {
  width: '100%',
  height: '100%',
};

/**
 * 将 UniverTemplateHost 中的 normalize Error 输入转换为调用方需要的稳定格式。
 * @param value - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function normalizeError(value: unknown): Error {
  if (value instanceof Error) {
    return value;
  }
  if (typeof value === 'string' && value.length > 0) {
    return new Error(value);
  }
  return new Error('Univer 操作失败，未返回可诊断的错误信息。');
}

/**
 * 负责 UniverTemplateHost 的 operation Error 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param message - message 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param cause - cause 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function operationError(message: string, cause: unknown): Error {
  const detail = normalizeError(cause).message;
  return new Error(detail.length > 0 ? `${message}：${detail}` : message);
}

/**
 * 执行 UniverTemplateHost 的 require Worksheet 核心流程，并保持错误和结果语义稳定。
 * @param workbook - 包含工作簿结构、单元格和样式信息的快照数据。
 * @param sheetId - 需要定位的业务记录标识。
 * @param purpose - purpose 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function requireWorksheet(
  workbook: UniverWorkbook,
  sheetId: string,
  purpose: string,
): UniverWorksheet {
  // facade 的类型在部分版本中不能完整表达 null/抛错行为，这里统一转成带业务用途的错误。
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

/**
 * 执行 UniverTemplateHost 的 require Range 核心流程，并保持错误和结果语义稳定。
 * @param worksheet - 当前读取或修改的工作表对象。
 * @param sheetId - 需要定位的业务记录标识。
 * @param row - 需要遍历、筛选或写入的业务数据集合。
 * @param column - 参与映射、显示或数据写入的字段集合。
 * @param numRows - 需要遍历、筛选或写入的业务数据集合。
 * @param numColumns - 参与映射、显示或数据写入的字段集合。
 * @param purpose - purpose 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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

/**
 * 更新 UniverTemplateHost 的 set Range Value 状态，并同步相关业务数据。
 * @param range - 需要读取、修改或校验的单元格范围。
 * @param value - 需要读取、校验或转换的业务值。
 */
function setRangeValue(
  range: UniverRange,
  value: string | number | boolean | null,
): void {
  // Univer 对“清空单元格”要求显式 cell object；普通标量则可直接写入并保留正确数据类型。
  range.setValue(value === null ? { v: null } : value);
}

/**
 * 等待 UniverTemplateHost 的 wait For Two Animation Frames 前置条件满足后继续执行，避免读取尚未就绪的状态。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function waitForTwoAnimationFrames(): Promise<void> {
  // 第一个动画帧让 Univer 提交内部布局，第二个帧再计时/切换状态，得到用户实际可见的渲染完成点。
  return new Promise((resolve) => {
    requestAnimationFrame(() => {
      requestAnimationFrame(() => resolve());
    });
  });
}

/**
 * 创建并返回 UniverTemplateHost 的 to Printable Border 配置对象，集中提供相关状态和操作入口。
 * @param edge - edge 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function toPrintableBorder(
  edge: UniverPrintableBorderEdge | null | undefined,
): PrintableBorder | undefined {
  if (edge === null || edge === undefined || edge.s === 0) return undefined;
  const widthPt = edge.s === 13
    ? 2.25
    : edge.s >= 8
      ? 1.5
      : edge.s === 7
        ? 2
        : edge.s === 2
          ? 0.25
          : 0.75;
  const style: PrintableBorder['style'] = edge.s === 7
    ? 'double'
    : edge.s === 3
      ? 'dotted'
      : [4, 5, 6, 9, 10, 11, 12].includes(edge.s)
        ? 'dashed'
        : 'solid';
  return {
    color: edge.cl.rgb ?? undefined,
    style,
    widthPt,
  };
}

/**
 * 按 to Printable Cell Style 规则筛选 UniverTemplateHost 数据，并返回满足条件的结果。
 * @param textStyle - textStyle 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param richText - 需要读取、校验或转换的业务值。
 * @param background - background 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param horizontal - horizontal 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param vertical - vertical 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param wrap - wrap 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function toPrintableCellStyle(
  textStyle: UniverTextStyle | null | undefined,
  richText: UniverRichTextValue | null | undefined,
  background: string | undefined,
  horizontal: string | undefined,
  vertical: string | undefined,
  wrap: boolean | undefined,
): PrintableCellStyle {
  // 0.25.1 的 facade 声明没有完整暴露单元格四边结构，运行时仍使用 IBorderData 的 t/r/b/l。
  const borders = textStyle?.border as UniverPrintableBorderData | null | undefined;
  const cellStyle: PrintableCellStyle = {
    background,
    bold: textStyle?.bold,
    borders: borders == null ? undefined : {
      top: toPrintableBorder(borders.t),
      right: toPrintableBorder(borders.r),
      bottom: toPrintableBorder(borders.b),
      left: toPrintableBorder(borders.l),
    },
    color: textStyle?.color?.rgb ?? undefined,
    fontFamily: textStyle?.fontFamily ?? undefined,
    fontSizePt: textStyle?.fontSize,
    horizontal,
    italic: textStyle?.italic,
    strikethrough: textStyle?.strikethrough?.show,
    underline: textStyle?.underline?.show,
    vertical,
    wrap,
  };
  const richTextStyle = richText
    ?.getData()
    .body
    ?.textRuns
    ?.find((run) => run.ts != null)
    ?.ts as PrintableRichTextStyleData | undefined;
  return applyPrintableRichTextStyle(cellStyle, richTextStyle);
}

/**
 * 构建 UniverTemplateHost 所需的 create Printable Sheet 内容，并保证生成结果符合调用约定。
 * @param worksheet - 当前读取或修改的工作表对象。
 * @param reportName - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function createPrintableSheet(
  worksheet: UniverWorksheet,
  reportName?: string,
): PrintableSheet {
  let dataRange: UniverRange;
  let mergeRanges: PrintableMerge[];
  try {
    dataRange = worksheet.getDataRange();
    mergeRanges = worksheet.getMergeData().map((range) => ({ ...range.getRange() }));
  } catch (error) {
    // 第一阶段：根据 createPrintableSheet 当前分支转换输入数据，并保存该阶段产生的中间结果。
    throw operationError('Univer 读取打印范围失败', error);
  }

  let startRow = dataRange.getRow();
  let endRow = dataRange.getLastRow();
  let startColumn = dataRange.getColumn();
  let endColumn = dataRange.getLastColumn();
  for (const merge of mergeRanges) {
    const intersects = merge.startRow <= endRow
      && merge.endRow >= startRow
      && merge.startColumn <= endColumn
      && merge.endColumn >= startColumn;
    if (!intersects) continue;
    startRow = Math.min(startRow, merge.startRow);
    endRow = Math.max(endRow, merge.endRow);
    startColumn = Math.min(startColumn, merge.startColumn);
    endColumn = Math.max(endColumn, merge.endColumn);
  }

  const rowCount = endRow - startRow + 1;
  const columnCount = endColumn - startColumn + 1;
  let range: UniverRange;
  try {
    range = worksheet.getRange(startRow, startColumn, rowCount, columnCount);
  } catch (error) {
    throw operationError('Univer 创建打印数据范围失败', error);
  }

  // 第2阶段：遍历 createPrintableSheet 当前数据集合，逐项转换并维护处理结果。
  try {
    const values = range.getDisplayValues();
    if (!values.some((row) => row.some((value) => value.length > 0))) {
      throw new Error('当前工作表没有可打印内容。');
    }
    const textStyles = range.getCellStyles();
    const richTextValues = (range as UniverRichTextRange).getRichTextValues();
    const backgrounds = range.getBackgrounds();
    const horizontalAlignments = range.getHorizontalAlignments();
    const verticalAlignments = range.getVerticalAlignments();
    const wraps = range.getWraps();
    const rowHeights = Array.from({ length: rowCount }, (_, index) => (
      normalizeUniverRuntimeNumber(
        worksheet.getRowHeight(startRow + index),
        `工作表 '${worksheet.getSheetId()}' 第 ${startRow + index + 1} 行的打印高度`,
      )
    ));
    const columnWidths = Array.from({ length: columnCount }, (_, index) => (
      normalizeUniverRuntimeNumber(
        worksheet.getColumnWidth(startColumn + index),
        `工作表 '${worksheet.getSheetId()}' 第 ${startColumn + index + 1} 列的打印宽度`,
      )
    ));
    // 第3阶段：遍历 createPrintableSheet 当前数据集合，逐项转换并维护处理结果。
    const styles = values.map((row, rowIndex) => row.map((_value, columnIndex) => (
      toPrintableCellStyle(
        textStyles[rowIndex]?.[columnIndex],
        richTextValues[rowIndex]?.[columnIndex],
        backgrounds[rowIndex]?.[columnIndex],
        horizontalAlignments[rowIndex]?.[columnIndex],
        verticalAlignments[rowIndex]?.[columnIndex],
        wraps[rowIndex]?.[columnIndex],
      )
    )));

    return {
      columnWidths,
      documentTitle: reportName,
      merges: mergeRanges,
      name: worksheet.getSheetName(),
      rowHeights,
      showGridlines: worksheet.getSheet().getConfig().showGridlines === 1,
      startColumn,
      startRow,
      styles,
      values,
    };
  } catch (error) {
    throw operationError('Univer 生成打印数据失败', error);
  }
}

export const UniverTemplateHost = forwardRef<
  UniverTemplateHandle,
  UniverTemplateHostProps
>(function UniverTemplateHost(
  {
    initialSnapshot,
    mode,
    reportName,
    toolsExpanded = true,
    suppressNumericTextWarnings = false,
    onChanged,
    onRendered,
    onError,
  },
  ref,
) {
  // 生命周期资源：DOM 容器、Univer 运行时、公式 Worker 和活动工作簿命令订阅必须成组创建/释放。
  const containerRef = useRef<HTMLDivElement>(null);
  const runtimeRef = useRef<UniverRuntime | null>(null);
  const workerRef = useRef<Worker | null>(null);
  const previewFilterTargetRef = useRef<PreviewFilterTarget | null>(null);
  const commandSubscriptionRef = useRef<CommandSubscription | null>(null);
  const currentWorkbookEditableRef = useRef<boolean | null>(null);
  // 深度计数支持嵌套程序操作；大于 0 时命令订阅不得把初始化、预览写入等标成用户修改。
  const internalOperationDepthRef = useRef(0);
  // generation 解决异步 autoFill/动画帧返回乱序问题，新操作开始后旧操作只能取消，不能继续提交状态。
  const operationGenerationRef = useRef(0);
  const activeTemplateOperationRef = useRef<TemplateOperation | null>(null);
  // Univer 运行时只初始化一次，使用 ref 读取最新模式/回调可避免 effect 因函数身份变化而销毁整个编辑器。
  const initialSnapshotRef = useRef(initialSnapshot);
  const modeRef = useRef(mode);
  const onChangedRef = useRef(onChanged);
  const onRenderedRef = useRef(onRendered);
  const onErrorRef = useRef(onError);

  modeRef.current = mode;
  onChangedRef.current = onChanged;
  onRenderedRef.current = onRendered;
  onErrorRef.current = onError;

  const safeNotifyError = useCallback((error: Error) => {
    // 第三方运行时回调边界绝不能让页面回调异常重新逃回 Univer，避免破坏其命令队列。
    try {
      onErrorRef.current(error);
    } catch (callbackError) {
      try {
        console.error('页面错误回调失败。', callbackError);
      } catch {
        // 即使控制台被宿主环境重写并抛错，也不能让错误继续逃入 Univer 或 React。
      }
    }
  }, []);

  const safeNotifyChanged = useCallback(() => {
    try {
      onChangedRef.current();
    } catch (error) {
      safeNotifyError(operationError('页面变更回调失败', error));
    }
  }, [safeNotifyError]);

  const safeNotifyRendered = useCallback((duration: number) => {
    try {
      onRenderedRef.current(duration);
    } catch (error) {
      safeNotifyError(operationError('页面渲染回调失败', error));
    }
  }, [safeNotifyError]);

  const acquireInternalOperation = useCallback(() => {
    // 返回幂等 release，保证 catch/finally 多路径清理不会把深度减成负数。
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
    // 新的模板级操作会使前一个代次失效，并释放它可能仍持有的内部操作抑制。
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

  const attachCommandSubscription = useCallback((
    workbook: UniverWorkbook,
    errorContext: string,
  ): CommandSubscription => {
    try {
      return workbook.onCommandExecuted((commandInfo) => {
        // 只接受设计模式下的顶层用户 COMMAND。选区/滚动 OPERATION 和公式回填 MUTATION 不代表草稿变更。
        if (
          modeRef.current === 'design' &&
          internalOperationDepthRef.current === 0 &&
          isTemplateChangeCommand(commandInfo)
        ) {
          safeNotifyChanged();
        }
      });
    } catch (error) {
      throw operationError(errorContext, error);
    }
  }, [safeNotifyChanged]);

  const replaceWorkbook = useCallback((
    snapshot: IWorkbookData,
    editable: boolean,
    generation: number,
  ) => {
    // Univer 没有“用快照原子替换当前工作簿”的单一 API，本函数把释放旧工作簿、创建新工作簿和失败回滚
    // 组合成事务式流程。任何中间步骤失败都尝试恢复旧快照、编辑状态和命令订阅。
    assertCurrentGeneration(generation);
    const runtime = runtimeRef.current;
    if (runtime === null) {
      throw new Error('Univer 编辑器尚未初始化，无法切换工作簿。');
    }

    // createWorkbook 可能规范化或持有传入对象，必须克隆以保护 App 保存的设计草稿不被第三方运行时修改。
    let normalizedSnapshot: IWorkbookData;
    try {
      normalizedSnapshot = normalizeWorkbookSnapshot(snapshot);
    } catch (error) {
      throw operationError('Univer 归一化目标工作簿快照失败', error);
    }

    let clonedSnapshot: IWorkbookData;
    try {
      clonedSnapshot = structuredClone(normalizedSnapshot);
    } catch (error) {
      throw operationError('Univer 克隆目标工作簿快照失败', error);
    }

    let previousWorkbook: UniverWorkbook | null;
    try {
      previousWorkbook = runtime.univerAPI.getActiveWorkbook();
    } catch (error) {
      throw operationError('Univer 读取当前工作簿失败', error);
    }

    // 在释放旧 unit 前保存完整恢复材料；不能只保留 facade 对象，因为 disposeUnit 后该对象已经失效。
    let previousWorkbookId: string | null = null;
    let previousSnapshot: IWorkbookData | null = null;
    let previousEditable: boolean | null = null;
    if (previousWorkbook !== null) {
      try {
        previousWorkbookId = previousWorkbook.getId();
      } catch (error) {
        throw operationError('Univer 读取当前工作簿标识失败', error);
      }
      previousEditable = currentWorkbookEditableRef.current;
      if (previousEditable === null) {
        throw new Error('Univer 无法确认当前工作簿的编辑状态，已取消切换。');
      }
      try {
        previousSnapshot = normalizeWorkbookSnapshot(previousWorkbook.save());
      } catch (error) {
        throw operationError('Univer 保存旧工作簿恢复快照失败', error);
      }
    }

    const previousSubscription = commandSubscriptionRef.current;
    let previousUnitDisposed = false;
    let newWorkbook: UniverWorkbook | null = null;
    let newSubscription: CommandSubscription | null = null;

    /**
     * 查询或读取 UniverTemplateHost 所需的 get Workbook Id 数据，并按调用约定返回结果。
     * @param workbook - 包含工作簿结构、单元格和样式信息的快照数据。
     * @param context - 本次操作共享的运行上下文。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    const getWorkbookId = (workbook: UniverWorkbook, context: string): string => {
      try {
        return workbook.getId();
      } catch (error) {
        throw operationError(context, error);
      }
    };

    /**
     * 停止 UniverTemplateHost 的 dispose Workbook 流程，并释放监听器、连接或其他运行资源。
     * @param workbook - 包含工作簿结构、单元格和样式信息的快照数据。
     * @param context - 本次操作共享的运行上下文。
     */
    const disposeWorkbook = (workbook: UniverWorkbook, context: string): void => {
      const workbookId = getWorkbookId(workbook, `${context}时读取工作簿标识失败`);
      let disposed: boolean;
      try {
        disposed = runtime.univerAPI.disposeUnit(workbookId);
      } catch (error) {
        throw operationError(`${context} '${workbookId}' 失败`, error);
      }
      if (!disposed) {
        throw new Error(`${context} '${workbookId}' 失败：Univer 返回未释放。`);
      }
    };

    /**
     * 遍历 UniverTemplateHost 数据并执行 restore Previous Workbook 转换，生成后续流程需要的结果。
     */
    const restorePreviousWorkbook = (): void => {
      if (
        previousWorkbook === null ||
        previousWorkbookId === null ||
        previousSnapshot === null ||
        previousEditable === null
      ) {
        return;
      }

      // 回滚尽量完成全部清理并聚合错误，单个 dispose 失败不能阻止后续恢复尝试。
      const rollbackErrors: Error[] = [];
      if (newSubscription !== null) {
        try {
          newSubscription.dispose();
        } catch (error) {
          rollbackErrors.push(operationError('Univer 回滚时释放新工作簿命令订阅失败', error));
        }
      }

      if (newWorkbook !== null) {
        try {
          disposeWorkbook(newWorkbook, 'Univer 回滚时释放新工作簿');
        } catch (error) {
          rollbackErrors.push(normalizeError(error));
        }
      }

      let restoredWorkbook: UniverWorkbook | null = null;
      try {
        let activeWorkbook: UniverWorkbook | null;
        try {
          activeWorkbook = runtime.univerAPI.getActiveWorkbook();
        } catch (error) {
          throw operationError('Univer 回滚时读取活动工作簿失败', error);
        }

        if (!previousUnitDisposed && activeWorkbook === previousWorkbook) {
          // 新工作簿创建前失败时旧 unit 仍有效，可以直接恢复订阅和 editable，避免不必要重建。
          restoredWorkbook = previousWorkbook;
        } else {
          if (activeWorkbook !== null) {
            disposeWorkbook(activeWorkbook, 'Univer 回滚时清理残留工作簿');
          }
          let rollbackSnapshot: IWorkbookData;
          try {
            rollbackSnapshot = structuredClone(previousSnapshot);
          } catch (error) {
            throw operationError('Univer 克隆旧工作簿恢复快照失败', error);
          }
          try {
            restoredWorkbook = runtime.univerAPI.createWorkbook(rollbackSnapshot);
          } catch (error) {
            throw operationError('Univer 重新创建旧工作簿失败', error);
          }
        }

        try {
          restoredWorkbook.setEditable(previousEditable);
        } catch (error) {
          throw operationError('Univer 恢复旧工作簿编辑状态失败', error);
        }
        const restoredSubscription = attachCommandSubscription(
          restoredWorkbook,
          'Univer 恢复旧工作簿命令订阅失败',
        );
        commandSubscriptionRef.current = restoredSubscription;
        currentWorkbookEditableRef.current = previousEditable;
      } catch (error) {
        rollbackErrors.push(normalizeError(error));
      }

      if (rollbackErrors.length > 0) {
        throw new Error(rollbackErrors.map((error) => error.message).join('；'));
      }
    };

    try {
      // 先停订阅再 dispose，避免释放过程中产生的内部命令被误当作用户修改。
      currentWorkbookEditableRef.current = null;
      try {
        previousSubscription?.dispose();
      } catch (error) {
        commandSubscriptionRef.current = null;
        throw operationError('Univer 释放旧工作簿命令订阅失败', error);
      }
      if (commandSubscriptionRef.current === previousSubscription) {
        commandSubscriptionRef.current = null;
      }

      if (previousWorkbook !== null && previousWorkbookId !== null) {
        let disposed: boolean;
        try {
          disposed = runtime.univerAPI.disposeUnit(previousWorkbookId);
        } catch (error) {
          throw operationError(`Univer 释放旧工作簿 '${previousWorkbookId}' 失败`, error);
        }
        if (!disposed) {
          throw new Error(`Univer 无法释放旧工作簿 '${previousWorkbookId}'。`);
        }
        previousUnitDisposed = true;
      }

      assertCurrentGeneration(generation);
      try {
        newWorkbook = runtime.univerAPI.createWorkbook(clonedSnapshot);
      } catch (error) {
        throw operationError('Univer 创建新工作簿失败', error);
      }
      try {
        newWorkbook.setEditable(editable);
      } catch (error) {
        throw operationError('Univer 设置新工作簿编辑状态失败', error);
      }
      newSubscription = attachCommandSubscription(
        newWorkbook,
        'Univer 订阅新工作簿命令失败',
      );
      assertCurrentGeneration(generation);

      commandSubscriptionRef.current = newSubscription;
      currentWorkbookEditableRef.current = editable;
      return newWorkbook;
    } catch (error) {
      if (!isCurrentGeneration(generation)) {
        throw new Error(operationCancelledMessage);
      }

      const loadError = operationError('Univer 加载新工作簿失败', error);
      if (previousWorkbook === null || previousSnapshot === null) {
        // 首次初始化没有旧工作簿可回滚；这里只清理局部资源，外层 effect 负责释放整个 runtime/worker。
        if (newSubscription !== null) {
          try {
            newSubscription.dispose();
          } catch {
            // 没有旧工作簿时由初始化 effect 统一释放运行时资源。
          }
        }
        if (newWorkbook !== null) {
          try {
            disposeWorkbook(newWorkbook, 'Univer 清理加载失败的新工作簿');
          } catch {
            // 没有旧工作簿时由初始化 effect 统一释放运行时资源。
          }
        }
        throw loadError;
      }

      try {
        restorePreviousWorkbook();
      } catch (rollbackError) {
        throw new Error(
          `新工作簿加载失败且旧工作簿恢复失败：${loadError.message}；${normalizeError(rollbackError).message}`,
        );
      }
      throw loadError;
    }
  }, [
    assertCurrentGeneration,
    attachCommandSubscription,
    isCurrentGeneration,
  ]);

  useEffect(() => {
    // Univer 是命令式且重量级的运行时，只在组件挂载时创建一次；模式切换通过替换工作簿完成。
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
    /**
     * 更新 UniverTemplateHost 的 sync Workbench Height 状态，并把变化同步到关联组件或业务数据。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    const syncWorkbenchHeight = () => {
      // Univer 内层 workbench 使用自己的高度；观察容器和子节点可处理侧栏/工具栏挂载导致的尺寸变化。
      const workbench = container.firstElementChild as HTMLElement | null;
      if (workbench !== null) {
        workbench.style.height = `${container.clientHeight}px`;
      }
    };

    try {
      // 主线程 preset 渲染 UI，显式 workerURL 把公式引擎连接到独立 Worker 入口。
      worker = new UniverWorker();
      runtime = createUniver({
        locale: LocaleType.ZH_CN,
        locales: {
          [LocaleType.ZH_CN]: mergeLocales(
            UniverPresetSheetsCoreZhCN,
            UniverPresetSheetsFilterZhCN,
          ),
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
            // 设计模板包含 {{detail.*}} 占位符，初始化时不能把它们当普通文本参与公式计算。
            // 预览写入真实数据后会通过 recalculateFormulas() 显式执行完整计算。
            formula: {
              initialFormulaComputing: CalculationMode.NO_CALCULATION,
            },
            sheets: createSpreadsheetWarningConfig(suppressNumericTextWarnings),
            contextMenu: true,
            disableAutoFocus: true,
          }),
          UniverSheetsFilterPreset(),
        ],
      });

      // Univer 默认只把网格线切换放在工作表底部菜单；报表编辑时应在 Excel 顶部工具栏直接可用。
      // 复用内置命令和图标，保证按钮状态会随活动工作表及命令执行自动同步。
      runtime.univerAPI.createMenu({
        id: 'hd-report-toggle-gridlines',
        title: '网格线',
        tooltip: '显示/隐藏网格线',
        icon: 'HideGridlinesDoubleIcon',
        /**
         * 响应 UniverTemplateHost 的 action 事件，更新界面状态并触发必要的后续操作。
         * @returns 返回值与当前方法声明的类型及空值约束保持一致。
         */
        action: () => {
          void runtime?.univerAPI
            .executeCommand('sheet.command.toggle-gridlines')
            .catch((error: unknown) => {
              safeNotifyError(operationError('切换工作表网格线失败', error));
            });
        },
        order: 2,
      }).appendTo('ribbon.start.history');

      workerRef.current = worker;
      runtimeRef.current = runtime;
      // 初始工作簿加载也视为内部模板操作，屏蔽 createWorkbook 触发的初始化 mutation。
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
      try {
        resizeObserver?.disconnect();
      } catch {
        // 初始化最终只上报一个聚合后的主错误，清理错误不覆盖根因。
      }
      try {
        mutationObserver?.disconnect();
      } catch {
        // 初始化最终只上报一个聚合后的主错误，清理错误不覆盖根因。
      }
      if (resizeFrame !== null) {
        try {
          cancelAnimationFrame(resizeFrame);
        } catch {
          // 初始化最终只上报一个聚合后的主错误，清理错误不覆盖根因。
        }
      }
      try {
        commandSubscriptionRef.current?.dispose();
      } catch {
        // 初始化最终只上报一个聚合后的主错误，清理错误不覆盖根因。
      }
      commandSubscriptionRef.current = null;
      currentWorkbookEditableRef.current = null;
      runtimeRef.current = null;
      workerRef.current = null;
      try {
        runtime?.univer.dispose();
      } catch {
        // 初始化最终只上报一个聚合后的主错误，清理错误不覆盖根因。
      }
      try {
        worker?.terminate();
      } catch {
        // 初始化最终只上报一个聚合后的主错误，清理错误不覆盖根因。
      }
      safeNotifyError(operationError('Univer 初始化失败', error));
      return;
    }

    return () => {
      // 先增加代次让所有挂起异步步骤失效，再按观察器 -> 订阅 -> runtime -> worker 的顺序释放。
      operationGenerationRef.current += 1;
      activeTemplateOperationRef.current?.releaseInternal();
      activeTemplateOperationRef.current = null;
      internalOperationDepthRef.current = 0;
      try {
        resizeObserver?.disconnect();
      } catch (error) {
        safeNotifyError(operationError('Univer 卸载时断开尺寸观察器失败', error));
      }
      try {
        mutationObserver?.disconnect();
      } catch (error) {
        safeNotifyError(operationError('Univer 卸载时断开结构观察器失败', error));
      }
      if (resizeFrame !== null) {
        try {
          cancelAnimationFrame(resizeFrame);
        } catch (error) {
          safeNotifyError(operationError('Univer 卸载时取消渲染帧失败', error));
        }
      }
      try {
        commandSubscriptionRef.current?.dispose();
      } catch (error) {
        safeNotifyError(operationError('Univer 卸载时释放命令订阅失败', error));
      }
      commandSubscriptionRef.current = null;
      currentWorkbookEditableRef.current = null;
      runtimeRef.current = null;
      workerRef.current = null;
      try {
        runtime?.univer.dispose();
      } catch (error) {
        safeNotifyError(operationError('Univer 卸载时释放运行时失败', error));
      }
      try {
        worker?.terminate();
      } catch (error) {
        safeNotifyError(operationError('Univer 卸载时终止 Worker 失败', error));
      }
    };
  }, [
    beginTemplateOperation,
    finishTemplateOperation,
    safeNotifyError,
    replaceWorkbook,
  ]);

  const applyDataProtection = useCallback(async (
    workbook: UniverWorkbook,
    runtime: UniverRuntime,
    protectedMode: boolean,
    protectionName: string,
  ) => {
    const points = {
      setCellValue: runtime.univerAPI.Enum.WorksheetPermissionPoint.SetCellValue,
      deleteProtection: runtime.univerAPI.Enum.WorksheetPermissionPoint.DeleteProtection,
      filter: runtime.univerAPI.Enum.WorksheetPermissionPoint.Filter,
    };
    const permissions = workbook
      .getSheets()
      .map((sheet) => sheet.getWorksheetPermission());
    const previousSnapshots = permissions.map((permission) => permission.getSnapshot());
    const createdProtections = permissions.map(() => false);
    const previousEditable = currentWorkbookEditableRef.current;

    try {
      workbook.setEditable(true);
      currentWorkbookEditableRef.current = true;
      for (let index = 0; index < permissions.length; index += 1) {
        createdProtections[index] = await configureWorksheetDataProtection(
          permissions[index],
          protectedMode,
          points,
          protectionName,
        );
      }
    } catch (error) {
      for (let index = 0; index < permissions.length; index += 1) {
        try {
          if (createdProtections[index]) {
            await permissions[index].unprotect();
          } else if (permissions[index].isProtected()) {
            await permissions[index].applyConfig({ points: previousSnapshots[index] });
          }
        } catch {
          // 保留首个错误作为主要诊断，回滚由后续重新渲染兜底。
        }
      }
      if (previousEditable !== null) {
        try {
          workbook.setEditable(previousEditable);
          currentWorkbookEditableRef.current = previousEditable;
        } catch {
          // 保留权限设置的原始错误。
        }
      }
      throw operationError('Univer 设置数据保护权限失败', error);
    }
  }, []);

  useImperativeHandle(ref, () => ({
    /**
     * 更新 UniverTemplateHost 的 save Draft 状态，并同步相关业务数据。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    saveDraft() {
      // saveDraft 只序列化活动工作簿，不改变模式；归一化后快照才能进入扫描器和 .NET 后端。
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
          return normalizeWorkbookSnapshot(workbook.save());
        } catch (error) {
          throw operationError('Univer 序列化模板草稿失败', error);
        }
      } catch (error) {
        const contextual = operationError('Univer 保存模板草稿失败', error);
        safeNotifyError(contextual);
        throw contextual;
      }
    },

    /**
     * 查询或读取 UniverTemplateHost 所需的 load Design 数据，并按调用约定返回结果。
     * @param snapshot - 包含工作簿结构、单元格和样式信息的快照数据。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    loadDesign(snapshot) {
      // 设计模式永远从保留 token 的 draftSnapshot 创建新工作簿，不复用已经写入后台数据的预览对象。
      const startedAt = performance.now();
      const operation = beginTemplateOperation();
      try {
        previewFilterTargetRef.current = null;
        const runtime = runtimeRef.current;
        if (runtime === null) {
          throw new Error('Univer 编辑器尚未初始化。');
        }
        // 设计模式恢复 Univer 默认的保护范围提示，方便模板作者识别已有保护规则。
        setWorksheetProtectionShadowVisible(runtime.univerAPI, true);
        replaceWorkbook(snapshot, true, operation.generation);
      } catch (error) {
        if (!isCurrentGeneration(operation.generation)) {
          throw new Error(operationCancelledMessage);
        }
        const contextual = operationError('Univer 加载设计模板失败', error);
        safeNotifyError(contextual);
        throw contextual;
      } finally {
        finishTemplateOperation(operation);
      }

      void waitForTwoAnimationFrames().then(() => {
        if (!isCurrentGeneration(operation.generation)) {
          return;
        }
        safeNotifyRendered(performance.now() - startedAt);
      });
    },

    /**
     * 负责 UniverTemplateHost 的 insert Binding 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
     * @param token - 调用受保护接口或校验请求身份所需的访问令牌。
     * @param kind - kind 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    insertBinding(token, kind) {
      try {
        // 先检查模板级操作和 editable 状态，避免在预览切换/异步复制期间写进即将被释放的工作簿。
        if (token.trim().length === 0) {
          throw new Error('绑定 token 不能为空。');
        }
        if (
          activeTemplateOperationRef.current !== null ||
          internalOperationDepthRef.current > 0
        ) {
          throw new Error('模板操作进行中，暂时不能插入绑定 token。');
        }
        if (modeRef.current !== 'design') {
          throw new Error('当前不是设计模式，不能插入绑定 token。');
        }
        if (currentWorkbookEditableRef.current !== true) {
          throw new Error('当前工作簿为只读状态，不能插入绑定 token。');
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

        // 参数 token 可追加到普通文本中；明细 token 必须独占单元格，因此直接替换当前值。
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

        // setValue 会触发 Univer 命令；内部深度先屏蔽订阅，再手工发送一次 changed，保证 dirty 只变化一次。
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
        safeNotifyChanged();
      } catch (error) {
        const contextual = operationError('Univer 插入绑定 token 失败', error);
        safeNotifyError(contextual);
        throw contextual;
      }
    },

    /**
     * 整理 UniverTemplateHost 的 print Current Sheet 数据，生成可下载、导出或打印的最终内容。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    async printCurrentSheet() {
      try {
        if (
          activeTemplateOperationRef.current !== null
          || internalOperationDepthRef.current > 0
        ) {
          throw new Error('模板操作进行中，暂时不能打印。');
        }
        if (modeRef.current !== 'preview') {
          throw new Error('请先预览报表数据，再执行打印。');
        }

        let workbook: UniverWorkbook | null | undefined;
        try {
          workbook = runtimeRef.current?.univerAPI.getActiveWorkbook();
        } catch (error) {
          throw operationError('Univer 获取待打印工作簿失败', error);
        }
        if (workbook == null) {
          throw new Error('Univer 当前没有可打印的工作簿。');
        }

        let worksheet: UniverWorksheet;
        try {
          worksheet = workbook.getActiveSheet();
        } catch (error) {
          throw operationError('Univer 获取待打印工作表失败', error);
        }
        await printPrintableSheet(createPrintableSheet(worksheet, reportName));
      } catch (error) {
        const contextual = operationError('Univer 打印当前工作表失败', error);
        safeNotifyError(contextual);
        throw contextual;
      }
    },

    /**
     * 更新 UniverTemplateHost 的 toggle Filter 状态，并把变化同步到关联组件或业务数据。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    async toggleFilter() {
      try {
        if (
          activeTemplateOperationRef.current !== null
          || internalOperationDepthRef.current > 0
        ) {
          throw new Error('模板操作进行中，暂时不能切换筛选。');
        }

        // 第一阶段：构造 toggleFilter 当前阶段需要的中间数据和执行参数。
        const runtime = runtimeRef.current;
        if (runtime === null) {
          throw new Error('Univer 编辑器尚未初始化。');
        }

        let workbook: UniverWorkbook | null | undefined;
        try {
          workbook = runtime.univerAPI.getActiveWorkbook();
        } catch (error) {
          throw operationError('Univer 获取工作簿失败', error);
        }
        if (workbook == null) {
          throw new Error('Univer 当前没有可筛选的工作簿。');
        }

        const worksheet = workbook.getActiveSheet();
        const commandTarget = {
          unitId: workbook.getId(),
          subUnitId: worksheet.getSheetId(),
        };
        const target = previewFilterTargetRef.current;
        const expectedRange = target?.sheetId === commandTarget.subUnitId
          ? target.range
          : null;
        const existingFilter = worksheet.getFilter();

        if (existingFilter !== null) {
          const removed = await runtime.univerAPI.executeCommand<object, boolean>(
            RemoveSheetFilterCommand.id,
            commandTarget,
          );
          if (!removed) {
            // 第2阶段：根据 toggleFilter 当前分支转换输入数据，并保存该阶段产生的中间结果。
            throw new Error('Univer 未能移除当前筛选范围。');
          }
          return null;
        }

        const activeSelection = workbook.getActiveRange()?.getRange() ?? null;
        let success = await runtime.univerAPI.executeCommand<object, boolean>(
          SmartToggleSheetsFilterCommand.id,
        );
        if (!success && expectedRange !== null) {
          success = await runtime.univerAPI.executeCommand<object, boolean>(
              SetSheetFilterRangeCommand.id,
              { ...commandTarget, range: expectedRange },
            );
        }
        if (!success) {
          throw new Error('请先选择包含表头和数据的连续区域，筛选区域至少需要两行。');
        }

        let appliedFilter = worksheet.getFilter();
        if (appliedFilter === null) {
          throw new Error('Univer 未返回已经启用的筛选范围。');
        }

        const appliedRange = appliedFilter.getRange().getRange();
        if (
          expectedRange !== null
          && shouldRepairPreviewFilterRange(
            activeSelection,
            appliedRange,
            expectedRange,
          )
        ) {
          // 第3阶段：调用 toggleFilter 依赖的异步能力，等待并校验返回数据。
          const removed = await runtime.univerAPI.executeCommand<object, boolean>(
            RemoveSheetFilterCommand.id,
            commandTarget,
          );
          const repaired = removed
            && await runtime.univerAPI.executeCommand<object, boolean>(
              SetSheetFilterRangeCommand.id,
              { ...commandTarget, range: expectedRange },
            );
          if (!repaired) {
            throw new Error('Univer 未能把默认筛选范围校正到完整报表区域。');
          }
          appliedFilter = worksheet.getFilter();
          if (appliedFilter === null) {
            throw new Error('Univer 未返回校正后的筛选范围。');
          }
        }

        return formatFilterRange(appliedFilter.getRange().getRange());
      } catch (error) {
        const contextual = operationError('Univer 切换当前区域筛选失败', error);
        safeNotifyError(contextual);
        throw contextual;
      }
    },

    /**
     * 更新 UniverTemplateHost 的 set Data Protected 状态，并同步相关业务数据。
     * @param protectedMode - protectedMode 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    async setDataProtected(protectedMode) {
      try {
        if (
          activeTemplateOperationRef.current !== null ||
          internalOperationDepthRef.current > 0
        ) {
          throw new Error('模板操作进行中，暂时不能切换数据保护状态。');
        }

        const runtime = runtimeRef.current;
        let workbook: UniverWorkbook | null | undefined;
        try {
          workbook = runtime?.univerAPI.getActiveWorkbook();
        } catch (error) {
          throw operationError('Univer 获取工作簿失败', error);
        }
        if (workbook == null) {
          throw new Error('Univer 当前没有可设置的工作簿。');
        }

        if (runtime === null) {
          throw new Error('Univer 编辑器尚未初始化。');
        }
        setWorksheetProtectionShadowVisible(runtime.univerAPI, !protectedMode);
        await applyDataProtection(workbook, runtime, protectedMode, '报表数据保护');
      } catch (error) {
        const contextual = operationError('Univer 切换数据保护状态失败', error);
        safeNotifyError(contextual);
        throw contextual;
      }
    },

    /**
     * 遍历 UniverTemplateHost 数据并执行 render Preview 转换，生成后续流程需要的结果。
     * @param snapshot - 包含工作簿结构、单元格和样式信息的快照数据。
     * @param plan - plan 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    async renderPreview(snapshot, plan) {
      // 预览始终基于草稿克隆：先替换参数，再展开/复制明细模板行，最后覆盖绑定字段并切只读。
      const startedAt = performance.now();
      const operation = beginTemplateOperation();
      let previewWorkbook: UniverWorkbook | null = null;
      previewFilterTargetRef.current = null;
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

          // 插行前读取源行高度；0.25.1 在部分环境返回 "026" 字符串，必须通过边界归一化。
          let sourceRowHeight: number;
          try {
            sourceRowHeight = normalizeUniverRuntimeNumber(
              worksheet.getRowHeight(detail.templateRow),
              `工作表 '${detail.sheetId}' 第 ${detail.templateRow + 1} 行的高度`,
            );
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

          // 第一条数据复用模板行，所以只为剩余数据插入 result.rows.length - 1 行。
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
            maxColumns = normalizeUniverRuntimeNumber(
              worksheet.getMaxColumns(),
              `工作表 '${detail.sheetId}' 的列数`,
            );
          } catch (error) {
            throw operationError(`Univer 无法读取明细模板列数（工作表 '${detail.sheetId}'）`, error);
          }
          if (!Number.isSafeInteger(maxColumns) || maxColumns < 1) {
            throw new Error(`Univer 返回了无效的明细模板列数（工作表 '${detail.sheetId}'）。`);
          }

          // 复制整行而非逐格复制，才能同时保留样式、数字格式、边框和未绑定的公式列。
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
              // COPY 让相对公式随目标行偏移，例如 E6=C6+D6 会在下一行变成 E7=C7+D7。
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

          // 先复制模板再批量写后台字段：连续字段共用一次命令，字段间隙中的公式仍保留 autoFill 结果。
          for (const batch of createDetailValueBatches(detail.rows)) {
            const range = requireRange(
              worksheet,
              detail.sheetId,
              batch.row,
              batch.column,
              batch.values.length,
              batch.values[0].length,
              '明细批量写入目标',
            );
            const values = batch.values.map((row) => row.map((value) => (
              value === null ? { v: null } : value
            )));
            try {
              // Univer 运行时允许标量与显式清空对象混合，但 0.25.1 的声明只表达两种纯矩阵。
              range.setValues(values as Parameters<UniverRange['setValues']>[0]);
            } catch (error) {
              throw operationError(
                `Univer 批量写入明细失败（工作表 '${detail.sheetId}'，起始行 ${batch.row + 1}，起始列 ${batch.column + 1}，行数 ${batch.values.length}，列数 ${batch.values[0].length}）`,
                error,
              );
            }
          }
        }

        // 设计态公式携带 XLSX 缓存值以避免 token 被提前求值；参数和明细全部替换为真实数据后，
        // 再显式要求 Worker 重算并等待结果回填，确保预览不会继续显示导入时的缓存占位值。
        assertCurrentGeneration(operation.generation);
        const runtime = runtimeRef.current;
        if (runtime === null) {
          throw new Error('Univer 编辑器已释放，无法重算预览公式。');
        }
        try {
          await recalculateFormulas(runtime.univerAPI.getFormula());
        } catch (error) {
          throw operationError('Univer 重算预览公式失败', error);
        }
        assertCurrentGeneration(operation.generation);

        // 所有程序写入完成后只关闭数据值修改，保留字体、颜色、边框和对齐等格式工具。
        assertCurrentGeneration(operation.generation);
        try {
          // 预览数据依然受保护，但不显示会遮盖整张报表的斜纹覆盖层。
          setWorksheetProtectionShadowVisible(runtime.univerAPI, false);
          await applyDataProtection(
            previewWorkbook,
            runtime,
            true,
            '报表预览数据保护',
          );
        } catch (error) {
          throw operationError('Univer 保护预览数据失败', error);
        }
        assertCurrentGeneration(operation.generation);
        await waitForTwoAnimationFrames();
        assertCurrentGeneration(operation.generation);
        const previewFilterTarget = createPreviewFilterTarget(plan);
        previewFilterTargetRef.current = previewFilterTarget;
        if (previewFilterTarget !== null) {
          const filterWorksheet = previewWorkbook.getSheetBySheetId(
            previewFilterTarget.sheetId,
          );
          filterWorksheet
            ?.getRange(
              previewFilterTarget.range.startRow,
              previewFilterTarget.range.startColumn,
              1,
              1,
            )
            .activate();
        }
        const duration = performance.now() - startedAt;
        safeNotifyRendered(duration);
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
            currentWorkbookEditableRef.current = false;
          } catch (readonlyError) {
            const readonlyContext = operationError(
              'Univer 在预览失败后将工作簿切换为只读失败',
              readonlyError,
            );
            contextual = new Error(`${contextual.message}；${readonlyContext.message}`);
          }
        }
        safeNotifyError(contextual);
        throw contextual;
      } finally {
        finishTemplateOperation(operation);
      }
    },
  }), [
    acquireInternalOperation,
    applyDataProtection,
    assertCurrentGeneration,
    beginTemplateOperation,
    finishTemplateOperation,
    isCurrentGeneration,
    reportName,
    safeNotifyChanged,
    safeNotifyError,
    safeNotifyRendered,
    replaceWorkbook,
  ]);

  return (
    <div
      ref={containerRef}
      className={toolsExpanded
        ? 'univer-template-host'
        : 'univer-template-host spreadsheet-tools-collapsed'}
      data-testid="univer-template-host"
      style={hostStyle}
    />
  );
});
