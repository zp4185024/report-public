import { useEffect, useMemo, useState, type ChangeEvent, type DragEvent } from 'react';
import {
  ArrowLeft,
  ArrowRight,
  CircleCheck,
  FileUp,
  LoaderCircle,
  PackageOpen,
  TriangleAlert,
  Zap,
} from 'lucide-react';
import { dataAssetApi, dataSyncApi } from './api';
import { StatusToasts } from './StatusToasts';
import { parseSyncPackageText } from './syncTaskDesign';
import {
  recallTargetDataSourceCode,
  rememberTargetDataSourceCode,
} from './reportPackageWizard';
import type {
  DataSyncImportResult,
  DataSyncPackage,
  DataSyncTryRunResult,
  DataSyncValidateResult,
  DataSourceDocument,
} from './types';

/** 向导步骤定义：上传 → 服务端校验 → 试跑 → 确认导入。 */
type WizardStep = 1 | 2 | 3 | 4;

const wizardSteps: { step: WizardStep; label: string }[] = [
  { step: 1, label: '上传同步包' },
  { step: 2, label: '校验与目标库' },
  { step: 3, label: '试跑数据' },
  { step: 4, label: '确认导入' },
];

/** 试跑样例表最多展示的行数；服务端本身也会把查询截断到 50 行。 */
const DRY_RUN_DISPLAY_ROW_LIMIT = 50;

/**
 * 渲染向导顶部的步骤指示条，让操作员随时知道当前进行到哪一步。
 * @param current - 当前所在的向导步骤。
 * @returns 步骤指示条 JSX。
 */
function WizardStepBar({ current }: { current: WizardStep }) {
  return (
    <ol className="package-wizard-steps" aria-label="同步包导入步骤">
      {wizardSteps.map((item) => (
        <li
          key={item.step}
          className={item.step === current
            ? 'current'
            : item.step < current
              ? 'done'
              : undefined}
          aria-current={item.step === current ? 'step' : undefined}
        >
          <span className="package-wizard-step-no">{item.step}</span>
          <span>{item.label}</span>
        </li>
      ))}
    </ol>
  );
}

/**
 * 渲染同步包试跑结果面板：目标列结论、失败统计与“源字段→目标列”前后对照表。
 * @param result - dry-run 接口返回的结果。
 * @returns 试跑结果面板 JSX。
 */
