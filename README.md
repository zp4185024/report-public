# hd-report-center

独立报表中心项目，用于承接 HIS、LIS、PACS、外卖等系统的查询、统计、报表设计和导出能力。

## 当前基线

- 后端底座：Admin.NET，基于 .NET 8、Furion、SqlSugar。
- 前端底座：Admin.NET Web，Vue 3、Element Plus、Vite。
- 基础库：SQL Server，配置文件为 `Admin.NET/Admin.NET.Application/Configuration/Database.json`。
- 报表参考：`references/luck-report`，用于分析报表设计器、SQL 数据集、参数、公式、预览和导出逻辑。
- 新增模块：
  - `Admin.NET/HD.ReportCenter.Application`：报表中心接口和 Admin.NET 接入层。
  - `Admin.NET/HD.ReportCenter.ReportEngine`：报表领域核心，后续承载模板、参数、公式、导出和渲染逻辑。

## 第一阶段目标

- 独立部署，不写入 HIS-MEDICAL 主系统。
- 支持其他系统通过 URL 或 iframe 嵌入报表页面。
- 支持 URL 参数传入院区、科室、人员、业务日期、系统来源等上下文。
- 支持复杂查询、统计公式、Excel 导出、HTML 打印。
- 非编程人员可以编辑查询条件、报表布局和常用统计公式。

## 技术取舍

- 优先复刻 `luck-report` 的成熟交互和报表逻辑，不直接照搬有授权风险的商业组件。
- 第一版优先做 Excel 导出和 HTML 打印；PDF 导出暂缓，避免引入 iText 这类潜在商业授权风险。
- Admin.NET 的权限、菜单、代码生成和 CRUD 能力可复用，但报表中心业务逻辑单独放在 `HD.ReportCenter.*` 模块中。

## 快速检查

```powershell
cd D:\zp\git\hd-report-center
dotnet --version
dotnet restore .\Admin.NET\Admin.NET.sln
dotnet build .\Admin.NET\Admin.NET.sln -f net8.0 --no-restore
```

默认 SQL Server 基础库：

```text
Server=192.168.2.5;Database=lis_admin;User Id=hdhis;Password=CHANGE_ME;Encrypt=True;TrustServerCertificate=True;
```

不要把真实密码提交到仓库。开发运行时用环境变量覆盖完整连接串：

```powershell
$env:DbConnection__ConnectionConfigs__0__ConnectionString='Server=192.168.2.5;Database=lis_admin;User Id=hdhis;Password=实际密码;Encrypt=True;TrustServerCertificate=True;'
```

后端健康检查接口占位：

```text
GET /api/report-center/health
```

## 参考说明

- Admin.NET 原始说明已保留在 `README.AdminNET.md`。
- 参考源码说明见 `references/README.md`。
- 架构说明见 `docs/architecture/README.md`。
