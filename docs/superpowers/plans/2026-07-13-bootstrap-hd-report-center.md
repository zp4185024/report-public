# Bootstrap hd-report-center

## 目标

在 `D:\zp\git\hd-report-center` 新建独立报表中心项目，以 Admin.NET 作为 .NET 8 + Vue 3 后台底座，保留 `luck-report` 作为功能参考，新增报表中心应用层和报表引擎模块。

## 已完成

- 创建 `D:\zp\git\hd-report-center`。
- 复制 Admin.NET 干净源码作为项目底座。
- 复制 `LuckyPools/luck-report` 到 `references/luck-report`。
- 新增 `global.json`，锁定 .NET SDK 8.0.416。
- 新增 `HD.ReportCenter.Application`。
- 新增 `HD.ReportCenter.ReportEngine`。
- 将报表中心应用层挂入 Admin.NET Web 引用链。
- 增加 `/api/report-center/health` 健康检查占位接口。

## 后续执行计划

1. 完成后端依赖还原和 .NET 8 构建验证。
2. 跑 NuGet 过期和弃用包检查，列出需要优先处理的依赖。
3. 梳理 `luck-report` 的表结构、数据集、参数、公式和导出流程。
4. 在 `HD.ReportCenter.ReportEngine` 建立模板、数据集、参数、单元格和公式模型。
5. 在 `HD.ReportCenter.Application` 建立报表模板、数据源、数据集、预览和导出接口。
6. 在 Vue 3 前端新增报表中心菜单、设计器、预览页和 iframe 嵌入页。
7. 用一个 HIS 统计报表样例打通查询、公式、预览和 Excel 导出。

## 验收标准

- 后端解决方案可按 `net8.0` 构建。
- 前端依赖可安装并启动。
- 访问健康检查接口能确认报表中心模块加载。
- 至少一个报表可通过 URL 参数打开预览。
- 查询结果可统计并导出 Excel。