function SyncDryRunResultPanel({ result }: { result: DataSyncTryRunResult }) {
  // 阶段：汇总源字段键并限制展示行数，试跑数据用于结构核对而非业务浏览。
  const displayRows = result.samples.slice(0, DRY_RUN_DISPLAY_ROW_LIMIT);
  const sourceKeys = useMemo(() => {
    const keys: string[] = [];
    const seen = new Set<string>();
    for (const sample of result.samples) {
      for (const key of Object.keys(sample.source)) {
        if (!seen.has(key)) {
          seen.add(key);
          keys.push(key);
        }
      }
    }
    return keys;
  }, [result]);

  // 阶段：结果汇总。先透出结论与失败统计，再渲染前后对照表供逐列核对。
  return (
    <section className="sync-tryrun-panel" aria-label="同步包试跑结果">
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
      {/* 阶段：渲染前后对照表。左组为源字段原值，右组为转换链输出，空值统一显示占位符。 */}
      <div className="sync-tryrun-scroll">
        <table className="sync-tryrun-table">
          <thead>
            <tr>
              <th colSpan={Math.max(sourceKeys.length, 1)} className="sync-tryrun-group">源字段（转换前）</th>
              <th colSpan={Math.max(result.targetColumns.length, 1)} className="sync-tryrun-group target">目标列（转换后）</th>
            </tr>
            <tr>
              {sourceKeys.map((key) => <th key={key}>{key}</th>)}
              {result.targetColumns.map((key) => <th key={key}>{key}</th>)}
            </tr>
          </thead>
          <tbody>
            {displayRows.length === 0 ? (
              <tr>
                <td colSpan={Math.max(sourceKeys.length + result.targetColumns.length, 1)} className="sync-table-empty">
                  试跑成功，但当前数据集没有返回数据。
                </td>
              </tr>
            ) : displayRows.map((sample, index) => (
              <tr key={index}>
                {sourceKeys.map((key) => {
                  const cell = sample.source[key];
                  return <td key={`s-${key}`}>{cell === null || cell === undefined ? '—' : String(cell)}</td>;
                })}
                {result.targetColumns.map((key) => {
                  const cell = sample.target[key];
                  return <td key={`t-${key}`} className="sync-tryrun-target-cell">{cell === null || cell === undefined ? '—' : String(cell)}</td>;
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

/**
 * 渲染导入完成结果面板：动作摘要、服务端警告和进入同步任务列表的入口。
 * @param importResult - 导入接口返回的动作摘要。
 * @returns 导入结果面板 JSX。
 */
function SyncImportResultPanel({ importResult }: { importResult: DataSyncImportResult }) {
  return (
    <section className="package-import-result" aria-label="导入结果">
      <header>
        <CircleCheck size={18} />
        <h3>导入完成</h3>
      </header>
      {/* 阶段：回显数据集与同步任务实际执行的动作，供操作员留档核对。 */}
      <dl>
        <div><dt>任务编码</dt><dd><code>{importResult.taskCode}</code></dd></div>
        <div><dt>数据集动作</dt><dd>{importResult.datasetAction}</dd></div>
        <div><dt>同步任务动作</dt><dd>{importResult.taskAction}</dd></div>
      </dl>
      {importResult.warnings.length > 0 && (
        <div className="package-callout warning">
          <p><TriangleAlert size={15} />导入警告：</p>
          <ul>{importResult.warnings.map((item) => <li key={item}>{item}</li>)}</ul>
        </div>
      )}
      <a className="button button-primary" href="/data-sync-tasks">
        <PackageOpen size={16} />打开同步任务列表
      </a>
    </section>
  );
}

/**
 * 同步包导入向导页：线上管理员上传开发机导出的同步包，经服务端校验、
 * 选择目标数据源、真实试跑后确认写入。任何一步失败都停留在当前步骤，
 * 修复后可重试；导入成功后禁止重复提交。
 */
export default function DataSyncPackageImportPage() {
  // 阶段：向导状态声明。步骤位置、包内容、校验结论、试跑与导入产物各自独立，失败可就地重试。
  const [step, setStep] = useState<WizardStep>(1);
  const [requestPackage, setRequestPackage] = useState<DataSyncPackage | null>(null);
  const [fileName, setFileName] = useState('');
  const [handlingFile, setHandlingFile] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [validation, setValidation] = useState<DataSyncValidateResult | null>(null);
  const [dataSources, setDataSources] = useState<DataSourceDocument[]>([]);
  const [targetDataSourceCode, setTargetDataSourceCode] = useState('');
  const [mode, setMode] = useState<'Create' | 'Replace'>('Create');
  const [dryRunning, setDryRunning] = useState(false);
  const [dryRunResult, setDryRunResult] = useState<DataSyncTryRunResult | null>(null);
  const [dryRunPerformed, setDryRunPerformed] = useState(false);
  const [importing, setImporting] = useState(false);
  const [importResult, setImportResult] = useState<DataSyncImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // 进入页面即拉取数据源列表，并尝试带出上次导入使用的目标数据源。
  useEffect(() => {
    let active = true;
    /**
     * 加载已启用的全局数据源并回填记忆的选择值，供向导第 2 步使用。
     */
    const loadDataSources = async () => {
      try {
        const sources = await dataAssetApi.dataSources();
        if (!active) return;
        setDataSources(sources);
        const remembered = recallTargetDataSourceCode();
        if (remembered && sources.some((item) => item.enabled && item.code === remembered)) {
          setTargetDataSourceCode(remembered);
        }
      } catch (exception) {
        if (active) setError(exception instanceof Error ? exception.message : '数据源列表加载失败。');
      }
    };
    void loadDataSources();
    // 阶段：卸载保护。置 active=false 阻止组件销毁后异步回调继续写状态。
    return () => { active = false; };
  }, []);

  // 阶段：派生状态。集中计算数据源过滤、冲突决策与校验结论，让渲染层只读结论。
  const enabledDataSources = useMemo(
    () => dataSources.filter((item) => item.enabled),
    [dataSources],
  );
  const selectedDataSource = enabledDataSources.find((item) => item.code === targetDataSourceCode) ?? null;
  const collisions = validation?.collisions ?? null;
  const hasBlockingErrors = (validation?.errors.length ?? 0) > 0;
  const createDisabled = collisions?.taskCodeExists === true;
  const effectiveMode: 'Create' | 'Replace' = createDisabled ? 'Replace' : mode;
  const dataSourceTypeMismatch = requestPackage !== null
    && selectedDataSource !== null
    && selectedDataSource.dbType !== requestPackage.targetDataSourceType;

  // 阶段：上传处理。本地解析与服务端校验两步推进，任何失败都停留在第 1 步。
  /**
   * 读取上传文件并完成本地解析和服务端校验，成功后进入第 2 步；
   * 任何失败都停留在第 1 步并就地给出原因。
   * @param file - 用户选择或拖入的同步包文件。
   */
  const handleFile = async (file: File | null | undefined) => {
    if (!file || handlingFile) return;
    if (!/\.json$/iu.test(file.name) && file.type !== 'application/json') {
      setUploadError('请选择 .json 格式的同步包文件。');
      return;
    }
    setHandlingFile(true);
    setUploadError(null);
    setError(null);
    try {
      // 阶段一：本地解析，先把明显不是同步包的文件拦在服务端请求之前。
      const parsed = parseSyncPackageText(await file.text());
      if ('error' in parsed) {
        setUploadError(parsed.error);
        return;
      }
      // 阶段二：提交服务端做完整校验与目标库冲突探测，结果驱动第 2 步的选项。
      const validateResult = await dataSyncApi.validatePackage(parsed.package);
      setRequestPackage(parsed.package);
      setFileName(file.name);
      setValidation(validateResult);
      setMode(validateResult.collisions?.taskCodeExists ? 'Replace' : 'Create');
      setDryRunResult(null);
      setDryRunPerformed(false);
      setImportResult(null);
      setStep(2);
      // 阶段：信封解包提示。兼容旧导出端产物的同时明确建议重新导出，避免旧包装文件继续流转。
      setNotice(parsed.unwrappedFromEnvelope
        ? '检测到接口信封包装，已自动解包；建议升级导出端重新导出。同步包读取成功，请核对校验结果并选择目标数据源。'
        : '同步包读取成功，请核对校验结果并选择目标数据源。');
    } catch (exception) {
      setUploadError(exception instanceof Error ? exception.message : '同步包读取或校验失败。');
    } finally {
      setHandlingFile(false);
    }
  };

  /**
   * 响应文件选择框的变化并转交 handleFile 处理。
   * @param event - 文件输入框变化事件。
   */
  const onFileChange = (event: ChangeEvent<HTMLInputElement>) => {
    void handleFile(event.target.files?.[0]);
    event.target.value = '';
  };

  /**
   * 响应拖放区域的文件拖入，阻止默认打开行为并转交 handleFile 处理。
   * @param event - 拖放事件。
   */
  const onFileDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    void handleFile(event.dataTransfer.files?.[0]);
  };

  /**
   * 更新目标数据源选择：写入 localStorage 记忆，并作废与之绑定的
   * 试跑结果与导入结果，避免沿用旧数据源的验证结论。
   * @param code - 新选定的数据源编码。
   */
  const changeTargetDataSource = (code: string) => {
    setTargetDataSourceCode(code);
    rememberTargetDataSourceCode(code);
    setDryRunResult(null);
    setDryRunPerformed(false);
    setImportResult(null);
  };

  /**
   * 更新导入模式并作废已产生的导入结果，确保第 4 步摘要始终对应最新选择。
   * @param nextMode - 新的导入模式。
   */
  const changeImportMode = (nextMode: 'Create' | 'Replace') => {
    // 阶段：决策变化即作废旧导入结果，防止第 4 步摘要与最新选择不一致。
    setMode(nextMode);
    setImportResult(null);
  };

  // 阶段：试跑执行。用选定的目标数据源真实查询并按映射转换，最多 50 行且不写目标库。
  /**
   * 用选定的目标数据源真实执行包内源数据集并按映射转换（最多 50 行，不写目标库），
   * 成功后记录结果；失败时停留在第 3 步展示后端错误。
   */
  const runDryRun = async () => {
    if (!requestPackage || !targetDataSourceCode || dryRunning) return;
    setDryRunning(true);
    setError(null);
    try {
      const result = await dataSyncApi.dryRunPackage({
        package: requestPackage,
        targetDataSourceCode,
      });
      // 阶段：记录试跑结论并作废旧导入结果，第 4 步摘要必须基于最新一次验证。
      setDryRunResult(result);
      setDryRunPerformed(true);
      setImportResult(null);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '试跑请求失败。');
    } finally {
      setDryRunning(false);
    }
  };

  // 阶段：导入提交。确认弹窗列明写入后果，成功后锁定按钮防止重复写入。
  /**
   * 提交导入请求；成功后记录动作摘要并锁定提交按钮防止重复写入，
   * 失败时保留在第 4 步并展示后端错误供重试。
   */
  const confirmImport = async () => {
    if (!requestPackage || !targetDataSourceCode || importing || importResult) return;
    // 阶段：导入前确认。写入会修改目标库配置并生成审计文件，需操作员明确同意。
    const confirmLines = dryRunPerformed
      ? '已完成试跑。'
      : '注意：尚未试跑，未验证的 SQL 与映射可能失败。';
    if (!window.confirm(`确认导入同步任务「${requestPackage.taskCode}」？\n${confirmLines}\n数据集与同步任务将写入当前环境（服务端会记录审计文件）。`)) {
      return;
    }
    setImporting(true);
    setError(null);
    try {
      const result = await dataSyncApi.importPackage({
        package: requestPackage,
        targetDataSourceCode,
        mode: effectiveMode,
        dryRunPerformed,
      });
      // 阶段：成功后写入结果即锁定提交，importResult 非空时按钮永久禁用防止重复导入。
      setImportResult(result);
      setNotice('同步包导入完成。');
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '导入请求失败。');
    } finally {
      setImporting(false);
    }
  };

  /**
   * 重置整个向导回到第 1 步，用于更换包文件或导入失败后重新开始。
   */
  const restartWizard = () => {
    if (handlingFile || importing) return;
    // 阶段：整包重置向导状态，仅在不处于上传或导入进行中时允许执行。
    setStep(1);
    setRequestPackage(null);
    setFileName('');
    setUploadError(null);
    setValidation(null);
    setMode('Create');
    setDryRunResult(null);
    setDryRunPerformed(false);
    setImportResult(null);
    setError(null);
    setNotice(null);
  };

  // 阶段：页面骨架。头部说明 + 步骤条 + 全局通知，正文按当前步骤切换。
  return (
    <main className="report-package-import-page">
      <header className="package-import-header">
        <div>
          <span className="app-kicker">REPORT CENTER</span>
          <h1>同步包导入</h1>
          <p>上传开发机导出的数据同步包，校验后在当前环境启用；包内不含连接串和密码。</p>
        </div>
        {step > 1 && (
          <button type="button" className="button" onClick={restartWizard} disabled={handlingFile || importing}>
            重新选择包文件
          </button>
        )}
      </header>

      <WizardStepBar current={step} />

      <StatusToasts
        error={error}
        success={notice}
        onErrorClose={() => setError(null)}
        onSuccessClose={() => setNotice(null)}
      />

      {step === 1 && (
        <section className="package-import-card" aria-label="上传同步包">
          <div
            className={`package-upload-zone${handlingFile ? ' busy' : ''}`}
            onDragOver={(event) => event.preventDefault()}
            onDrop={onFileDrop}
          >
            {handlingFile
              ? <LoaderCircle className="spin" size={30} />
              : <FileUp size={30} />}
            <strong>{handlingFile ? '正在读取并校验同步包…' : '选择或拖入同步包文件'}</strong>
            <span>仅支持 .json 同步包（由数据同步任务列表「导出包」生成）</span>
            <label className="button button-primary package-upload-button">
              <FileUp size={16} />选择文件
              <input
                type="file"
                accept=".json,application/json"
                disabled={handlingFile}
                onChange={onFileChange}
              />
            </label>
          </div>
          {uploadError && (
            <p className="package-callout danger" role="alert"><TriangleAlert size={15} />{uploadError}</p>
          )}
        </section>
      )}

      {step === 2 && validation !== null && requestPackage !== null && (
        <section className="package-import-card" aria-label="校验结果与目标库选择">
          {/* 阶段：包摘要。集中展示包内是什么，帮助操作员确认拿对了文件。 */}
          {validation.packageSummary && (
            <div className="package-summary-grid">
              <div><span>任务编码</span><code>{validation.packageSummary.taskCode}</code></div>
              <div><span>源数据集</span><code>{validation.packageSummary.sourceDatasetCode}</code></div>
              <div><span>目标表</span><strong>{validation.packageSummary.targetTable}</strong></div>
              <div><span>映射字段数</span><strong>{validation.packageSummary.mappingCount}</strong></div>
              <div><span>导出方目标数据源类型</span><strong>{validation.packageSummary.targetDataSourceType}</strong></div>
              <div className="wide"><span>包文件</span><small>{fileName}（导出于 {requestPackage.exportedAt}）</small></div>
            </div>
          )}

          {/* 阶段：服务端校验结论。errors 阻断导入，warnings 仅提示。 */}
          {hasBlockingErrors && (
            <div className="package-callout danger" role="alert">
              <p><TriangleAlert size={15} />校验未通过，包不可导入：</p>
              <ul>{validation.errors.map((item, index) => <li key={`${item.code}-${index}`}>{item.message}</li>)}</ul>
            </div>
          )}
          {validation.warnings.length > 0 && (
            <div className="package-callout warning">
              <p><TriangleAlert size={15} />校验警告：</p>
              <ul>{validation.warnings.map((item, index) => <li key={`${item.code}-${index}`}>{item.message}</li>)}</ul>
            </div>
          )}

          {/* 阶段：目标数据源与冲突决策。选择结果决定试跑与导入写入的位置和方式。 */}
          <div className="package-decision-grid">
            <label className="package-decision-field">
              <span>目标数据源（数据集落库与映射试跑都使用它）</span>
              <select
                value={targetDataSourceCode}
                onChange={(event) => changeTargetDataSource(event.target.value)}
              >
                <option value="">请选择数据源</option>
                {enabledDataSources.map((item) => (
                  <option key={item.code} value={item.code} disabled={!item.passwordConfigured}>
                    {item.name}（{item.code} · {item.dbType} · {item.server}:{item.port}/{item.database}）
                    {!item.passwordConfigured ? '（未配置密码）' : ''}
                  </option>
                ))}
              </select>
              <small>选择会保存在本机浏览器，下次进入自动带出。</small>
            </label>

            {dataSourceTypeMismatch && selectedDataSource && (
              <p className="package-callout warning" role="alert">
                <TriangleAlert size={15} />
                目标数据源类型是 {selectedDataSource.dbType}，与导出方数据源类型 {requestPackage.targetDataSourceType} 不一致，
                源查询与映射转换的方言可能不兼容，强烈建议先试跑验证。
              </p>
            )}

            <fieldset className="package-decision-field">
              <legend>导入模式</legend>
              <label className="package-radio">
                <input
                  type="radio"
                  name="sync-package-import-mode"
                  checked={effectiveMode === 'Create'}
                  disabled={createDisabled || importing}
                  onChange={() => changeImportMode('Create')}
                />
                <span>
                  新建同步任务
                  {createDisabled && <small>目标库已存在同编码任务，只能选择替换。</small>}
                </span>
              </label>
              <label className="package-radio">
                <input
                  type="radio"
                  name="sync-package-import-mode"
                  checked={effectiveMode === 'Replace'}
                  disabled={importing}
                  onChange={() => changeImportMode('Replace')}
                />
                <span>替换现有同步任务（先删旧任务再建）</span>
              </label>
            </fieldset>

            {collisions?.datasetCodeExists && collisions.datasetIdentical === true && (
              <p className="package-callout info">
                <CircleCheck size={15} />
                目标库已存在同编码数据集且内容一致，导入时将直接复用，不会重复创建。
              </p>
            )}
            {collisions?.datasetCodeExists && collisions.datasetIdentical === false && (
              <p className="package-callout warning">
                <TriangleAlert size={15} />
                目标库已存在同编码数据集但内容不一致，导入时将更新该数据集
                {collisions.datasetReferencedByReports === true && '；该数据集同时被其他报表引用，更新会同步影响那些报表'}。
              </p>
            )}
            {collisions?.datasetCodeExists !== true && (
              <p className="package-callout info">
                <CircleCheck size={15} />
                目标库不存在同编码数据集，导入时将新建数据集 {requestPackage.sourceDataset.code}。
              </p>
            )}
          </div>

          <footer className="package-card-footer">
            <button type="button" className="button" onClick={restartWizard} disabled={handlingFile}>
              <ArrowLeft size={16} />重新选择文件
            </button>
            <button
              type="button"
              className="button button-primary"
              disabled={!validation.ok || hasBlockingErrors || !targetDataSourceCode}
              onClick={() => setStep(3)}
            >
              下一步：试跑数据<ArrowRight size={16} />
            </button>
          </footer>
        </section>
      )}

      {step === 3 && requestPackage !== null && (
        <section className="package-import-card" aria-label="试跑执行">
          <p className="package-callout info">
            同步包的源数据集没有查询参数，试跑将按包内 SQL 直接执行（只读查询，最多返回 50 行、受数据集超时约束），
            并按映射转换生成目标列样例，数据本身不会被修改。
          </p>

          {/* 阶段：执行动作。试跑可反复执行；未试跑也可直接进入确认，由导入确认弹窗再次提醒。 */}
          <div className="package-dryrun-actions">
            <button type="button" className="button" onClick={() => setStep(2)} disabled={dryRunning}>
              <ArrowLeft size={16} />上一步
            </button>
            <button
              type="button"
              className="button package-skip-button"
              onClick={() => setStep(4)}
              disabled={dryRunning}
            >
              跳过试跑
            </button>
            <button
              type="button"
              className="button button-primary"
              disabled={dryRunning || !targetDataSourceCode}
              onClick={() => void runDryRun()}
            >
              {dryRunning ? <LoaderCircle className="spin" size={16} /> : <Zap size={16} />}
              {dryRunning ? '正在试跑…' : `执行试跑${selectedDataSource ? `（${selectedDataSource.name}）` : ''}`}
            </button>
          </div>

          {dryRunResult && <SyncDryRunResultPanel result={dryRunResult} />}

          <footer className="package-card-footer">
            <button type="button" className="button" onClick={() => setStep(2)} disabled={dryRunning}>
              <ArrowLeft size={16} />上一步
            </button>
            <button type="button" className="button button-primary" onClick={() => setStep(4)}>
              下一步：确认导入<ArrowRight size={16} />
            </button>
          </footer>
        </section>
      )}

      {step === 4 && requestPackage !== null && validation !== null && (
        <section className="package-import-card" aria-label="确认导入">
          {/* 阶段：动作摘要。导入前把将要发生的写入逐项列明，供最后人工核对。 */}
          <div className="package-summary-grid">
            <div><span>任务动作</span><strong>{effectiveMode === 'Create' ? `新建同步任务 ${requestPackage.taskCode}` : `替换现有同步任务 ${requestPackage.taskCode}`}</strong></div>
            <div><span>数据集动作</span><strong>{
              collisions?.datasetCodeExists === true
                ? collisions.datasetIdentical === true
                  ? `复用现有数据集 ${requestPackage.sourceDataset.code}`
                  : `更新数据集 ${requestPackage.sourceDataset.code}`
                : `新建数据集 ${requestPackage.sourceDataset.code}`
            }</strong></div>
            <div><span>目标数据源</span><strong>{selectedDataSource
              ? `${selectedDataSource.name}（${selectedDataSource.dbType} · ${selectedDataSource.server}:${selectedDataSource.port}/${selectedDataSource.database}）`
              : targetDataSourceCode || '未选择'}</strong></div>
            <div><span>目标表 / 冲突策略</span><strong>{requestPackage.targetTable} · {requestPackage.conflictPolicy}</strong></div>
            <div><span>试跑状态</span><strong>{dryRunPerformed
              ? `已试跑（耗时 ${dryRunResult?.elapsedMilliseconds ?? '--'} ms）`
              : '已跳过试跑（未验证 SQL 与映射）'}</strong></div>
          </div>

          {validation.warnings.length > 0 && (
            <div className="package-callout warning">
              <p><TriangleAlert size={15} />校验警告（将随导入保留，请确认可接受）：</p>
              <ul>{validation.warnings.map((item, index) => <li key={`${item.code}-${index}`}>{item.message}</li>)}</ul>
            </div>
          )}

          {importResult ? <SyncImportResultPanel importResult={importResult} /> : (
            <footer className="package-card-footer">
              <button
                type="button"
                className="button"
                onClick={() => setStep(3)}
                disabled={importing}
              >
                <ArrowLeft size={16} />上一步
              </button>
              <button
                type="button"
                className="button button-primary"
                disabled={importing || !targetDataSourceCode || hasBlockingErrors}
                onClick={() => void confirmImport()}
              >
                {importing ? <LoaderCircle className="spin" size={16} /> : <PackageOpen size={16} />}
                {importing ? '正在导入…' : '确认导入'}
              </button>
            </footer>
          )}
        </section>
      )}
    </main>
  );
}
