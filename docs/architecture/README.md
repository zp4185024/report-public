# hd-report-center 架构说明

## 目标定位

`hd-report-center` 是一个独立报表中心，不绑定 HIS-MEDICAL，也不把 LIS、PACS、外卖等系统的统计逻辑写死到某一个业务系统里。

核心职责：

- 统一维护报表模板、查询条件、数据源和导出配置。
- 通过 URL 或 iframe 被其他系统嵌入。
- 从 URL 接收必要上下文参数，再由报表中心按模板执行查询。
- 给非编程人员提供可编辑的统计布局、公式和查询条件。
- 基础库使用 SQL Server，不使用 SQLite。

## 模块边界

- `Admin.NET.Web.Entry`：后端入口。
- `Admin.NET.Web.Core`：Admin.NET Web 核心配置，当前已引用报表中心应用层。
- `HD.ReportCenter.Application`：报表中心 API、应用服务、权限接入和嵌入入口。
- `HD.ReportCenter.ReportEngine`：报表模板、数据集、参数、公式、渲染和导出核心。
- `Web`：Vue 3 前端，后续新增报表设计器、预览页和嵌入页。
- `references/luck-report`：参考源码，不直接作为最终模块发布。

## 基础库

Admin.NET 系统库使用 SQL Server：

```text
Server=192.168.2.5;Database=lis_admin;User Id=hdhis;Password=CHANGE_ME;Encrypt=True;TrustServerCertificate=True;
```

提交到仓库的配置不能包含真实密码。开发和部署时通过环境变量 `DbConnection__ConnectionConfigs__0__ConnectionString` 覆盖完整连接串。报表查询外部系统时，再为 HIS、LIS、PACS 等系统单独配置只读数据源。

## 第一版能力拆分

1. 数据源管理：配置 HIS、LIS、PACS、外卖等系统的只读查询连接。
2. 数据集管理：SQL 查询、参数定义、字段预览和测试执行。
3. 报表设计：表格布局、查询表单、统计单元格、常用公式。
4. 报表预览：通过模板编码和 URL 参数打开可嵌入页面。
5. 导出打印：优先 Excel 导出和 HTML 打印。

## URL 嵌入约定

建议第一版采用：

```text
/report/view/{reportCode}?source=his&orgId=1&deptId=1001&startDate=2026-07-01&endDate=2026-07-13
```

设计要点：

- URL 参数只传上下文和筛选值，不传 SQL。
- 报表中心按 `reportCode` 找模板，再校验允许的参数。
- 数据库连接只配置只读账号。
- 对外嵌入页面可以做轻量签名，避免 URL 被随意改参。

## 授权和商业风险取舍

- Admin.NET 底座许可证为 MIT / Apache-2.0，需要保留原许可证和免责声明。
- `luck-report` 作为参考源码保留，最终实现应按业务目标重写 .NET 8 后端逻辑和 Vue 3 前端集成。
- 第一版不采用 iText 5 PDF 方案，先用 Excel 和浏览器打印满足快速上线。
- 暂不引入 Univer 作为核心编辑器，避免商业授权边界影响发布。
