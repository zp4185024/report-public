import type { IWorkbookData } from '@univerjs/presets';

/**
 * 本文件镜像 .NET CoreValidation 的公开 JSON 契约。
 * 属性名采用 ASP.NET Core 默认 camelCase 结果；修改字段时必须同步后端 record 和序列化测试。
 */

/** 模板设计人员选择的预览模式：整表或分页。 */
export type PreviewMode = 'Full' | 'Paged';

/** 控制预览每次拉取多少行，以及是否为了页码展示执行精确总数查询。 */
export interface PreviewSettings {
  mode: PreviewMode;
  pageSize: number;
  showExactTotal: boolean;
}

/** 普通导出直接返回文件；大文件导出通过后台任务生成并轮询进度。 */
export type DataExportMode = 'Normal' | 'Large';

/** 模板拥有的导出阈值和默认策略，与预览是否分页相互独立。 */
export interface ExportSettings {
  defaultMode: DataExportMode;
  normalMaxRows: number;
  normalMaxCells: number;
  normalTimeoutSeconds: number;
  largeExportEnabled: boolean;
  rowsPerSheet: number;
  fileRetentionHours: number;
}

export interface ValueOption {
  value: string;
  label: string;
}

/** 返回字段的值显示映射；它不参与查询参数取值。 */
export interface FieldPresentationDefinition {
  code: string;
  valueMappings: ValueOption[];
}

/** 共享页的数据保护状态；密码和密码摘要永远不进入浏览器公开契约。 */
export interface ShareProtectionSettings {
  enabled: boolean;
  passwordConfigured: boolean;
}

export interface SaveShareProtectionSettings {
  enabled: boolean;
  newPassword: string | null;
}

export interface ShareProtectionUnlockResult {
  valid: boolean;
}

/** 后台参数定义；defaultValue 已由后台默认函数解析，浏览器不重复计算日期规则。 */
export interface ParameterDefinition {
  code: string;
  displayName: string;
  controlType: string;
  sourceType: string;
  defaultValue: string | null;
  visible: boolean;
  dataType: string;
  defaultExpression: string | null;
  fixedValue: string | null;
  readOnly: boolean;
  required: boolean;
  allowUrlPrefill: boolean;
  urlKey: string | null;
  options?: ValueOption[] | null;
  sourceCode?: string | null;
  sourceFieldCode?: string | null;
}

export interface ExternalParameterFieldDefinition {
  code: string;
  displayName: string;
  dataType: string;
  responsePath: string;
}

export interface ExternalRequestParameterDefinition {
  name: string;
  urlKey: string;
  location: 'Query' | 'Body' | 'Header';
  required: boolean;
}

export interface ExternalParameterSourceDefinition {
  code: string;
  name: string;
  enabled: boolean;
  fields: ExternalParameterFieldDefinition[];
  contextKeys: string[];
}

export interface ExternalParameterSourceDocument {
  code: string;
  name: string;
  baseUrl: string;
  endpoint: string;
  httpMethod: 'GET' | 'POST';
  requestBodyTemplate: string;
  headersTemplate: string;
  requestParameters: ExternalRequestParameterDefinition[];
  timeoutSeconds: number;
  enabled: boolean;
  revision: number;
  fields: ExternalParameterFieldDefinition[];
}

export type ExternalParameterSourceInput = ExternalParameterSourceDocument;

export interface ExternalParameterSourceTestRequest {
  context: Record<string, string | null>;
}

export interface ExternalParameterSourceTestResult {
  values: Record<string, string>;
}

/** 数据集声明的 SQL 输入变量；取值来源可为查询表单，或固定值/滚动表达式兜底。 */
export interface DatasetVariableDefinition {
  code: string;
  displayName: string;
  dataType: string;
  required: boolean;
  sourceType?: 'Form' | 'External';
  sourceCode?: string | null;
  sourceFieldCode?: string | null;
  /** 固定兜底值：调用方未传值时使用；与 defaultExpression 同时存在时表达式现算优先。 */
  defaultValue?: string | null;
  /** 时间滚动默认表达式（如 startofmonth(addmonths(today(),-1))），每次执行按当前时间现算，仅日期类变量可用。 */
  defaultExpression?: string | null;
}

/** 查询返回字段，只描述数据契约；表头、列宽和公式属于工作簿模板。 */
export interface DatasetFieldDefinition {
  code: string;
  displayName: string;
  dataType: string;
  /** 可选列注释（中文业务含义）；预览接口按数据库注释填充，取不到为 null。 */
  description?: string | null;
}

