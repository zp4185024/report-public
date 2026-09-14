# hd-report-center 架构说明

## 目标定位

`hd-report-center` 是一个独立报表中心，不绑定 HIS-MEDICAL，也不把 LIS、PACS、外卖等系统的统计逻辑写死到某一个业务系统里。

项目范围限定为报表服务本身：数据源和数据集、报表模板、查询预览、打印导出、共享查看，以及供外部业务系统调用或嵌入的接口和页面。Admin.NET 管理后台的用户、组织、角色、菜单和系统运维能力不属于本项目交付范围；仓库中的 Admin.NET 代码仅承担现有宿主底座和兼容承载职责。

核心职责：

- 统一维护报表模板、查询条件、数据源和导出配置。
- 通过 URL 或 iframe 被其他系统嵌入。
- 从 URL 接收必要上下文参数，再由报表中心按模板执行查询。
- 给非编程人员提供可编辑的统计布局、公式和查询条件。
- 基础库使用 SQL Server，不使用 SQLite。

## 模块边界

- `Admin.NET.Web.Entry`：报表服务后端入口所使用的宿主进程。
- `Admin.NET.Web.Core`：报表服务运行所需的宿主配置；Admin.NET 管理后台不在本项目实施范围内。
- `HD.ReportCenter.Application`：报表中心 API、应用服务、接口校验和嵌入入口。
- `HD.ReportCenter.ReportEngine`：报表模板、数据集、参数、公式、渲染和导出核心。
- `ReportWeb`：React 19 + Univer 的报表模板设计、预览和配置验证页。
- `references/luck-report`：参考源码，不直接作为最终模块发布。

## 基础库

Admin.NET 系统库使用 SQL Server：

```text
Server=192.168.2.5;Database=lis_admin;User Id=hdhis;Password=CHANGE_ME;Encrypt=True;TrustServerCertificate=True;
```

提交到仓库的配置不能包含真实密码。开发和部署时通过环境变量 `HD_REPORT_CENTER_DB_PASSWORD` 覆盖密码，或者通过 `HD_REPORT_CENTER_DB_CONNECTION_STRING` 覆盖完整连接串。报表查询外部系统时，再为 HIS、LIS、PACS 等系统单独配置只读数据源。

## 第一版能力拆分

1. 数据源管理：配置 HIS、LIS、PACS、外卖等系统的只读查询连接，当前支持 SQL Server、Oracle、MySQL 和 PostgreSQL。
2. 数据集管理：SQL 查询、输入变量定义、返回字段契约和测试执行。
3. 报表设计：为数据集变量配置取值来源，并维护表格布局、统计单元格和公式。
4. 报表预览：通过模板编码和 URL 参数打开可嵌入页面。
5. 导出打印：普通 Excel 直接下载，大数据量由后台任务流式生成。

当前第一阶段把数据源和数据集作为全局复用资源，统一在 `/data-assets` 页面维护。数据源通过统一提供器收敛 SQL Server、Oracle、MySQL 和 PostgreSQL 的连接串、连接测试、类型化参数和分页语法差异。数据集拥有数据源引用、参数化 `SELECT/WITH` SQL、查询限制、SQL 输入变量和查询返回字段；它不拥有列标题、列宽或公式等 Excel 表现属性。报表通过 `DatasetCode` 引用数据集，并为每个数据集变量配置查询表单、系统默认或固定值等具体取值策略；工作簿模板负责布局、表头和公式。

数据集预览位于独立的 `/data-assets/preview` 页面，只执行已保存数据集，可临时输入变量并查看最多 50 行、最长 30 秒的只读结果。成功预览后可把实际返回的字段名和类型保存为数据集输出契约。一个数据集可被多张报表引用，但当前单张报表仍只支持一个主数据集。运行时会再次校验只读 SQL，并通过数据库参数对象绑定输入值；暂不支持多主数据集、参数级联选项和配置发布审批。

当前资源关系：

```text
RcDataSource 1 <- N RcDataset 1 <- N RcReport
                    |          |
                    |          +-- RcReportParameter（把数据集变量具体化为取值和控件策略）
                    +-- 输入变量 + 返回字段契约（不含 Excel 公式和列样式）
```

数据源和数据集分别维护独立修订号，资源保存与报表保存不共用事务。这样修改报表参数不会顺带覆盖共享 SQL，编辑共享数据集时也会通过乐观并发检查阻止旧页面覆盖新版本。

## URL 嵌入约定

建议第一版采用：

```text
/report/view/{reportCode}?source=his&orgId=1&deptId=1001&startDate=2026-07-01&endDate=2026-07-13
```

设计要点：

- URL 参数只传上下文和筛选值，不传 SQL。
- 报表中心按 `reportCode` 找模板，再校验允许的参数。
- 每种外部数据库连接都只配置单独的只读账号，不复用业务系统高权限账号。
- 对外嵌入页面可以做轻量签名，避免 URL 被随意改参。

## 服务边界和部署责任

- 报表服务负责报表配置、只读查询、模板渲染、预览、打印和导出，不负责业务系统的用户或组织管理。
- 调用方通过报表编码、允许的查询参数和 iframe/HTTP 接口接入；调用方或部署网关负责登录态、访问范围和网络隔离。
- 报表数据源统一使用只读账号；报表中心主库和运行配置按本项目部署说明维护。
- Admin.NET 原有管理前端和管理接口不作为报表服务使用入口，也不作为本项目交付验收内容。

## 授权和商业风险取舍

- Admin.NET 底座许可证为 MIT / Apache-2.0，需要保留原许可证和免责声明。
- `luck-report` 作为参考源码保留，最终实现应按业务目标重写 .NET 8 后端逻辑和 Vue 3 前端集成。
- 第一版不采用 iText 5 PDF 方案，先用 Excel 和浏览器打印满足快速上线。
- 当前验证页使用 Univer 作为可编辑工作簿引擎；正式发布前仍需完成授权条款复核和版本锁定。
