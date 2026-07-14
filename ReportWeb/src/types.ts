import type { IWorkbookData } from '@univerjs/presets';

export type PreviewMode = 'Full' | 'Paged';

export interface PreviewSettings {
  mode: PreviewMode;
  pageSize: number;
  showExactTotal: boolean;
}

export interface ParameterDefinition {
  code: string;
  displayName: string;
  controlType: string;
  sourceType: string;
  defaultValue: string | null;
  visible: boolean;
}

export interface ColumnDefinition {
  code: string;
  header: string;
  dataType: string;
  width: number;
  formulaTemplate: string | null;
}

export interface ReportDefinition {
  code: string;
  name: string;
  sheetName: string;
  preview: PreviewSettings;
  parameters: ParameterDefinition[];
  columns: ColumnDefinition[];
}

export interface ExecuteRequest {
  page: number;
  hospitalId: string | null;
  deptId: string | null;
  personId: string | null;
  startDate: string | null;
  endDate: string | null;
}

export interface ResolvedParameters {
  hospitalId: string;
  deptId: string;
  personId: string;
  startDate: string;
  endDate: string;
}

export interface WorkloadRow {
  sequence: number;
  departmentName: string;
  outpatientCount: number;
  dischargeCount: number;
}

export interface ParameterBinding {
  code: string;
  sheetId: string;
  row: number;
  column: number;
}

export interface DetailFieldBinding {
  code: keyof WorkloadRow;
  column: number;
}

export interface DetailBinding {
  datasetCode: 'detail';
  sheetId: string;
  templateRow: number;
  fields: DetailFieldBinding[];
}

export interface TemplateBinding {
  version: 1;
  parameterBindings: ParameterBinding[];
  detailBinding: DetailBinding | null;
}

export interface TemplateDocument {
  reportCode: string;
  snapshot: IWorkbookData;
  binding: TemplateBinding;
  revision: number;
}

export interface SaveTemplateRequest {
  snapshot: IWorkbookData;
  binding: TemplateBinding;
  revision: number;
}

export interface ExecuteResult {
  page: number;
  pageSize: number;
  hasMore: boolean;
  parameters: ResolvedParameters;
  rows: WorkloadRow[];
}

export type ExportJobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';

export interface ExportJob {
  jobNo: string;
  status: ExportJobStatus;
  totalRows: number;
  writtenRows: number;
  error: string | null;
  createdAt: string;
  completedAt: string | null;
}