/** 字段面板、查询区和模板校验共同消费的报表元数据。 */
export interface ReportDefinition {
  code: string;
  name: string;
  sheetName: string;
  preview: PreviewSettings;
  export: ExportSettings;
  parameters: ParameterDefinition[];
  fields: DatasetFieldDefinition[];
  fieldPresentations?: FieldPresentationDefinition[] | null;
  shareProtection?: ShareProtectionSettings | null;
  externalSources?: ExternalParameterSourceDefinition[] | null;
}

/** 一次预览或导出的查询条件。page 始终使用一基页码，空输入转换为 null。 */
export interface ExecuteRequest {
  page: number;
  parameters: Record<string, string | null>;
  context?: Record<string, string | null>;
  /** 仅开发环境请求后端返回 SQL 调试信息。 */
  sqlDebug?: boolean;
}

/** 数据导出只引用数据库中的已保存模板，不能携带浏览器中的未保存快照。 */
export interface DataExportRequest {
  templateRevision: number;
  query: ExecuteRequest;
}

/** 后台解析完成的非空参数，值可直接替换工作簿中的 params token。 */
export type ResolvedParameters = Record<string, string>;

/** 后台明细行；合计等公式字段不由后台返回。 */
export type DataRowValue = string | number | boolean | null;
export type WorkloadRow = Record<string, DataRowValue>;

export type WorkloadRowFieldCode = string;

/** 参数 token 的零基工作簿坐标。一个单元格可以对应多个参数绑定。 */
export interface ParameterBinding {
  code: string;
  sheetId: string;
  row: number;
  column: number;
}

/** 明细模板行中的字段列坐标。 */
export interface DetailFieldBinding {
  code: WorkloadRowFieldCode;
  column: number;
}

/** 单一明细区域；当前执行器只支持一个工作表中的一条模板行。 */
export interface DetailBinding {
  datasetCode: 'detail';
  sheetId: string;
  templateRow: number;
  fields: DetailFieldBinding[];
}

/** 从工作簿扫描出的规范绑定。version 用于阻止未来不兼容协议被静默执行。 */
export interface TemplateBinding {
  version: 1;
  parameterBindings: ParameterBinding[];
  detailBinding: DetailBinding | null;
}

/** 数据库模板文档。revision 用于保存时的乐观并发控制。 */
export interface TemplateDocument {
  reportCode: string;
  snapshot: IWorkbookData;
  binding: TemplateBinding;
  revision: number;
  validationWarning: string | null;
}

/** 保存请求；后端会重新扫描 snapshot，不会直接信任这里提交的 binding。 */
export interface SaveTemplateRequest {
  snapshot: IWorkbookData;
  binding: TemplateBinding;
  revision: number;
}

/** XLSX 转换提示。ruleCode 可直接定位数据库中的 RcPocExchangeMapping 记录。 */
export interface TemplateExchangeDiagnostic {
  ruleCode: string;
  message: string;
  sheetName: string | null;
  cellAddress: string | null;
}

/** 后台完成 XLSX 转换和 token 校验后返回的可编辑草稿。 */
export interface TemplateXlsxImportResult {
  snapshot: IWorkbookData;
  binding: TemplateBinding;
  univerVersion: string;
  mappingVersion: number;
  usedCellCount: number;
  warnings: TemplateExchangeDiagnostic[];
}

/** 一页后台结果。hasMore 允许不查询精确总数也能驱动下一页按钮。 */
export interface ExecuteResult {
  page: number;
  pageSize: number;
  hasMore: boolean;
  parameters: ResolvedParameters;
  rows: WorkloadRow[];
  sqlDebug?: SqlDebugInfo | null;
}

export interface SqlDebugParameter {
  name: string;
  dataType: string;
  value: unknown;
}

export interface SqlDebugInfo {
  parameterizedSql: string;
  displaySql: string;
  parameters: SqlDebugParameter[];
}

export type DataSourceDbType = 'SqlServer' | 'Oracle' | 'MySql' | 'PostgreSQL';

export interface DataSourceDocument {
  code: string;
  name: string;
  dbType: DataSourceDbType;
  server: string;
  port: number;
  database: string;
  userName: string;
  passwordConfigured: boolean;
  timeoutSeconds: number;
  maxRows: number;
  enabled: boolean;
  revision: number;
}

export interface DataSourceInput extends Omit<DataSourceDocument, 'passwordConfigured'> {
  password: string | null;
  passwordConfigured?: boolean;
}

export interface DatasetDocument {
  code: string;
  name: string;
  dataSourceCode: string;
  queryText: string;
  variables: DatasetVariableDefinition[];
  fields: DatasetFieldDefinition[];
  timeoutSeconds: number;
  maxRows: number;
  enabled: boolean;
  revision: number;
}

export interface DatasetPreviewRequest {
  parameters: Record<string, string | null>;
  context?: Record<string, string | null>;
}

