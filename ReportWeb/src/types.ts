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
}

/** 报表数据集列；formulaTemplate 仅属于公式列，普通字段来自 ExecuteResult.rows。 */
export interface ColumnDefinition {
  code: string;
  header: string;
  dataType: string;
  width: number;
  formulaTemplate: string | null;
}

/** 字段面板、查询区和模板校验共同消费的报表元数据。 */
export interface ReportDefinition {
  code: string;
  name: string;
  sheetName: string;
  preview: PreviewSettings;
  export: ExportSettings;
  parameters: ParameterDefinition[];
  columns: ColumnDefinition[];
}

/** 一次预览或导出的查询条件。page 始终使用一基页码，空输入转换为 null。 */
export interface ExecuteRequest {
  page: number;
  parameters: Record<string, string | null>;
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
}

export interface DataSourceDocument {
  code: string;
  name: string;
  dbType: 'SqlServer';
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
  parameterCodes: string[];
  columns: ColumnDefinition[];
  timeoutSeconds: number;
  maxRows: number;
  enabled: boolean;
  revision: number;
}

export interface DatasetOption {
  code: string;
  name: string;
  dataSourceCode: string;
  parameterCodes: string[];
  columns: ColumnDefinition[];
  enabled: boolean;
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
  columns: ColumnDefinition[];
  revision: number;
}

export interface SaveReportConfigurationRequest {
  name: string;
  sheetName: string;
  preview: PreviewSettings;
  export: ExportSettings;
  datasetCode: string;
  parameters: ParameterDefinition[];
  revision: number;
}

export interface DataSourceTestResult {
  success: boolean;
  durationMilliseconds: number;
  databaseVersion: string;
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
