# Template Data Export And Formula Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan.

**Goal:** 修复 XLSX 模板导入后公式显示 `#VALUE!` 的问题，并基于已保存模板实现“导出模板”和“导出完整数据”两种导出，其中分页报表按页读取全部数据后输出一个 XLSX，大数据量采用后台流式任务。

**Architecture:** XLSX 模板导入时同时保存公式和缓存值，设计态避免公式立即对绑定占位符求值；预览态完成参数及明细填充后显式触发 Univer 重算。数据导出统一从数据库读取已保存模板及绑定配置，冻结成不可变导出计划；普通导出使用 ClosedXML 按模板行复制全部数据，大文件导出使用 Open XML 前向写入并复用同一公式转换器。前端导出前执行保存门禁，未保存时确认并先保存，再按模板配置选择普通下载或后台大文件任务。

**Tech Stack:** .NET 8、ASP.NET Core、SqlSugar/SQLite、ClosedXML 0.105、ClosedXML.Parser 2.0、DocumentFormat.OpenXml 3.1、React 19、TypeScript、Univer 0.25.1、Vitest、xUnit

---

## 实施约束

- 模板导出继续导出设计态 XLSX，占位符和公式原样保留。
- 数据导出必须使用数据库中已保存的模板版本；请求版本与数据库版本不一致时拒绝导出。
- `Preview.Mode=Paged` 只决定后台分批查询，最终 XLSX 仍包含全部查询结果。
- `Export.DefaultMode` 决定普通导出或大文件导出，与预览分页互不替代。
- 当前 `dept-workload` 验证数据为 137 行；分页读取应得到 7 页，最终只生成一个工作簿。
- 本阶段不在模板外重复计算汇总；行公式和整体统计公式均由 Excel/Univer 公式完成。
- 普通导出禁止只取当前预览页；大文件导出禁止把全部业务行装入内存。

### Task 1: 导入公式同时保留缓存值

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.Tests/PocXlsxExchangeConverterTests.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocXlsxExchangeConverter.cs`

**Step 1: 写入失败测试**

在现有公式导入测试中增加缓存值断言；测试工作簿中的公式单元格同时设置公式和上次计算值，验证导入结果包含 `f`、`v`、`t`。

```csharp
var formulaCell = importedSnapshot["sheets"]!["sheet-1"]!["cellData"]!["5"]!["4"]!;
Assert.Equal("=C6+D6", formulaCell.Value<string>("f"));
Assert.Equal(0d, formulaCell.Value<double>("v"));
Assert.Equal(2, formulaCell.Value<int>("t"));
```

再增加一个 XLSX 没有可用缓存值的用例，验证回退为数值 `0`，而不是只写 `f`。

**Step 2: 验证测试失败**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocXlsxExchangeConverterTests"
```

Expected: 缓存值断言失败，当前结果只有 `f`。

**Step 3: 实现公式缓存导入**

在 `ReadCell` 的公式分支中，继续使用数据库映射规则写入公式，同时按值映射和类型映射写入缓存结果：

```csharp
private static (object Value, int CellType) ReadFormulaCache(IXLCell source)
{
    if (source.CachedValue.Type == XLDataType.Number)
    {
        return (source.CachedValue.GetNumber(), UniverNumberCellType);
    }

    if (source.CachedValue.Type == XLDataType.Boolean)
    {
        return (source.CachedValue.GetBoolean(), UniverBooleanCellType);
    }

    if (source.CachedValue.Type == XLDataType.Text &&
        !string.IsNullOrWhiteSpace(source.CachedValue.GetText()))
    {
        return (source.CachedValue.GetText(), UniverStringCellType);
    }

    return (0d, UniverNumberCellType);
}
```

实现时使用 ClosedXML 0.105 实际公开的缓存值 API；如 `CachedValue` 不公开，则从工作表 XML 的 `<v>` 读取，最终行为保持上述契约。`v` 和 `t` 的目标路径必须来自现有 `RcPocExchangeMapping`，不得硬编码 JSON 路径。