export interface DatasetPreviewResult {
  fields: DatasetFieldDefinition[];
  rows: DataRowValue[][];
  hasMore: boolean;
  limit: number;
  durationMilliseconds: number;
}

export interface DatasetReferenceDocument {
  reportCode: string;
  reportName: string;
  sheetName: string;
  enabled: boolean;
}

/** 删除接口的确认回执，只回传被删除的资源编码。 */
export interface DeletedDocument {
  code: string;
}

export interface ReportSummaryDocument {
  reportCode: string;
  name: string;
  sheetName: string;
  datasetCode: string;
  enabled: boolean;
  configurationRevision: number;
  templateRevision: number;
  updatedAt: string | null;
}

/** 管理列表的分页响应，items 只包含当前页记录。 */
export interface PagedDocument<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
}

export interface CreateReportRequest {
  reportCode: string;
  name: string;
  sheetName: string;
  datasetCode: string;
}

export interface DatasetOption {
  code: string;
  name: string;
  dataSourceCode: string;
  variables: DatasetVariableDefinition[];
  fields: DatasetFieldDefinition[];
  enabled: boolean;
}

export interface ReportCreateOptionsDocument {
  datasets: DatasetOption[];
}

export interface ReportConfigurationDocument {
  reportCode: string;
  name: string;
  sheetName: string;
  preview: PreviewSettings;
  export: ExportSettings;
  datasetCode: string;
  datasets: DatasetOption[];
  parameters: ParameterDefinition[];
  fields: DatasetFieldDefinition[];
  revision: number;
  fieldPresentations?: FieldPresentationDefinition[] | null;
  shareProtection?: ShareProtectionSettings | null;
  externalSources?: ExternalParameterSourceDefinition[] | null;
}

export interface SaveReportConfigurationRequest {
  name: string;
  sheetName: string;
  preview: PreviewSettings;
  export: ExportSettings;
  datasetCode: string;
  parameters: ParameterDefinition[];
  revision: number;
  fieldPresentations?: FieldPresentationDefinition[] | null;
  shareProtection: SaveShareProtectionSettings;
}

export interface DataSourceTestResult {
  success: boolean;
  durationMilliseconds: number;
  databaseVersion: string;
}

/** 报表中心前端启动所需的可选功能开关。 */
export interface ReportCenterFeatures {
  jnpfMigrationMenuEnabled: boolean;
}

/** JNPF 报表迁移程序返回的只读盘点项；标记和阻断原因由服务端拼接为文本。 */
export interface JnpfMigrationInventoryItem {
  sourceReportId: string | null;
  sourceVersionId: string | null;
  reportName: string | null;
  sourceReportCode: string | null;
  targetReportCode: string | null;
  dataSourceNames: string | null;
  dbTypes: string | null;
  migratable: boolean;
  status: 'Ready' | 'Blocked' | string | null;
  fieldCount: number;
  parameterCount: number;
  detailFieldCount: number;
  blockers: string | null;
  markers: string | null;
}

export interface JnpfMigrationRunResult {
  inventory: JnpfMigrationInventoryItem[];
  results: JnpfMigrationResultItem[];
  reportDataSourceCode: string;
  reportDataSourceName: string;
  reportDataSourceDbType: DataSourceDbType;
  writeTargetDatabase: string;
  writeTargetDbType: DataSourceDbType;
}

export interface JnpfMigrationResultItem {
  reportCode: string | null;
  reportName: string | null;
  status: string | null;
  message: string | null;
}

/** 异步大文件导出状态，与后端字符串枚举保持一致。 */
export type ExportJobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';

/** 大文件任务轮询快照。 */
export interface ExportJob {
  jobNo: string;
  status: ExportJobStatus;
  totalRows: number;
  writtenRows: number;
  error: string | null;
  createdAt: string;
  completedAt: string | null;
}

/** 报表包文件的固定类型标识；上传向导用它先做一次本地识别。 */
export const REPORT_PACKAGE_KIND = 'hd-report-center.report-package';

/** 当前前端支持的报表包结构版本；更高的版本必须先升级导入向导。 */
export const REPORT_PACKAGE_SUPPORTED_VERSION = 1;

/** 报表包内的报表定义节；只含业务内容，不带数据库主键、修订号和分享保护口令。 */
export interface ReportPackageReport {
  code: string;
  name: string;
  sheetName: string;
  datasetCode: string;
  previewSettings: PreviewSettings;
  exportSettings: ExportSettings;
  fieldPresentations: FieldPresentationDefinition[] | null;
  isEnabled: boolean;
}

