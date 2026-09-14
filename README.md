# hd-report-center

`hd-report-center` 是一个独立部署的报表中心，面向 HIS、LIS、PACS、外卖等业务系统提供统一的报表配置、查询、预览、模板设计、打印和 Excel 导出能力。

## 项目范围

本项目只负责报表服务：报表数据源和数据集、报表模板、查询预览、打印导出，以及供业务系统嵌入的报表页面和接口。Admin.NET 管理后台、用户管理、组织机构、角色权限、菜单和系统运维等管理能力不属于本项目交付范围，也不作为报表服务的业务前提；仓库中的 Admin.NET 代码仅作为现有宿主底座和兼容代码保留。

项目将“数据怎么查”和“结果怎么展示”分开管理：数据源与 SQL 数据集是可复用资源，报表配置查询参数和取值方式，工作簿模板负责表头、布局、公式和样式。外部系统可以通过 URL 或 iframe 嵌入报表页面，并传入院区、科室、人员、日期等业务上下文。

> 当前仓库同时包含 Admin.NET 宿主底座、报表中心后端、React/Univer 独立报表工作台和若干部署工具。管理后台不在本项目实施范围内；报表服务正式上线前只需按实际部署方式配置接口访问控制、数据源只读账号和生产部署参数。

## 功能介绍

### 报表模板

- 报表模板列表、搜索、新增和编辑。
- 基于 Univer 的在线工作簿设计器，支持单元格编辑、公式、合并、列宽、样式、撤销/重做等常用表格操作。
- 通过 `{{params.xxx}}` 绑定查询参数，通过明细绑定将数据集字段展开到模板行。
- 模板绑定校验、字段面板和模板修订号控制，避免无效字段或旧页面覆盖新版本。
- XLSX 模板导入和导出；导入后先生成可编辑草稿，保存模板时才写入数据库。

### 数据源与数据集

- 全局数据源管理：SQL Server、Oracle、MySQL、PostgreSQL。
- 数据源连接测试、启停、超时和最大行数配置；页面只返回 `passwordConfigured`，不回显密码或连接串。
- 参数化只读 SQL 数据集，仅允许单条 `SELECT/WITH` 查询。
- 数据集输入变量、类型、必填规则和返回字段契约维护。
- 独立数据集预览：临时输入变量，最多返回 50 行，并可将实际结果保存为字段契约。
- 一个数据集可以被多张报表复用；当前单张报表支持一个主数据集。

### 查询与预览

- 查询控件支持文本、日期、下拉、多选、复选框等配置方式。
- 参数可来自查询表单、系统默认值、固定值或外部参数接口。
- 支持 `Full` 完整预览和 `Paged` 分页预览；分页查询使用数据库方言适配器。
- 开发环境可请求参数化 SQL 调试信息，生产环境应关闭或限制该能力。
- 预览结果由同一份报表配置驱动，避免预览和导出使用不同 SQL 或字段定义。

### 导出、打印与共享

- 普通 Excel 导出：在 HTTP 请求内生成文件，并校验行数、单元格数和超时限制。
- 大文件导出：后台任务生成临时文件，支持状态轮询、进度显示、取消和完成后下载。
- 使用公式保留和公式平移策略，导出后的 Excel/WPS 可继续计算模板公式。
- 浏览器打印：按当前工作簿内容生成打印页。
- 共享查看页：`/share/reports/{reportCode}`，可携带允许共享的查询参数，并支持服务端解除保护密码。
- URL 参数只用于传入上下文和筛选值，不允许通过 URL 注入 SQL。

### 外部参数

- 外部参数接口源：配置 GET/POST 地址、请求模板、上下文参数、返回字段 JSONPath 和超时。
- 报表参数可以引用外部接口返回字段，统一转换为报表查询变量。

## 集成组件与技术栈