**Step 4: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocXlsxExchangeConverterTests"
```

Expected: PASS。

```powershell
git add Admin.NET/HD.ReportCenter.Tests/PocXlsxExchangeConverterTests.cs Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocXlsxExchangeConverter.cs
git commit -m "fix: preserve imported formula cache"
```

### Task 2: 预览填充后显式触发 Univer 公式重算

**Files:**
- Create: `ReportWeb/src/formulaCalculation.ts`
- Create: `ReportWeb/src/formulaCalculation.test.ts`
- Modify: `ReportWeb/src/UniverTemplateHost.tsx`

**Step 1: 写入失败测试**

将公式重算封装为可测试的异步助手，测试执行顺序、成功回调和超时：

```ts
it('starts calculation and resolves after result is applied', async () => {
  const calls: string[] = [];
  const formula = {
    executeCalculation: () => calls.push('execute'),
    onCalculationResultApplied: async (timeout?: number) =>
      calls.push(`wait:${timeout}`),
  };

  const pending = recalculateFormulas(formula, 100);
  await pending;

  expect(calls).toEqual(['execute', 'wait:100']);
});
```

**Step 2: 验证测试失败**

Run:

```powershell
npm test -- --run src/formulaCalculation.test.ts
```

Workdir: `ReportWeb`

Expected: 模块不存在或导出函数不存在。

**Step 3: 实现并接入预览渲染**

实现以下最小接口。Univer 0.25.1 的等待方法直接返回 Promise 并跟踪最新计算会话，因此先执行计算，再等待结果应用；超时由第三方 API 自身处理：

```ts
export interface FormulaCalculationController {
  executeCalculation(): void;
  onCalculationResultApplied(timeout?: number): Promise<void>;
}

export function recalculateFormulas(
  formula: FormulaCalculationController,
  timeoutMs = 10_000,
): Promise<void>;
```

在 `UniverTemplateHost.renderPreview` 中，参数填充、明细扩展和合计行调整全部完成后调用：

```ts
await recalculateFormulas(univerAPI.getFormula());
```

设计态加载不强制重算，从而保留导入的缓存值；预览态必须等待重算完成再上报渲染完成。

**Step 4: 验证并提交**

Run:

```powershell
npm test -- --run src/formulaCalculation.test.ts src/templatePreview.test.ts
npm run build
```

Workdir: `ReportWeb`

Expected: PASS。

```powershell
git add ReportWeb/src/formulaCalculation.ts ReportWeb/src/formulaCalculation.test.ts ReportWeb/src/UniverTemplateHost.tsx
git commit -m "fix: recalculate formulas after preview binding"
```

### Task 3: 增加导出配置、请求契约和全量分页读取器

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportContracts.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportCatalog.cs`
- Modify: `Admin.NET/HD.ReportCenter.Tests/PocReportCatalogTests.cs`
- Modify: `ReportWeb/src/types.ts`

**Step 1: 写入失败测试**

增加全量分页读取测试：

```csharp
[Fact]
public async Task ReadExportPages_ReturnsAllRowsInStableOrder()
{
    var pages = new List<IReadOnlyList<PocWorkloadRow>>();
    await foreach (var page in PocReportCatalog.ReadExportPages(
        new PocExecuteRequest(1, "H01", "D02", "P03", "2026-07-01", "2026-07-15"),
        CancellationToken.None))
    {
        pages.Add(page);
    }

    Assert.Equal(7, pages.Count);
    Assert.Equal(137, pages.Sum(page => page.Count));
    Assert.Equal(Enumerable.Range(1, 137), pages.SelectMany(page => page).Select(row => row.Sequence));
}
```

**Step 2: 验证测试失败**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocReportCatalogTests"
```

Expected: `ReadExportPages` 或新契约不存在。

**Step 3: 实现契约和读取器**

新增：

```csharp
public enum PocDataExportMode
{
    Normal,
    Large,
}

public sealed record PocExportSettings(
    PocDataExportMode DefaultMode,
    int NormalMaxRows,
    int NormalMaxCells,
    int NormalTimeoutSeconds,
    bool LargeExportEnabled,
    int RowsPerSheet,
    int FileRetentionHours);

