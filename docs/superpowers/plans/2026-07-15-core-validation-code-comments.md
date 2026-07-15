# 核心验证代码注释补充实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为报表核心验证的前后端生产代码补充完整、准确的简体中文注释，使后续迁移人员能够理解数据边界、关键算法、并发控制和设计取舍。

**Architecture:** 不改变运行逻辑，只增加 C# XML 文档注释、TypeScript TSDoc、模块说明和复杂代码块注释。注释重点说明“为什么这样处理”、输入输出约束、失败策略及前后端必须保持一致的协议；简单赋值和直观 JSX 不逐行复述。

**Tech Stack:** .NET 8、ASP.NET Core/Furion、SqlSugar、React 19、TypeScript、Univer OSS 0.25.1、Vitest

---

### Task 1: 后端接口、契约与持久化注释

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportTemplate.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateService.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateStore.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportContracts.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateContracts.cs`

- [x] 为公开类型、接口方法、请求/响应契约和持久化字段增加 XML 文档注释。
- [x] 解释种子模板回退、绑定重新扫描、规范化比较和乐观并发修订号的原因。
- [x] 解释 API 层如何把领域异常稳定映射为 HTTP 错误码。

### Task 2: 后端校验、分页与流式导出注释

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocLargeExportJobStore.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocExcelExporter.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportCatalog.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateValidator.cs`

- [x] 解释固定测试数据、后台默认参数和模板定义分页的验证用途。
- [x] 解释直接写 ZIP/XML 的低内存导出方式、公式延迟计算和进度上报粒度。
- [x] 解释模板 token 扫描规则、单明细行限制、合并单元格限制和确定性排序。
- [x] 解释后台任务状态同步、取消、失败文件清理和当前单机 POC 边界。

### Task 3: 前端契约、绑定扫描与预览计划注释

**Files:**
- Modify: `ReportWeb/src/types.ts`
- Modify: `ReportWeb/src/api.ts`
- Modify: `ReportWeb/src/TemplateFieldPanel.tsx`
- Modify: `ReportWeb/src/templateBinding.ts`
- Modify: `ReportWeb/src/templateChangeCommand.ts`
- Modify: `ReportWeb/src/templatePreview.ts`
- Modify: `ReportWeb/src/workbookSnapshot.ts`
- Modify: `ReportWeb/src/univer.worker.ts`

- [x] 说明前后端契约对应关系和模板绑定版本约束。
- [x] 说明浏览器端扫描不是安全边界，后台保存时仍会重新扫描。
- [x] 说明参数单元格分组替换、明细行展开、零数据处理和运行时防御性校验。
- [x] 说明 Univer 运行时类型偏差的归一化处理，以及 Worker 承担公式计算的边界。

### Task 4: 页面状态流与 Univer 生命周期注释

**Files:**
- Modify: `ReportWeb/src/App.tsx`
- Modify: `ReportWeb/src/UniverTemplateHost.tsx`

- [x] 说明设计快照与预览副本隔离、保存/预览互斥、失败恢复和分页状态流。
- [x] 说明 React state 与 ref 双轨保存快照的原因。
- [x] 说明 Univer 工作簿替换事务、回滚、命令过滤、内部操作深度和 generation 取消机制。
- [x] 说明预览行插入、整行复制、相对公式偏移、数据覆盖顺序和只读切换。

### Task 5: 验证

**Files:**
- Verify: `ReportWeb/src/**/*.ts`
- Verify: `ReportWeb/src/**/*.tsx`
- Verify: `Admin.NET/HD.ReportCenter.*/**/*.cs`

- [x] 运行 `git diff --check`，确保只增加注释且无格式问题。
- [x] 运行 `pnpm test`，预期 91 项前端测试全部通过。
- [x] 运行 `pnpm build`，预期 TypeScript 与 Vite 生产构建成功。
- [x] 运行 `dotnet test HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore`，预期 63 项 .NET 测试全部通过。