| 层次 | 组件 | 用途 |
| --- | --- | --- |
| 运行时 | .NET 8、ASP.NET Core | 后端 API 和宿主进程 |
| 后端宿主底座 | Admin.NET、Furion、SqlSugar | 提供报表服务运行所需的宿主、配置和 ORM；管理后台能力不在本项目范围内 |
| 报表后端 | `HD.ReportCenter.Application`、`HD.ReportCenter.ReportEngine` | 报表 API、模板校验、参数解析、SQL 执行和导出 |
| 数据库驱动 | `Microsoft.Data.SqlClient`、`Oracle.ManagedDataAccess.Core`、`MySqlConnector`、`Npgsql` | 多数据库只读数据源接入 |
| Excel 处理 | ClosedXML、DocumentFormat.OpenXml、Newtonsoft.Json | XLSX 交换、快照处理和流式导出 |
| 报表工作台 | React 19、TypeScript、Vite | 独立报表设计和验证页面 |
| 工作簿引擎 | Univer OSS 0.25.1、Univer Web Worker | 电子表格编辑、公式计算和预览渲染 |
| SQL 编辑 | CodeMirror SQL | 数据集 SQL 编辑和高亮 |
| 界面组件 | `lucide-react` | 操作图标和状态图标 |
| 管理前端 | Vue 3、Element Plus、Pinia、Vue Router、ECharts | Admin.NET 原有后台兼容代码，不属于报表服务交付入口 |
| 测试 | xUnit、Vitest | 后端领域逻辑和前端交互/转换测试 |
| 部署配套 | Docker Compose、Nginx、Redis、MinIO | 通用 Admin.NET 部署和基础设施配套；报表中心运行库仍固定为 SQL Server |
| 参考实现 | `references/luck-report` | 仅用于研究报表设计、参数、公式、预览和导出思路 |

### 核心数据关系

```text
RcDataSource 1 ──< RcDataset 1 ──< RcReport
                         │             │
                         │             ├─ RcReportParameter
                         │             └─ RcReportTemplate
                         ├─ SQL 输入变量
                         └─ 返回字段契约
```

- `RcDataSource`：共享数据库连接和查询限制。
- `RcDataset`：参数化只读 SQL、输入变量和输出字段，不保存 Excel 样式。
- `RcReport`：报表名称、数据集引用、预览/导出设置和共享保护配置。
- `RcReportParameter`：把数据集变量具体化为表单、系统默认、固定值或外部接口取值。
- `RcReportTemplate`：Univer 工作簿快照、字段绑定和模板修订号。

数据源、数据集、报表配置和模板分别使用修订号做乐观并发控制，资源保存之间不共用事务，避免编辑一张报表时意外覆盖共享 SQL 或其他人的模板修改。

## 目录结构

```text
hd-report-center/
├─ Admin.NET/                              # .NET 解决方案
│  ├─ Admin.NET.Web.Entry/                 # Web 进程入口
│  ├─ Admin.NET.Web.Core/                  # Admin.NET Web 中间件和宿主配置
│  ├─ Admin.NET.Application/               # Admin.NET 系统业务模块
│  ├─ Admin.NET.Core/                      # 配置、数据库、鉴权、日志、缓存等基础设施
│  ├─ HD.ReportCenter.Application/         # 报表 API、应用服务和数据访问
│  │  └─ CoreValidation/                   # 当前报表中心核心验证实现
│  ├─ HD.ReportCenter.ReportEngine/        # 报表领域契约、模板校验、公式和导出引擎
│  ├─ HD.ReportCenter.Tests/               # 报表中心 xUnit 测试
│  └─ Plugins/                             # Admin.NET 插件（审批、钉钉、企业微信、GoView 等）
├─ ReportWeb/                              # React + Univer 独立报表工作台
│  ├─ src/App.tsx                          # 报表设计、预览、导出和共享主页面
│  ├─ src/ReportListPage.tsx               # 报表模板列表
│  ├─ src/DataAssetPage.tsx                # 数据源/数据集管理
│  ├─ src/DatasetPreviewPage.tsx           # 数据集预览
│  ├─ src/ExternalParameterSourcePage.tsx  # 外部参数接口管理
│  ├─ src/UniverTemplateHost.tsx           # Univer 工作簿宿主和编辑器副作用封装
│  ├─ src/api.ts                            # 前端 API 契约
│  └─ src/*.test.ts                         # Vitest 测试
├─ Web/                                    # Admin.NET 原有 Vue 3 管理前端
├─ scripts/database/                       # SQL Server 初始化和升级脚本
├─ docker/                                 # Docker Compose、Nginx、Redis 和应用配置
├─ docs/architecture/                      # 架构说明
├─ docs/validation/                        # 功能和性能验证记录
├─ docs/superpowers/                       # 设计规格和实施计划
├─ references/luck-report/                 # 报表参考源码和许可证
├─ test-engine/engine/                     # 独立的下一代报表引擎实验目录
├─ README.AdminNET.md                      # Admin.NET 原始说明
├─ DISCLAIMER.md                           # 项目免责声明
├─ LICENSE-MIT / LICENSE-APACHE            # 许可证
└─ global.json                             # .NET SDK 版本约束
```

