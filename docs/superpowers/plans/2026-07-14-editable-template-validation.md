# Editable Report Template Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a database-backed Univer OSS template editor that binds backend parameters and one detail row, previews real paged data with relative formulas, restores the untouched draft, and survives save/reload.

**Architecture:** The .NET 8 application stores an opaque Univer `IWorkbookData` snapshot plus a server-validated binding index in one POC table. The React page owns separate design and preview states; pure TypeScript scans placeholders and creates a preview plan, while one long-lived Univer runtime applies row insertion and `autoFill(..., 'COPY')` so Univer itself shifts relative formulas.

**Tech Stack:** .NET 8, Furion/Admin.NET, SqlSugar, SQLite/SQL Server-compatible code-first entity, xUnit, React 19, TypeScript 7, Univer OSS 0.25.1, Vitest, Playwright.

---

## Scope And File Map

The work remains one testable vertical feature. It touches backend persistence, frontend binding logic, and the editor UI because none of those parts independently proves the requested edit-to-preview loop.

Backend files:

- Create `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateContracts.cs`: stable binding records shared by validation and API code.
- Create `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateValidator.cs`: parse snapshot placeholders and return a canonical binding.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportTemplate.cs`: SqlSugar POC entity.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateStore.cs`: database read and optimistic save.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateService.cs`: seed loading, validation, and DTO conversion.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/Seeds/dept-workload.json`: initial editable Univer snapshot.
- Modify `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`: expose template GET/PUT.
- Modify `Admin.NET/HD.ReportCenter.Application/HD.ReportCenter.Application.csproj`: embed the seed JSON.
- Modify `Admin.NET/HD.ReportCenter.Application/GlobalUsings.cs`: add Admin.NET, dependency injection, and SqlSugar imports.
- Create `Admin.NET/HD.ReportCenter.Tests/PocTemplateValidatorTests.cs`: snapshot scanning and invalid-template tests.
- Create `Admin.NET/HD.ReportCenter.Tests/PocTemplateStoreTests.cs`: SQLite persistence and revision tests.

Frontend files:

- Modify `ReportWeb/src/types.ts`: template, binding, and editor types.
- Modify `ReportWeb/src/api.ts`: template GET/PUT methods; keep execute and export APIs available but do not expose export UI.
- Create `ReportWeb/src/templateBinding.ts`: pure placeholder scanner and error model.
- Create `ReportWeb/src/templateBinding.test.ts`: scanner tests.
- Create `ReportWeb/src/templatePreview.ts`: pure preview plan construction.
- Create `ReportWeb/src/templatePreview.test.ts`: parameter replacement and row plan tests.
- Create `ReportWeb/src/UniverTemplateHost.tsx`: one Univer runtime, design editing, token insertion, preview execution, and workbook replacement.
- Create `ReportWeb/src/TemplateFieldPanel.tsx`: clickable parameter/detail fields.
- Rewrite `ReportWeb/src/App.tsx`: editor mode, preview mode, save, paging, errors, and unsaved state.
- Modify `ReportWeb/src/App.css`: compact editor layout and responsive states.
- Delete `ReportWeb/src/ReportSheet.tsx`: fixed result renderer is superseded by the editor host.
- Delete `ReportWeb/src/reportWorkbook.ts`: hard-coded result workbook is superseded by the database seed and binding pipeline.
- Delete `ReportWeb/src/reportWorkbook.test.ts`: replaced by binding and preview tests.
- Modify `docs/validation/2026-07-14-core-validation-results.md`: record the corrected edit-to-preview evidence.

## Task 1: Server-Side Binding Contracts And Validation

**Files:**

- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateContracts.cs`
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateValidator.cs`
- Create: `Admin.NET/HD.ReportCenter.Tests/PocTemplateValidatorTests.cs`

- [ ] **Step 1: Write failing validator tests**

Create tests with a minimal `JObject` workbook. The valid case must include embedded parameter tokens, four detail tokens on one row, and two formula cells:

