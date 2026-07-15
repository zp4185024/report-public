# Full Preview Default Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让报表预览默认一次返回全部数据，只有报表定义明确配置 `Paged` 时才分页。

**Architecture:** `PocReportCatalog` 保留统一执行入口，并增加接收显式 `PocPreviewSettings` 的重载，使默认 `Full` 和可选 `Paged` 共用同一数据生成逻辑。React 通过纯函数生成预览统计栏展示状态，`Full` 隐藏分页按钮，`Paged` 保留现有翻页行为。

**Tech Stack:** .NET 8、xUnit、React 19、TypeScript、Vitest、Univer 0.25.1

---

### Task 1: 后台默认 Full 并保留显式 Paged 分支

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.Tests/PocReportCatalogTests.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportCatalog.cs`

- [ ] **Step 1: 写入失败测试**

把当前定义断言改为 `Full`，并增加完整预览和显式分页测试：

```csharp
Assert.Equal(PocPreviewMode.Full, definition.Preview.Mode);

var full = PocReportCatalog.Execute(request with { Page = 2 });
Assert.Equal(1, full.Page);
Assert.Equal(137, full.Rows.Count);
Assert.False(full.HasMore);

var paged = PocReportCatalog.Execute(
    request with { Page = 2 },
    new PocPreviewSettings(PocPreviewMode.Paged, 20, false));
Assert.Equal(20, paged.Rows.Count);
Assert.Equal(21, paged.Rows[0].Sequence);
Assert.True(paged.HasMore);
```

更新导出页测试：默认 `Full` 应得到一个 137 行批次；显式 `Paged` 应得到 7 个批次且总计仍为 137 行。

- [ ] **Step 2: 运行测试并确认失败原因**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PocReportCatalogTests"
```

Expected: `GetDefinition` 仍返回 `Paged`，完整预览只有 20 行，且显式设置重载尚不存在。

- [ ] **Step 3: 实现最小后台分支**

将当前定义改为：

```csharp
new PocPreviewSettings(PocPreviewMode.Full, PageSize, false)
```

保留 `Execute(PocExecuteRequest, DateTimeOffset?)`，让它读取默认定义；增加接收 `PocPreviewSettings` 的重载。执行逻辑按模式分支：

```csharp
if (preview.Mode == PocPreviewMode.Full)
{
    page = 1;
    offset = 0;
    count = PreviewRowCount;
}
else
{
    page = Math.Max(1, request.Page);
    offset = (page - 1) * preview.PageSize;
    count = Math.Max(0, Math.Min(preview.PageSize, PreviewRowCount - offset));
}
```

为 `ReadExportPages` 增加接收显式 `PocPreviewSettings` 的重载；默认入口传入当前定义。`Full` 返回一个批次，`Paged` 继续循环执行到 `HasMore=false`。

- [ ] **Step 4: 运行聚焦测试并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PocReportCatalogTests"
```

Expected: 全部 `PocReportCatalogTests` 通过。

Commit:

```powershell
git add Admin.NET/HD.ReportCenter.Tests/PocReportCatalogTests.cs Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportCatalog.cs
git commit -m "fix: default report preview to full data"
```

### Task 2: 前端 Full 模式隐藏分页栏

**Files:**
- Create: `ReportWeb/src/previewToolbar.ts`
- Create: `ReportWeb/src/previewToolbar.test.ts`
- Modify: `ReportWeb/src/App.tsx`

- [ ] **Step 1: 写入失败测试**

为纯函数定义期望行为：

```typescript
expect(createPreviewToolbarPresentation('Full', result)).toEqual({
  labels: ['共 137 行'],
  showPager: false,
});
expect(createPreviewToolbarPresentation('Paged', result)).toEqual({
  labels: ['第 2 页', '20 行'],
  showPager: true,
});
```

- [ ] **Step 2: 运行测试并确认失败原因**

Run:

```powershell
pnpm vitest run src/previewToolbar.test.ts
```

Expected: FAIL，`previewToolbar` 模块尚不存在。

- [ ] **Step 3: 实现纯函数并接入 App**

`createPreviewToolbarPresentation` 接收 `PreviewMode` 和 `ExecuteResult | null`：

```typescript
export function createPreviewToolbarPresentation(
  mode: PreviewMode,
  result: ExecuteResult | null,
): PreviewToolbarPresentation {
  const rowCount = result?.rows.length ?? 0;
  return mode === 'Paged'
    ? { labels: [`第 ${result?.page ?? 1} 页`, `${rowCount} 行`], showPager: true }
    : { labels: [`共 ${rowCount} 行`], showPager: false };
}
```

`App.tsx` 使用该结果渲染统计文字；只有 `mode === 'preview' && previewToolbar.showPager` 时渲染翻页按钮。

- [ ] **Step 4: 运行聚焦测试、构建并提交**

Run:

```powershell
pnpm vitest run src/previewToolbar.test.ts
pnpm build
```

Expected: 测试与生产构建通过。

Commit:

```powershell
git add ReportWeb/src/previewToolbar.ts ReportWeb/src/previewToolbar.test.ts ReportWeb/src/App.tsx
git commit -m "fix: hide pagination for full preview"
```

### Task 3: 更新验证文档并完成全量验证

**Files:**
- Modify: `docs/validation/2026-07-15-editable-template-operation-guide.md`
- Modify: `docs/validation/2026-07-14-core-validation-results.md`

- [ ] **Step 1: 更新操作说明和验证结果**

明确当前验证报表为 `Full`，预览一次显示 137 条，数据区域为第 6 至 142 行；`Paged` 仅在报表配置明确启用时生效。

- [ ] **Step 2: 运行完整自动化验证**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore
pnpm vitest run
dotnet build Admin.NET/Admin.NET.Web.Entry/Admin.NET.Web.Entry.csproj -c Release --no-restore
pnpm build
```

Expected: 后端和前端测试零失败，两个生产构建成功。

- [ ] **Step 3: 真实页面验证**

打开：

```text
http://localhost:5175/?hospitalId=H01&deptId=D02&personId=P03
```

确认统计栏显示 `共 137 行`，没有翻页按钮，工作表最后一条明细序号为 137，公式列已计算。

- [ ] **Step 4: 提交文档**

```powershell
git add docs/validation/2026-07-15-editable-template-operation-guide.md docs/validation/2026-07-14-core-validation-results.md
git commit -m "docs: record full preview validation"
```