public sealed record PocDataExportRequest(
    int TemplateRevision,
    PocExecuteRequest Query);
```

给 `PocReportDefinition` 增加 `Export`，当前报表配置为 `Normal / 20000 / 500000 / 60 / true / 1000000 / 24`。实现 `ReadExportPages(PocExecuteRequest request, CancellationToken cancellationToken)`：`Paged` 模式按定义页大小读取，`Full` 模式返回一个页；两者均输出全部结果且保持顺序。

同步 TypeScript 定义，枚举值按 JSON 实际序列化形式建模。

**Step 4: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocReportCatalogTests"
npm test -- --run src/api.test.ts
```

第二条命令 Workdir: `ReportWeb`。

```powershell
git add Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportContracts.cs Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportCatalog.cs Admin.NET/HD.ReportCenter.Tests/PocReportCatalogTests.cs ReportWeb/src/types.ts
git commit -m "feat: define full data export contract"
```

### Task 4: 使用公式解析器实现模板行公式平移

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/HD.ReportCenter.ReportEngine.csproj`
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocFormulaTranslator.cs`
- Create: `Admin.NET/HD.ReportCenter.Tests/PocFormulaTranslatorTests.cs`

**Step 1: 写入失败测试**

```csharp
[Theory]
[InlineData("=C6+D6", 6, 5, 7, 5, "=C7+D7")]
[InlineData("=$C6+D$6", 6, 5, 8, 5, "=$C8+D$6")]
[InlineData("=AVERAGE(C6:D6)", 6, 6, 9, 6, "=AVERAGE(C9:D9)")]
[InlineData("='数据表'!C6+D6", 6, 5, 7, 5, "='数据表'!C7+D7")]
public void MoveFormula_PreservesExcelReferenceSemantics(
    string formula, int sourceRow, int sourceColumn,
    int targetRow, int targetColumn, string expected)
{
    Assert.Equal(expected, PocFormulaTranslator.MoveFormula(
        formula, sourceRow, sourceColumn, targetRow, targetColumn));
}
```

**Step 2: 验证测试失败**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocFormulaTranslatorTests"
```

Expected: 类型不存在。

**Step 3: 实现公式平移**

显式引用 `ClosedXML.Parser` 2.0.0，避免依赖传递引用的偶然性：

```xml
<PackageReference Include="ClosedXML.Parser" Version="2.0.0" />
```

公式转换使用解析器的 A1/R1C1 双向转换，不使用正则表达式：

```csharp
public static string MoveFormula(
    string formulaA1,
    int sourceRow,
    int sourceColumn,
    int targetRow,
    int targetColumn)
{
    var normalized = formulaA1.StartsWith('=') ? formulaA1[1..] : formulaA1;
    var r1C1 = FormulaConverter.ToR1C1(normalized, sourceRow, sourceColumn);
    return "=" + FormulaConverter.ToA1(r1C1, targetRow, targetColumn);
}
```

**Step 4: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocFormulaTranslatorTests"
```

```powershell
git add Admin.NET/HD.ReportCenter.ReportEngine/HD.ReportCenter.ReportEngine.csproj Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocFormulaTranslator.cs Admin.NET/HD.ReportCenter.Tests/PocFormulaTranslatorTests.cs
git commit -m "feat: translate copied Excel formulas"
```

### Task 5: 冻结数据库模板为不可变导出计划

**Files:**
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocTemplateDataExportPlan.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateDataExportService.cs`
- Create: `Admin.NET/HD.ReportCenter.Tests/PocTemplateDataExportServiceTests.cs`

**Step 1: 写入失败测试**

覆盖三条核心契约：

```csharp
[Fact]
public async Task CreatePlan_RejectsUnsavedRevisionZero();

[Fact]
public async Task CreatePlan_RejectsStaleRevision();

[Fact]
public async Task CreatePlan_FreezesSavedSnapshotBindingQueryAndMappings();
```

成功用例保存模板后创建计划，再修改原请求对象和数据库记录，断言计划中的快照、绑定、查询条件、映射版本和导出配置不变。

**Step 2: 验证测试失败**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocTemplateDataExportServiceTests"
```