```csharp
[Fact]
public void Scan_ReturnsCanonicalBindingsForParametersAndOneDetailRow()
{
    var snapshot = JObject.Parse("""
    {
      "sheets": {
        "report-sheet": {
          "cellData": {
            "1": { "0": { "v": "医院：{{params.hospitalId}}" } },
            "5": {
              "0": { "v": "{{detail.sequence}}" },
              "1": { "v": "{{detail.departmentName}}" },
              "2": { "v": "{{detail.outpatientCount}}" },
              "3": { "v": "{{detail.dischargeCount}}" },
              "4": { "f": "=C6+D6" },
              "5": { "f": "=AVERAGE(C6:D6)" }
            }
          },
          "mergeData": []
        }
      }
    }
    """);

    var binding = PocTemplateValidator.Scan(snapshot.ToString(Formatting.None));

    Assert.Single(binding.ParameterBindings);
    Assert.Equal("hospitalId", binding.ParameterBindings[0].Code);
    Assert.NotNull(binding.DetailBinding);
    Assert.Equal(5, binding.DetailBinding!.TemplateRow);
    Assert.Equal(4, binding.DetailBinding.Fields.Count);
}
```

Add separate tests asserting `PocTemplateValidationException` for an unknown field, a detail token mixed with text, two detail rows, a merge touching the detail row, malformed braces, and a snapshot over 4 MiB.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore --filter PocTemplateValidatorTests
```

Expected: compilation fails because `PocTemplateValidator` and its records do not exist.

- [ ] **Step 3: Add stable binding records**

Define records with zero-based coordinates:

```csharp
public sealed record PocParameterBinding(string Code, string SheetId, int Row, int Column);
public sealed record PocDetailFieldBinding(string Code, int Column);
public sealed record PocDetailBinding(
    string DatasetCode,
    string SheetId,
    int TemplateRow,
    IReadOnlyList<PocDetailFieldBinding> Fields);
public sealed record PocTemplateBinding(
    int Version,
    IReadOnlyList<PocParameterBinding> ParameterBindings,
    PocDetailBinding? DetailBinding);

public sealed class PocTemplateValidationException(string message) : Exception(message);
```

- [ ] **Step 4: Implement canonical snapshot scanning**

Implement `PocTemplateValidator.Scan(string snapshotJson)` with these exact rules:

```csharp
private static readonly Regex TokenPattern = new(
    @"\{\{\s*(params|detail)\.([A-Za-z][A-Za-z0-9]*)\s*\}\}",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

private static readonly HashSet<string> ParameterCodes =
    PocReportCatalog.GetDefinition().Parameters.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);

private static readonly HashSet<string> DetailCodes =
    PocReportCatalog.GetDefinition().Columns
        .Where(x => !string.Equals(x.DataType, "formula", StringComparison.OrdinalIgnoreCase))
        .Select(x => x.Code)
        .ToHashSet(StringComparer.Ordinal);
```

Reject input longer than `4 * 1024 * 1024` characters before parsing. Iterate `sheets -> sheetId -> cellData -> row -> column -> v`, scan only string values, require every `detail` match to equal the trimmed full cell value, and collect all detail matches on one `(sheetId,row)`. Inspect `mergeData`; reject any range where `startRow <= templateRow <= endRow`. Sort parameter bindings by sheet, row, column, and code; sort detail fields by column; return `Version = 1` and `DatasetCode = "detail"`. After removing all valid tokens from a string, reject leftover `{{` or `}}` as malformed syntax.

- [ ] **Step 5: Run validator tests**

Run the same filtered command. Expected: all `PocTemplateValidatorTests` pass.

- [ ] **Step 6: Commit backend validation**

```powershell
git add Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateContracts.cs Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocTemplateValidator.cs Admin.NET/HD.ReportCenter.Tests/PocTemplateValidatorTests.cs
git commit -m "feat: validate editable report templates"
```

## Task 2: Database-Backed Template GET And PUT

**Files:**

- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportTemplate.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateStore.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateService.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/Seeds/dept-workload.json`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/GlobalUsings.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/HD.ReportCenter.Application.csproj`
- Create: `Admin.NET/HD.ReportCenter.Tests/PocTemplateStoreTests.cs`

- [ ] **Step 1: Write failing SQLite store tests**

Create an in-memory SqlSugar client, initialize only the POC table, and verify first save, readback, revision increment, and stale-save rejection:

```csharp
private static SqlSugarClient CreateDatabase()
{
    var db = new SqlSugarClient(new ConnectionConfig
    {
        ConnectionString = "Data Source=:memory:",
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = false,
        InitKeyType = InitKeyType.Attribute
    });
    db.Ado.Open();
    db.CodeFirst.InitTables<PocReportTemplate>();
    return db;
}

