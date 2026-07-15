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

/** 后台参数定义；defaultValue 已由后台默认函数解析，浏览器不重复计算日期规则。 */
export interface ParameterDefinition {
  code: string;
  displayName: string;
  controlType: string;
  sourceType: string;
  defaultValue: string | null;
  visible: boolean;
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
  parameters: ParameterDefinition[];
  columns: ColumnDefinition[];
}

/** 一次预览或导出的查询条件。page 始终使用一基页码，空输入转换为 null。 */
export interface ExecuteRequest {
  page: number;
  hospitalId: string | null;
  deptId: string | null;
  personId: string | null;
  startDate: string | null;
  endDate: string | null;
}

/** 后台解析完成的非空参数，值可直接替换工作簿中的 params token。 */
export interface ResolvedParameters {
  hospitalId: string;
  deptId: string;
  personId: string;
  startDate: string;
  endDate: string;
}

/** 当前验证报表的后台明细行；合计等公式字段不由后台返回。 */
export interface WorkloadRow {
  sequence: number;
  departmentName: string;
  outpatientCount: number;
  dischargeCount: number;
}

/**
 * 明细 token 的运行时白名单。
 * satisfies 确保这里的字符串在编译期仍是 WorkloadRow 的真实属性，防止前后端字段漂移。
 */
export const workloadRowFieldCodes = [
  'sequence',
  'departmentName',
  'outpatientCount',
  'dischargeCount',
] as const satisfies readonly (keyof WorkloadRow)[];

export type WorkloadRowFieldCode = typeof workloadRowFieldCodes[number];

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

/** 一页后台结果。hasMore 允许不查询精确总数也能驱动下一页按钮。 */
export interface ExecuteResult {
  page: number;
  pageSize: number;
  hasMore: boolean;
  parameters: ResolvedParameters;
  rows: WorkloadRow[];
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