/** 报表包内的参数定义；字段与后台 RcReportParameter 对应，默认值是表达式原文。 */
export interface ReportPackageParameter {
  code: string;
  displayName: string;
  dataType: string;
  controlType: string;
  sourceType: string;
  sourceCode: string | null;
  sourceFieldCode: string | null;
  urlKey: string | null;
  defaultExpression: string | null;
  fixedValue: string | null;
  options: ValueOption[] | null;
  isVisible: boolean;
  isReadOnly: boolean;
  isRequired: boolean;
  allowUrlPrefill: boolean;
  displayOrder: number;
}

/** 报表包内的模板节；revision 只反映导出时的模板版本，导入后从 1 重新开始。 */
export interface ReportPackageTemplate {
  snapshot: IWorkbookData;
  binding: TemplateBinding;
  revision: number;
}

/** 报表包内的数据集字段契约：入参变量与返回字段的声明。 */
export interface ReportPackageDatasetSchema {
  variables: DatasetVariableDefinition[];
  fields: DatasetFieldDefinition[];
}

/** 报表包内的数据集节；不含连接信息，导入时由用户选择目标数据源。 */
export interface ReportPackageDataset {
  code: string;
  name: string;
  queryText: string;
  fieldSchema: ReportPackageDatasetSchema;
  timeoutSeconds: number;
  maxRows: number;
  isEnabled: boolean;
}

/** 报表包根契约；包内绝不包含数据源连接串或密码。 */
export interface ReportPackage {
  packageKind: string;
  packageVersion: number;
  exportedAt: string;
  sourceDataSourceCode: string;
  sourceDataSourceType: string;
  report: ReportPackageReport;
  parameters: ReportPackageParameter[];
  template: ReportPackageTemplate;
  dataset: ReportPackageDataset;
}

/** 校验报告中的包内容摘要，供向导展示将要导入什么。 */
export interface ReportPackageSummary {
  reportCode: string;
  reportName: string;
  datasetCode: string;
  parameterCount: number;
  fieldCount: number;
  sourceDataSourceType: string;
}

/** 与目标库的冲突探测结果；datasetIdentical 在数据集不存在时为 null。 */
export interface ReportPackageCollisions {
  reportCodeExists: boolean;
  datasetCodeExists: boolean;
  datasetIdentical: boolean | null;
  datasetReferencedByOther: boolean | null;
}

/** 校验报告中的单条错误或警告；code 为稳定业务错误码，message 面向管理员展示。 */
export interface ReportPackageIssue {
  code: string;
  message: string;
}

/** 报表包校验结果；errors 非空表示不可导入，warnings 只提示不阻断。 */
export interface ReportPackageValidateResult {
  ok: boolean;
  errors: ReportPackageIssue[];
  warnings: ReportPackageIssue[];
  packageSummary: ReportPackageSummary;
  collisions: ReportPackageCollisions;
}

/** 试跑请求：整份包 + 用户选择的目标数据源 + 为包参数填写的样例取值。 */
export interface ReportPackageDryRunRequest {
  package: ReportPackage;
  targetDataSourceCode: string;
  parameterValues: Record<string, string>;
}

/** 试跑返回的样例行；键为查询结果列名，值为标量。 */
export type ReportPackageDryRunRow = Record<string, string | number | null>;

/** 试跑结果：最多 50 行真实查询数据和字段契约比对结论。 */
export interface ReportPackageDryRunResult {
  columns: string[];
  rows: ReportPackageDryRunRow[];
  elapsedMilliseconds: number;
  rowCountCapped: boolean;
  fieldSchemaMatch: boolean;
  fieldSchemaMismatches: string[];
}

/** 导入提交请求；mode=Replace 表示先删旧报表再建，replaceDataset 控制是否替换同编码数据集。 */
export interface ReportPackageImportRequest {
  package: ReportPackage;
  targetDataSourceCode: string;
  mode: 'Create' | 'Replace';
  replaceDataset: boolean;
  dryRunPerformed: boolean;
}

/** 导入完成后的动作摘要，告诉管理员数据集和报表各自执行了什么动作。 */
export interface ReportPackageImportResult {
  reportCode: string;
  datasetAction: string;
  reportAction: string;
  warnings: string[];
}

/* ===== 数据同步任务契约（镜像 CoreValidation/PocDataSyncContracts.cs 与 PocDataSyncApi.cs）===== */

/** 数据同步包文件的固定类型标识；导入向导用它先做一次本地识别。镜像 PocDataSyncPackageFormat.PackageKind。 */
export const DATA_SYNC_PACKAGE_KIND = 'hd-report-center.sync-package';

/** 当前前端支持的同步包结构版本；更高的版本必须先升级导入向导。镜像 PocDataSyncPackageFormat.PackageVersion。 */
export const DATA_SYNC_PACKAGE_SUPPORTED_VERSION = 1;

