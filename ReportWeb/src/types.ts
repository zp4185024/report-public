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

/** 数据集声明的 SQL 输入变量；具体值来源由每张报表配置。 */
export interface DatasetVariableDefinition {
  code: string;
  displayName: string;
  dataType: string;
  required: boolean;
  sourceType?: 'Form' | 'External';
  sourceCode?: string | null;
  sourceFieldCode?: string | null;
}

/** 查询返回字段，只描述数据契约；表头、列宽和公式属于工作簿模板。 */
export interface DatasetFieldDefinition {
  code: string;
  displayName: string;
  dataType: string;
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
