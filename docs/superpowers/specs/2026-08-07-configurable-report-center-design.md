# 可配置报表中心设计（数据源 / 数据集 / 参数定义）

- 日期：2026-08-07
- 状态：待评审
- 关联：`docs/architecture/README.md` 第一版能力拆分第 1-3 项

## 一、背景与目标

### 1.1 现状

报表中心当前处于 PoC 阶段（`CoreValidation` 目录，`Poc` 前缀），核心验证已跑通"定义返回 → 分页执行 → 普通导出 → 流式大文件导出 → 任务轮询/取消/下载"端到端链路。但全部硬编码：

- 仅 `dept-workload` 一张报表，`EnsureReportExists` 拒绝其他编码。
- 数据源：**无**，纯内存算术生成 Mock 数据（`PocReportCatalog.CreateRow`）。
- 参数：5 个固定参数硬编码在 `GetDefinition()`，`PocExecuteRequest` 是字段固定的 record。
- 列/公式：6 列含公式模板写死。
- 无任何 SqlSugar 实体，无报表中心数据库表。

### 1.2 目标

将架构文档规划的"数据源管理 / 数据集管理 / 参数定义"落地为可在线编辑、可配置的子系统，使报表从"代码写死"变为"配置驱动"：

1. **数据源管理**：在线配置 HIS/LIS/PACS 等系统的只读数据库连接，支持测试连接，支持多种数据库。
2. **数据集管理**：在线编写参数化 SQL，定义 SQL 参数（名称/类型/默认值），预览执行结果与字段结构。
3. **报表参数定义**：在线定义报表查询表单参数（控件类型/默认值/可见性），并绑定到数据集参数。
4. **配置驱动执行**：报表的定义/执行/导出从配置表读取，脱离硬编码。

### 1.3 非目标（本轮不做）

- 报表布局/单元格的在线可视化设计器（类 Excel 拖拽排版）。
- 图表/交叉表/分组聚合等高级报表能力。
- Word/PPT 报表。
- 多租户隔离（报表中心不内置多租户，数据归属由外部传参决定，详见 2.2）。

## 二、架构与模块边界

### 2.1 代码归属

新增 `HD.ReportCenter.Application/Configuration/` 命名空间，承载全部配置管理与执行引擎：

```
HD.ReportCenter.Application/
├── Configuration/
│   ├── Entities/              # 6 个 SqlSugar 实体
│   │   ├── ReportDataSource.cs
│   │   ├── ReportDataset.cs
│   │   ├── ReportDatasetParameter.cs
│   │   ├── ReportDefinition.cs
│   │   ├── ReportParameter.cs
│   │   └── ReportColumn.cs
│   ├── Dto/                   # 输入/输出 DTO
│   ├── Services/              # CRUD 服务（IDynamicApiController）
│   │   ├── ReportDataSourceService.cs
│   │   ├── ReportDatasetService.cs
│   │   └── ReportDesignService.cs
│   ├── Engine/                # 配置驱动执行引擎
│   │   ├── ReportExecutionEngine.cs
│   │   └── DataSourceConnectionFactory.cs
│   └── Contracts/             # 动态执行契约（替代 PocReportContracts）
│       └── DynamicReportContracts.cs
├── CoreValidation/            # 现有 PoC，保留不动
└── ReportCenterHealthApi.cs
```

### 2.2 选型理由

- **实体与服务放在 Application 层**：Application 已引用 `Admin.NET.Core`（含 SqlSugar、`EntityBase`、`UserManager`），实体可直接继承 `EntityBase` 获得审计字段，CRUD 服务可复用 `SqlSugarRepository<T>` 与 `OrderBuilder`/`WhereIF`/`ToPagedListAsync` 等既有模式。
- **不启用多租户**：报表中心遵循"可复制原则"，不内置多租户隔离。数据归属（哪个医院/机构）由外部调用方通过上下文参数（如 `hospitalId`、`deptId`、`userId`）传入决定，实体不带 `TenantId`。这样报表中心可被任意系统复制部署并嵌入，不与具体租户体系耦合。
- **ReportEngine 保持不变**：现有的 `PocExcelExporter`（流式导出）与 `PocReportCatalog`（Mock 执行）不动，配置驱动执行引擎作为新链路并存，PoC 链路保留用于回归对照。
- **执行引擎用 SqlSugar 动态连接**：`new SqlSugarClient(new ConnectionConfig{ DbType, ConnectionString, IsAutoCloseConnection=true })` 动态连接外部数据源，用 `Ado.GetDataTableAsync(sql, SugarParameter[])` 执行参数化 SQL。SqlSugar 的 `DbType` 枚举原生覆盖 SqlServer/MySQL/Oracle/PostgreSQL/达梦/人大金仓/SQLite 等，无需自建驱动抽象。