/** 字段级映射中的单个转换器步骤；Type 是内置转换器名，Args 值均为文本。镜像 PocSyncConverterStep。 */
export interface SyncConverterStep {
  type: string;
  args: Record<string, string | null> | null;
}

/** 单个字段的同步映射：源字段经转换链后写入目标列。镜像 PocSyncColumnMapping。 */
export interface SyncColumnMapping {
  sourceField: string;
  targetField: string;
  targetType: string;
  length: number | null;
  nullable: boolean;
  converters: SyncConverterStep[];
  /** 合并键标志：仅冲突策略为 Upsert 时生效，全部映射中必须有且只有一条为 true 且该列不允许空。 */
  isKey?: boolean;
}

/** 任务调度定义；Type 为 Manual/Daily/Interval/Cron。镜像 PocSyncScheduleDefinition。 */
export interface SyncScheduleDefinition {
  type: string;
  time: string | null;
  intervalMinutes: number;
  /** Cron 调度表达式（标准 5 段）；仅 Type 为 Cron 时有值，其余类型为 null。镜像 CronExpression。 */
  cronExpression?: string | null;
}

/** 同步任务完整定义（DefinitionJson 的对象形态）。镜像 PocSyncTaskDefinition。 */
export interface SyncTaskDefinition {
  mappings: SyncColumnMapping[];
  schedule: SyncScheduleDefinition | null;
}

/** 同步包内的源数据集快照；fieldSchema 后端按 JObject 原样携带，不做结构解释。镜像 PocDataSyncPackageDataset。 */
export interface DataSyncPackageDataset {
  code: string;
  name: string;
  dataSourceCode: string;
  dataSourceType: string;
  queryText: string;
  fieldSchema: Record<string, unknown>;
  timeoutSeconds: number;
  maxRows: number;
  isEnabled: boolean;
}

/** 数据同步包根契约；包内绝不包含连接串或密码。镜像 PocDataSyncPackage。 */
export interface DataSyncPackage {
  packageKind: string;
  packageVersion: number;
  exportedAt: string;
  taskCode: string;
  sourceDataset: DataSyncPackageDataset;
  targetDataSourceCode: string;
  targetDataSourceType: string;
  targetTable: string;
  conflictPolicy: string;
  scheduleType: string;
  scheduleTime: string | null;
  intervalMinutes: number;
  mappings: SyncColumnMapping[];
  /** Cron 调度表达式（标准 5 段）；仅 scheduleType 为 Cron 时有值，旧包无此字段。 */
  scheduleCron?: string | null;
}

/** 同步包校验请求体：直接携带整份解析后的同步包。镜像 PocDataSyncValidateRequest。 */
export interface DataSyncValidateRequest {
  package: DataSyncPackage | null;
}

/** 校验报告中的单条错误或警告；code 为稳定业务错误码。镜像 PocDataSyncIssue。 */
export interface DataSyncIssue {
  code: string;
  message: string;
}

/** 与当前库的冲突探测结果；数据集一致性结论在数据集不存在时为 null。镜像 PocDataSyncCollisions。 */
export interface DataSyncCollisions {
  taskCodeExists: boolean;
  datasetCodeExists: boolean;
  datasetIdentical: boolean | null;
  datasetReferencedByReports: boolean | null;
  targetDataSourceExists: boolean;
  targetDataSourceTypeMatch: boolean | null;
}

/** 校验报告里的包内容摘要，供导入向导展示将导入什么。镜像 PocDataSyncPackageSummary。 */
export interface DataSyncPackageSummary {
  taskCode: string;
  sourceDatasetCode: string;
  targetTable: string;
  mappingCount: number;
  targetDataSourceType: string;
}

/** 同步包校验结果；errors 非空表示不可导入，warnings 只提示不阻断。镜像 PocDataSyncValidateResult。 */
export interface DataSyncValidateResult {
  ok: boolean;
  errors: DataSyncIssue[];
  warnings: DataSyncIssue[];
  collisions: DataSyncCollisions | null;
  packageSummary: DataSyncPackageSummary | null;
}

/** 同步任务试跑请求：行数上限（服务端封顶 50）与源数据集变量的可选取值（缺省回落滚动表达式现算）。镜像 PocDataSyncTryRunRequest。 */
export interface DataSyncTryRunRequest {
  rowLimit: number;
  parameters?: Record<string, string | null> | null;
}

/** 同步包试跑请求：包 + 用户选择的目标数据源。镜像 PocDataSyncDryRunRequest。 */
export interface DataSyncDryRunRequest {
  package: DataSyncPackage | null;
  targetDataSourceCode: string | null;
  parameters?: Record<string, string | null>;
}