Expected: 服务和计划类型不存在。

**Step 3: 实现导出计划服务**

导出计划采用只读值：

```csharp
public sealed record PocTemplateDataExportPlan(
    string ReportCode,
    int TemplateRevision,
    JObject Snapshot,
    PocTemplateBinding Binding,
    PocExecuteRequest Query,
    PocExportSettings Export,
    PocExchangeRuleSet Mappings);
```

`CreatePlanAsync` 必须：

1. 拒绝 `TemplateRevision <= 0`。
2. 读取数据库模板，拒绝请求版本和当前版本不一致。
3. 深拷贝 `Snapshot` 和绑定配置。
4. 读取当前启用的映射记录并固化顺序。
5. 复制查询参数，不保留前端对象引用。

错误使用有明确中文消息的领域异常，API 后续映射为 `409 Conflict`。

**Step 4: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocTemplateDataExportServiceTests"
```

```powershell
git add Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocTemplateDataExportPlan.cs Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateDataExportService.cs Admin.NET/HD.ReportCenter.Tests/PocTemplateDataExportServiceTests.cs
git commit -m "feat: freeze saved template export plans"
```

### Task 6: 用已保存模板完成普通全量数据导出

**Files:**
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocTemplateDataExporter.cs`
- Create: `Admin.NET/HD.ReportCenter.Tests/PocTemplateDataExporterTests.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocFormulaTranslator.cs`

**Step 1: 写入失败测试**

用当前可编辑模板创建导出计划，导出 137 行后重新打开 XLSX，验证：

```csharp
Assert.Equal("H01", sheet.Cell("B2").GetString());
Assert.Equal("D02", sheet.Cell("D2").GetString());
Assert.Equal(137, ReadDetailRows(sheet).Count);
Assert.Equal(1d, sheet.Cell("B6").GetDouble());
Assert.Equal(137d, sheet.Cell("B142").GetDouble());
Assert.Equal("=C6+D6", sheet.Cell("E6").FormulaA1);
Assert.Equal("=C142+D142", sheet.Cell("E142").FormulaA1);
Assert.Equal("=AVERAGE(C142:D142)", sheet.Cell("F142").FormulaA1);
Assert.DoesNotContain("{{", GetAllCellText(sheet));
```

另测：没有明细行时保留一行空模板样式且公式引用合法；跨表和绝对引用公式使用 `PocFormulaTranslator` 平移。

**Step 2: 验证测试失败**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocTemplateDataExporterTests"
```

Expected: 导出器不存在。

**Step 3: 实现普通导出器**

实现流程：

```csharp
public async Task WriteNormalAsync(
    Stream destination,
    PocTemplateDataExportPlan plan,
    IAsyncEnumerable<IReadOnlyList<PocWorkloadRow>> pages,
    CancellationToken cancellationToken)
{
    await using var templateStream = new MemoryStream();
    _exchangeConverter.Export(templateStream, plan.Snapshot, plan.Mappings);
    templateStream.Position = 0;
    using var workbook = new XLWorkbook(templateStream);

    var sheet = ResolveBoundWorksheet(workbook, plan);
    ReplaceParameterBindings(sheet, plan.Binding.Parameters, plan.Query);
    var rows = await MaterializeWithinNormalLimitsAsync(pages, plan.Export, cancellationToken);
    ExpandDetailRows(sheet, plan.Binding.Detail, rows);
    workbook.SaveAs(destination);
}
```

`ExpandDetailRows` 在复制前捕获模板行公式及源坐标，复制行样式、边框、行高和合并关系；字段单元格写入业务值，公式单元格调用 `MoveFormula`。插入明细行后，整体统计行由 ClosedXML 的插行语义扩展引用范围；增加测试锁定该行为。超过 `NormalMaxRows`、`NormalMaxCells` 或超时后抛出明确异常，提示改用大文件导出。

**Step 4: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocTemplateDataExporterTests"
```

```powershell
git add Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocTemplateDataExporter.cs Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocFormulaTranslator.cs Admin.NET/HD.ReportCenter.Tests/PocTemplateDataExporterTests.cs
git commit -m "feat: export all data with saved template"
```