### 2.3 与现有 PoC 的关系

| 链路 | 路由前缀 | 数据来源 | 状态 |
|---|---|---|---|
| PoC（Mock） | `/api/report-center/poc/{reportCode}` | `PocReportCatalog` 硬编码 | 保留，回归对照 |
| 配置驱动 | `/api/report-center/reports/{reportCode}` | 配置表 | 本轮新建 |

两套链路独立，前端通过配置项切换。`dept-workload` 报表将通过种子数据迁移到配置表，使配置驱动链路复现 PoC 行为。

## 三、数据模型

6 张实体表，全部继承 `EntityBase`（含 Id/CreateTime/UpdateTime/CreateUserId/CreateUserName/UpdateUserId/UpdateUserName），获得审计字段。**不启用多租户**，实体不带 TenantId（详见 2.2 可复制原则）。

### 3.1 ReportDataSource（数据源）

| 字段 | 类型 | 说明 |
|---|---|---|
| Id | long | 雪花主键 |
| Code | string(64) | 数据源编码，全局唯一，如 `his-readonly` |
| Name | string(128) | 显示名称，如 `HIS 只读库` |
| DbType | SqlSugar.DbType | 数据库类型枚举（SqlServer/MySql/Oracle/PostgreSQL/Dm/KdGauss/SQLite...） |
| ConnectionString | string(512) | 连接字符串，含账号密码 |
| Status | int | 0=禁用 1=启用 |
| Remark | string?(256) | 备注 |

索引：`Code` 唯一、`Name` 普通索引。

### 3.2 ReportDataset（数据集）

| 字段 | 类型 | 说明 |
|---|---|---|
| Id | long | 主键 |
| DataSourceId | long | 所属数据源（FK → ReportDataSource） |
| Code | string(64) | 数据集编码，全局唯一 |
| Name | string(128) | 显示名称 |
| SqlText | string(Text) | 参数化 SQL，参数用 `@name` 占位，如 `WHERE dept_id = @deptId` |
| Status | int | 0=禁用 1=启用 |
| Remark | string?(256) | 备注 |

约束：`SqlText` 必须为 SELECT 语句。索引：`Code` 唯一、`DataSourceId` 索引。

### 3.3 ReportDatasetParameter（数据集 SQL 参数）

| 字段 | 类型 | 说明 |
|---|---|---|
| Id | long | 主键 |
| DatasetId | long | 所属数据集 |
| Name | string(64) | 参数名，对应 SQL 中的 `@name` |
| DataType | string(32) | String/Integer/Float/Date/DateTime/Boolean |
| DefaultValue | string?(128) | 默认值 |
| Required | bool | 是否必填 |
| Sort | int | 排序 |
| Remark | string?(128) | 备注 |

索引：`(DatasetId, Name)` 唯一。

### 3.4 ReportDefinition（报表定义）

| 字段 | 类型 | 说明 |
|---|---|---|
| Id | long | 主键 |
| Code | string(64) | 报表编码，全局唯一，如 `dept-workload` |
| Name | string(128) | 报表名称 |
| SheetName | string(64) | Sheet 页签名 |
| DatasetId | long | 绑定的数据集 |
| PreviewMode | string(16) | `Full` / `Paged` |
| PageSize | int | 分页大小（PreviewMode=Paged 时生效） |
| ShowExactTotal | bool | 是否显示精确总行数 |
| Status | int | 0=禁用 1=已发布 |
| Remark | string?(256) | 备注 |

索引：`Code` 唯一。

### 3.5 ReportParameter（报表查询表单参数）

对应现有 `PocParameterDefinition`，描述查询表单上每个输入控件。

| 字段 | 类型 | 说明 |
|---|---|---|
| Id | long | 主键 |
| ReportId | long | 所属报表 |
| Code | string(64) | 参数编码，如 `startDate` |
| DisplayName | string(64) | 显示名，如 `开始日期` |
| ControlType | string(32) | `text`/`date`/`datetime`/`select`/`number` |
| SourceType | string(32) | `url`/`default-function`/`default`/`dict`/`sql` |
| DefaultValue | string?(128) | 默认值；SourceType=default-function 时存函数名（如 `month-start`/`today`） |
| Visible | bool | 是否在查询表单显示 |
| BindDatasetParameter | string?(64) | 绑定到数据集的参数名（可为空，表示仅展示用） |
| DictCode | string?(64) | SourceType=dict 时的字典编码 |
| SqlForOptions | string?(Text) | SourceType=sql 时取下拉选项的 SQL（返回 value/label 两列） |
| Sort | int | 排序 |