[Fact]
public async Task SaveAsync_PersistsAndRejectsStaleRevision()
{
    using var db = CreateDatabase();
    var store = new PocTemplateStore(db);
    var first = await store.SaveAsync("dept-workload", "{\"sheets\":{}}", "{\"version\":1}", 0);
    var second = await store.SaveAsync("dept-workload", "{\"sheets\":{}}", "{\"version\":1}", first.Revision);

    Assert.Equal(2, second.Revision);
    await Assert.ThrowsAsync<PocTemplateRevisionConflictException>(() =>
        store.SaveAsync("dept-workload", "{\"sheets\":{}}", "{\"version\":1}", first.Revision));
}
```

- [ ] **Step 2: Run store tests and confirm failure**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore --filter PocTemplateStoreTests
```

Expected: compilation fails because the entity and store do not exist.

- [ ] **Step 3: Add the code-first entity**

Use the existing Admin.NET table conventions:

```csharp
[SugarTable("RcPocReportTemplate", "报表中心模板核心验证表")]
[SugarIndex("uk_RcPocReportTemplate_ReportCode", nameof(ReportCode), OrderByType.Asc, IsUnique = true)]
[SysTable]
public sealed class PocReportTemplate : EntityBase
{
    [SugarColumn(ColumnDescription = "报表编码", Length = 64)]
    public string ReportCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "Univer模板快照", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string SnapshotJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "模板绑定", ColumnDataType = StaticConfig.CodeFirst_BigString)]
    public string BindingJson { get; set; } = string.Empty;

    [SugarColumn(ColumnDescription = "修订号")]
    public int Revision { get; set; }
}
```

Add these global imports:

```csharp
global using Admin.NET.Core;
global using Furion.DependencyInjection;
global using SqlSugar;
```

- [ ] **Step 4: Implement atomic revision persistence**

`PocTemplateStore : ITransient` accepts `ISqlSugarClient`. `GetAsync(reportCode)` queries the table. `SaveAsync` inserts revision 1 only when `expectedRevision == 0`; updates with this predicate:

```csharp
public sealed record PocStoredTemplate(
    string ReportCode,
    string SnapshotJson,
    string BindingJson,
    int Revision);

public sealed class PocTemplateRevisionConflictException : Exception
{
    public PocTemplateRevisionConflictException()
        : base("Template revision is stale.")
    {
    }
}

var affected = await _db.Updateable<PocReportTemplate>()
    .SetColumns(x => x.SnapshotJson == snapshotJson)
    .SetColumns(x => x.BindingJson == bindingJson)
    .SetColumns(x => x.Revision == expectedRevision + 1)
    .SetColumns(x => x.UpdateTime == DateTime.Now)
    .Where(x => x.ReportCode == reportCode && x.Revision == expectedRevision)
    .ExecuteCommandAsync();

if (affected != 1)
{
    throw new PocTemplateRevisionConflictException();
}
```

For insert, set `ReportCode`, both JSON strings, `Revision = 1`, and `CreateTime = DateTime.Now`; Admin.NET supplies the snowflake ID. Catch a duplicate-key race and translate it to `PocTemplateRevisionConflictException`.