/** 试跑样例行：转换前后的字段值对照，键分别为源字段名与目标列名。镜像 PocDataSyncSampleRow。 */
export interface DataSyncSampleRow {
  source: Record<string, DataRowValue>;
  target: Record<string, DataRowValue>;
}

/** 试跑结果：最多 50 行端到端读+转换（不写目标库），附目标列结论。镜像 PocDataSyncTryRunResult。 */
export interface DataSyncTryRunResult {
  ok: boolean;
  targetColumns: string[];
  samples: DataSyncSampleRow[];
  elapsedMilliseconds: number;
  rowCountCapped: boolean;
  failedRows: number;
  errors: string[];
}

/** 同步包导入提交请求；mode 为 Create（新建）或 Replace（先删旧任务再建）。镜像 PocDataSyncImportRequest。 */
export interface DataSyncImportRequest {
  package: DataSyncPackage | null;
  targetDataSourceCode: string | null;
  mode: string;
  dryRunPerformed: boolean;
}

/** 导入完成后的动作摘要，告诉管理员数据集和同步任务各自执行了什么动作。镜像 PocDataSyncImportResult。 */
export interface DataSyncImportResult {
  taskCode: string;
  datasetAction: string;
  taskAction: string;
  warnings: string[];
}

/** 同步任务对外文档：任务列配置 + 解析后的映射定义。镜像 PocSyncTaskDocument。 */
export interface SyncTaskDocument {
  code: string;
  name: string;
  sourceDatasetCode: string;
  targetDataSourceCode: string;
  targetTable: string;
  conflictPolicy: string;
  scheduleType: string;
  scheduleTime: string | null;
  intervalMinutes: number;
  enabled: boolean;
  revision: number;
  lastRunAt: string | null;
  mappings: SyncColumnMapping[];
  /** Cron 调度表达式（标准 5 段）；仅 scheduleType 为 Cron 时有值，其余类型为 null。 */
  scheduleCron?: string | null;
}

/** 同步任务保存输入；revision 是乐观并发期望修订号，0 表示首次落库。镜像 PocSyncTaskInput。 */
export interface SyncTaskInput {
  code: string;
  name: string;
  /** 旧契约的源数据集编码；携带 source 节时后端忽略本字段，保留是为了兼容旧客户端。 */
  sourceDatasetCode?: string;
  targetDataSourceCode: string;
  targetTable: string;
  conflictPolicy: string;
  scheduleType: string;
  scheduleTime: string | null;
  intervalMinutes: number;
  mappings: SyncColumnMapping[];
  enabled: boolean;
  revision: number;
  /** 设计器新契约的源取数节；后端据此自动维护 `sync_src_<taskCode>` 源数据集。 */
  source?: SyncTaskSourceInput | null;
  /** Cron 调度表达式（标准 5 段）；仅 scheduleType 为 Cron 时提交，其余类型置 null（与后端裁剪规则一致）。 */
  scheduleCron?: string | null;
}

/** 任务保存输入的源取数节：数据源编码 + 原样 SQL（选表模式由前端按方言生成）。镜像 PocSyncTaskSourceInput。 */
export interface SyncTaskSourceInput {
  dataSourceCode: string;
  queryText: string;
  name?: string | null;
  /** 选表模式下的源表名：后端据此把数据库注释写入源数据集字段的 displayName。 */
  sourceTable?: string | null;
  /** 取数参数契约：与 SQL @引用一一对应，随源数据集落库并随同步包携带；固定值/滚动表达式按次运行现算兜底。 */
  variables?: DatasetVariableDefinition[] | null;
}

/** 数据源表清单响应：该数据源连接库内的基础表名。镜像 PocSyncTableMetadataDocument。 */
export interface SyncTableMetadataDocument {
  dataSourceCode: string;
  tables: string[];
}

/** 目标表列元数据：列名、数据库类型文本、可空标记与列注释（取不到为 null）。镜像 PocSyncColumnMetadataDocument。 */
export interface SyncColumnMetadata {
  name: string;
  dataType: string;
  nullable: boolean;
  length: number | null;
  description: string | null;
  /** 数据库真实主键标志：Upsert 冲突策略选合并键时提示“建议选它”。 */
  isPrimaryKey?: boolean;
}

/** 目标表列清单响应：目标字段下拉选项的数据来源。镜像 PocSyncTableColumnsDocument。 */
export interface SyncTableColumnsDocument {
  dataSourceCode: string;
  table: string;
  columns: SyncColumnMetadata[];
}