索引：`(ReportId, Code)` 唯一。

### 3.6 ReportColumn（报表列定义）

对应现有 `PocColumnDefinition`。

| 字段 | 类型 | 说明 |
|---|---|---|
| Id | long | 主键 |
| ReportId | long | 所属报表 |
| Code | string(64) | 列编码 |
| Header | string(64) | 表头文字 |
| DataType | string(32) | `text`/`number`/`formula`/`date` |
| Width | int | 列宽 |
| FormulaTemplate | string?(256) | 公式模板，如 `=C{row}+D{row}`，`{row}` 运行时替换 |
| BindField | string?(64) | 绑定数据集返回的字段名（formula 类型可空） |
| Sort | int | 排序 |

索引：`(ReportId, Code)` 唯一。

### 3.7 关系图

```
ReportDataSource 1 ──── N ReportDataset 1 ──── N ReportDatasetParameter
                                          │
                                          └── 绑定 ──┐
                                                     │
ReportDefinition  N ──── 1 ReportDataset             │
       │ 1                                          │
       ├── N ReportParameter ──── BindDatasetParameter ┘
       │
       └── N ReportColumn
```

## 四、执行引擎

### 4.1 DataSourceConnectionFactory

负责根据 `ReportDataSource` 创建短生命周期的 `ISqlSugarClient`：

```csharp
public ISqlSugarClient CreateClient(ReportDataSource dataSource)
{
    return new SqlSugarClient(new ConnectionConfig
    {
        DbType = dataSource.DbType,
        ConnectionString = dataSource.ConnectionString,
        IsAutoCloseConnection = true,
    });
}
```

- 每次 SQL 执行创建独立 client，`using` 包裹，执行完即释放，不缓存长连接（v1 简单可靠，后续可加连接池）。
- 仅用于执行数据集 SQL，不参与 Admin.NET 主库的 ORM 操作。

### 4.2 ReportExecutionEngine

核心方法：

**TestConnection(ReportDataSource)**：执行 `SELECT 1`，成功返回 true，失败抛出含数据库错误信息的友好异常。

**PreviewDataset(datasetId, parameters, limit)**：
1. 加载数据集 + 数据集参数。
2. 用 `DataSourceConnectionFactory.CreateClient` 创建连接。
3. 构建 `SugarParameter[]`（按参数 DataType 转换值）。
4. 执行 `Ado.GetDataTableAsync(sql, parameters)`，limit 默认 100 行（管理端预览用），用 `Ado.SqlQuery` + `TOP/ LIMIT` 或客户端截断。
5. 返回字段列表 + 前 N 行数据（N=limit，默认 100）。

**GetFieldSchema(datasetId)**：执行 `WHERE 1=0` 形式的 SQL（或 limit 1）获取列元数据（列名、类型）。

**Execute(reportCode, DynamicExecuteRequest)**：
1. 按 reportCode 加载 `ReportDefinition` + 数据集 + 数据集参数 + 报表参数 + 列定义。
2. 将 `request.Parameters`（前端传入的 name→value 字典）按 `ReportParameter.BindDatasetParameter` 映射到数据集 SQL 参数。
3. 应用各参数默认值（SourceType=default-function 的计算 `month-start`/`today` 等）。
4. 构建参数化 SQL 的分页：v1 采用**客户端分页**（执行全量后内存分页），规避不同数据库分页语法差异；大文件导出走流式枚举全量。
5. 构建 `SugarParameter[]`，执行 SQL 得到 `DataTable`。
6. 按 `ReportColumn.BindField` 将每行映射为 `Dictionary<string, object?>`。
7. 返回 `DynamicExecuteResult`（含 page/pageSize/hasMore/parameters/rows）。

### 4.3 参数值转换

按 `ReportDatasetParameter.DataType` 转换前端字符串值为强类型：

| DataType | 转换 |
|---|---|
| String | 原样 string |
| Integer | int.Parse |
| Float | double.Parse |
| Date | DateTime.Parse（仅日期） |
| DateTime | DateTime.Parse |
| Boolean | bool.Parse |

转换失败抛友好异常，提示参数名与期望类型。

### 4.4 默认值函数

`SourceType=default-function` 时 `DefaultValue` 存函数名：

| 函数名 | 计算 |
|---|---|
| `month-start` | 当前月第一天 `yyyy-MM-dd` |
| `today` | 今天 `yyyy-MM-dd` |
| `now` | 当前 `yyyy-MM-dd HH:mm:ss` |
| `year-start` | 当年 1 月 1 日 |