### Task 7: 普通数据导出 API 改用已保存模板

**Files:**
- Create: `Admin.NET/HD.ReportCenter.Tests/PocReportApiTests.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`

**Step 1: 写入失败测试**

增加 API 测试：

- `POST /api/report-center/poc/dept-workload/exports/normal` 接收 `{ templateRevision, query }`。
- revision 为 0 返回冲突并提示先保存。
- revision 过期返回冲突。
- revision 正确时下载文件包含 137 行，而不是预览当前页 20 行。

**Step 2: 验证测试失败**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocReportApiTests"
```

Expected: 旧接口仍接收 `PocExecuteRequest` 或只导出固定布局。

**Step 3: 实现 API**

```csharp
[HttpPost("/api/report-center/poc/{reportCode}/exports/normal")]
public async Task<IActionResult> NormalExport(
    string reportCode,
    [FromBody] PocDataExportRequest request,
    CancellationToken cancellationToken)
{
    var plan = await _dataExportService.CreatePlanAsync(reportCode, request, cancellationToken);
    var stream = new MemoryStream();
    await _dataExporter.WriteNormalAsync(
        stream,
        plan,
        PocReportCatalog.ReadExportPages(request.Query, cancellationToken),
        cancellationToken);
    stream.Position = 0;
    return File(stream, XlsxContentType, BuildFileName(reportCode));
}
```

通过现有模块服务解析方式创建服务；领域冲突返回 `409`，普通导出超限返回 `413` 并给出大文件导出提示。

**Step 4: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocReportApiTests"
```

```powershell
git add Admin.NET/HD.ReportCenter.Tests/PocReportApiTests.cs Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs
git commit -m "feat: expose saved-template normal export"
```

### Task 8: 大文件任务按模板分页流式写入

**Files:**
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocStreamingTemplateDataExporter.cs`
- Create: `Admin.NET/HD.ReportCenter.Tests/PocStreamingTemplateDataExporterTests.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocFormulaTranslator.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocLargeExportJobStore.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`

**Step 1: 写入失败测试**

测试 100,000 行的大文件任务，断言：

```csharp
Assert.Equal(100_000, CountDetailRows(output));
Assert.Equal("=C100005+D100005", ReadFormula(output, "E100005"));
Assert.DoesNotContain("{{detail.", ReadSharedAndInlineStrings(output));
Assert.True(progressSamples.SequenceEqual(progressSamples.OrderBy(value => value)));
Assert.InRange(memoryAfter - memoryBefore, 0, 96 * 1024 * 1024);
```

增加取消测试，取消后状态为 `Cancelled`，临时文件被清理；增加分页测试，确认每写完一页才更新进度且从未一次性枚举全部行。

**Step 2: 验证测试失败**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocStreamingTemplateDataExporterTests"
```

Expected: 流式模板导出器不存在。

**Step 3: 实现流式导出器**

实现边界：

```csharp
public Task WriteLargeAsync(
    Stream destination,
    PocTemplateDataExportPlan plan,
    IAsyncEnumerable<IReadOnlyList<PocWorkloadRow>> pages,
    IProgress<PocExportProgress> progress,
    CancellationToken cancellationToken);
```

具体算法：

1. 用现有交换转换器生成一次模板 XLSX 骨架。
2. 复制非目标 ZIP 项；目标 worksheet XML 用 `XmlReader` + `XmlWriter` 前向重写。
3. 参数绑定单元格在复制时替换值。
4. 读到绑定模板行时缓存一行 XML；每读取一页业务数据即克隆模板行并写出，行号和单元格引用同步改写。
5. 行公式使用 `PocFormulaTranslator.MoveFormula`，禁止正则替换公式引用。
6. 模板行之后的单元格、合并区域、自动筛选、打印区域及统计公式使用解析器按插入行数调整。
7. 达到 `RowsPerSheet` 时复制模板工作表结构到下一张表，继续写入；每张表保留表头、列宽、冻结窗格和打印配置。
8. 每页后检查取消标记并报告已写行数；任何失败均删除未完成文件。

