import { Fragment, Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ArrowLeft,
  Bot,
  CircleCheck,
  Download,
  FilePenLine,
  History,
  LoaderCircle,
  Play,
  Plus,
  Power,
  ScrollText,
  Trash2,
  TriangleAlert,
  Wand2,
  Zap,
} from 'lucide-react';
import { aiApi, dataAssetApi, dataSyncApi } from './api';
import { confirmDeleteMessage, deleteErrorMessage } from './assetDeletion';
import { StatusToasts } from './StatusToasts';
import {
  applyAiSyncMatchSuggestions,
  buildDefaultTableMappings,
  buildInitialMappings,
  buildTableSelectSql,
  buildTryRunPairColumns,
  conflictPolicyLabel,
  CONFLICT_POLICY_OPTIONS,
  CONVERTER_CATALOG,
  converterLabel,
  converterSummary,
  createConverterStep,
  CRON_EXPRESSION_HINT,
  findConverterDescriptor,
  formatColumnWithDescription,
  previewCronExpression,
  scheduleCronForSave,
  scheduleSummary,
  SCHEDULE_TYPE_OPTIONS,
  SKIP_TARGET_OPTION,
  targetColumnsFromMetadata,
  tryRunCellDisplay,
  validateCronScheduleInput,
  validateSyncMappings,
  type AiSyncMatchMark,
  type SyncSourceColumn,
  type SyncTargetColumn,
} from './syncTaskDesign';
import type {
  DataSyncSampleRow,
  DataSyncTryRunResult,
  DataSourceDocument,
  DatasetDocument,
  DatasetPreviewResult,
  DeletedDocument,
  SyncColumnMapping,
  SyncColumnMetadata,
  SyncConverterStep,
  SyncRunLogDocument,
  SyncTaskDocument,
  SyncTaskInput,
} from './types';

/** 运行记录面板里每条运行最多展开展示的失败明细条数。 */
const RUN_FAILURE_DISPLAY_LIMIT = 5;

/** 设计器里“立即试跑”请求的样例行数上限；服务端自身封顶 50 行。 */
const DESIGN_TRY_RUN_ROW_LIMIT = 10;

/** 列表页“立即执行”后的轮询间隔（毫秒）。 */
const RUN_POLL_INTERVAL_MS = 5000;

/** 源取数预览面板展示的样例行数上限，完整样例可展开区域滚动查看。 */
const SOURCE_PREVIEW_ROW_LIMIT = 5;

/** SQL 编辑器体积较大，与数据资产页保持一致按需加载，避免拖慢任务列表首屏。 */
const SqlEditor = lazy(() => import('./SqlEditor'));

/**
 * 设计器的任务编辑草稿：包含任务列配置、源/目标设计状态与映射表格数据。
 * isNew 标记尚未落库的新任务——试跑/导出包等按已保存任务解析的接口不可用，
 * 但源取数预览（preview-source）走独立端点，新任务同样可用。
 */
interface SyncTaskDraft {
  code: string;
  name: string;
  isNew: boolean;
  /** 服务端绑定的源数据集编码；保存携带 source 节时服务端会自动维护它。 */
  sourceDatasetCode: string;
  /** 源取数模式：table=选表（SQL 由前端按方言生成），sql=直接写 SQL。 */
  sourceMode: 'table' | 'sql';
  sourceDataSourceCode: string;
  sourceTableName: string;
  sourceQueryText: string;
  /** 目标模式：existing=选择现有表（目标字段下拉来自真实列），create=新建表（目标字段为文本）。 */
  targetMode: 'existing' | 'create';
  targetDataSourceCode: string;
  targetTable: string;
  conflictPolicy: string;
  scheduleType: string;
  scheduleTime: string;
  intervalMinutes: number;
  /** Cron 调度表达式原文；仅调度类型为 Cron 时有意义，其余类型保存时裁剪为 null。 */
  scheduleCron: string;
  enabled: boolean;
  revision: number;
  mappings: SyncColumnMapping[];
}

/**
 * 从既有任务文档构造设计器草稿，保留服务端返回的乐观并发修订号。
 * 源状态从绑定的数据集回填（数据源 + 原样 SQL），保存时按新契约自动维护源数据集。
 * @param document - 服务端返回的任务文档。
 * @param datasets - 已加载的全局数据集列表，用于回填源数据集定义。
 * @returns 可直接编辑的设计器草稿。
 */
function draftFromDocument(document: SyncTaskDocument, datasets: readonly DatasetDocument[]): SyncTaskDraft {
  const dataset = datasets.find((item) => item.code === document.sourceDatasetCode);
  return {
    code: document.code,
    name: document.name,
    isNew: false,
    sourceDatasetCode: document.sourceDatasetCode,
    sourceMode: 'sql',
    sourceDataSourceCode: dataset?.dataSourceCode ?? '',
    sourceTableName: '',
    sourceQueryText: dataset?.queryText ?? '',
    targetMode: 'existing',
    targetDataSourceCode: document.targetDataSourceCode,
    targetTable: document.targetTable,
    conflictPolicy: document.conflictPolicy,
    scheduleType: document.scheduleType,
    scheduleTime: document.scheduleTime ?? '',
    intervalMinutes: document.intervalMinutes,
    scheduleCron: document.scheduleCron ?? '',
    enabled: document.enabled,
    revision: document.revision,
    mappings: document.mappings.map((mapping) => ({ ...mapping })),
  };
}

/**
 * 构造新任务的空白草稿：编码按现有任务数量顺序生成，调度默认手动，
 * 源默认“选表”模式、目标默认“选择现有表”模式，映射为空。
 * @param existingCodes - 现有任务编码集合，用于避免建议编码撞名。
 * @returns 全新的设计器草稿。
 */
function newSyncTaskDraft(existingCodes: readonly string[]): SyncTaskDraft {
  let index = existingCodes.length + 1;
  while (existingCodes.includes(`sync_task_${index}`)) index += 1;
  return {
    code: `sync_task_${index}`,
    name: '新建同步任务',
    isNew: true,
    sourceDatasetCode: '',
    sourceMode: 'table',
    sourceDataSourceCode: '',
    sourceTableName: '',
    sourceQueryText: '',
    targetMode: 'existing',
    targetDataSourceCode: '',
    targetTable: '',
    conflictPolicy: 'TruncateFirst',
    scheduleType: 'Manual',
    scheduleTime: '02:30',
    intervalMinutes: 60,
    scheduleCron: '',
    enabled: true,
    revision: 0,
    mappings: [],
  };
}

/**
 * 把 ISO 时间格式化为本地可读文本；空值返回占位符。
 * @param value - ISO 时间字符串或 null。
 * @returns `yyyy-MM-dd HH:mm:ss` 形态的文本或 '—'。
 */
function formatDateTime(value: string | null): string {
  if (!value) return '—';
  return value.replace('T', ' ').slice(0, 19);
}

/**
 * 渲染运行状态徽标：Running 蓝、Completed 绿、Failed 红、Cancelled 灰，
 * 供运行记录面板和轮询提示复用。
 * @param status - 运行状态代码。
 * @returns 状态徽标 JSX。
 */
function RunStatusBadge({ status }: { status: string }) {
  const className = status === 'Completed'
    ? 'ok'
    : status === 'Failed'
      ? 'fail'
      : status === 'Running'
        ? 'running'
        : 'idle';
  const label = status === 'Completed'
    ? '已完成'
    : status === 'Failed'
      ? '失败'
      : status === 'Running'
        ? '运行中'
        : '已取消';
  return <span className={`sync-run-badge ${className}`}>{label}</span>;
}

/**
 * 渲染运行记录面板：按倒序列出最近运行的状态、行数统计、消息与失败明细（前 N 条），
 * 轮询进行中时在标题旁显示自动刷新提示。
 * @param runs - 运行日志列表（服务端倒序）。
 * @param loading - 是否正在加载运行记录。
 * @param polling - 是否处于 5 秒轮询状态。
 * @returns 运行记录面板 JSX。
 */