在执行前解析，注入到参数字典。

### 4.5 上下文参数透传

报表中心被外部系统（HIS/LIS 等）嵌入时，调用方通过 URL 查询参数或请求体传入上下文信息，这些参数原样透传到数据集 SQL：

```
外部系统 ──URL/请求体──> ExecuteRequest.Parameters ──BindDatasetParameter映射──> SQL @param
```

典型上下文参数：

| 参数 | 来源 | 用途 |
|---|---|---|
| `hospitalId` | 外部系统 URL | 限定医院范围，透传到 SQL `WHERE hospital_id = @hospitalId` |
| `deptId` | 外部系统 URL | 限定科室范围 |
| `userId` | 外部系统 URL / 登录态 | 数据权限/操作人标识 |

配置方式：
1. 在报表的 `ReportParameter` 中定义该参数，`SourceType = url`，`Visible = true/false`（userId 等可不展示）。
2. 在 `BindDatasetParameter` 填入数据集 SQL 中对应的 `@paramName`。
3. 数据集 SQL 用 `@paramName` 占位，如 `SELECT * FROM workload WHERE hospital_id = @hospitalId AND dept_id = @deptId`。

执行时引擎将 `ExecuteRequest.Parameters` 中的值按绑定关系注入 SQL 参数，未传值的参数使用数据集参数的 `DefaultValue`。**这些上下文参数同时也决定了"查询哪个医院/机构的数据"**，替代了多租户隔离的作用。

## 五、动态执行契约

替代 `PocExecuteRequest`（固定 5 字段）与 `PocExecuteResult`（固定 WorkloadRow）。

```csharp
public sealed record DynamicExecuteRequest(
    int Page,
    Dictionary<string, string?> Parameters);

public sealed record DynamicExecuteResult(
    int Page,
    int PageSize,
    bool HasMore,
    Dictionary<string, string?> ResolvedParameters,
    IReadOnlyList<Dictionary<string, object?>> Rows);

public sealed record DynamicReportDefinition(
    string Code,
    string Name,
    string SheetName,
    DynamicPreviewSettings Preview,
    IReadOnlyList<DynamicParameterDefinition> Parameters,
    IReadOnlyList<DynamicColumnDefinition> Columns);
```

字段结构与现有 PoC 契约保持一致（前端 types.ts 无需大改），仅 `Rows` 从固定结构变为动态字典。前端 `reportWorkbook.ts` 按 `ReportColumn.BindField` + `FormulaTemplate` 渲染。

## 六、API 接口设计

### 6.1 数据源管理 `ReportDataSourceService`

| 方法 | 路由（Furion 动态生成） | 说明 |
|---|---|---|
| Page | POST | 分页查询，支持 Name/Code/DbType 过滤 |
| Add | POST | 新增，Code 唯一校验 |
| Update | POST | 修改 |
| Delete | POST | 删除（校验是否被数据集引用） |
| GetList | GET | 启用列表（供数据集下拉选择） |
| TestConnection | POST | 测试连接（传连接配置，不落库） |

路由前缀 `/api/report-center/data-source`（通过 `[ApiDescriptionSettings]` 控制）。

### 6.2 数据集管理 `ReportDatasetService`

| 方法 | 说明 |
|---|---|
| Page | 分页查询 |
| Add/Update/Delete | CRUD（Delete 校验是否被报表引用） |
| Detail | 详情含参数列表 |
| GetFieldSchema | 获取字段结构（执行 limit 0） |
| Preview | 预览执行（传参数值，返回前 N 行） |
| SaveParameters | 批量保存数据集参数 |

### 6.3 报表设计 `ReportDesignService`

| 方法 | 说明 |
|---|---|
| Page | 分页查询 |
| Add/Update/Delete | CRUD |
| Detail | 详情含参数+列定义（管理端用） |
| SaveParameters | 批量保存报表查询参数 |
| SaveColumns | 批量保存列定义 |
| Publish | 设为已发布（Status=1） |

### 6.4 配置驱动执行 `ReportExecutionApi`

| 路由 | 说明 |
|---|---|
| GET `/api/report-center/reports/{reportCode}/definition` | 返回 DynamicReportDefinition |
| POST `/api/report-center/reports/{reportCode}/execute` | 执行查询 |
| POST `/api/report-center/reports/{reportCode}/exports/normal` | 普通导出 |
| POST `/api/report-center/reports/{reportCode}/exports/large` | 大文件导出任务 |
| GET/POST `/api/report-center/export-jobs/{jobNo}...` | 复用现有 PoC 任务管理 |