## 本地开发

### 环境要求

- .NET SDK 8.0（仓库 `global.json` 当前锁定 8.0.416）。
- Node.js 22+ 和 pnpm 11+。
- PowerShell 7（运行数据库工具时使用 `pwsh`）。
- 可访问的 SQL Server 报表中心数据库；外部报表数据源按实际需要配置。

### 编译后端

```powershell
cd D:\zp\git\hd-report-center
dotnet restore .\Admin.NET\Admin.NET.sln
dotnet build .\Admin.NET\Admin.NET.sln -f net8.0 --no-restore
dotnet test .\Admin.NET\HD.ReportCenter.Tests\HD.ReportCenter.Tests.csproj --no-restore
```

### 启动后端

报表中心运行库必须使用 SQL Server。不要把真实密码写入 JSON、命令参数或 Git；通过当前 PowerShell 进程的环境变量提供密码：

```powershell
$env:HD_REPORT_CENTER_DB_PASSWORD = '<报表中心数据库密码>'
dotnet run --project .\Admin.NET\Admin.NET.Web.Entry\Admin.NET.Web.Entry.csproj --urls http://localhost:5006
```

也可以直接覆盖完整连接串：

```powershell
$env:HD_REPORT_CENTER_DB_CONNECTION_STRING = 'Server=<server>;Database=hd_report_center;User Id=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=True;'
```

正常启动不会自动建库、改表或重放种子。首次建库请使用 [数据库工具说明](scripts/database/README.md) 中的保护脚本。

### 启动报表工作台

```powershell
pnpm --dir .\ReportWeb install
pnpm --dir .\ReportWeb dev
```

工作台固定使用 `http://localhost:5175`，并将浏览器的 `/api` 请求代理到 `http://localhost:5006`。可以使用 `VITE_API_URL` 指向其他后端：

```powershell
$env:VITE_API_URL = 'http://localhost:5007'
pnpm --dir .\ReportWeb dev
```

生产构建和测试：

```powershell
pnpm --dir .\ReportWeb test
pnpm --dir .\ReportWeb build
```

## 页面与接口入口

### ReportWeb 页面

| 页面 | 地址 | 用途 |
| --- | --- | --- |
| 模板列表 | `/reports` | 搜索、新增和进入报表模板 |
| 模板设计/预览 | `/reports/{reportCode}` | 编辑模板、输入参数、预览、打印和导出 |
| 共享查看 | `/share/reports/{reportCode}` | 只读查看共享报表 |
| 数据源 | `/data-sources` 或 `/data-assets` | 管理全局数据库连接 |
| 数据集 | `/datasets` | 管理 SQL 数据集和返回字段 |
| 数据集预览 | `/data-assets/preview` | 输入临时变量并验证数据集 |
| 外部参数 | `/external-parameters` | 管理接口参数源和字段映射 |

### 后端接口分组

后端报表 API 统一位于 `/api/report-center/poc` 下，主要包括：

