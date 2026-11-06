import {
  useEffect,
  useMemo,
  useState,
  type ChangeEvent,
  type DragEvent,
  type FormEvent,
} from 'react';
import {
  ArrowLeft,
  ArrowRight,
  CircleCheck,
  FileUp,
  LoaderCircle,
  PackageOpen,
  TriangleAlert,
  X,
} from 'lucide-react';
import { dataAssetApi, reportPackageApi } from './api';
import { StatusToasts } from './StatusToasts';
import { parameterInputAttributes, parameterInputValue } from './parameterInput';
import { readMultiSelectValues, serializeMultiSelectValues } from './localDataQuery';
import {
  buildInitialParameterValues,
  missingRequiredParameterCodes,
  parseReportPackageText,
  recallTargetDataSourceCode,
  rememberTargetDataSourceCode,
} from './reportPackageWizard';
import type {
  DataSourceDocument,
  ReportPackage,
  ReportPackageDryRunResult,
  ReportPackageImportResult,
  ReportPackageParameter,
  ReportPackageValidateResult,
} from './types';

/** 向导步骤定义：上传 → 服务端校验 → 试跑 → 确认导入。 */
type WizardStep = 1 | 2 | 3 | 4;

const wizardSteps: { step: WizardStep; label: string }[] = [
  { step: 1, label: '上传报表包' },
  { step: 2, label: '校验与目标库' },
  { step: 3, label: '试跑数据' },
  { step: 4, label: '确认导入' },
];

/** 试跑结果表最多展示的行数；服务端本身也会把查询截断到 50 行。 */
const DRY_RUN_DISPLAY_ROW_LIMIT = 50;

/**
 * 渲染向导顶部的步骤指示条，让操作员随时知道当前进行到哪一步。
 * @param current - 当前所在的向导步骤。
 * @returns 步骤指示条 JSX。
 */