- [ ] **Step 5: Add the embedded seed template**

Create one workbook named `科室工作量统计` with sheet ID `report-sheet`. Preserve the current title/header styles, merges, widths, row heights, and formulas, but use these exact design cells:

| Cell | Content |
|---|---|
| A1 | `科室工作量统计（可编辑模板）` |
| A2 | `医院：{{params.hospitalId}}` |
| C2 | `科室：{{params.deptId}}` |
| E2 | `人员：{{params.personId}}` |
| A3 | `统计期间：{{params.startDate}} 至 {{params.endDate}}` |
| A5:F5 | `序号`、`科室`、`门诊人次`、`出院人次`、`合计`、`平均值` |
| A6 | `{{detail.sequence}}` |
| B6 | `{{detail.departmentName}}` |
| C6 | `{{detail.outpatientCount}}` |
| D6 | `{{detail.dischargeCount}}` |
| E6 | `=C6+D6` |
| F6 | `=AVERAGE(C6:D6)` |

The detail row has no merge. Set `rowCount` to 50 and retain number/formula styles from the current fixed workbook. Embed it:

```xml
<ItemGroup>
  <EmbeddedResource Include="CoreValidation\Seeds\dept-workload.json" />
</ItemGroup>
```

- [ ] **Step 6: Implement service DTOs and endpoints**

Define JSON-object DTOs in `PocTemplateService.cs`:

```csharp
public sealed record PocTemplateDocument(
    string ReportCode,
    JObject Snapshot,
    PocTemplateBinding Binding,
    int Revision);

public sealed record PocSaveTemplateRequest(
    JObject Snapshot,
    PocTemplateBinding Binding,
    int Revision);
```

`GetAsync` returns a database record or embedded seed with revision 0. In both paths call `PocTemplateValidator.Scan(snapshotJson)` and compare the canonical binding with stored/input binding using normalized `JsonConvert.SerializeObject`; a mismatch is `TEMPLATE_BINDING_MISMATCH`. `SaveAsync` scans the submitted snapshot, rejects a noncanonical binding, persists normalized compact JSON, and returns the saved document.

Add to `PocReportApi`:

```csharp
[HttpGet("/api/report-center/poc/{reportCode}/template")]
public async Task<PocTemplateDocument> Template([FromRoute] string reportCode)
{
    EnsureReportExists(reportCode);
    return await _templateService.GetAsync(reportCode);
}

[HttpPut("/api/report-center/poc/{reportCode}/template")]
public async Task<PocTemplateDocument> SaveTemplate(
    [FromRoute] string reportCode,
    [FromBody] PocSaveTemplateRequest request)
{
    EnsureReportExists(reportCode);
    try
    {
        return await _templateService.SaveAsync(reportCode, request);
    }
    catch (PocTemplateRevisionConflictException)
    {
        throw Oops.Oh("TEMPLATE_REVISION_CONFLICT").StatusCode(409);
    }
}
```

Inject `PocTemplateService` through the controller constructor. Keep all existing execute/export endpoints unchanged.