function RunsPanel({ runs, loading, polling }: { runs: SyncRunLogDocument[]; loading: boolean; polling: boolean }) {
  // 阶段：失败明细区。逐条运行展开前 N 条失败定位，帮助管理员快速修正脏数据。
  const failureSections = runs.filter((run) => run.failures.length > 0).map((run) => (
    <div className="sync-run-failures" key={`failures-${run.runNo}`}>
      <strong>运行 {run.runNo} 的失败明细（前 {Math.min(run.failures.length, RUN_FAILURE_DISPLAY_LIMIT)} 条{run.failures.length > RUN_FAILURE_DISPLAY_LIMIT ? `，共 ${run.failures.length} 条` : ''}）</strong>
      <ul>
        {run.failures.slice(0, RUN_FAILURE_DISPLAY_LIMIT).map((failure, index) => (
          <li key={`${failure.rowIndex}-${failure.field}-${index}`}>
            第 {failure.rowIndex} 行 · {failure.field} · {failure.reason}
            {failure.originalValue !== null && <code> 原值：{failure.originalValue}</code>}
          </li>
        ))}
      </ul>
    </div>
  ));
  // 阶段：运行记录表格。状态徽标与行数统计按运行倒序排列，空态单独提示避免误判。
  return (
    <section className="sync-runs-panel" aria-label="运行记录">
      <header className="sync-runs-header">
        <h3><ScrollText size={16} />运行记录</h3>
        {polling && <span className="sync-runs-polling"><LoaderCircle className="spin" size={13} />每 5 秒自动刷新，直到运行结束</span>}
      </header>
      <div className="sync-runs-scroll">
        <table className="sync-runs-table">
          <thead>
            <tr>
              <th>运行编号</th>
              <th>状态</th>
              <th>开始时间</th>
              <th>结束时间</th>
              <th>读取行数</th>
              <th>写入行数</th>
              <th>失败行数</th>
              <th>消息</th>
            </tr>
          </thead>
          <tbody>
            {loading && runs.length === 0 ? (
              <tr><td colSpan={8} className="sync-table-empty"><LoaderCircle className="spin" size={18} />正在读取运行记录</td></tr>
            ) : runs.length === 0 ? (
              <tr><td colSpan={8} className="sync-table-empty">该任务还没有运行记录。</td></tr>
            ) : runs.map((run) => (
              <tr key={run.runNo}>
                <td><code>{run.runNo}</code></td>
                <td><RunStatusBadge status={run.status} /></td>
                <td>{formatDateTime(run.startedAt)}</td>
                <td>{formatDateTime(run.finishedAt)}</td>
                <td>{run.rowsRead}</td>
                <td>{run.rowsWritten}</td>
                <td className={run.rowsFailed > 0 ? 'sync-failed-count' : undefined}>{run.rowsFailed}</td>
                <td className="sync-runs-message">{run.message ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {failureSections}
    </section>
  );
}

/**
 * 渲染试跑结果面板：目标列结论、失败统计与逐条记录的“源→目标”前后配对表。
 * 配对列的顺序由映射关系决定——第 i 列恒为第 i 条映射（源字段 → 目标列），因此源行
 * 字段值与目标行转换后的值在同一列垂直对齐（如字典转换 14 与“诊疗费”正对可查）。
 * 源行与目标行渲染在同一个 table 中，列边界由浏览器按整表统一计算，多条记录之间
 * 以及横向滚动时列位置都不会漂移。每条样例渲染为一对紧挨的行——源（转换前，蓝色系）
 * 在上、目标（转换后，绿色系）紧随，行首固定“源 #n / 目标 #n”标签列，横向滚动时
 * 保持可见；每个数据单元格上方为字段名（含数据库注释小字，按名称从设计器源列/目标列
 * 匹配，取不到只显示字段名，超长时单行截断）、下方为加粗值，两者超长均截断并以
 * title 悬停查看完整内容，保证同行各列的值共享同一基线、配对行逐列上下正对；
 * 键在样例中缺失时显示“—”。errors 非空时以醒目样式提示映射与真实数据不兼容。
 * @param result - 试跑接口返回的结果。
 * @param mappings - 设计器当前的映射列表，决定配对列顺序与两侧字段键。
 * @param sourceColumns - 设计器的源列集合（含注释），用于单元格含义标注。
 * @param targetColumns - 设计器的目标列集合（含注释），用于单元格含义标注。
 * @returns 试跑结果面板 JSX。
 */
function TryRunResultPanel({
  result,
  mappings,
  sourceColumns,
  targetColumns,
}: {
  result: DataSyncTryRunResult;
  mappings: readonly SyncColumnMapping[];
  sourceColumns: readonly SyncSourceColumn[];
  targetColumns: readonly SyncTargetColumn[];
}) {
  // 阶段：配对列。第 i 列 = 第 i 条映射（源字段→目标列），保证转换前后垂直对齐；
  // 映射为空（防御路径，正常设计不可能出现）时回退样例键序对照，并在面板提示对齐不保证。
  const pairColumns = useMemo(
    () => buildTryRunPairColumns(mappings, result.samples, result.targetColumns),
    [mappings, result],
  );
  // 阶段：注释匹配。试跑结果本身不带注释，按列名（忽略大小写）从设计器列补齐，取不到不渲染占位含义。
  /**
   * 按列名（忽略大小写）在给定列集合中查找注释；取不到返回 null，单元格不渲染占位。
   * @param columns - 源列或目标列集合。
   * @param key - 试跑样例的字段名或目标列名。
   * @returns 命中的注释或 null。
   */
  const descriptionOf = (
    columns: readonly (SyncSourceColumn | SyncTargetColumn)[],
    key: string,
  ): string | null => columns.find((column) => column.name.toLowerCase() === key.toLowerCase())?.description ?? null;
  /**
   * 渲染一行配对单元格：按配对列顺序取当前侧（source/target）的字段键，字段名（含注释）
   * 小字在上、加粗值在下；键在样例中缺失或配对列没有该侧键时统一显示“—”占位，
   * 空值由纯函数统一转成占位符。标签与值都固定单行省略号截断（样式层保证同一行内
   * 所有值共享一条基线、配对行逐列正对），两者的完整内容分别通过各自的 title 悬停查看。
   * @param sample - 当前样例行。
   * @param side - 渲染源行还是目标行，决定取值映射与注释匹配的列集合。
   * @returns 单元格数组。
   */
  const renderPairCells = (sample: DataSyncSampleRow, side: 'source' | 'target') =>
    pairColumns.pairs.map((pair, index) => {
      const key = side === 'source' ? pair.sourceKey : pair.targetKey;
      const values = side === 'source' ? sample.source : sample.target;
      const columns = side === 'source' ? sourceColumns : targetColumns;
      const display = tryRunCellDisplay(key === null ? undefined : values[key]);
      const caption = key === null ? '—' : formatColumnWithDescription(key, descriptionOf(columns, key));
      return (
        <td key={`${index}:${key ?? ''}`} title={display.title ?? undefined}>
          <span className="sync-tryrun-cell-name" title={caption}>{caption}</span>
          <strong className="sync-tryrun-cell-value">{display.text}</strong>
        </td>
      );
    });
  // 阶段：结果汇总。先透出结论与失败统计，再渲染逐条配对表供上下核对。
  return (
    <section className="sync-tryrun-panel" aria-label="试跑结果">
      <header className="sync-tryrun-header">
        <h3><Zap size={16} />试跑结果</h3>
        <span>
          {result.ok ? '映射可执行' : '映射存在兼容性问题'} · 耗时 {result.elapsedMilliseconds} ms · 样本 {result.samples.length} 行 · 失败 {result.failedRows} 行
        </span>
      </header>
      {result.rowCountCapped && (
        <p className="sync-callout warning"><TriangleAlert size={14} />源数据超过试跑行数上限，仅返回前 50 行样例。</p>
      )}
      {result.errors.length > 0 && (
        <div className="sync-callout danger" role="alert">
          <p><TriangleAlert size={14} />试跑发现问题：</p>
          <ul>{result.errors.map((item, index) => <li key={index}>{item}</li>)}</ul>
        </div>
      )}
      {!pairColumns.fromMappings && (
        <p className="sync-callout warning">
          <TriangleAlert size={14} />当前映射列表为空，以下按样例字段出现顺序对照展示，源/目标两侧可能无法垂直对齐。
        </p>
      )}
      {/* 阶段：渲染逐条配对表。每条记录一对行：源（蓝）在上、目标（绿）紧随，行标签列 sticky，记录间加粗分隔。 */}
      <div className="sync-tryrun-scroll">
        <table className="sync-tryrun-table sync-tryrun-pairs">
          <caption className="sync-tryrun-caption">每条样例记录由“源（转换前）”与“目标（转换后）”两行配对展示。</caption>
          <tbody>
            {result.samples.length === 0 ? (
              <tr><td className="sync-table-empty">没有返回样例行。</td></tr>
            ) : result.samples.map((sample, index) => {
              const recordNo = index + 1;
              return (
                <Fragment key={index}>
                  <tr className="sync-tryrun-row source">
                    <th scope="row" className="sync-tryrun-row-label source">源 #{recordNo}</th>
                    {renderPairCells(sample, 'source')}
                  </tr>
                  <tr className="sync-tryrun-row target sync-tryrun-pair-end">
                    <th scope="row" className="sync-tryrun-row-label target">目标 #{recordNo}</th>
                    {renderPairCells(sample, 'target')}
                  </tr>
                </Fragment>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

/**
 * 渲染映射表格里单个转换器步骤的参数编辑器：复用原转换链面板的参数表单逻辑
 * （文本/数字/下拉/文本域按目录描述渲染），所有修改通过回调回传，组件不持有业务状态。
 * @param step - 当前转换器步骤。
 * @param index - 步骤在转换链中的下标（用于展示序号）。
 * @param canMoveDown - 是否允许下移（最后一步禁用）。
 * @param onArgChange - 单参数修改回调。
 * @param onMove - 链内上移/下移回调。
 * @param onRemove - 删除该步骤回调。
 * @returns 转换器步骤编辑器 JSX。
 */
function ConverterStepEditor({
  step,
  index,
  canMoveDown,
  onArgChange,
  onMove,
  onRemove,
}: {
  step: SyncConverterStep;
  index: number;
  canMoveDown: boolean;
  onArgChange: (key: string, value: string) => void;
  onMove: (direction: -1 | 1) => void;
  onRemove: () => void;
}) {
  const descriptor = findConverterDescriptor(step.type);
  const fields = descriptor?.fields ?? [];
  // 阶段一：步骤头。名称加序号标识执行顺序，上移/下移/删除按钮直接回调父级。
  return (
    <div className="sync-converter-step">
      <div className="sync-converter-step-head">
        <strong>{index + 1}. {converterLabel(step.type)}</strong>
        <div className="sync-converter-step-actions">
          <button type="button" title="上移" disabled={index === 0} onClick={() => onMove(-1)}>↑</button>
          <button type="button" title="下移" disabled={!canMoveDown} onClick={() => onMove(1)}>↓</button>
          <button type="button" title="删除该转换器" onClick={onRemove}>✕</button>
        </div>
      </div>
      {
      // 阶段二：参数表单。控件形态按目录描述渲染，参数保持文本形态与后端契约一致。
      fields.length === 0 ? (
        <small className="sync-converter-empty">该转换器没有参数，按默认行为执行。</small>
      ) : (
        <div className="sync-converter-args">
          {fields.map((field) => (
            <label className="sync-field" key={field.key}>
              <span>{field.label}{field.required && <em className="sync-required-mark">必填</em>}</span>
              {field.control === 'select' ? (
                <select
                  value={step.args?.[field.key] ?? ''}
                  onChange={(event) => onArgChange(field.key, event.target.value)}
                >
                  {!field.required && <option value="">（默认）</option>}
                  {(field.options ?? []).map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </select>
              ) : field.control === 'textarea' ? (
                <textarea
                  rows={3}
                  value={step.args?.[field.key] ?? ''}
                  placeholder={field.hint}
                  onChange={(event) => onArgChange(field.key, event.target.value)}
                />
              ) : (
                <input
                  type={field.control === 'number' ? 'number' : 'text'}
                  value={step.args?.[field.key] ?? ''}
                  placeholder={field.hint}
                  onChange={(event) => onArgChange(field.key, event.target.value)}
                />
              )}
              <small>{field.hint}</small>
            </label>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * 映射表格（表格+下拉交互）：每行 = 源字段（名称+类型徽标）→ 目标字段（下拉或文本）
 * → 目标类型 → 转换器 chip 区。转换链为空的行按“直通”语义保存（converters 空数组）；
 * 未建立映射的源行展示为“(不同步)”置灰状态。行内转换器 chip 点击展开参数编辑器。
 * @param sourceColumns - 源字段列（预览结果或数据集字段声明）。
 * @param targetOptions - 目标字段下拉选项（现有表模式来自真实列元数据，可为空数组）。
 * @param targetMode - 目标模式：existing=下拉选列，create=文本输入目标列名。
 * @param mappings - 当前映射列表（唯一数据源，保存时原样提交）。
 * @param expandedKey - 当前展开转换器编辑器的源字段名，null 表示全部收起。
 * @param onExpandChange - 展开/收起回调。
 * @param onChange - 映射列表整体更新回调。
 * @returns 映射表格 JSX。
 */
function SyncMappingTable({
  sourceColumns,
  targetOptions,
  targetMode,
  mappings,
  expandedKey,
  onExpandChange,
  onChange,
}: {
  sourceColumns: readonly SyncSourceColumn[];
  targetOptions: readonly SyncTargetColumn[];
  targetMode: 'existing' | 'create';
  mappings: readonly SyncColumnMapping[];
  expandedKey: string | null;
  onExpandChange: (sourceField: string | null) => void;
  onChange: (next: SyncColumnMapping[]) => void;
}) {
  // 阶段一：映射定位与替换。映射列表是唯一数据源，所有编辑都以源字段名为键整体回传。
  /**
   * 按源字段名（忽略大小写）定位映射；未同步的源行返回 null。
   * @param sourceField - 源字段名。
   * @returns 命中的映射或 null。
   */
  const findMapping = (sourceField: string): SyncColumnMapping | null =>
    mappings.find((item) => item.sourceField.toLowerCase() === sourceField.toLowerCase()) ?? null;

  /**
   * 把编辑后的完整映射按源字段名（忽略大小写）写回映射列表。
   * @param originalSourceField - 编辑前的源字段名。
   * @param next - 编辑后的映射。
   */
  const replaceMapping = (originalSourceField: string, next: SyncColumnMapping) => {
    onChange(mappings.map((item) => (
      item.sourceField.toLowerCase() === originalSourceField.toLowerCase() ? next : item
    )));
  };

  /**
   * 追加一条新映射（同名自动匹配或新建表默认映射时使用）。
   * @param mapping - 新映射。
   */
  const appendMapping = (mapping: SyncColumnMapping) => {
    onChange([...mappings, mapping]);
  };

  /**
   * 移除指定源字段的映射，该源字段回到“(不同步)”状态。
   * @param sourceField - 源字段名。
   */
  const removeBySource = (sourceField: string) => {
    onChange(mappings.filter((item) => item.sourceField.toLowerCase() !== sourceField.toLowerCase()));
  };

  // 阶段二：目标列声明编辑。类型/长度/可空直接作用在已同步映射上。
  /**
   * 更新映射的目标列声明（类型/长度/可空）；仅已同步的行可触发。
   * @param sourceField - 源字段名。
   * @param patch - 目标列声明字段的部分覆盖。
   */
  const patchMapping = (sourceField: string, patch: Partial<SyncColumnMapping>) => {
    const mapping = findMapping(sourceField);
    if (mapping) replaceMapping(sourceField, { ...mapping, ...patch });
  };

  // 阶段三：目标字段选择。现有表模式在“(不同步)”与真实列之间切换并沿用列元数据类型。
  /**
   * 现有表模式下处理目标字段下拉：选“(不同步)”移除映射；选列则新建映射并沿用
   * 列元数据推断的类型/长度/可空（已有映射的转换链保留，仅更新目标列声明）。
   * @param sourceColumn - 源字段行。
   * @param optionValue - 下拉选中值（SKIP_TARGET_OPTION 表示不同步）。
   */
  const changeExistingTarget = (sourceColumn: SyncSourceColumn, optionValue: string) => {
    if (optionValue === SKIP_TARGET_OPTION || optionValue === '') {
      removeBySource(sourceColumn.name);
      onExpandChange(null);
      return;
    }
    const matched = targetOptions.find((column) => column.name === optionValue);
    const mapping = findMapping(sourceColumn.name);
    if (mapping) {
      replaceMapping(sourceColumn.name, {
        ...mapping,
        targetField: optionValue,
        targetType: matched?.targetType ?? mapping.targetType,
        length: matched?.length ?? null,
        nullable: matched?.nullable ?? mapping.nullable,
      });
      return;
    }
    appendMapping({
      sourceField: sourceColumn.name,
      targetField: optionValue,
      targetType: matched?.targetType ?? 'string',
      length: matched?.length ?? null,
      nullable: matched?.nullable ?? true,
      converters: [],
    });
  };

  // 阶段四：新建表模式的目标字段文本。非空即同步、清空即不同步，目标名默认沿用源名。
  /**
   * 新建表模式下处理目标字段文本：非空即视为同步（新建/更新映射），清空即回“(不同步)”。
   * 目标列名默认等于源字段名，类型默认沿用现有映射或按源类型推断。
   * @param sourceColumn - 源字段行。
   * @param name - 输入的目标列名。
   */
  const changeCreateTarget = (sourceColumn: SyncSourceColumn, name: string) => {
    const mapping = findMapping(sourceColumn.name);
    if (!name.trim()) {
      removeBySource(sourceColumn.name);
      return;
    }
    if (mapping) {
      replaceMapping(sourceColumn.name, { ...mapping, targetField: name });
      return;
    }
    appendMapping({
      sourceField: sourceColumn.name,
      targetField: name,
      targetType: 'string',
      length: null,
      nullable: true,
      converters: [],
    });
  };

  // 阶段五：转换器链编辑。添加、改参、排序与删除都只作用于单条映射的 converters 数组。
  /**
   * 向指定映射的转换链追加一个带默认参数的转换器，并展开该行编辑器供继续调整参数。
   * @param sourceField - 源字段名。
   * @param type - 转换器类型代码。
   */
  const addConverter = (sourceField: string, type: string) => {
    const mapping = findMapping(sourceField);
    if (!mapping || !type) return;
    replaceMapping(sourceField, { ...mapping, converters: [...mapping.converters, createConverterStep(type)] });
    onExpandChange(sourceField);
  };

  /**
   * 更新转换链中某一步的某个参数；参数写入保持文本形态。
   * @param sourceField - 源字段名。
   * @param index - 步骤下标。
   * @param key - 参数键。
   * @param value - 参数文本值。
   */
  const changeConverterArg = (sourceField: string, index: number, key: string, value: string) => {
    const mapping = findMapping(sourceField);
    if (!mapping) return;
    const step = mapping.converters[index];
    const args = { ...(step.args ?? {}), [key]: value };
    replaceConverterStep(sourceField, index, { ...step, args });
  };

  /**
   * 替换转换链中某一步的完整定义。
   * @param sourceField - 源字段名。
   * @param index - 步骤下标。
   * @param next - 新的步骤定义。
   */
  const replaceConverterStep = (sourceField: string, index: number, next: SyncConverterStep) => {
    const mapping = findMapping(sourceField);
    if (!mapping) return;
    replaceMapping(sourceField, {
      ...mapping,
      converters: mapping.converters.map((step, position) => (position === index ? next : step)),
    });
  };

  // 阶段六：链内排序与删除。顺序即执行顺序，链空后该映射按源值直通。
  /**
   * 在转换链内上移/下移某一步；顺序即执行顺序，越界请求被忽略。
   * @param sourceField - 源字段名。
   * @param index - 步骤下标。
   * @param direction - 移动方向。
   */
  const moveConverter = (sourceField: string, index: number, direction: -1 | 1) => {
    const mapping = findMapping(sourceField);
    if (!mapping) return;
    const target = index + direction;
    if (target < 0 || target >= mapping.converters.length) return;
    const converters = [...mapping.converters];
    [converters[index], converters[target]] = [converters[target], converters[index]];
    replaceMapping(sourceField, { ...mapping, converters });
  };

  /**
   * 删除转换链中的某一步；链空后该映射按源值直通处理。
   * @param sourceField - 源字段名。
   * @param index - 步骤下标。
   */
  const removeConverter = (sourceField: string, index: number) => {
    const mapping = findMapping(sourceField);
    if (!mapping) return;
    replaceMapping(sourceField, {
      ...mapping,
      converters: mapping.converters.filter((_, position) => position !== index),
    });
  };

  // 阶段七：渲染映射表格。逐行渲染源字段、目标字段、类型与转换器 chip，展开行紧跟其后。
  return (
    <div className="sync-mapping-table-scroll">
      <table className="sync-mapping-table">
        <thead>
          <tr>
            <th>源字段</th>
            <th>目标字段</th>
            <th>目标类型</th>
            <th>转换器</th>
          </tr>
        </thead>
        <tbody>
          {sourceColumns.length === 0 ? (
            <tr><td colSpan={4} className="sync-table-empty">还没有源字段：请先在步骤 1 完成“预览源列”。</td></tr>
          ) : sourceColumns.map((column) => {
            // 逐行派生：定位映射、计算展开态与下拉回显值，“(不同步)”行走置灰样式。
            const mapping = findMapping(column.name);
            const expanded = mapping !== null && expandedKey !== null &&
              expandedKey.toLowerCase() === column.name.toLowerCase();
            const optionKey = mapping
              ? (targetOptions.find((option) => option.name.toLowerCase() === mapping.targetField.toLowerCase())?.name ?? mapping.targetField)
              : SKIP_TARGET_OPTION;
            return (
              <Fragment key={column.name}>
                <tr className={mapping ? undefined : 'sync-mapping-row-skipped'}>
                  <td>
                    <span className="sync-mapping-source-name">{column.name}</span>
                    <span className="sync-type-badge">{column.dataType}</span>
                    {
                    // 列注释（中文业务含义）：取不到不渲染占位，避免制造“无含义”的噪音。
                    column.description && (
                      <small className="sync-source-comment">{column.description}</small>
                    )}
                  </td>
                  <td>
                    {targetMode === 'create' ? (
                      <div className="sync-create-target-cell">
                        <input
                          value={mapping?.targetField ?? ''}
                          placeholder={column.name}
                          title={column.description ?? undefined}
                          aria-label={`源字段 ${column.name} 的目标列名`}
                          onChange={(event) => changeCreateTarget(column, event.target.value)}
                        />
                        {
                        // 新建表模式的目标名默认沿用源字段名，行内同样展示源字段注释方便对照编辑。
                        column.description && (
                          <small className="sync-create-comment">{column.description}</small>
                        )}
                      </div>
                    ) : (
                      <select
                        value={mapping ? optionKey : SKIP_TARGET_OPTION}
                        aria-label={`源字段 ${column.name} 的目标字段`}
                        onChange={(event) => changeExistingTarget(column, event.target.value)}
                      >
                        <option value={SKIP_TARGET_OPTION}>(不同步)</option>
                        {targetOptions.length === 0 && <option value="" disabled>（请先在第 3 步选择目标表）</option>}
                        {targetOptions.map((option) => (
                          <option
                            key={option.name}
                            value={option.name}
                            title={option.description ?? undefined}
                          >
                            {formatColumnWithDescription(option.name, option.description)}
                          </option>
                        ))}
                      </select>
                    )}
                  </td>
                  <td>
                    <select
                      value={mapping?.targetType ?? 'string'}
                      disabled={!mapping}
                      aria-label={`源字段 ${column.name} 的目标类型`}
                      onChange={(event) => patchMapping(column.name, { targetType: event.target.value })}
                    >
                      <option value="string">文本</option>
                      <option value="number">整数</option>
                      <option value="decimal">小数</option>
                      <option value="datetime">日期时间</option>
                      <option value="bool">布尔</option>
                    </select>
                  </td>
                  <td>
                    <div className="sync-converter-cell">
                      {
                      // 转换器 chip 区：chip 显示“名称+参数摘要”，点标签展开参数编辑，✕ 直接删除。
                      mapping?.converters.map((step, index) => (
                        <span
                          key={`${step.type}-${index}`}
                          className={`sync-converter-chip${expanded && expandedKey?.toLowerCase() === column.name.toLowerCase() ? ' active' : ''}`}
                        >
                          <button
                            type="button"
                            title="编辑参数"
                            onClick={() => onExpandChange(expanded ? null : column.name)}
                          >
                            {converterSummary(step)}
                          </button>
                          <button
                            type="button"
                            className="chip-remove"
                            title="删除该转换器"
                            onClick={() => removeConverter(column.name, index)}
                          >
                            ✕
                          </button>
                        </span>
                      ))}
                      {mapping && mapping.converters.length === 0 && (
                        <span className="sync-converter-none">直通</span>
                      )}
                      {mapping && (
                        <select
                          className="sync-converter-add-select"
                          value=""
                          aria-label={`为源字段 ${column.name} 添加转换器`}
                          onChange={(event) => {
                            addConverter(column.name, event.target.value);
                            event.target.value = '';
                          }}
                        >
                          <option value="">+ 添加转换器</option>
                          {CONVERTER_CATALOG.map((item) => (
                            <option key={item.type} value={item.type}>{item.label}（{item.type}）</option>
                          ))}
                        </select>
                      )}
                      {!mapping && <span className="sync-converter-none">不同步</span>}
                    </div>
                  </td>
                </tr>
                {expanded && mapping && (
                  // 阶段八：展开编辑器行。目标列高级声明（长度/可空）与转换链参数表单按行内嵌渲染。
                  <tr className="sync-mapping-editor-row">
                    <td colSpan={4}>
                      <div className="sync-mapping-editor">
                        <div className="sync-mapping-editor-decl">
                          <label className="sync-field">
                            <span>长度（文本类型生效）</span>
                            <input
                              type="number"
                              min={0}
                              value={mapping.length ?? ''}
                              onChange={(event) => patchMapping(column.name, {
                                length: event.target.value === '' ? null : Number(event.target.value),
                              })}
                            />
                          </label>
                          <label className="sync-field checkbox">
                            <input
                              type="checkbox"
                              checked={mapping.nullable}
                              onChange={(event) => patchMapping(column.name, { nullable: event.target.checked })}
                            />
                            <span>允许空值</span>
                          </label>
                          <button
                            type="button"
                            className="button sync-mapping-editor-remove"
                            onClick={() => {
                              removeBySource(column.name);
                              onExpandChange(null);
                            }}
                          >
                            <Trash2 size={14} />移除映射（不同步）
                          </button>
                        </div>
                        {/* 阶段：转换链编辑。自上而下即执行顺序，链空表示源值直通写入。 */}
                        <div className="sync-converter-chain">
                          {mapping.converters.length === 0 && (
                            <p className="sync-converter-empty">尚未配置转换器，源值将按目标类型直接收敛写入。</p>
                          )}
                          {mapping.converters.map((step, index) => (
                            <ConverterStepEditor
                              key={`${step.type}-${index}`}
                              step={step}
                              index={index}
                              canMoveDown={index < mapping.converters.length - 1}
                              onArgChange={(key, value) => changeConverterArg(column.name, index, key, value)}
                              onMove={(direction) => moveConverter(column.name, index, direction)}
                              onRemove={() => removeConverter(column.name, index)}
                            />
                          ))}
                        </div>
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * 渲染源取数预览面板：动态字段列表（名称+类型）与前 N 行样例，
 * 帮助设计器在保存前确认取数定义真实可用。
 * @param preview - preview-source 接口返回的动态列与样例行。
 * @returns 源预览面板 JSX。
 */
function SourcePreviewPanel({ preview }: { preview: DatasetPreviewResult }) {
  const columns = preview.fields;
  const rows = preview.rows.slice(0, SOURCE_PREVIEW_ROW_LIMIT);
  // 阶段：字段表头带类型徽标，样例行空值统一占位，超出行数提示已折叠。
  return (
    <div className="sync-source-preview">
      <div className="sync-source-preview-head">
        <strong>源列（{columns.length}）</strong>
        <span>样例 {preview.rows.length} 行 · {preview.durationMilliseconds} ms{preview.hasMore ? ' · 超过 50 行已截断' : ''}</span>
      </div>
      <div className="sync-table-scroll">
        <table className="sync-table sync-source-preview-table">
          <thead>
            <tr>
              {columns.map((field) => (
                <th key={field.code} title={field.description ?? undefined}>
                  {field.code}
                  {
                  // 列注释（中文业务含义）紧随列名，类型徽标放最后一行；无注释不占位。
                  field.description && <small className="sync-field-comment">{field.description}</small>
                  }
                  <small>{field.dataType}</small>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr><td colSpan={Math.max(columns.length, 1)} className="sync-table-empty">查询没有返回数据行。</td></tr>
            ) : rows.map((row, rowIndex) => (
              <tr key={rowIndex}>
                {row.map((cell, cellIndex) => (
                  <td key={cellIndex}>{cell === null || cell === undefined ? '—' : String(cell)}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {preview.rows.length > rows.length && (
        <small>仅展示前 {rows.length} 行样例，映射与试跑仍使用完整受限查询。</small>
      )}
    </div>
  );
}

/** 设计器分步视图的步骤号：1 源取数、2 字段映射、3 目标设置与调度、4 试跑与完成。 */
type DesignerStep = 1 | 2 | 3 | 4;

/** 设计器步骤条元数据：步骤号与展示标签，与四个分步区块一一对应。 */
const designerSteps: { step: DesignerStep; label: string }[] = [
  { step: 1, label: '源取数' },
  { step: 2, label: '字段映射' },
  { step: 3, label: '目标设置与调度' },
  { step: 4, label: '试跑与完成' },
];

/**
 * 渲染设计器顶部的可点击步骤条：设计器一次只渲染当前步骤区块，
 * 点击任意步骤直接跳转（数据都在设计器组件状态里，切换不丢失，不做强制卡点）；
 * 当前步骤高亮，排在当前步骤之前的步骤按完成态打勾（仅视觉，不新增状态机）。
 * @param current - 当前所在的设计器步骤。
 * @param onSelect - 步骤点击回调，参数为目标步骤号。
 * @returns 步骤条 JSX。
 */
function SyncDesignerStepBar({
  current,
  onSelect,
}: {
  current: DesignerStep;
  onSelect: (step: DesignerStep) => void;
}) {
  return (
    <ol className="sync-step-bar" aria-label="设计步骤">
      {designerSteps.map((item) => (
        <li
          key={item.step}
          className={item.step === current ? 'current' : item.step < current ? 'done' : undefined}
          aria-current={item.step === current ? 'step' : undefined}
        >
          <button type="button" onClick={() => onSelect(item.step)}>
            <span className="sync-step-no">{item.step < current ? '✓' : item.step}</span>
            <span>{item.label}</span>
          </button>
        </li>
      ))}
    </ol>
  );
}

/**
 * 数据同步设计器视图：顶部步骤条分四步组织——源取数 → 字段映射 → 目标设置与调度 →
 * 试跑与完成，一次只渲染当前步骤，保存任务与导出同步包在顶栏常驻随时可用。
 * 源取数支持“选表（按方言生成 SELECT）”与“写 SQL”两种模式，预览源列走独立端点，
 * 任务保存时由服务端自动把源落成 `sync_src_<code>` 数据集。未落库的新任务禁用
 * 试跑与导出包（后端按已保存任务解析数据集）。
 * @param draft - 当前编辑中的任务草稿。
 * @param datasets - 可选的全局数据集列表（用于回填存量任务的源定义）。
 * @param dataSources - 可选的全局数据源列表。
 * @param onDraftChange - 草稿更新回调。
 * @param onSaved - 保存成功回调，参数为服务端返回的任务文档。
 * @param onBack - 返回列表视图回调。
 * @returns 设计器视图 JSX。
 */
function SyncTaskDesigner({
  draft,
  datasets,
  dataSources,
  aiAssistantEnabled = false,
  onDraftChange,
  onSaved,
  onBack,
}: {
  draft: SyncTaskDraft;
  datasets: DatasetDocument[];
  dataSources: DataSourceDocument[];
  /** AI 助手开关（来自 GET /ai/settings）；关闭时「AI 智能匹配」按钮不渲染，规则版同名匹配不受影响。 */
  aiAssistantEnabled?: boolean;
  onDraftChange: (next: SyncTaskDraft) => void;
  onSaved: (document: SyncTaskDocument) => void;
  onBack: () => void;
}) {
  const [preview, setPreview] = useState<DatasetPreviewResult | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [sourceTables, setSourceTables] = useState<string[] | null>(null);
  const [sourceTablesLoading, setSourceTablesLoading] = useState(false);
  const [targetTables, setTargetTables] = useState<string[] | null>(null);
  const [targetTablesLoading, setTargetTablesLoading] = useState(false);
  const [targetColumns, setTargetColumns] = useState<SyncColumnMetadata[] | null>(null);
  const [targetColumnsLoading, setTargetColumnsLoading] = useState(false);
  const [expandedConverter, setExpandedConverter] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  // 阶段：分步视图。当前步骤只是渲染开关（1..4），所有草稿/预览/试跑状态保持常驻，切换步骤不丢数据。
  const [designerStep, setDesignerStep] = useState<DesignerStep>(1);
  const [tryRunning, setTryRunning] = useState(false);
  const [tryRunResult, setTryRunResult] = useState<DataSyncTryRunResult | null>(null);
  const [exporting, setExporting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // AI 智能匹配状态：逐行置信标注（按目标列小写键）与需人工处理目标列；写回 mappings 时标注被丢弃。
  const [aiMatchBusy, setAiMatchBusy] = useState(false);
  const [aiMatchMarks, setAiMatchMarks] = useState<Record<string, AiSyncMatchMark>>({});
  const [aiMatchManualTargets, setAiMatchManualTargets] = useState<string[]>([]);
  const [aiMatchSummary, setAiMatchSummary] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // 草稿引用：异步回调（列加载完成后的自动匹配）里读取最新草稿，避免闭包旧值覆盖新编辑。
  const draftRef = useRef(draft);
  draftRef.current = draft;

  const sourceDataSource = dataSources.find((item) => item.code === draft.sourceDataSourceCode) ?? null;
  const targetDataSource = dataSources.find((item) => item.code === draft.targetDataSourceCode) ?? null;
  const sourceDataset = datasets.find((item) => item.code === draft.sourceDatasetCode) ?? null;

  // 阶段：派生源列与目标列选项。预览结果优先（注释来自数据库），其次回退到存量数据集的
  // 字段声明（中文含义在 displayName，与列名不同时作为注释展示），最后兜底映射中已出现的源字段。
  const sourceColumns = useMemo<SyncSourceColumn[]>(() => {
    if (preview) {
      return preview.fields.map((field) => ({
        name: field.code,
        dataType: field.dataType,
        description: field.description ?? null,
      }));
    }
    if (sourceDataset) {
      return sourceDataset.fields.map((field) => ({
        name: field.code,
        dataType: field.dataType,
        description: field.description
          ?? (field.displayName && field.displayName !== field.code ? field.displayName : null),
      }));
    }
    return draft.mappings.map((mapping) => ({ name: mapping.sourceField, dataType: 'string' }));
  }, [preview, sourceDataset, draft.mappings]);
  const targetOptions = useMemo<SyncTargetColumn[]>(
    () => (targetColumns ? targetColumnsFromMetadata(targetColumns) : []),
    [targetColumns],
  );
  // 阶段：映射校验错误由纯函数模块统一给出，表格、保存与提示共用同一份结论。
  const validationErrors = useMemo(
    () => validateSyncMappings(sourceColumns, targetOptions, draft.mappings),
    [sourceColumns, targetOptions, draft.mappings],
  );
  // 阶段：Cron 表达式实时预览（中文释义 + 未来 3 次执行）。尽力而为：解析失败进入降级分支
  // 只提示“以服务端校验为准”，不阻塞输入；后端保存校验是唯一裁决者。
  const cronPreview = useMemo(
    () => (draft.scheduleType === 'Cron' ? previewCronExpression(draft.scheduleCron) : null),
    [draft.scheduleType, draft.scheduleCron],
  );

  /**
   * 更新草稿的某个字段；预览与试跑结论是否作废由调用方按字段语义自行处理。
   * @param patch - 草稿字段的部分覆盖。
   */
  const patchDraft = (patch: Partial<SyncTaskDraft>) => {
    onDraftChange({ ...draft, ...patch });
  };

  /**
   * 按当前目标模式执行同名自动匹配：现有表模式做同名（忽略大小写）预匹配，
   * 新建表模式为尚未同步的源列生成 1:1 默认映射；已有映射原样保留。
   * @param nextSources - 参与匹配的源列。
   * @param base - 变更前的草稿。
   * @returns 应用匹配后的新草稿。
   */
  const applyAutoMatch = (nextSources: readonly SyncSourceColumn[], base: SyncTaskDraft): SyncTaskDraft => {
    if (base.targetMode === 'create') {
      const mappedKeys = new Set(base.mappings.map((item) => item.sourceField.toLowerCase()));
      const defaults = buildDefaultTableMappings(
        nextSources.filter((column) => !mappedKeys.has(column.name.toLowerCase())),
      );
      return { ...base, mappings: [...base.mappings, ...defaults] };
    }
    return { ...base, mappings: buildInitialMappings(nextSources, targetOptions, base.mappings) };
  };

  /**
   * “按同名自动匹配”按钮：对当前源列执行一次自动匹配并即时写回草稿。
   */
  const runAutoMatch = () => {
    onDraftChange(applyAutoMatch(sourceColumns, draft));
    setNotice(draft.targetMode === 'create'
      ? '已为未同步的源列生成默认映射，可继续逐行调整。'
      : '已按同名（忽略大小写）自动匹配目标字段。');
  };

  /**
   * 调用 /ai/sync-match 做智能匹配（同名基线 + LLM 歧义补齐 + 白名单转换链建议）：
   * 输入全部来自当前设计器草稿与已加载的目标列元数据，不新增取数；
   * 建议只回填空白映射（与人工冲突时以人工为准），置信标注仅供逐行确认，保存时丢弃。
   */
  const runAiMatch = async () => {
    if (aiMatchBusy || sourceColumns.length === 0 || targetColumns === null || targetColumns.length === 0) {
      setError('请先完成源取数与目标表列清单加载，再使用 AI 智能匹配。');
      return;
    }
    // 第一段：装配请求载荷——源字段/目标列元数据/当前映射全部来自设计器草稿，不新增取数。
    const targetDataSource = dataSources.find((item) => item.code === draft.targetDataSourceCode) ?? null;
    setAiMatchBusy(true);
    setError(null);
    setAiMatchSummary(null);
    try {
      const result = await aiApi.syncMatch({
        taskCode: draft.code.trim() || null,
        sourceDbType: sourceDataSource?.dbType ?? null,
        targetDbType: targetDataSource?.dbType ?? null,
        sourceFields: sourceColumns.map((column) => ({
          name: column.name,
          dataType: column.dataType,
          description: column.description ?? null,
        })),
        targetColumns,
        mappings: draft.mappings,
      });
      // 第二段：回填走纯函数——只填空、不覆盖人工映射、needsManual 项只进需人工处理清单。
      const backfill = applyAiSyncMatchSuggestions(
        sourceColumns,
        targetColumnsFromMetadata(targetColumns),
        draft.mappings,
        result.suggestions,
      );
      onDraftChange({ ...draft, mappings: backfill.mappings });
      setAiMatchMarks(backfill.marks);
      setAiMatchManualTargets(backfill.manualTargets);
      const hits = Object.keys(backfill.marks).length;
      const suffix = backfill.manualTargets.length > 0
        ? `；需人工处理 ${backfill.manualTargets.length} 列`
        : '';
      setAiMatchSummary(
        (result.degraded ? `${result.message ?? 'LLM 不可用，已降级为同名规则结果'}。` : '') +
        `AI 建议已回填 ${hits} 行，已有映射保持不变${suffix}。请逐行确认后再保存（保存仍走现有校验链与试跑）。`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : 'AI 智能匹配失败。');
    } finally {
      setAiMatchBusy(false);
    }
  };

  /**
   * 调用 preview-source 获取真实列与样例行，成功后执行一次自动匹配。
   * 任务尚未保存也可用（独立端点），失败原因透出在状态条。
   */
  const loadPreview = async () => {
    if (previewLoading) return;
    if (!draft.sourceDataSourceCode || !draft.sourceQueryText.trim()) {
      setError('请先选择源数据源并完成取数定义（选表或写 SQL）。');
      return;
    }
    setPreviewLoading(true);
    setError(null);
    try {
      const result = await dataSyncApi.previewSource({
        dataSourceCode: draft.sourceDataSourceCode,
        queryText: draft.sourceQueryText,
        // 选表模式携带源表名：后端按列名匹配数据库注释填充 fields 的 description。
        sourceTable: draft.sourceMode === 'table' ? draft.sourceTableName : null,
      });
      const nextSources = result.fields.map((field) => ({ name: field.code, dataType: field.dataType }));
      setPreview(result);
      onDraftChange(applyAutoMatch(nextSources, draft));
      setNotice(`已读取 ${result.fields.length} 个源列与 ${result.rows.length} 行样例，并完成自动匹配。`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '源列预览失败。');
    } finally {
      setPreviewLoading(false);
    }
  };

  /**
   * 切换源取数模式：切到“选表”时若已选表则按方言重新生成 SQL，切到“写 SQL”保留现文本。
   * @param mode - 目标模式。
   */
  const changeSourceMode = (mode: 'table' | 'sql') => {
    if (mode === 'table' && draft.sourceTableName && sourceDataSource) {
      onDraftChange({
        ...draft,
        sourceMode: mode,
        sourceQueryText: buildTableSelectSql(sourceDataSource.dbType, draft.sourceTableName),
      });
      return;
    }
    patchDraft({ sourceMode: mode });
  };

  /**
   * 选表模式下选中源表：按方言生成取数 SQL 并作废旧预览。
   * @param table - 选中的表名。
   */
  const changeSourceTable = (table: string) => {
    const queryText = table && sourceDataSource
      ? buildTableSelectSql(sourceDataSource.dbType, table)
      : '';
    setPreview(null);
    setTryRunResult(null);
    patchDraft({ sourceTableName: table, sourceQueryText: queryText });
  };

  /**
   * 切换源数据源：作废表清单、选中表与预览；写 SQL 模式的文本也一并清空，
   * 避免把上一个库的 SQL 带到新数据源上误跑。
   * @param code - 新选中的数据源编码。
   */
  const changeSourceDataSource = (code: string) => {
    setSourceTables(null);
    setPreview(null);
    setTryRunResult(null);
    patchDraft({ sourceDataSourceCode: code, sourceTableName: '', sourceQueryText: '' });
  };

  /**
   * 切换目标数据源：清空目标表选择与列清单，映射保留（目标列语义由用户重新选择）。
   * @param code - 新选中的数据源编码。
   */
  const changeTargetDataSource = (code: string) => {
    setTargetTables(null);
    setTargetColumns(null);
    patchDraft({ targetDataSourceCode: code, targetTable: '' });
  };

  /**
   * 切换目标模式（现有表/新建表）：清空目标表与列清单；切到新建表时为未同步的
   * 源列生成 1:1 默认映射，让目标字段文本直接可用。
   * @param mode - 目标模式。
   */
  const changeTargetMode = (mode: 'existing' | 'create') => {
    setTargetColumns(null);
    if (mode === 'create') {
      const mappedKeys = new Set(draft.mappings.map((item) => item.sourceField.toLowerCase()));
      const defaults = buildDefaultTableMappings(
        sourceColumns.filter((column) => !mappedKeys.has(column.name.toLowerCase())),
      );
      onDraftChange({ ...draft, targetMode: mode, targetTable: '', mappings: [...draft.mappings, ...defaults] });
      return;
    }
    patchDraft({ targetMode: mode, targetTable: '' });
  };

  /**
   * 现有表模式下选中目标表：清空旧列清单；列清单由专用 effect 拉取并触发自动匹配。
   * @param table - 选中的表名。
   */
  const changeTargetTable = (table: string) => {
    setTargetColumns(null);
    patchDraft({ targetTable: table });
  };

  // 阶段：元数据加载。源表清单、目标表清单与目标列清单分别按依赖变化拉取，全部只读。
  useEffect(() => {
    if (!draft.sourceDataSourceCode) {
      setSourceTables(null);
      return;
    }
    let active = true;
    setSourceTablesLoading(true);
    dataSyncApi.listTables(draft.sourceDataSourceCode)
      .then((document_) => {
        if (active) setSourceTables(document_.tables);
      })
      .catch((exception: unknown) => {
        if (active) setError(exception instanceof Error ? exception.message : '源数据源表清单加载失败。');
      })
      .finally(() => {
        if (active) setSourceTablesLoading(false);
      });
    return () => {
      active = false;
    };
  }, [draft.sourceDataSourceCode]);

  useEffect(() => {
    if (!draft.targetDataSourceCode) {
      setTargetTables(null);
      return;
    }
    let active = true;
    setTargetTablesLoading(true);
    dataSyncApi.listTables(draft.targetDataSourceCode)
      .then((document_) => {
        if (active) setTargetTables(document_.tables);
      })
      .catch((exception: unknown) => {
        if (active) setError(exception instanceof Error ? exception.message : '目标数据源表清单加载失败。');
      })
      .finally(() => {
        if (active) setTargetTablesLoading(false);
      });
    return () => {
      active = false;
    };
  }, [draft.targetDataSourceCode]);

  // 目标列清单：现有表模式下按“数据源 + 表”拉取；成功后做一次同名自动匹配（幂等）。
  useEffect(() => {
    if (draft.targetMode !== 'existing' || !draft.targetDataSourceCode || !draft.targetTable) {
      setTargetColumns(null);
      return;
    }
    let active = true;
    setTargetColumnsLoading(true);
    dataSyncApi.listTableColumns(draft.targetDataSourceCode, draft.targetTable)
      .then((document_) => {
        if (!active) return;
        setTargetColumns(document_.columns);
        // 阶段：自动匹配。列清单就绪后按同名补齐缺失映射；draftRef 保证读到最新编辑。
        const current = draftRef.current;
        const options = targetColumnsFromMetadata(document_.columns);
        const merged = buildInitialMappings(sourceColumns, options, current.mappings);
        if (merged.length !== current.mappings.length) {
          onDraftChange({ ...current, mappings: merged });
        }
      })
      .catch((exception: unknown) => {
        if (active) setError(exception instanceof Error ? exception.message : '目标表列清单加载失败。');
      })
      .finally(() => {
        if (active) setTargetColumnsLoading(false);
      });
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft.targetMode, draft.targetDataSourceCode, draft.targetTable]);

  // 阶段：保存流程分本地校验、输入组装与服务端回写三步推进，任一步失败都停留在设计器。
  /**
   * 提交任务保存：先做任务级与映射级本地校验，再携带源节（source）按乐观并发修订号
   * 调用保存接口；服务端自动把源 SQL 落成/更新为 `sync_src_<code>` 数据集。
   * 保存成功后以服务端文档为准回写草稿，并通知父组件刷新列表数据。
   */
  const saveTask = async () => {
    if (saving) return;
    // 阶段一：本地校验。任务级必填、源/目标定义与映射完整性先行拦截，避免注定失败的服务端往返。
    const localErrors: string[] = [];
    if (!/^[a-z][a-z0-9_-]{0,63}$/iu.test(draft.code.trim())) {
      localErrors.push('任务编码必须以字母开头，只能包含字母、数字、下划线和连字符。');
    }
    if (!draft.name.trim()) localErrors.push('任务名称不能为空。');
    if (!draft.sourceDataSourceCode) localErrors.push('请选择源数据源。');
    if (!draft.sourceQueryText.trim()) localErrors.push('源取数定义不能为空（选表或写 SQL）。');
    if (!draft.targetDataSourceCode) localErrors.push('请选择目标数据源。');
    if (!draft.targetTable.trim()) localErrors.push('目标表名不能为空（现有表下拉或新建表名）。');
    if (draft.scheduleType === 'Daily' && !draft.scheduleTime.trim()) {
      localErrors.push('每天定时调度必须填写执行时刻。');
    }
    if (draft.scheduleType === 'Interval' && (!Number.isInteger(draft.intervalMinutes) || draft.intervalMinutes <= 0)) {
      localErrors.push('固定间隔调度必须填写大于 0 的整数分钟数。');
    }
    const cronError = validateCronScheduleInput(draft.scheduleType, draft.scheduleCron);
    if (cronError) localErrors.push(cronError);
    if (draft.mappings.length === 0) localErrors.push('至少需要一个字段映射（不同步的源列不计入）。');
    const mappingErrors = validateSyncMappings(sourceColumns, targetOptions, draft.mappings);
    const allErrors = [...localErrors, ...mappingErrors];
    if (allErrors.length > 0) {
      setError(`保存被阻止，请先修正以下问题：\n${allErrors.map((item, index) => `${index + 1}. ${item}`).join('\n')}`);
      return;
    }
    // 阶段二：组装保存输入。调度字段按类型裁剪；source 节由服务端自动维护源数据集，
    // sourceDatasetCode 仍原样携带（兼容旧服务端语义，Source 存在时服务端忽略它）。
    const input: SyncTaskInput = {
      code: draft.code.trim(),
      name: draft.name.trim(),
      sourceDatasetCode: draft.sourceDatasetCode,
      source: {
        dataSourceCode: draft.sourceDataSourceCode,
        queryText: draft.sourceQueryText,
        // 选表模式携带源表名：保存后服务端把数据库注释写入源数据集字段的 displayName。
        sourceTable: draft.sourceMode === 'table' ? draft.sourceTableName : null,
      },
      targetDataSourceCode: draft.targetDataSourceCode,
      targetTable: draft.targetTable.trim(),
      conflictPolicy: draft.conflictPolicy,
      scheduleType: draft.scheduleType,
      scheduleTime: draft.scheduleType === 'Daily' ? draft.scheduleTime.trim() : null,
      intervalMinutes: draft.scheduleType === 'Interval' ? draft.intervalMinutes : 0,
      // Cron 表达式按调度类型裁剪：仅 Cron 类型提交原文，其余类型置 null（与后端裁剪规则一致）。
      scheduleCron: scheduleCronForSave(draft.scheduleType, draft.scheduleCron),
      mappings: draft.mappings,
      enabled: draft.enabled,
      revision: draft.revision,
    };
    setSaving(true);
    setError(null);
    try {
      const document_ = await dataSyncApi.saveTask(input.code, input);
      // 阶段三：同步服务端状态。修订号、落库标记与源数据集编码以返回文档为准，防止并发覆盖。
      onDraftChange({
        ...draft,
        revision: document_.revision,
        isNew: false,
        name: document_.name,
        sourceDatasetCode: document_.sourceDatasetCode,
      });
      onSaved(document_);
      setNotice(`同步任务已保存，源数据集 ${document_.sourceDatasetCode} 已自动维护。`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '同步任务保存失败。');
    } finally {
      setSaving(false);
    }
  };

  // 阶段：试跑执行。真实读源并转换，校验目标列结论，绝不写目标库。
  /**
   * 端到端试跑当前映射（最多 10 行，不写目标库）；仅已保存任务可用。
   */
  const runTry = async () => {
    if (draft.isNew || tryRunning) return;
    setTryRunning(true);
    setError(null);
    try {
      const result = await dataSyncApi.tryRun(draft.code, { rowLimit: DESIGN_TRY_RUN_ROW_LIMIT });
      setTryRunResult(result);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '试跑失败。');
    } finally {
      setTryRunning(false);
    }
  };

  // 阶段：包导出。仅已保存任务可用，取包后由前端本地生成浏览器下载。
  /**
   * 导出当前任务的同步包 JSON（仅已保存任务）；服务端聚合源数据集快照后由
   * 前端触发浏览器下载，文件名固定为 `<任务编码>.sync-package.json`。
   */
  const exportPackage = async () => {
    if (draft.isNew || exporting) return;
    setExporting(true);
    setError(null);
    try {
      // 阶段：取包并本地生成下载。包对象经 requestJson 解出信封后直接序列化，
      // 避免依赖响应头文件名，跨浏览器行为一致。
      const requestPackage = await dataSyncApi.getTaskPackage(draft.code);
      const blob = new Blob([JSON.stringify(requestPackage, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `${draft.code}.sync-package.json`;
      anchor.click();
      URL.revokeObjectURL(url);
      setNotice('同步包已开始下载。');
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '同步包导出失败。');
    } finally {
      setExporting(false);
    }
  };

  // 阶段：页面骨架。头部（返回/标题/保存/导出常驻）→ 可点击步骤条 → 当前步骤区块 → 分步导航。
  return (
    <main className="sync-designer-page">
      <header className="sync-page-header">
        <div>
          <button type="button" className="sync-back-link" onClick={onBack}>
            <ArrowLeft size={15} />返回任务列表
          </button>
          <span className="app-kicker">REPORT CENTER</span>
          <h1>{draft.isNew ? '新建同步任务' : `编辑同步任务：${draft.name}`}</h1>
        </div>
        <div className="sync-header-actions">
          <button type="button" className="button" onClick={() => void exportPackage()} disabled={draft.isNew || exporting}>
            {exporting ? <LoaderCircle className="spin" size={15} /> : <Download size={15} />}导出同步包
          </button>
          <button type="button" className="button button-primary" onClick={() => void saveTask()} disabled={saving}>
            {saving ? <LoaderCircle className="spin" size={15} /> : <CircleCheck size={15} />}保存任务
          </button>
        </div>
      </header>

      <StatusToasts
        error={error}
        success={notice}
        onErrorClose={() => setError(null)}
        onSuccessClose={() => setNotice(null)}
      />

      {/* 阶段：步骤条。点击任意步骤直接跳转，不强制按顺序完成。 */}
      <SyncDesignerStepBar current={designerStep} onSelect={setDesignerStep} />

      {designerStep === 1 && (
      /* 第 1 步：基本信息与源取数。选表模式按方言自动生成 SELECT，写 SQL 模式复用 SqlEditor。 */
      <section className="sync-step-card" aria-label="基本信息与源取数">
        <h2><span className="sync-step-no">1</span>基本信息与源取数</h2>
        <div className="sync-form-grid">
          <label className="sync-field">
            <span>任务编码{draft.isNew ? '' : '（保存后不可修改）'}</span>
            <input
              value={draft.code}
              readOnly={!draft.isNew}
              onChange={(event) => patchDraft({ code: event.target.value })}
            />
          </label>
          <label className="sync-field">
            <span>任务名称</span>
            <input value={draft.name} onChange={(event) => patchDraft({ name: event.target.value })} />
          </label>
          <label className="sync-field">
            <span>源数据源</span>
            <select
              value={draft.sourceDataSourceCode}
              onChange={(event) => changeSourceDataSource(event.target.value)}
            >
              <option value="">请选择数据源</option>
              {dataSources.map((source) => (
                <option key={source.code} value={source.code} disabled={!source.enabled}>
                  {source.name}（{source.code} · {source.dbType}）
                </option>
              ))}
            </select>
          </label>
          <div className="sync-field">
            <span>取数方式</span>
            <div className="sync-mode-switch" role="group" aria-label="源取数方式">
              <button
                type="button"
                className={draft.sourceMode === 'table' ? 'active' : undefined}
                onClick={() => changeSourceMode('table')}
              >
                选表
              </button>
              <button
                type="button"
                className={draft.sourceMode === 'sql' ? 'active' : undefined}
                onClick={() => changeSourceMode('sql')}
              >
                写 SQL
              </button>
            </div>
          </div>
          {draft.sourceMode === 'table' && (
            <label className="sync-field">
              <span>源表{sourceDataSource ? `（${sourceDataSource.dbType}）` : ''}</span>
              <select
                value={draft.sourceTableName}
                disabled={!draft.sourceDataSourceCode || sourceTablesLoading}
                onChange={(event) => changeSourceTable(event.target.value)}
              >
                <option value="">
                  {!draft.sourceDataSourceCode
                    ? '请先选择源数据源'
                    : sourceTablesLoading
                      ? '正在读取表清单…'
                      : '请选择源表'}
                </option>
                {(sourceTables ?? []).map((table) => (
                  <option key={table} value={table}>{table}</option>
                ))}
              </select>
              {draft.sourceTableName && <small>已生成取数 SQL，可在下方核对。</small>}
            </label>
          )}
          <div className="sync-field">
            <span>源列预览</span>
            <div className="sync-preview-actions">
              <button
                type="button"
                className="button"
                disabled={!draft.sourceDataSourceCode || !draft.sourceQueryText.trim() || previewLoading}
                onClick={() => void loadPreview()}
              >
                {previewLoading ? <LoaderCircle className="spin" size={15} /> : <Zap size={15} />}预览源列
              </button>
              {preview && (
                <small>已读取 {preview.fields.length} 列 / {preview.rows.length} 行样例（{preview.durationMilliseconds} ms）</small>
              )}
            </div>
            <small>预览走真实受限查询（≤50 行、不写库）；完成后自动按同名匹配目标字段。</small>
          </div>
        </div>
        {/* 写 SQL 模式：复用数据资产页同款参数化 SQL 编辑器，按源数据源方言高亮。 */}
        {draft.sourceMode === 'sql' && (
          <div className="sync-source-sql">
            <div className="sync-source-sql-head">
              <span>源取数 SQL（单条只读 SELECT/WITH）</span>
              {sourceDataSource && <span className="sql-dialect-badge">{sourceDataSource.dbType}</span>}
            </div>
            <Suspense
              fallback={(
                <div className="asset-sql-editor-loading" aria-label="SQL 编辑器加载中">
                  <LoaderCircle className="spin" size={18} />
                </div>
              )}
            >
              <SqlEditor
                value={draft.sourceQueryText}
                dbType={sourceDataSource?.dbType ?? 'SqlServer'}
                onChange={(queryText) => patchDraft({ sourceQueryText: queryText })}
              />
            </Suspense>
          </div>
        )}
        {draft.sourceMode === 'table' && draft.sourceQueryText && (
          <div className="sync-source-sql">
            <div className="sync-source-sql-head">
              <span>自动生成的取数 SQL</span>
              {sourceDataSource && <span className="sql-dialect-badge">{sourceDataSource.dbType}</span>}
            </div>
            <code className="sync-generated-sql">{draft.sourceQueryText}</code>
          </div>
        )}
        {preview && <SourcePreviewPanel preview={preview} />}
      </section>
      )}

      {designerStep === 2 && (
      /* 第 2 步：映射表格。每行 = 源字段 → 目标字段 → 类型 → 转换器 chip；"(不同步)"置灰。 */
      <section className="sync-step-card" aria-label="字段映射">
        <h2>
          <span className="sync-step-no">2</span>字段映射
          <span className="sync-step-actions">
            <button
              type="button"
              className="button"
              disabled={sourceColumns.length === 0}
              onClick={runAutoMatch}
            >
              <Wand2 size={14} />按同名自动匹配
            </button>
            {aiAssistantEnabled && (
              <button
                type="button"
                className="button"
                title="同名基线 + LLM 语义匹配 + 白名单转换链建议；只填空不覆盖人工映射"
                disabled={aiMatchBusy || sourceColumns.length === 0 || targetColumns === null || targetColumns.length === 0}
                onClick={() => void runAiMatch()}
              >
                {aiMatchBusy ? <LoaderCircle className="spin" size={14} /> : <Bot size={14} />}AI 智能匹配
              </button>
            )}
          </span>
        </h2>
        <p className="sync-step-hint">
          每行一个源字段：选择目标字段即同步（默认沿用目标列类型），选“(不同步)”跳过该字段；
          转换链为空表示源值直通写入。点击转换器标签可展开参数编辑，标签右侧 ✕ 直接删除。
        </p>
        <SyncMappingTable
          sourceColumns={sourceColumns}
          targetOptions={targetOptions}
          targetMode={draft.targetMode}
          mappings={draft.mappings}
          expandedKey={expandedConverter}
          onExpandChange={setExpandedConverter}
          onChange={(next) => patchDraft({ mappings: next })}
        />
        {(Object.keys(aiMatchMarks).length > 0 || aiMatchManualTargets.length > 0 || aiMatchSummary !== null) && (
          <div className="ai-match-panel" aria-label="AI 匹配置信标注">
            <strong>AI 匹配置信标注（仅供逐行确认，保存时丢弃）</strong>
            <ul>
              {Object.values(aiMatchMarks).map((mark) => (
                <li key={mark.targetField.toLowerCase()}>
                  <code>{mark.targetField}</code>
                  <i className={`ai-match-confidence ${mark.confidence}`}>
                    {mark.confidence === 'high' ? '高' : mark.confidence === 'low' ? '低' : '中'}
                  </i>
                  <span>{mark.reason}</span>
                </li>
              ))}
            </ul>
            {aiMatchManualTargets.length > 0 && (
              <p className="ai-match-manual">需人工处理（未找到对应源字段）：{aiMatchManualTargets.join('、')}</p>
            )}
            {aiMatchSummary && <p className="ai-match-summary">{aiMatchSummary}</p>}
          </div>
        )}
        {draft.targetMode === 'existing' && targetColumnsLoading && (
          <p className="sync-step-hint"><LoaderCircle className="spin" size={13} />正在读取目标表列清单…</p>
        )}
        {draft.targetMode === 'existing' && draft.targetDataSourceCode && draft.targetTable
          && !targetColumnsLoading && targetOptions.length === 0 && (
          <p className="sync-step-hint">目标表列清单为空或读取失败，目标字段下拉暂不可用。</p>
        )}
        {validationErrors.length > 0 && (
          <div className="sync-callout warning" role="alert">
            <p><TriangleAlert size={14} />映射设计还有 {validationErrors.length} 个问题（保存前需全部解决）：</p>
            <ul>{validationErrors.map((item, index) => <li key={index}>{item}</li>)}</ul>
          </div>
        )}
      </section>
      )}

      {designerStep === 3 && (
      /* 第 3 步：目标设置与调度。现有表模式目标字段下拉来自真实列；新建表模式保存后自动建表。 */
      <section className="sync-step-card" aria-label="目标设置与调度">
        <h2><span className="sync-step-no">3</span>目标设置与调度</h2>
        <div className="sync-form-grid">
          <label className="sync-field">
            <span>目标数据源</span>
            <select
              value={draft.targetDataSourceCode}
              onChange={(event) => changeTargetDataSource(event.target.value)}
            >
              <option value="">请选择数据源</option>
              {dataSources.map((source) => (
                <option key={source.code} value={source.code} disabled={!source.enabled}>
                  {source.name}（{source.code} · {source.dbType}）
                </option>
              ))}
            </select>
          </label>
          <div className="sync-field">
            <span>目标表方式</span>
            <div className="sync-mode-switch" role="group" aria-label="目标表方式">
              <button
                type="button"
                className={draft.targetMode === 'existing' ? 'active' : undefined}
                onClick={() => changeTargetMode('existing')}
              >
                选择现有表
              </button>
              <button
                type="button"
                className={draft.targetMode === 'create' ? 'active' : undefined}
                onClick={() => changeTargetMode('create')}
              >
                新建表
              </button>
            </div>
          </div>
          {draft.targetMode === 'existing' ? (
            <label className="sync-field">
              <span>目标表{targetDataSource ? `（${targetDataSource.dbType}）` : ''}</span>
              <select
                value={draft.targetTable}
                disabled={!draft.targetDataSourceCode || targetTablesLoading}
                onChange={(event) => changeTargetTable(event.target.value)}
              >
                <option value="">
                  {!draft.targetDataSourceCode
                    ? '请先选择目标数据源'
                    : targetTablesLoading
                      ? '正在读取表清单…'
                      : '请选择目标表'}
                </option>
                {(targetTables ?? []).map((table) => (
                  <option key={table} value={table}>{table}</option>
                ))}
              </select>
              <small>选中后自动读取列清单，作为步骤 2 的目标字段下拉选项。</small>
            </label>
          ) : (
            <label className="sync-field">
              <span>新表名</span>
              <input
                value={draft.targetTable}
                placeholder="如 ods_registration"
                onChange={(event) => patchDraft({ targetTable: event.target.value })}
              />
              <small>保存后首次运行时由引擎按映射声明自动建表。</small>
            </label>
          )}
          <label className="sync-field">
            <span>冲突策略</span>
            <select
              value={draft.conflictPolicy}
              onChange={(event) => patchDraft({ conflictPolicy: event.target.value })}
            >
              {CONFLICT_POLICY_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>
          <label className="sync-field checkbox">
            <input
              type="checkbox"
              checked={draft.enabled}
              onChange={(event) => patchDraft({ enabled: event.target.checked })}
            />
            <span>任务启用（停用后调度不再触发）</span>
          </label>
          <label className="sync-field">
            <span>调度类型</span>
            <select
              value={draft.scheduleType}
              onChange={(event) => patchDraft({ scheduleType: event.target.value })}
            >
              {SCHEDULE_TYPE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>
          {draft.scheduleType === 'Daily' && (
            <label className="sync-field">
              <span>每天执行时刻</span>
              <input
                type="time"
                value={draft.scheduleTime}
                onChange={(event) => patchDraft({ scheduleTime: event.target.value })}
              />
            </label>
          )}
          {draft.scheduleType === 'Interval' && (
            <label className="sync-field">
              <span>执行间隔（分钟）</span>
              <input
                type="number"
                min={1}
                value={draft.intervalMinutes}
                onChange={(event) => patchDraft({ intervalMinutes: Number(event.target.value) })}
              />
            </label>
          )}
          {draft.scheduleType === 'Cron' && (
            <label className="sync-field">
              <span>Cron 表达式</span>
              <input
                value={draft.scheduleCron}
                placeholder="如 */2 * * * * 或 0 2 * * ?"
                onChange={(event) => patchDraft({ scheduleCron: event.target.value })}
              />
              <small>{CRON_EXPRESSION_HINT}</small>
              {cronPreview?.description && (
                <small>释义：{cronPreview.description}</small>
              )}
              {cronPreview && cronPreview.nextRuns.length > 0 && (
                <small>未来 3 次执行：{cronPreview.nextRuns.join('、')}</small>
              )}
              {cronPreview?.unavailableReason && (
                <small>{cronPreview.unavailableReason}</small>
              )}
            </label>
          )}
        </div>
      </section>
      )}

      {designerStep === 4 && (
      /* 第 4 步：试跑与完成。真实读源并转换，不写目标库；保存与导出在顶栏常驻，不在步骤内重复。 */
      <section className="sync-step-card" aria-label="试跑与完成">
        <h2><span className="sync-step-no">4</span>试跑（不写目标库）</h2>
        <div className="sync-preview-actions">
          <button
            type="button"
            className="button button-primary"
            disabled={draft.isNew || tryRunning}
            onClick={() => void runTry()}
          >
            {tryRunning ? <LoaderCircle className="spin" size={15} /> : <Play size={15} />}
            试跑（最多 {DESIGN_TRY_RUN_ROW_LIMIT} 行）
          </button>
          {draft.isNew && <small>任务保存后才能试跑。</small>}
          {!draft.isNew && <small>试跑按映射真实读取源数据并转换，校验目标列结论，但绝不写入目标库。</small>}
        </div>
        {tryRunResult && (
          <TryRunResultPanel
            result={tryRunResult}
            mappings={draft.mappings}
            sourceColumns={sourceColumns}
            targetColumns={targetOptions}
          />
        )}
        <p className="sync-step-hint sync-step-finale">
          设计完成：确认试跑结论无误后，点击顶栏「保存任务」落库；迁移到其他环境时，使用顶栏「导出同步包」。
        </p>
      </section>
      )}

      {/* 阶段：分步导航。上一步靠左、下一步靠右；第一步无上一步、第四步无下一步。 */}
      <div className="sync-step-nav">
        {designerStep > 1 && (
          <button
            type="button"
            className="button sync-step-nav-prev"
            onClick={() => setDesignerStep((designerStep - 1) as DesignerStep)}
          >
            上一步
          </button>
        )}
        {designerStep < 4 && (
          <button
            type="button"
            className="button button-primary sync-step-nav-next"
            onClick={() => setDesignerStep((designerStep + 1) as DesignerStep)}
          >
            下一步：{designerSteps[designerStep].label}
          </button>
        )}
      </div>
    </main>
  );
}

/**
 * 数据同步任务页根组件：维护“列表 ↔ 设计器”两个视图与列表侧的任务、运行记录、
 * 轮询状态。立即执行后按 5 秒间隔轮询运行记录，直到目标运行结束或组件卸载。
 * @returns 数据同步任务页 JSX。
 */
export default function DataSyncTaskPage({ aiAssistantEnabled = false }: { aiAssistantEnabled?: boolean }) {
  const [view, setView] = useState<'list' | 'design'>('list');
  const [draft, setDraft] = useState<SyncTaskDraft | null>(null);
  const [tasks, setTasks] = useState<SyncTaskDocument[]>([]);
  const [datasets, setDatasets] = useState<DatasetDocument[]>([]);
  const [dataSources, setDataSourceList] = useState<DataSourceDocument[]>([]);
  const [tasksLoading, setTasksLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyCode, setBusyCode] = useState<string | null>(null);
  const [busyKind, setBusyKind] = useState<'open' | 'delete' | 'run' | 'toggle' | 'export' | null>(null);
  const [runsTaskCode, setRunsTaskCode] = useState<string | null>(null);
  const [runs, setRuns] = useState<SyncRunLogDocument[]>([]);
  const [runsLoading, setRunsLoading] = useState(false);
  const [pollingRunNo, setPollingRunNo] = useState<string | null>(null);
  const [pollingTaskCode, setPollingTaskCode] = useState<string | null>(null);

  // 阶段：数据加载。任务列表与数据资产下拉一次并发拉取，失败保留已加载内容。
  /**
   * 拉取任务列表与数据资产下拉数据；失败时透出错误并保留已加载内容。
   */
  const loadTasks = useCallback(async () => {
    setError(null);
    try {
      const [nextTasks, nextDatasets, nextSources] = await Promise.all([
        dataSyncApi.tasks(),
        dataAssetApi.datasets(),
        dataAssetApi.dataSources(),
      ]);
      setTasks(nextTasks);
      setDatasets(nextDatasets);
      setDataSourceList(nextSources);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '同步任务列表加载失败。');
    } finally {
      setTasksLoading(false);
    }
  }, []);

  // 首次进入加载任务与数据资产；轮询运行记录每 5 秒刷新一次，卸载时清理定时器。
  useEffect(() => {
    void loadTasks();
  }, [loadTasks]);

  useEffect(() => {
    if (pollingTaskCode === null || pollingRunNo === null) return;
    let active = true;
    /**
     * 轮询一次运行记录：命中目标运行结束后停止轮询并刷新完整列表。
     */
    const poll = async () => {
      try {
        const nextRuns = await dataSyncApi.taskRuns(pollingTaskCode);
        if (!active) return;
        setRuns(nextRuns);
        const target = nextRuns.find((run) => run.runNo === pollingRunNo);
        if (target && target.status !== 'Running') {
          setPollingRunNo(null);
          setPollingTaskCode(null);
        }
      } catch {
        // 轮询失败不中断界面；下一轮继续尝试，最终由用户手动查看。
      }
    };
    const timer = window.setInterval(() => { void poll(); }, RUN_POLL_INTERVAL_MS);
    // 阶段：卸载保护。清掉定时器并置 active=false，阻止组件销毁后继续写状态。
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [pollingTaskCode, pollingRunNo]);

  // 阶段：进入设计器。已有任务先取最新文档，保证修订号与映射不落后于列表视图。
  /**
   * 进入设计器视图：已有任务先拉取最新文档（保证修订号与映射最新），新任务直接建草稿。
   * @param task - 列表行对应的任务摘要；null 表示新建。
   */
  const openDesigner = async (task: SyncTaskDocument | null) => {
    setError(null);
    if (task === null) {
      setDraft(newSyncTaskDraft(tasks.map((item) => item.code)));
      setView('design');
      return;
    }
    setBusyCode(task.code);
    setBusyKind('open');
    try {
      const document_ = await dataSyncApi.getTask(task.code);
      setDraft(draftFromDocument(document_, datasets));
      setView('design');
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '同步任务加载失败。');
    } finally {
      setBusyCode(null);
      setBusyKind(null);
    }
  };

  // 阶段：退出设计器。统一复位运行面板与轮询状态，回到列表后重新拉取数据。
  /**
   * 退出设计器并刷新列表；运行记录面板与轮询状态一并复位。
   */
  const closeDesigner = () => {
    setDraft(null);
    setPollingRunNo(null);
    setPollingTaskCode(null);
    setView('list');
    setTasksLoading(true);
    void loadTasks();
  };

  // 阶段：启停切换。按当前修订号整体回存任务（旧契约：仅带 sourceDatasetCode），成功后刷新列表行。
  /**
   * 切换任务启停状态：按当前修订号提交保存，成功后刷新列表行。
   * @param task - 待切换的任务。
   */
  const toggleTask = async (task: SyncTaskDocument) => {
    if (busyCode !== null) return;
    setBusyCode(task.code);
    setBusyKind('toggle');
    setError(null);
    try {
      await dataSyncApi.saveTask(task.code, {
        code: task.code,
        name: task.name,
        sourceDatasetCode: task.sourceDatasetCode,
        targetDataSourceCode: task.targetDataSourceCode,
        targetTable: task.targetTable,
        conflictPolicy: task.conflictPolicy,
        scheduleType: task.scheduleType,
        scheduleTime: task.scheduleTime,
        intervalMinutes: task.intervalMinutes,
        // 启停切换回存必须携带原表达式：Cron 任务缺少表达式会被后端保存校验拒绝。
        scheduleCron: task.scheduleCron ?? null,
        mappings: task.mappings,
        enabled: !task.enabled,
        revision: task.revision,
      });
      setNotice(task.enabled ? `任务「${task.name}」已停用` : `任务「${task.name}」已启用`);
      await loadTasks();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '任务状态切换失败。');
    } finally {
      setBusyCode(null);
      setBusyKind(null);
    }
  };

  // 阶段：删除交互。确认后调用删除接口，运行面板与轮询状态随任务一并复位。
  /**
   * 确认后删除任务并刷新列表；被引用或不存在时后端错误直接透出。
   * @param task - 待删除的任务。
   */
  const deleteTask = async (task: SyncTaskDocument) => {
    if (busyCode !== null) return;
    if (!window.confirm(confirmDeleteMessage('同步任务', { code: task.code, name: task.name }))) return;
    setBusyCode(task.code);
    setBusyKind('delete');
    setError(null);
    try {
      const receipt: DeletedDocument = await dataSyncApi.deleteTask(task.code);
      setNotice(`同步任务「${receipt.code}」已删除`);
      if (runsTaskCode === task.code) {
        setRunsTaskCode(null);
        setRuns([]);
        setPollingRunNo(null);
        setPollingTaskCode(null);
      }
      await loadTasks();
    } catch (exception) {
      setError(deleteErrorMessage(exception, '同步任务'));
    } finally {
      setBusyCode(null);
      setBusyKind(null);
    }
  };

  // 阶段：立即执行。启动后台运行并以返回日志为轮询目标，重复执行由后端防重入拦截。
  /**
   * 立即执行任务：启动后台运行并进入 5 秒轮询，直到该运行结束。
   * 后端以 Running 日志占位防重入，重复执行会收到 409 提示。
   * @param task - 待执行的任务。
   */
  const runTaskNow = async (task: SyncTaskDocument) => {
    if (busyCode !== null) return;
    if (!window.confirm(`确认立即执行同步任务「${task.name}」？\n按冲突策略 ${conflictPolicyLabel(task.conflictPolicy)} 写入目标表 ${task.targetTable}。`)) return;
    setBusyCode(task.code);
    setBusyKind('run');
    setError(null);
    try {
      // 阶段：启动运行并打开运行记录面板；以返回日志的 runNo 作为轮询目标。
      const initialRun = await dataSyncApi.runTask(task.code);
      setRunsTaskCode(task.code);
      setRuns([initialRun]);
      setRunsLoading(false);
      setPollingRunNo(initialRun.runNo);
      setPollingTaskCode(task.code);
      setNotice(`任务「${task.name}」已开始后台运行（${initialRun.runNo}）。`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '任务启动失败。');
    } finally {
      setBusyCode(null);
      setBusyKind(null);
    }
  };

  // 阶段：运行记录展开与收起共用一个入口，重复点击同一任务即收起面板。
  /**
   * 展开某任务的运行记录面板；重复点击同一任务时收起面板。
   * @param task - 目标任务。
   */
  const showRuns = async (task: SyncTaskDocument) => {
    setError(null);
    if (runsTaskCode === task.code) {
      setRunsTaskCode(null);
      setRuns([]);
      setPollingRunNo(null);
      setPollingTaskCode(null);
      return;
    }
    setRunsTaskCode(task.code);
    setRunsLoading(true);
    try {
      const nextRuns = await dataSyncApi.taskRuns(task.code);
      setRuns(nextRuns);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '运行记录加载失败。');
    } finally {
      setRunsLoading(false);
    }
  };

  // 阶段：包导出。先取完整包对象再由前端本地生成下载，文件名固定带任务编码。
  /**
   * 确认后导出任务同步包 JSON；服务端聚合完成由前端触发浏览器下载。
   * @param task - 待导出的任务。
   */
  const exportTaskPackage = async (task: SyncTaskDocument) => {
    if (busyCode !== null) return;
    if (!window.confirm(`确认导出同步任务「${task.name}」的同步包？\n包内包含源数据集快照、映射与调度定义，不包含数据源连接串与密码。`)) return;
    setBusyCode(task.code);
    setBusyKind('export');
    setError(null);
    try {
      const requestPackage = await dataSyncApi.getTaskPackage(task.code);
      const blob = new Blob([JSON.stringify(requestPackage, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `${task.code}.sync-package.json`;
      anchor.click();
      URL.revokeObjectURL(url);
      setNotice(`同步任务「${task.name}」的同步包已开始下载`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '同步包导出失败。');
    } finally {
      setBusyCode(null);
      setBusyKind(null);
    }
  };

  // 阶段：视图分派。设计器视图整体交给 SyncTaskDesigner；列表视图含表格与运行记录面板。
  if (view === 'design' && draft !== null) {
    return (
      <SyncTaskDesigner
        draft={draft}
        datasets={datasets}
        dataSources={dataSources}
        aiAssistantEnabled={aiAssistantEnabled}
        onDraftChange={setDraft}
        onSaved={() => { /* 保存成功后由 closeDesigner 刷新；此处仅回写修订号。 */ }}
        onBack={closeDesigner}
      />
    );
  }

  return (
    <main className="sync-list-page">
      <header className="sync-page-header">
        <div>
          <span className="app-kicker">REPORT CENTER</span>
          <h1>数据同步任务</h1>
          <p>把源库查询结果定时同步到目标库；表格化设计映射，经同步包迁移到线上运行。</p>
        </div>
        <div className="sync-header-actions">
          <a className="button" href="/data-sync-package-import">
            <Download size={15} />导入同步包
          </a>
          <button
            type="button"
            className="button button-primary"
            onClick={() => void openDesigner(null)}
          >
            <Plus size={15} />新建同步任务
          </button>
        </div>
      </header>

      <StatusToasts
        error={error}
        success={notice}
        onErrorClose={() => setError(null)}
        onSuccessClose={() => setNotice(null)}
      />

      <section className="sync-table-section" aria-label="同步任务列表">
        <div className="sync-table-scroll">
          <table className="sync-table">
            <thead>
              <tr>
                <th>任务</th>
                <th>源数据集</th>
                <th>目标库 / 目标表</th>
                <th>冲突策略</th>
                <th>调度</th>
                <th>状态</th>
                <th>上次运行</th>
                <th aria-label="操作" />
              </tr>
            </thead>
            <tbody>
              {tasksLoading ? (
                <tr><td colSpan={8} className="sync-table-empty"><LoaderCircle className="spin" size={20} />正在读取同步任务</td></tr>
              ) : tasks.length === 0 ? (
                <tr><td colSpan={8} className="sync-table-empty">还没有同步任务，点击「新建同步任务」开始设计。</td></tr>
              ) : tasks.map((task) => (
                <tr key={task.code}>
                  <td><strong>{task.name}</strong><small><code>{task.code}</code></small></td>
                  <td><code>{task.sourceDatasetCode}</code></td>
                  <td>
                    <span>{dataSources.find((source) => source.code === task.targetDataSourceCode)?.name ?? task.targetDataSourceCode}</span>
                    <small><code>{task.targetTable}</code></small>
                  </td>
                  <td>{conflictPolicyLabel(task.conflictPolicy)}</td>
                  <td>{scheduleSummary(task.scheduleType, task.scheduleTime, task.intervalMinutes, task.scheduleCron)}</td>
                  <td>
                    <span className={`sync-run-badge ${task.enabled ? 'ok' : 'idle'}`}>{task.enabled ? '启用' : '停用'}</span>
                  </td>
                  <td>{formatDateTime(task.lastRunAt)}</td>
                  <td>
                    <div className="sync-row-actions">
                      <button
                        type="button"
                        className="table-icon"
                        title="设计映射"
                        aria-label={`设计同步任务 ${task.name}`}
                        disabled={busyCode !== null}
                        onClick={() => void openDesigner(task)}
                      >
                        {busyCode === task.code && busyKind === 'open'
                          ? <LoaderCircle className="spin" size={16} />
                          : <FilePenLine size={16} />}
                      </button>
                      <button
                        type="button"
                        className="table-icon"
                        title={task.enabled ? '停用任务' : '启用任务'}
                        aria-label={`${task.enabled ? '停用' : '启用'}同步任务 ${task.name}`}
                        disabled={busyCode !== null}
                        onClick={() => void toggleTask(task)}
                      >
                        {busyCode === task.code && busyKind === 'toggle'
                          ? <LoaderCircle className="spin" size={16} />
                          : <Power size={16} />}
                      </button>
                      <button
                        type="button"
                        className="table-icon"
                        title="立即执行"
                        aria-label={`立即执行同步任务 ${task.name}`}
                        disabled={busyCode !== null}
                        onClick={() => void runTaskNow(task)}
                      >
                        {busyCode === task.code && busyKind === 'run'
                          ? <LoaderCircle className="spin" size={16} />
                          : <Play size={16} />}
                      </button>
                      <button
                        type="button"
                        className="table-icon"
                        title="运行记录"
                        aria-label={`查看同步任务 ${task.name} 的运行记录`}
                        disabled={busyCode !== null}
                        onClick={() => void showRuns(task)}
                      >
                        <History size={16} />
                      </button>
                      <button
                        type="button"
                        className="table-icon"
                        title="导出同步包"
                        aria-label={`导出同步任务 ${task.name} 的同步包`}
                        disabled={busyCode !== null}
                        onClick={() => void exportTaskPackage(task)}
                      >
                        {busyCode === task.code && busyKind === 'export'
                          ? <LoaderCircle className="spin" size={16} />
                          : <Download size={16} />}
                      </button>
                      <button
                        type="button"
                        className="table-icon danger"
                        title="删除任务"
                        aria-label={`删除同步任务 ${task.name}`}
                        disabled={busyCode !== null}
                        onClick={() => void deleteTask(task)}
                      >
                        {busyCode === task.code && busyKind === 'delete'
                          ? <LoaderCircle className="spin" size={16} />
                          : <Trash2 size={16} />}
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {runsTaskCode !== null && (
          <RunsPanel runs={runs} loading={runsLoading} polling={pollingTaskCode === runsTaskCode && pollingRunNo !== null} />
        )}
      </section>
    </main>
  );
}