- `GET /api/report-center/health`：运行时和能力健康检查。
- `GET /{reportCode}/definition`：读取报表定义；`GET/PUT /{reportCode}/configuration`：读取或保存报表配置；`GET/PUT /{reportCode}/template`：读取或保存模板。
- `POST /{reportCode}/execute`：按查询参数执行预览。
- `POST /{reportCode}/template/import-xlsx`、`template/export-xlsx`：模板 XLSX 交换。
- `GET/PUT /data-sources/{code}`、`POST /data-sources/test`：数据源管理和连接测试。
- `GET/PUT /datasets/{code}`、`POST /datasets/{code}/preview`：数据集管理和预览。
- `GET/PUT /external-parameter-sources/{code}`、`POST .../test`：外部参数接口源。
- `POST /{reportCode}/exports/normal`：普通 Excel 导出。
- `POST /{reportCode}/exports/large`、`GET/POST /export-jobs/{jobNo}...`：大文件导出任务。

完整路由以 [PocReportApi.cs](Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs) 和 [api.ts](ReportWeb/src/api.ts) 为准。

## iframe 与 URL 参数

报表页面可以直接打开，也可以被 HIS 等系统通过 iframe 嵌入。推荐使用报表编码和允许的业务参数：

```text
http://localhost:5175/reports/dept-workload?hospitalId=H01&deptId=D02&personId=P03&startDate=2026-07-01&endDate=2026-07-13
```

约定如下：

- URL 只传上下文和筛选值，不传 SQL 或模板快照。
- 只有报表参数明确设置 `allowUrlPrefill=true` 时，才允许 URL 覆盖初始值。
- `Fixed`、`System` 或只读参数拒绝客户端覆盖。
- 业务系统嵌入生产环境前，应由调用方或部署网关负责登录态传递、目标地址、跨域策略和令牌有效期校验。
- 对外分享建议使用受保护共享页，并通过网关或签名机制限制任意改参。

## 数据库初始化与迁移

数据库脚本位于 `scripts/database`，常用流程如下：

```powershell
$env:HD_REPORT_CENTER_DB_PASSWORD = '<报表中心数据库密码>'

# 首次创建 hd_report_center、初始化表和种子
pwsh -File .\scripts\database\Initialize-ReportCenterSqlServer.ps1

# 已有数据库时按版本升级报表配置表
pwsh -File .\scripts\database\Apply-ReportConfigurationSchema.ps1
pwsh -File .\scripts\database\Apply-ReusableDataAssetSchema.ps1
```

## 当前边界与上线前事项

- 管理后台不属于本项目交付范围；报表接口的访问控制由实际部署网关、宿主系统或调用方按项目要求负责，报表服务本身只保留必要的接口校验。
- 报表中心主库固定为 SQL Server；Oracle、MySQL、PostgreSQL 是报表查询数据源类型，不是主库替代方案。
- SQL 静态校验只允许只读 `SELECT/WITH`，仍必须使用数据库层只读账号和网络隔离。
- 当前单张报表只支持一个主数据集，暂不支持多主数据集、参数级联选项、配置发布审批和历史版本回滚。
- 大文件任务当前使用进程内任务存储，生产环境应替换为持久化任务表和独立后台执行器。
- PDF 导出暂不作为第一阶段能力；浏览器打印和 Excel 导出是当前主要交付路径。
- `references/luck-report` 仅供功能和交互研究，不能未经许可证审查直接作为商业发布模块。
- Univer OSS 的授权条款、版本锁定和生产分发方式需要在正式发布前完成复核。

## 文档索引

- [架构说明](docs/architecture/README.md)
- [数据库工具与迁移说明](scripts/database/README.md)
- [参考源码说明](references/README.md)
- [核心验证结果](docs/validation/2026-07-14-core-validation-results.md)
- [全局数据资源与报表参数验证](docs/validation/2026-08-10-custom-report-configuration-results.md)
- [多数据库数据源验证](docs/validation/2026-08-10-multi-database-data-source-results.md)
- [SQL Server 迁移验证](docs/validation/2026-08-07-sqlserver-migration-results.md)
- [Admin.NET 原始说明](README.AdminNET.md)
- [免责声明](DISCLAIMER.md)

## 许可证

项目底座和仓库中的相关组件分别遵循其原始许可证。根目录保留 [LICENSE-MIT](LICENSE-MIT)、[LICENSE-APACHE](LICENSE-APACHE) 和 [DISCLAIMER.md](DISCLAIMER.md)；`references/luck-report` 目录另有其许可证文件。部署、修改和再分发前请分别核对各依赖和参考源码的授权条款。