- [ ] **Step 7: Run backend tests and full host build**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore
dotnet build Admin.NET/Admin.NET.Web.Entry/Admin.NET.Web.Entry.csproj -c Release --no-restore
```

Expected: all report-center tests pass; host build completes without new warnings or errors.

- [ ] **Step 8: Commit database template API**

```powershell
git add Admin.NET/HD.ReportCenter.Application Admin.NET/HD.ReportCenter.Tests/PocTemplateStoreTests.cs
git commit -m "feat: persist editable report templates"
```

## Task 3: Frontend Template Types And Placeholder Scanner

**Files:**

- Modify: `ReportWeb/src/types.ts`
- Modify: `ReportWeb/src/api.ts`
- Create: `ReportWeb/src/templateBinding.ts`
- Create: `ReportWeb/src/templateBinding.test.ts`

- [ ] **Step 1: Write failing scanner tests**

Use a typed minimal workbook fixture. Verify embedded parameter replacement positions, the one detail row, sorted field columns, and validation errors:

```ts
it('scans embedded parameters and one typed detail row', () => {
  const binding = scanTemplateBindings(workbook, definition);

  expect(binding.parameterBindings).toEqual([
    { code: 'hospitalId', sheetId: 'report-sheet', row: 1, column: 0 },
  ]);
  expect(binding.detailBinding).toEqual({
    datasetCode: 'detail',
    sheetId: 'report-sheet',
    templateRow: 5,
    fields: [
      { code: 'sequence', column: 0 },
      { code: 'departmentName', column: 1 },
    ],
  });
});
```

Add tests for unknown parameter/detail code, detail mixed with text, two detail rows, a detail-row merge, malformed braces, and immutability of the input workbook.

- [ ] **Step 2: Run the scanner test and confirm failure**

Run:

```powershell
pnpm --dir ReportWeb test -- templateBinding.test.ts
```

Expected: test suite fails because the scanner module does not exist.

- [ ] **Step 3: Add frontend contract types**

Add `IWorkbookData` as a type-only import and define:

```ts
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
```

Add `template()` and `saveTemplate(document)` to `reportApi`, using GET/PUT `${basePath}/template`. PUT sends only `snapshot`, `binding`, and `revision`.

- [ ] **Step 4: Implement the scanner**

Use the same token regex and rules as the backend. Export `TemplateBindingError` with `sheetId`, `row`, `column`, and a formatted A1 cell address. The public signature is:

```ts
export function scanTemplateBindings(
  snapshot: IWorkbookData,
  definition: ReportDefinition,
): TemplateBinding;
```

Read sparse `cellData` without normalizing or mutating it. Parameter codes come from `definition.parameters`; detail codes come from non-formula `definition.columns`. Return deterministic sorted arrays so saved JSON is stable.

- [ ] **Step 5: Run scanner tests and frontend build**

```powershell
pnpm --dir ReportWeb test -- templateBinding.test.ts
pnpm --dir ReportWeb build
```

Expected: scanner tests pass; TypeScript and Vite build pass.

- [ ] **Step 6: Commit frontend binding contracts**

```powershell
git add ReportWeb/src/types.ts ReportWeb/src/api.ts ReportWeb/src/templateBinding.ts ReportWeb/src/templateBinding.test.ts
git commit -m "feat: scan report template bindings"
```

## Task 4: Pure Preview Plan Construction

**Files:**

- Create: `ReportWeb/src/templatePreview.ts`
- Create: `ReportWeb/src/templatePreview.test.ts`

- [ ] **Step 1: Write failing preview-plan tests**

Cover parameter interpolation and typed detail writes:

```ts
it('plans parameter replacements and twenty detail rows without mutating the draft', () => {
  const draft = structuredClone(workbook);
  const plan = createTemplatePreviewPlan(workbook, binding, result);

  expect(plan.parameterWrites[0]).toEqual({
    sheetId: 'report-sheet', row: 1, column: 0, value: '医院：H01',
  });
  expect(plan.detail?.insertCount).toBe(19);
  expect(plan.detail?.rows[1].values).toEqual([
    { column: 0, value: 2 },
    { column: 1, value: 'D02-02' },
  ]);
  expect(workbook).toEqual(draft);
});
```

Add a test where the result has zero rows: `insertCount` is 0 and the one template detail row receives `null` for each bound field. Add a test for a parameter cell below the detail row; the plan marks it for writing before row insertion.

- [ ] **Step 2: Run and confirm failure**

```powershell
pnpm --dir ReportWeb test -- templatePreview.test.ts
```

Expected: module-not-found failure.

- [ ] **Step 3: Implement the preview plan**

Define:

```ts
export interface CellWrite {
  sheetId: string;
  row: number;
  column: number;
  value: string | number | boolean | null;
}

export interface DetailRowPlan {
  row: number;
  values: Array<{ column: number; value: string | number | null }>;
}