先实现当前验证模板所需的 worksheet、mergeCells、dimension、autoFilter、definedNames 变换；每种变换都有独立测试，未识别的结构原样复制并写日志。

**Step 4: 改造任务存储和 API**

`PocLargeExportJobStore.StartAsync` 在返回任务号前创建并冻结导出计划；后台任务只持有计划，不再回查最新版模板。现有接口改为接收 `PocDataExportRequest`：

```csharp
[HttpPost("/api/report-center/poc/{reportCode}/exports/large")]
public Task<PocExportJobSnapshot> StartLargeExport(
    string reportCode,
    [FromBody] PocDataExportRequest request,
    CancellationToken cancellationToken);
```

轮询、取消和下载 URL 保持兼容。

**Step 5: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "FullyQualifiedName~PocStreamingTemplateDataExporterTests|FullyQualifiedName~PocLargeExportJob"
```

```powershell
git add Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocStreamingTemplateDataExporter.cs Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Export/PocFormulaTranslator.cs Admin.NET/HD.ReportCenter.Tests/PocStreamingTemplateDataExporterTests.cs Admin.NET/HD.ReportCenter.Application/CoreValidation/PocLargeExportJobStore.cs Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs
git commit -m "feat: stream paged data through saved template"
```

### Task 9: 前端 API 区分模板导出和数据导出

**Files:**
- Modify: `ReportWeb/src/api.test.ts`
- Modify: `ReportWeb/src/api.ts`
- Modify: `ReportWeb/src/types.ts`

**Step 1: 写入失败测试**

```ts
it('sends saved revision and query to normal data export', async () => {
  await exportReportData('dept-workload', 3, request, 'normal');
  expect(fetchMock).toHaveBeenCalledWith(
    '/api/report-center/poc/dept-workload/exports/normal',
    expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({ templateRevision: 3, query: request }),
    }),
  );
});
```

大文件启动测试同样断言请求体；模板导出测试继续断言调用 `/template/export`，防止两个语义混淆。

**Step 2: 验证测试失败**

Run:

```powershell
npm test -- --run src/api.test.ts
```

Workdir: `ReportWeb`

Expected: 旧 API 只发送查询条件。

**Step 3: 实现类型和 API**

```ts
export interface DataExportRequest {
  templateRevision: number;
  query: ReportExecuteRequest;
}

export async function downloadNormalData(
  reportCode: string,
  templateRevision: number,
  query: ReportExecuteRequest,
): Promise<Blob>;

export async function startLargeDataExport(
  reportCode: string,
  templateRevision: number,
  query: ReportExecuteRequest,
): Promise<ExportJob>;
```

保留 `exportTemplate` 名称；删除或替换含义不清的旧 `downloadNormal`、`startLarge` 调用。

**Step 4: 验证并提交**

Run:

```powershell
npm test -- --run src/api.test.ts
npm run build
```

Workdir: `ReportWeb`

```powershell
git add ReportWeb/src/api.test.ts ReportWeb/src/api.ts ReportWeb/src/types.ts
git commit -m "feat: separate template and data export clients"
```

### Task 10: 前端保存门禁和数据导出交互

**Files:**
- Create: `ReportWeb/src/dataExportFlow.ts`
- Create: `ReportWeb/src/dataExportFlow.test.ts`
- Modify: `ReportWeb/src/App.tsx`
- Modify: `ReportWeb/src/App.css`

**Step 1: 写入失败测试**

覆盖以下状态：

```ts
it('exports immediately when the saved revision is clean');
it('asks for confirmation and saves before dirty design export');
it('cancels export when the user rejects saving');
it('saves preview draft snapshot before preview export');
it('selects large job flow from definition export mode');
it('polls a large job until the file can be downloaded');
```

关键顺序断言：

```ts
expect(calls).toEqual(['confirm', 'save', 'export:revision-4']);
```

**Step 2: 验证测试失败**

Run:

```powershell
npm test -- --run src/dataExportFlow.test.ts
```

Workdir: `ReportWeb`

Expected: 模块不存在。

**Step 3: 实现保存门禁**

`prepareSavedRevisionForExport` 接收当前 revision、dirty、确认函数和保存函数；dirty 或 revision=0 时显示：

```text
当前模板尚未保存，是否保存后继续导出数据？
```

用户同意后：

- 设计态调用编辑器 `saveDraft()` 后保存。
- 预览态保存 `draftSnapshotRef`，不保存已展开的预览数据副本。
- 保存成功后使用响应中的新 revision 发起导出，不能继续使用旧 revision。

**Step 4: 实现界面**

设计态工具栏按钮顺序：

```text
导入模板 | 导出模板 | 导出数据 | 保存模板
```

预览态保留“导出数据”。使用现有图标库中的上传、下载、文件表格、保存图标和工具提示。普通导出显示进度状态并直接下载；大文件导出轮询任务，提供进度、取消和完成下载。按钮 busy 状态互斥，错误信息使用后端中文消息。

**Step 5: 验证并提交**

Run:

```powershell
npm test -- --run src/dataExportFlow.test.ts src/api.test.ts src/templatePreview.test.ts
npm run build
```

Workdir: `ReportWeb`

```powershell
git add ReportWeb/src/dataExportFlow.ts ReportWeb/src/dataExportFlow.test.ts ReportWeb/src/App.tsx ReportWeb/src/App.css
git commit -m "feat: save templates before data export"
```

### Task 11: 集成验证、操作说明和实际页面验收

**Files:**
- Modify: `docs/核心验证操作说明.md`
- Modify: `docs/核心验证结论.md`

**Step 1: 全量自动测试**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj
npm test -- --run
npm run build
```