/** 源取数预览请求：任务保存前对“数据源 + SQL”做真实受限查询。镜像 PocSyncSourcePreviewRequest。 */
export interface SyncSourcePreviewRequest {
  dataSourceCode: string;
  queryText: string;
  /** 选表模式下的源表名：提供时按列名匹配数据库注释填充字段的 description。 */
  sourceTable?: string | null;
  /** 设计器正在编辑的取数参数契约：服务端按当前时间现算表达式兜底。 */
  variables?: DatasetVariableDefinition[] | null;
  /** 显式给定的变量取值，优先于变量默认值；两者皆无时 SQL 引用的变量按必填缺值拒绝。 */
  parameters?: Record<string, string | null> | null;
}

/** 同步运行日志状态；与后端字符串枚举保持一致。 */
export type SyncRunStatus = 'Running' | 'Completed' | 'Failed' | 'Cancelled';

/** 单行同步失败的定位明细；rowIndex 从 1 开始表示本次运行内的行序。镜像 PocDataSyncFailureDetail。 */
export interface DataSyncFailureDetail {
  rowIndex: number;
  field: string;
  reason: string;
  originalValue: string | null;
}

/** 运行日志对外文档：统计与失败明细，供任务页轮询。镜像 PocSyncRunLogDocument。 */
export interface SyncRunLogDocument {
  runNo: string;
  taskCode: string;
  status: SyncRunStatus;
  startedAt: string;
  finishedAt: string | null;
  rowsRead: number;
  rowsWritten: number;
  rowsFailed: number;
  message: string | null;
  failures: DataSyncFailureDetail[];
}

/** AI 助手全局设置文档；apiKeyMasked 为服务端打码值（sk-***last4），密钥原文永不下发。镜像 PocAiSettingsDocument。 */
export interface AiSettings {
  isEnabled: boolean;
  baseUrl: string;
  apiKeyMasked: string;
  apiKeyConfigured: boolean;
  model: string;
  /// 视觉模型名（附件图片理解用）；空 = 回退主 model。模型名非密钥，正常回显。
  visionModel: string;
  sampleRowLimit: number;
  sqlProbeEnabled: boolean;
  sqlProbeRowLimit: number;
  maxToolRounds: number;
  updateTime: string | null;
}

/** AI 设置保存输入；apiKey 传空表示保留服务端已存值，避免打码回显被误清空。镜像 PocAiSettingsInput。 */
export interface AiSettingsInput {
  isEnabled: boolean;
  baseUrl: string;
  apiKey: string;
  model: string;
  /// 视觉模型名；空串 = 回退主 model。
  visionModel: string;
  sampleRowLimit: number;
  sqlProbeEnabled: boolean;
  sqlProbeRowLimit: number;
  maxToolRounds: number;
}

/** AI 连通性测试结果；message 面向管理员展示成功或失败原因。镜像 PocAiConnectionTestResult。 */
export interface AiConnectionTestResult {
  success: boolean;
  message: string;
  latencyMs: number;
}

/** AI 聊天消息角色；tool 消息由前端渲染为折叠条而不是正文。 */
export type AiChatRole = 'user' | 'assistant' | 'tool';

/** AI 聊天单条展示消息；tool 消息携带工具名/SQL 摘要/行数，content 为空。镜像 PocAiChatMessageView。 */
export interface AiChatMessageView {
  role: AiChatRole;
  content: string | null;
  toolName: string | null;
  toolCallId: string | null;
  sqlSummary: string | null;
  rowCount: number | null;
  success: boolean;
  truncated: boolean;
}

/** AI 对话请求；sessionId 为空表示新会话，dataSourceCode 决定上下文注入与工具默认数据源。镜像 PocAiChatRequest。 */
export interface AiChatRequest {
  sessionId: string | null;
  dataSourceCode: string;
  message: string;
  /// 参考材料附件（base64 内联，≤3 个；图片≤5MB、xlsx/pdf≤10MB）；内容不落库，随会话过期。
  attachments?: AiChatAttachment[];
}

/** AI 聊天附件输入；dataBase64 不含 data: 前缀。镜像 PocAiChatAttachment。 */
export interface AiChatAttachment {
  fileName: string;
  contentType: string;
  sizeBytes: number;
  dataBase64: string;
}

/** AI 对话响应；expired=true 表示前端携带的会话已过期，本轮按新上下文重建。镜像 PocAiChatResult。 */
export interface AiChatResult {
  sessionId: string;
  messages: AiChatMessageView[];
  expired: boolean;
  draft: AiChatDraft | null;
}

/** AI 报表草稿状态：failed 表示最近一次编译/校验失败，等待 AI 按错误自修。镜像 PocAiDraftStatus。 */
export type AiDraftStatus = 'submitted' | 'validated' | 'dryRunOk' | 'failed';