export interface TemplatePreviewPlan {
  parameterWrites: CellWrite[];
  detail: null | {
    sheetId: string;
    templateRow: number;
    insertCount: number;
    rows: DetailRowPlan[];
  };
}
```

`createTemplatePreviewPlan(snapshot, binding, result)` reads each original parameter cell string and replaces all `{{params.code}}` tokens from `result.parameters`. For detail values, index each `WorkloadRow` by the binding code without stringifying numbers. It never modifies the workbook and does not rewrite formulas.

- [ ] **Step 4: Run all frontend unit tests**

```powershell
pnpm --dir ReportWeb test
```

Expected: all binding and preview tests pass, including existing tests until their fixed-workbook files are removed in Task 6.

- [ ] **Step 5: Commit preview planning**

```powershell
git add ReportWeb/src/templatePreview.ts ReportWeb/src/templatePreview.test.ts
git commit -m "feat: plan bound template previews"
```

## Task 5: Univer Template Host And Formula-Copy Validation

**Files:**

- Create: `ReportWeb/src/UniverTemplateHost.tsx`

- [ ] **Step 1: Define the imperative host boundary**

Export this interface so the page never reaches into Univer directly:

```ts
export interface UniverTemplateHandle {
  saveDraft(): IWorkbookData;
  loadDesign(snapshot: IWorkbookData): void;
  insertBinding(token: string, kind: 'parameter' | 'detail'): void;
  renderPreview(snapshot: IWorkbookData, plan: TemplatePreviewPlan): Promise<number>;
}
```

Props include `initialSnapshot`, `mode`, `onChanged`, `onRendered`, and `onError`.

- [ ] **Step 2: Create one long-lived Univer runtime**

Move the setup from `ReportSheet.tsx`, but enable the editing controls:

```ts
UniverSheetsCorePreset({
  container: containerRef.current,
  workerURL: worker,
  header: true,
  toolbar: true,
  footer: {
    sheetBar: true,
    statisticBar: false,
    menus: false,
    zoomSlider: true,
  },
  formulaBar: true,
  contextMenu: true,
  disableAutoFocus: true,
})
```

Keep the resize synchronization already proven by `ReportSheet`. Create runtime and Worker once. To replace a design/preview workbook, call `univerAPI.disposeUnit(activeWorkbook.getId())` then `createWorkbook(structuredClone(snapshot))`; do not dispose the runtime or Worker until component unmount.

- [ ] **Step 3: Implement design snapshot and field insertion**

`saveDraft()` returns `activeWorkbook.save()`. `loadDesign()` replaces the workbook and calls `setEditable(true)`. `insertBinding` gets `activeWorkbook.getActiveCell()`:

- Parameter: empty cell becomes the token; a string cell appends the token; a formula/non-string value throws a Chinese error.
- Detail: replace the cell with the token.
- After writing, call `onChanged()`.

Subscribe to `activeWorkbook.onCommandExecuted`; while design mode is active and the host is not applying an internal operation, mark the page dirty. False-positive command events are acceptable for this POC; never serialize the whole workbook on every command.

- [ ] **Step 4: Apply preview with Univer row operations**

`renderPreview` must perform operations in this order:

1. Replace the active workbook with a clone of the untouched draft and keep it editable during internal commands.
2. Apply every `parameterWrite` before inserting rows, so parameter cells below the detail row move with sheet insertion.
3. If `insertCount > 0`, call `worksheet.insertRows(templateRow + 1, insertCount)`.
4. Read source row height with `getRowHeight(templateRow)` and apply it to the whole detail range using `setRowHeights`.
5. Copy the source row across the target range with:

```ts
const source = worksheet.getRange(templateRow, 0, 1, worksheet.getMaxColumns());
const target = worksheet.getRange(
  templateRow,
  0,
  Math.max(1, plan.detail.rows.length),
  worksheet.getMaxColumns(),
);
const copied = await source.autoFill(target, 'COPY');
if (!copied) throw new Error('Univer 未能复制明细模板行');
```

6. Write only bound detail cells, preserving formula cells and styles.
7. Call `workbook.setEditable(false)` and report double-`requestAnimationFrame` render time.

Do not parse or rewrite formula strings. The browser acceptance test must prove `E7` becomes `=C7+D7`; failure means the OSS capability is not validated.

- [ ] **Step 5: Build after the Univer integration**

```powershell
pnpm --dir ReportWeb build
```

Expected: strict TypeScript build passes against Univer 0.25.1 public Facade APIs.

- [ ] **Step 6: Commit the editor host**

```powershell
git add ReportWeb/src/UniverTemplateHost.tsx
git commit -m "feat: host editable Univer templates"
```

## Task 6: Editor Page, Field Panel, Save, Preview, And Paging

**Files:**

- Create: `ReportWeb/src/TemplateFieldPanel.tsx`
- Rewrite: `ReportWeb/src/App.tsx`
- Modify: `ReportWeb/src/App.css`
- Delete: `ReportWeb/src/ReportSheet.tsx`
- Delete: `ReportWeb/src/reportWorkbook.ts`
- Delete: `ReportWeb/src/reportWorkbook.test.ts`

- [ ] **Step 1: Implement the field panel**

Render two compact sections from `ReportDefinition`: visible parameters and non-formula columns. Each field is a button with its display name and token in a tooltip. Clicking calls:

```ts
onInsert(`{{params.${parameter.code}}}`, 'parameter');
onInsert(`{{detail.${column.code}}}`, 'detail');
```

Use Lucide `Braces` and `Columns3` icons; do not add chart or collaboration controls.

- [ ] **Step 2: Replace App initialization with definition plus template loading**

Load both resources together:

```ts
const [nextDefinition, nextTemplate] = await Promise.all([
  reportApi.definition(),
  reportApi.template(),
]);
```

Initialize URL parameters and backend date defaults as today. Store `templateDocument`, `draftSnapshot`, `mode`, `dirty`, `saving`, `previewing`, `queryDuration`, and `renderDuration`. Do not execute report data during initial design load.

- [ ] **Step 3: Implement mode transitions**

Entering preview:

```ts
const snapshot = editorRef.current.saveDraft();
const binding = scanTemplateBindings(snapshot, definition);
const result = await reportApi.execute(request);
const plan = createTemplatePreviewPlan(snapshot, binding, result);
setDraftSnapshot(snapshot);
await editorRef.current.renderPreview(snapshot, plan);
setResult(result);
setMode('preview');
```

Returning to design calls `loadDesign(draftSnapshot)` and sets mode to `design`; it never saves the preview workbook. Paging in preview calls execute with the new page and reruns `renderPreview` from the same `draftSnapshot`.

- [ ] **Step 4: Implement save and conflict behavior**

Save only in design mode:

```ts
const snapshot = editorRef.current.saveDraft();
const binding = scanTemplateBindings(snapshot, definition);
const saved = await reportApi.saveTemplate({
  ...templateDocument,
  snapshot,
  binding,
});
setTemplateDocument(saved);
setDraftSnapshot(saved.snapshot);
setDirty(false);
```

Display server `TEMPLATE_REVISION_CONFLICT` as “模板已被其它页面修改，请刷新后重新编辑”，and do not clear the current draft. Add `beforeunload` only while `dirty` is true.

- [ ] **Step 5: Build the compact editor UI**

Top command bar:

- “模板设计 / 数据预览” segmented control using `Pencil` and `Eye`.
- `Save` icon button with text “保存模板” only in design mode.
- Unsaved marker and query/render timings.

Keep the existing five query inputs. Remove normal export, large export, and export-job strips from the page for this round. In design mode use a stable `220px minmax(0,1fr)` workspace grid with the field panel and sheet; in preview mode use only the sheet. Keep cards at 4px radius, no nested cards, no marketing text.

On widths below 720px, make the field panel a horizontal scroll strip above the sheet and keep all button text inside its bounds. Preserve a minimum 540px sheet height.

- [ ] **Step 6: Delete the fixed renderer and run frontend verification**

```powershell
pnpm --dir ReportWeb test
pnpm --dir ReportWeb build
```

Expected: all new Vitest suites pass; the production build passes; no imports reference deleted fixed-workbook files.

- [ ] **Step 7: Commit the complete edit/preview page**

```powershell
git add ReportWeb/src
git commit -m "feat: add editable template preview workflow"
```

## Task 7: End-To-End Technical Validation And Evidence

**Files:**

- Modify: `docs/validation/2026-07-14-core-validation-results.md`

- [ ] **Step 1: Restart only the validation services**

Keep the original port 5005 process untouched. Rebuild/restart the validation backend on 5006 and frontend on 5174 using the existing `.codex-run` process/log convention. Verify:

```powershell
Invoke-RestMethod http://localhost:5006/api/report-center/poc/dept-workload/template
Invoke-WebRequest http://localhost:5174/
```

Expected: template API returns revision 0 or the persisted revision; frontend returns HTTP 200.

- [ ] **Step 2: Verify the real edit-to-preview loop in Playwright**

Open:

```text
http://localhost:5174/?hospitalId=H01&deptId=D02&personId=P03
```

Perform and record all acceptance checks:

1. Design mode shows Univer toolbar, formula bar, field panel, and seed placeholders.
2. Change A1 text and its background color.
3. Select a blank parameter cell and click the hospital field.
4. Confirm the written token is `{{params.hospitalId}}`.
5. Enter preview without saving.
6. Confirm resolved H01/D02/P03 and 20 real backend rows are visible.
7. Inspect formulas: E6 is `=C6+D6`, E7 is `=C7+D7`; displayed values equal C+D.
8. Return to design and confirm A6:D6 are placeholders, not preview data.
9. Save, refresh, and confirm the title/style/binding survive with revision incremented.
10. Change a second browser/page from an old revision and confirm HTTP 409 does not overwrite the first save.

Capture desktop and 390px-wide mobile screenshots under ignored `output/playwright/`. Confirm console error count is zero and the canvas has non-white pixels.

- [ ] **Step 3: Measure template-switch performance**

Record at least five design-to-preview runs after warm-up. Report median and P95 for frontend binding/row application plus formula visibility, excluding backend query. Acceptance is plan construction below 50ms and preview rendering below 300ms on the current machine.

- [ ] **Step 4: Run the final verification matrix**

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore
dotnet build Admin.NET/Admin.NET.Web.Entry/Admin.NET.Web.Entry.csproj -c Release --no-restore
pnpm --dir ReportWeb test
pnpm --dir ReportWeb build
git diff --check
git status --short
```

Expected: all tests/builds pass, `git diff --check` is clean, and only the validation report remains intentionally modified.

- [ ] **Step 5: Update validation evidence**

Add a new section “可编辑模板闭环” to the validation report with:

- Template API revision and database table evidence.
- Placeholder scan and invalid-template test counts.
- Formula references from E6/E7 and displayed results.
- Save/reload and conflict behavior.
- Desktop/mobile screenshot paths and console status.
- Median/P95 preview timings.
- An explicit conclusion that export still uses the old hard-coded writer and is intentionally hidden until it consumes the saved template.

- [ ] **Step 6: Commit validation evidence**

```powershell
git add docs/validation/2026-07-14-core-validation-results.md
git commit -m "docs: validate editable report templates"
```

## Completion Gate

Do not declare the work complete unless all of the following are true:

- A user can edit title/style/formula and bind fields inside Univer.
- Unsaved draft preview uses backend parameters and 20 backend rows.
- Univer, not custom string replacement, shifts `=C6+D6` to `=C7+D7`.
- Returning to design restores placeholders.
- Database save/reload preserves snapshot and canonical bindings.
- A stale revision cannot overwrite a newer template.
- Backend tests/build, frontend tests/build, desktop/mobile browser checks, and console checks pass.