后两条命令 Workdir: `ReportWeb`。

Expected: 全部通过，前端无 TypeScript 构建错误。

**Step 2: 实际接口验证**

使用当前 SQLite 数据库和保存版本执行：

1. 导出模板，确认占位符和公式保留。
2. 重新导入模板，确认 E6 显示缓存结果而非 `#VALUE!`。
3. 预览，确认 20 行当前页公式重算成功。
4. 普通数据导出，确认 Excel 中有 137 行完整数据、E6 到 E142 行公式正确、整体统计公式引用完整数据范围。
5. 将导出模式临时设为 Large，确认后台任务按 7 页读取、只生成一个 XLSX 并可下载。
6. 修改模板但不保存后点击导出，确认弹出保存提示；取消不导出，同意则保存后使用新 revision 导出。

**Step 3: 浏览器验收**

在桌面和 1366×768 视口验证：

- 四个设计态按钮无重叠、文字不截断。
- “导出模板”和“导出数据”可明确区分。
- dirty 保存提示、普通导出、任务进度、取消和错误状态可见。
- 浏览器控制台无新增错误。

保存验收截图到 `.codex-run/verification/`，不得纳入 Git。

**Step 4: 更新说明并提交**

操作说明必须写明：

- 模板导出与数据导出的区别。
- 分页预览不会限制导出行数。
- 普通/大文件模式的判断标准。
- 未保存模板的确认和保存流程。
- 导入公式缓存与预览重算的行为。

Run:

```powershell
git add docs/核心验证操作说明.md docs/核心验证结论.md
git commit -m "docs: explain saved-template data export"
git status --short
```

Expected: 仅保留用户原有的无关改动；本功能相关文件均已提交。

## 最终验收标准

- 导入现有模板后，公式单元格不再在设计态显示 `#VALUE!`。
- 进入预览后，参数、137 行数据、行公式和整体统计公式均可验证。
- “导出模板”得到可再次导入编辑的占位符模板。
- “导出数据”只使用数据库中已保存的确切 revision，且包含全部 137 行。
- `Paged` 报表按 7 页读取但输出一个 XLSX，不丢页、不重复、不乱序。
- 普通导出超限时明确提示大文件导出；大文件导出按页写入，支持进度和取消。
- 未保存或有修改时先确认保存；取消时不产生导出任务，同意时使用保存后的新 revision。
- 公式平移由 ClosedXML.Parser 完成，绝对引用、相对引用、范围、跨表引用均有测试。
- .NET 全量测试、前端全量测试、前端生产构建和实际浏览器验收全部通过。