/**
 * 下发给前端草稿卡片的草稿：spec 原文、编译产物包、最近一次全链校验结果与状态。
 * package 在编译失败时为 null（此时卡片只展示错误态）；校验/试跑/导入由前端复用
 * reportPackageApi 完成，导入动作不经过任何 /ai/* 接口。镜像 PocAiChatDraftView。
 */
export interface AiChatDraft {
  status: AiDraftStatus;
  spec: Record<string, unknown> | null;
  package: ReportPackage | null;
  validateResult: ReportPackageValidateResult | null;
}

/** 会话关闭回执；closed=false 表示会话已不存在或早已过期，同样视为关闭成功。镜像 PocAiSessionClosedDocument。 */
export interface AiSessionClosedDocument {
  sessionId: string;
  closed: boolean;
}

/** AI 会话预创建请求：仅携带数据源编码；服务端创建空会话（无消息历史）并返回编号。镜像 PocAiSessionPreCreateRequest。 */
export interface AiSessionPreCreateRequest {
  dataSourceCode: string;
}

/** AI 会话插话（方向纠正）请求：message 为下一轮进入模型上下文的纠正文本。镜像 PocAiSessionGuidanceRequest。 */
export interface AiSessionGuidanceRequest {
  message: string;
}

/** AI 会话预创建回执：空会话编号与绑定数据源，前端后续每轮 /chat 直接携带该编号。镜像 PocAiSessionPreCreateDocument。 */
export interface AiSessionPreCreateDocument {
  sessionId: string;
  dataSourceCode: string;
}

/** 前端侧栏使用的轻量数据源选项；来自 dataAssetApi.dataSources 的脱敏文档。 */
export interface AiDataSourceOption {
  code: string;
  name: string;
  dbType: string;
}

/**
 * 同步 AI 智能匹配请求：直接携带源字段清单 + 目标列元数据 + 当前映射（设计器草稿在内存中齐备），
 * 或只传 taskCode 由服务端装配。镜像 PocAiSyncMatchRequest。
 */
export interface AiSyncMatchRequest {
  taskCode: string | null;
  sourceDbType: string | null;
  targetDbType: string | null;
  sourceFields: AiSyncMatchSourceField[];
  targetColumns: SyncColumnMetadata[];
  mappings: SyncColumnMapping[];
}

/** 同步匹配的源字段描述：名称 + 类型代码 + 可选注释。镜像 PocAiSyncMatchSourceField。 */
export interface AiSyncMatchSourceField {
  name: string;
  dataType: string;
  description: string | null;
}

/** 置信度：high/mid/low；low 多与“需人工处理”并存。 */
export type AiSyncMatchConfidence = 'high' | 'mid' | 'low';

/**
 * 单条映射建议：字段与 SyncColumnMapping 完全同构（可直接回填草稿），
 * confidence/reason 仅供 UI 标注，写回 mappings 时丢弃；needsManual=true 表示
 * 目标列无源字段对应（需人工处理），绝不写入 mappings。镜像 PocAiSyncMatchSuggestion。
 */
export interface AiSyncMatchSuggestion {
  sourceField: string;
  targetField: string;
  targetType: string;
  length: number | null;
  nullable: boolean;
  converters: SyncConverterStep[];
  confidence: AiSyncMatchConfidence;
  reason: string;
  needsManual: boolean;
}

/** 同步匹配结果：建议列表 + 降级标记与原因。镜像 PocAiSyncMatchResult。 */
export interface AiSyncMatchResult {
  suggestions: AiSyncMatchSuggestion[];
  degraded: boolean;
  message: string | null;
}

// ===== AI 透明化改造（方案 A/B）追加类型：表清单展示 + 工具循环进度轮询 =====

/** 数据源表清单条目：kind 区分基表与视图；comment 可为空。镜像 PocAiDataSourceTable。 */
export interface AiDataSourceTable {
  name: string;
  kind: 'table' | 'view';
  comment: string | null;
}

/**
 * AI 消息的 Seq 增量形态：服务端消息都带单调递增 seq（会话内从 1 开始），
 * 前端本地乐观插入的用户消息 seq 为 null，最终以 POST /chat 响应校正。镜像 PocAiChatMessageView.Seq。
 */
export interface AiChatSeqMessageView extends AiChatMessageView {
  seq: number | null;
}

/** 对话响应的 Seq 形态：messages 携带 seq，其余字段与 AiChatResult 一致。镜像 PocAiChatResult。 */
export interface AiChatSeqResult extends Omit<AiChatResult, 'messages'> {
  messages: AiChatSeqMessageView[];
}

/** 工具循环进度轮询文档：循环状态 + afterSeq 之后的消息。镜像 PocAiChatProgressDocument。 */
export interface AiChatProgressDocument {
  loopState: 'idle' | 'running' | 'completed' | 'failed';
  messages: AiChatSeqMessageView[];
}