导出复用 `PocExcelExporter` 的流式能力，但数据来源改为 `ReportExecutionEngine.EnumerateRows`（全量枚举）。

## 七、安全策略（前期暂缓）

前期为加快开发与调试速度，**暂不引入安全约束**，后续上线前再补齐：

- 不做 SQL 关键字校验（允许任意 SQL，信任编写者）。
- 不强制只读账号（由部署方自行决定账号权限）。
- 不做 SQL 执行超时限制。
- 连接字符串明文存库。
- 参数化 SQL（`@param` + `SugarParameter`）仍作为**技术机制**保留（防语法错误、正确处理类型），但不作为安全防线宣传。

上线前需补齐：只读账号强制、SQL 安全校验、连接串加密、执行超时、接口鉴权。

## 八、前端规划

### 8.1 Vue 管理端（Web/）

新增 `Web/src/views/report/`：

| 页面 | 功能 |
|---|---|
| `datasource/index.vue` | 数据源列表 + 新增/编辑/测试连接对话框 |
| `dataset/index.vue` | 数据集列表 + SQL 编辑器（CodeMirror/Monaco）+ 参数编辑器 + 预览 |
| `design/index.vue` | 报表列表 + 设计页（绑定数据集、编辑查询参数、编辑列） |

参考 `references/luck-report/luck-report-ui/src/views/report/designer/resource-panel/datasource-panel/` 的对话框结构（datasource-dialog / sql-dataset-dialog / parameter-dialog）。

### 8.2 React 运行时（ReportWeb/）

`ReportWeb` 保持为报表运行时预览。改造点：
- `api.ts` 的 `reportCode` 与 `basePath` 从 `poc` 切到 `reports`。
- `types.ts` 的 `WorkloadRow` 改为 `Record<string, unknown>` 动态行。
- `reportWorkbook.ts` 按 `ReportColumn.BindField` 从动态行取值渲染。

## 九、种子数据与迁移

提供种子数据将现有 `dept-workload` 迁移到配置表，使配置驱动链路复现 PoC 行为：

1. **数据源**：种子 `lis_admin`（Admin.NET 主库），供数据集查询。
2. **数据集**：种子 `dept-workload-dataset`，SQL 暂用一段返回 Mock 行的 SQL（或查真实表，待业务确认）。参数：`hospitalId/deptId/personId/startDate/endDate`。
3. **报表定义**：种子 `dept-workload`，PreviewMode=Paged, PageSize=20。
4. **报表参数**：种子 5 个参数（与现有 `GetDefinition()` 一致）。
5. **列定义**：种子 6 列（含公式模板）。

种子数据通过 SqlSugar 的 `SeedData` 机制或初始化 SQL 提供。

## 十、实施阶段

| 阶段 | 内容 | 产出 |
|---|---|---|
| 1 | 数据源管理：实体 + CRUD + 测试连接 | 可配置/测试数据库连接 |
| 2 | 数据集管理：实体 + CRUD + 参数 + 预览 + 字段结构 | 可编写/预览参数化 SQL |
| 3 | 报表定义管理：实体 + CRUD + 参数绑定 + 列绑定 | 可定义报表结构与查询表单 |
| 4 | 配置驱动执行引擎：definition/execute/export | 配置驱动的查询/导出链路 |
| 5 | 种子数据迁移 dept-workload | 配置链路复现 PoC |
| 6 | 前端管理页面（Vue）：数据源/数据集/报表设计 | 可视化配置 |
| 7 | ReportWeb 运行时切换到配置链路 | 端到端验证 |

阶段 1-4 为后端核心，阶段 5 验证，阶段 6-7 为前端。各阶段可独立提交、独立测试。

## 十一、测试策略

- **单元测试**（`HD.ReportCenter.Tests`）：
  - `DataSourceConnectionFactory` 对各 DbType 创建 client。
  - `ReportExecutionEngine` 参数值转换、默认值函数计算、上下文参数透传。
  - CRUD 服务的唯一性校验与引用校验。
- **集成测试**：用 SQLite 内存库作为外部数据源，验证参数化 SQL 执行与分页。
- **回归**：PoC 链路（`/poc/`）保持可用，作为对照基线。

## 十二、风险与取舍

| 风险 | 应对 |
|---|---|
| 客户端分页在大结果集下内存高 | v1 接受（预览仅分页，大导出走流式枚举）；v2 按 DbType 生成原生分页 SQL |
| SqlSugar DbType 枚举与实际驱动可用性 | 测试连接方法在配置时即验证，失败不保存 |
| 前期无安全约束 | 上线前统一补齐（见第七章） |