function WizardStepBar({ current }: { current: WizardStep }) {
  return (
    <ol className="package-wizard-steps" aria-label="导入步骤">
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
 * 渲染单个试跑参数输入控件：按 controlType 映射为文本、数字、日期、
 * 月份、下拉、多选或是否选择，行为与工作台查询区保持一致。
 * @param parameter - 包内参数定义。
 * @param value - 当前参数值（多选为序列化后的 JSON 字符串）。
 * @param onChange - 参数值变更回调，参数为新值。
 * @returns 单个参数的表单控件 JSX。
 */
function PackageParameterField({
  parameter,
  value,
  onChange,
}: {
  parameter: ReportPackageParameter;
  value: string;
  onChange: (next: string) => void;
}) {
  // 阶段：预处理控件类型与通用输入属性，为下方按类型分派做准备。
  const controlType = parameter.controlType.toLowerCase();
  const inputAttributes = parameterInputAttributes(parameter);

  // 阶段：按控件类型分派渲染，选项类控件保持"全部即空值"的查询语义。
  return (
    <label className="package-parameter-field">
      <span>
        {parameter.displayName}
        {parameter.isRequired && <em className="package-required-mark">必填</em>}
      </span>
      {/* 阶段：选项类控件（单选/多选/是否）渲染选择框，保证与工作台查询区一致的取值语义。 */}
      {controlType === 'select' ? (
        <select
          value={value}
          disabled={parameter.isReadOnly}
          required={parameter.isRequired}
          onChange={(event) => onChange(event.target.value)}
        >
          <option value="">全部</option>
          {(parameter.options ?? []).map((option) => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
      ) : controlType === 'multiselect' ? (
        <select
          className="package-multi-select"
          multiple
          size={Math.min(Math.max((parameter.options ?? []).length + 1, 2), 4)}
          value={readMultiSelectValues(value).length === 0 ? [''] : readMultiSelectValues(value)}
          disabled={parameter.isReadOnly}
          onChange={(event) => onChange(
            serializeMultiSelectValues(
              Array.from(event.currentTarget.selectedOptions, (option) => option.value)
                .filter(Boolean),
            ) ?? '',
          )}
        >
          <option value="">全部</option>
          {(parameter.options ?? []).map((option) => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
      ) : parameter.dataType.toLowerCase() === 'bool' || controlType === 'checkbox' ? (
        <select
          value={value}
          disabled={parameter.isReadOnly}
          required={parameter.isRequired}
          onChange={(event) => onChange(event.target.value)}
        >
          <option value="">全部</option>
          <option value="true">是</option>
          <option value="false">否</option>
        </select>
      ) : (
        <input
          type={inputAttributes.type}
          min={inputAttributes.min}
          max={inputAttributes.max}
          step={inputAttributes.step}
          value={parameterInputValue(parameter, value)}
          readOnly={parameter.isReadOnly}
          required={parameter.isRequired}
          onChange={(event) => onChange(event.target.value)}
        />
      )}
      {/* 阶段：标注默认值表达式来源，提示操作员该初始值是按当前日期计算的试跑样例。 */}
      {parameter.defaultExpression && !parameter.fixedValue && (
        <small className="package-parameter-hint">默认值表达式 {parameter.defaultExpression}（已按当前日期计算）</small>
      )}
    </label>
  );
}

/**
 * 渲染试跑结果面板：列清单、样例数据表格、耗时、截断提示和字段契约差异。
 * @param result - 试跑接口返回的结果。
 * @returns 试跑结果面板 JSX。
 */
function DryRunResultPanel({ result }: { result: ReportPackageDryRunResult }) {
  // 阶段：限制表格展示行数，试跑数据用于结构核对而非业务浏览。
  const displayRows = result.rows.slice(0, DRY_RUN_DISPLAY_ROW_LIMIT);
  return (
    <section className="package-dryrun-result" aria-label="试跑结果">
      <header>
        <h3>试跑结果</h3>
        <span>耗时 {result.elapsedMilliseconds} ms · 返回 {result.rows.length} 行</span>
      </header>
      {/* 阶段：先透出截断与字段契约警告，让操作员在读数据前知道结果的可信度边界。 */}
      {result.rowCountCapped && (
        <p className="package-callout warning">
          <TriangleAlert size={15} />结果行数达到试跑上限，仅返回前 50 行；完整结果请在导入后于工作台查询。
        </p>
      )}
      {!result.fieldSchemaMatch && (
        <div className="package-callout warning">
          <p><TriangleAlert size={15} />实际查询列与包内字段契约存在差异（属警告，不阻断导入）：</p>
          <ul>{result.fieldSchemaMismatches.map((item) => <li key={item}>{item}</li>)}</ul>
        </div>
      )}
      <div className="package-dryrun-columns">
        列（{result.columns.length}）：{result.columns.length > 0 ? result.columns.join('、') : '无返回列'}
      </div>
      {/* 阶段：渲染列清单与样例数据表；表头吸顶，空结果单独提示避免误判为失败。 */}
      <div className="package-dryrun-table-scroll">
        <table className="package-dryrun-table">
          <thead>
            <tr>{result.columns.map((column) => <th key={column}>{column}</th>)}</tr>
          </thead>
          <tbody>
            {displayRows.length === 0 ? (
              <tr><td colSpan={Math.max(result.columns.length, 1)} className="package-dryrun-empty">试跑成功，但当前参数下没有返回数据。</td></tr>
            ) : displayRows.map((row, index) => (
              <tr key={index}>
                {result.columns.map((column) => {
                  // 阶段：按列取值渲染单元格，空值显示占位符以便与空字符串区分。
                  const cell = row[column];
                  return <td key={column}>{cell === null || cell === undefined ? '—' : String(cell)}</td>;
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
 * 渲染导入完成结果面板：动作摘要、服务端警告和进入报表工作台的入口。
 * @param result - 导入接口返回的动作摘要。
 * @returns 导入结果面板 JSX。
 */
function ImportResultPanel({ result }: { result: ReportPackageImportResult }) {
  return (
    <section className="package-import-result" aria-label="导入结果">
      <header>
        <CircleCheck size={18} />
        <h3>导入完成</h3>
      </header>
      {/* 阶段：回显数据集与报表实际执行的动作，供操作员留档核对。 */}
      <dl>
        <div><dt>报表编码</dt><dd><code>{result.reportCode}</code></dd></div>
        <div><dt>数据集动作</dt><dd>{result.datasetAction}</dd></div>
        <div><dt>报表动作</dt><dd>{result.reportAction}</dd></div>
      </dl>
      {/* 阶段：展示服务端导入警告并给出直达工作台入口，完成迁移闭环。 */}
      {result.warnings.length > 0 && (
        <div className="package-callout warning">
          <p><TriangleAlert size={15} />导入警告：</p>
          <ul>{result.warnings.map((item) => <li key={item}>{item}</li>)}</ul>
        </div>
      )}
      <a className="button button-primary" href={`/reports/${encodeURIComponent(result.reportCode)}`}>
        <PackageOpen size={16} />打开报表
      </a>
    </section>
  );
}

/**
 * 报表包导入弹窗：由模板列表工具栏按钮打开，承载原独立导入页的完整向导流程
 * （上传 → 服务端校验 → 试跑 → 确认导入）。向导状态机与接口调用保持不变；
 * 弹窗关闭即卸载组件并向导状态清零，下次打开从第 1 步重新开始。
 * @param props - 组件属性；onClose 为请求宿主关闭弹窗的回调。
 * @returns 导入向导弹窗 JSX。
 */
export default function ReportPackageImportDialog({ onClose }: { onClose: () => void }) {
  // 阶段：向导状态声明。步骤位置、包内容、校验结论、试跑与导入产物各自独立，失败可就地重试。
  const [step, setStep] = useState<WizardStep>(1);
  const [requestPackage, setRequestPackage] = useState<ReportPackage | null>(null);
  const [fileName, setFileName] = useState('');
  const [handlingFile, setHandlingFile] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [validation, setValidation] = useState<ReportPackageValidateResult | null>(null);
  const [dataSources, setDataSources] = useState<DataSourceDocument[]>([]);
  const [targetDataSourceCode, setTargetDataSourceCode] = useState('');
  const [mode, setMode] = useState<'Create' | 'Replace'>('Create');
  const [replaceDataset, setReplaceDataset] = useState(false);
  const [parameterValues, setParameterValues] = useState<Record<string, string>>({});
  const [dryRunning, setDryRunning] = useState(false);
  const [dryRunResult, setDryRunResult] = useState<ReportPackageDryRunResult | null>(null);
  const [dryRunPerformed, setDryRunPerformed] = useState(false);
  const [importing, setImporting] = useState(false);
  const [importResult, setImportResult] = useState<ReportPackageImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // 弹窗挂载即拉取数据源列表，并尝试带出上次导入使用的目标数据源。
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

  // 阶段：派生状态。集中计算数据源过滤、冲突决策、模式回退和缺值必填，
  // 让渲染层只读结论，任何冲突组合的规则变化只改这一处。
  const enabledDataSources = useMemo(
    () => dataSources.filter((item) => item.enabled),
    [dataSources],
  );
  const selectedDataSource = enabledDataSources.find((item) => item.code === targetDataSourceCode) ?? null;
  const collisions = validation?.collisions ?? null;
  const hasBlockingErrors = (validation?.errors.length ?? 0) > 0;
  const createDisabled = collisions?.reportCodeExists === true;
  const replaceDatasetVisible = collisions?.datasetCodeExists === true && collisions.datasetIdentical === false;
  const replaceDatasetDisabled = collisions?.datasetReferencedByOther === true;
  const effectiveReplaceDataset = replaceDatasetVisible && !replaceDatasetDisabled && replaceDataset;
  const dbTypeMismatch = requestPackage !== null
    && selectedDataSource !== null
    && selectedDataSource.dbType !== requestPackage.sourceDataSourceType;
  const missingRequired = requestPackage !== null && step === 3
    ? missingRequiredParameterCodes(requestPackage, parameterValues)
    : [];
  const visibleParameters = useMemo(
    () => (requestPackage?.parameters ?? [])
      .filter((parameter) => parameter.isVisible && parameter.controlType.toLowerCase() !== 'hidden'),
    [requestPackage],
  );
  const effectiveMode: 'Create' | 'Replace' = createDisabled ? 'Replace' : mode;

  /**
   * 读取上传文件并完成本地解析和服务端校验，成功后进入第 2 步；
   * 任何失败都停留在第 1 步并就地给出原因。
   * @param file - 用户选择或拖入的报表包文件。
   */
  const handleFile = async (file: File | null | undefined) => {
    if (!file || handlingFile) return;
    if (!/\.json$/iu.test(file.name) && file.type !== 'application/json') {
      setUploadError('请选择 .json 格式的报表包文件。');
      return;
    }
    setHandlingFile(true);
    setUploadError(null);
    setError(null);
    try {
      // 阶段一：本地解析，先把明显不是报表包的文件拦在服务端请求之前。
      const parsed = parseReportPackageText(await file.text());
      if ('error' in parsed) {
        setUploadError(parsed.error);
        return;
      }
      // 阶段二：提交服务端做完整校验与目标库冲突探测，结果驱动第 2 步的选项。
      const validateResult = await reportPackageApi.validateReportPackage(parsed.package);
      setRequestPackage(parsed.package);
      setFileName(file.name);
      setValidation(validateResult);
      setParameterValues(buildInitialParameterValues(parsed.package, new Date()));
      setMode(validateResult.collisions.reportCodeExists ? 'Replace' : 'Create');
      setReplaceDataset(false);
      setDryRunResult(null);
      setDryRunPerformed(false);
      setImportResult(null);
      setStep(2);
      // 阶段：信封解包提示。兼容旧导出端产物的同时明确建议重新导出，避免旧包装文件继续流转。
      setNotice(parsed.unwrappedFromEnvelope
        ? '检测到接口信封包装，已自动解包；建议升级导出端重新导出。报表包读取成功，请核对校验结果并选择目标数据源。'
        : '报表包读取成功，请核对校验结果并选择目标数据源。');
    } catch (exception) {
      setUploadError(exception instanceof Error ? exception.message : '报表包读取或校验失败。');
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
   * 更新导入模式或数据集替换决定，并作废已产生的导入结果，
   * 确保第 4 步摘要始终对应最新选择。
   * @param nextMode - 新的导入模式。
   * @param nextReplaceDataset - 是否替换内容不一致的同编码数据集。
   */
  const changeImportDecisions = (
    nextMode: 'Create' | 'Replace',
    nextReplaceDataset: boolean,
  ) => {
    // 阶段：决策变化即作废旧导入结果，防止第 4 步摘要与最新选择不一致。
    setMode(nextMode);
    setReplaceDataset(nextReplaceDataset);
    setImportResult(null);
  };

  /**
   * 更新某个试跑参数的取值，同时作废与旧参数相关的试跑结论。
   * @param code - 参数编码。
   * @param value - 新取值。
   */
  const changeParameterValue = (code: string, value: string) => {
    setParameterValues((current) => ({ ...current, [code]: value }));
    setDryRunResult(null);
    setDryRunPerformed(false);
  };

  /**
   * 用当前参数真实执行包内数据集 SQL，成功后记录结果并放行第 4 步；
   * 失败时停留在第 3 步展示后端错误。
   * @param event - 表单提交事件。
   */
  const runDryRun = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!requestPackage || !targetDataSourceCode || dryRunning) return;
    // 阶段：执行前拦截。必填参数缺值时直接提示，不发出注定失败的试跑请求。
    if (missingRequired.length > 0) {
      setError(`请先填写必填参数：${missingRequired.join('、')}。`);
      return;
    }
    setDryRunning(true);
    setError(null);
    try {
      const result = await reportPackageApi.dryRunReportPackage({
        package: requestPackage,
        targetDataSourceCode,
        parameterValues,
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

  /**
   * 跳过试跑并直接进入确认步骤；跳过前必须二次确认，
   * 因为未验证的 SQL 在目标库方言下可能失败或返回意外结构。
   */
  const skipDryRun = () => {
    // 阶段：跳过试跑需二次确认，由操作员显式接受未验证 SQL 在目标库失败的风险。
    if (!window.confirm('跳过试跑意味着未在目标数据源上真实执行包内 SQL。\n如果 SQL 方言不兼容或字段结构变化，问题要等导入后使用时才会暴露。\n确定要跳过试跑直接进入确认导入吗？')) {
      return;
    }
    setDryRunResult(null);
    setDryRunPerformed(false);
    setStep(4);
  };

  /**
   * 提交导入请求；成功后记录动作摘要并锁定提交按钮防止重复写入，
   * 失败时保留在第 4 步并展示后端错误供重试。
   */
  const confirmImport = async () => {
    if (!requestPackage || !targetDataSourceCode || importing || importResult) return;
    // 阶段：导入前确认。写入会修改目标库配置并生成审计文件，需操作员明确同意。
    if (!window.confirm(`确认导入报表「${requestPackage.report.name}」？\n数据集与报表定义将写入当前环境（服务端会记录审计文件）。`)) {
      return;
    }
    setImporting(true);
    setError(null);
    try {
      const result = await reportPackageApi.importReportPackage({
        package: requestPackage,
        targetDataSourceCode,
        mode: effectiveMode,
        replaceDataset: effectiveReplaceDataset,
        dryRunPerformed,
      });
      // 阶段：成功后写入结果即锁定提交，importResult 非空时按钮永久禁用防止重复导入。
      setImportResult(result);
      setNotice('报表包导入完成。');
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
    setParameterValues({});
    setMode('Create');
    setReplaceDataset(false);
    setDryRunResult(null);
    setDryRunPerformed(false);
    setImportResult(null);
    setError(null);
    setNotice(null);
  };

  // 阶段：弹窗骨架。复用配置弹窗背板承载标题栏与可滚动向导正文，正文按当前步骤切换。
  return (
    <div className="configuration-backdrop" role="presentation">
      <section
        className="package-import-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="report-package-import-title"
      >
        <header className="configuration-header">
          <div>
            <h2 id="report-package-import-title">导入报表包</h2>
            <p>上传开发机导出的报表包，校验后在当前环境启用；包内不含连接串和密码。</p>
          </div>
          <div className="package-import-dialog-actions">
            {step > 1 && (
              <button type="button" className="button" onClick={restartWizard} disabled={handlingFile || importing}>
                重新选择包文件
              </button>
            )}
            <button
              type="button"
              className="icon-button"
              title="关闭"
              aria-label="关闭导入报表包"
              disabled={handlingFile || importing}
              onClick={onClose}
            >
              <X size={18} />
            </button>
          </div>
        </header>

        <div className="package-import-dialog-body">
          <WizardStepBar current={step} />

          <StatusToasts
            error={error}
            success={notice}
            onErrorClose={() => setError(null)}
            onSuccessClose={() => setNotice(null)}
          />

          {step === 1 && (
            <section className="package-import-card" aria-label="上传报表包">
              <div
                className={`package-upload-zone${handlingFile ? ' busy' : ''}`}
                onDragOver={(event) => event.preventDefault()}
                onDrop={onFileDrop}
              >
                {handlingFile
                  ? <LoaderCircle className="spin" size={30} />
                  : <FileUp size={30} />}
                <strong>{handlingFile ? '正在读取并校验报表包…' : '选择或拖入报表包文件'}</strong>
                <span>仅支持 .json 报表包（由模板列表「导出包」生成）</span>
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
              <div className="package-summary-grid">
                <div><span>报表编码</span><code>{validation.packageSummary.reportCode}</code></div>
                <div><span>报表名称</span><strong>{validation.packageSummary.reportName}</strong></div>
                <div><span>数据集</span><code>{validation.packageSummary.datasetCode}</code></div>
                <div><span>参数数量</span><strong>{validation.packageSummary.parameterCount}</strong></div>
                <div><span>字段数量</span><strong>{validation.packageSummary.fieldCount}</strong></div>
                <div><span>导出方数据源类型</span><strong>{validation.packageSummary.sourceDataSourceType}</strong></div>
                <div className="wide"><span>包文件</span><small>{fileName}（导出于 {requestPackage.exportedAt}）</small></div>
              </div>

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
                  <span>目标数据源（试跑与导入都使用它）</span>
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

                {dbTypeMismatch && selectedDataSource && (
                  <p className="package-callout warning" role="alert">
                    <TriangleAlert size={15} />
                    目标数据源类型是 {selectedDataSource.dbType}，与导出方数据源类型 {requestPackage.sourceDataSourceType} 不一致，
                    SQL 方言可能不兼容，强烈建议先试跑验证。
                  </p>
                )}

                <fieldset className="package-decision-field">
                  <legend>导入模式</legend>
                  <label className="package-radio">
                    <input
                      type="radio"
                      name="package-import-mode"
                      checked={effectiveMode === 'Create'}
                      disabled={createDisabled || importing}
                      onChange={() => changeImportDecisions('Create', replaceDataset)}
                    />
                    <span>
                      新建报表
                      {createDisabled && <small>目标库已存在同编码报表，只能选择替换。</small>}
                    </span>
                  </label>
                  <label className="package-radio">
                    <input
                      type="radio"
                      name="package-import-mode"
                      checked={effectiveMode === 'Replace'}
                      disabled={importing}
                      onChange={() => changeImportDecisions('Replace', replaceDataset)}
                    />
                    <span>替换现有报表（删除旧报表后重建）</span>
                  </label>
                </fieldset>

                {collisions?.datasetCodeExists && collisions.datasetIdentical === true && (
                  <p className="package-callout info">
                    <CircleCheck size={15} />
                    目标库已存在同编码数据集且内容一致，导入时将直接复用，不会重复创建。
                  </p>
                )}
                {replaceDatasetVisible && (
                  <label className={`package-decision-field checkbox${replaceDatasetDisabled ? ' disabled' : ''}`}>
                    <input
                      type="checkbox"
                      checked={replaceDataset && !replaceDatasetDisabled}
                      disabled={replaceDatasetDisabled || importing}
                      onChange={(event) => changeImportDecisions(mode, event.target.checked)}
                    />
                    <span>
                      替换内容不一致的同编码数据集（{requestPackage.dataset.code}）
                      {replaceDatasetDisabled && (
                        <small>该数据集被其他报表引用，替换可能影响那些报表，已禁止替换。</small>
                      )}
                    </span>
                  </label>
                )}
                {collisions?.datasetCodeExists !== true && (
                  <p className="package-callout info">
                    <CircleCheck size={15} />
                    目标库不存在同编码数据集，导入时将新建数据集 {requestPackage.dataset.code}。
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
            <section className="package-import-card" aria-label="试跑参数与执行">
              <form className="package-dryrun-form" onSubmit={(event) => void runDryRun(event)}>
                {/* 阶段：参数表单。按包内参数定义生成，缺值的必填参数阻止提交。 */}
                {visibleParameters.length === 0 ? (
                  <p className="package-callout info">该报表没有需要填写的查询参数，可直接执行试跑。</p>
                ) : (
                  <div className="package-parameter-grid">
                    {visibleParameters.map((parameter) => (
                      <PackageParameterField
                        key={parameter.code}
                        parameter={parameter}
                        value={parameterValues[parameter.code] ?? ''}
                        onChange={(value) => changeParameterValue(parameter.code, value)}
                      />
                    ))}
                  </div>
                )}
                {missingRequired.length > 0 && (
                  <p className="package-callout warning">
                    <TriangleAlert size={15} />以下必填参数尚未填写：{missingRequired.join('、')}。
                  </p>
                )}

                {/* 阶段：执行动作。试跑可反复执行；跳过需二次确认并承担风险。 */}
                <div className="package-dryrun-actions">
                  <button type="button" className="button" onClick={() => setStep(2)} disabled={dryRunning}>
                    <ArrowLeft size={16} />上一步
                  </button>
                  <button
                    type="button"
                    className="button package-skip-button"
                    onClick={skipDryRun}
                    disabled={dryRunning}
                  >
                    跳过试跑
                  </button>
                  <button
                    type="submit"
                    className="button button-primary"
                    disabled={dryRunning || !targetDataSourceCode || missingRequired.length > 0}
                  >
                    {dryRunning ? <LoaderCircle className="spin" size={16} /> : <PackageOpen size={16} />}
                    {dryRunning ? '正在试跑…' : `执行试跑${selectedDataSource ? `（${selectedDataSource.name}）` : ''}`}
                  </button>
                </div>
                <p className="package-dryrun-note">
                  试跑会在目标数据源上真实执行包内 SQL（只读查询，最多返回 50 行、超时 30 秒），
                  数据本身不会被修改。
                </p>
              </form>

              {dryRunResult && <DryRunResultPanel result={dryRunResult} />}

              {dryRunPerformed && (
                <footer className="package-card-footer">
                  <button type="button" className="button" onClick={() => setStep(2)} disabled={dryRunning}>
                    <ArrowLeft size={16} />上一步
                  </button>
                  <button type="button" className="button button-primary" onClick={() => setStep(4)}>
                    下一步：确认导入<ArrowRight size={16} />
                  </button>
                </footer>
              )}
            </section>
          )}

          {step === 4 && requestPackage !== null && validation !== null && (
            <section className="package-import-card" aria-label="确认导入">
              {/* 阶段：动作摘要。导入前把将要发生的写入逐项列明，供最后人工核对。 */}
              <div className="package-summary-grid">
                <div><span>报表动作</span><strong>{effectiveMode === 'Create' ? `新建报表 ${requestPackage.report.code}` : `替换现有报表 ${requestPackage.report.code}`}</strong></div>
                <div><span>数据集动作</span><strong>{
                  collisions?.datasetCodeExists === true
                    ? effectiveReplaceDataset
                      ? `替换数据集 ${requestPackage.dataset.code}`
                      : `复用现有数据集 ${requestPackage.dataset.code}`
                    : `新建数据集 ${requestPackage.dataset.code}`
                }</strong></div>
                <div><span>目标数据源</span><strong>{selectedDataSource
                  ? `${selectedDataSource.name}（${selectedDataSource.dbType} · ${selectedDataSource.server}:${selectedDataSource.port}/${selectedDataSource.database}）`
                  : targetDataSourceCode || '未选择'}</strong></div>
                <div><span>试跑状态</span><strong>{dryRunPerformed
                  ? `已试跑（耗时 ${dryRunResult?.elapsedMilliseconds ?? '--'} ms）`
                  : '已跳过试跑（未验证 SQL）'}</strong></div>
              </div>

              {validation.warnings.length > 0 && (
                <div className="package-callout warning">
                  <p><TriangleAlert size={15} />校验警告（将随导入保留，请确认可接受）：</p>
                  <ul>{validation.warnings.map((item, index) => <li key={`${item.code}-${index}`}>{item.message}</li>)}</ul>
                </div>
              )}

              {importResult ? <ImportResultPanel result={importResult} /> : (
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
        </div>
      </section>
    </div>
  );
}
