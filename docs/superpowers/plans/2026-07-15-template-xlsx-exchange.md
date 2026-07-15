# Template XLSX Exchange Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a database-configured `.xlsx` template import/export round trip for the current Univer `0.25.1` editor and prove it with the stored `dept-workload` template.

**Architecture:** A single SqlSugar table stores one conversion rule per row. The application layer selects and seeds the current rule set, while a focused ReportEngine converter uses ClosedXML plus those rules to map between `.xlsx` and Univer `IWorkbookData` JSON. The React page uploads or downloads the current design draft through two narrow API methods and never treats template exchange as data-result export.

**Tech Stack:** .NET 8, Furion dynamic APIs, SqlSugar, SQLite/SQL Server, ClosedXML 0.105.0, Newtonsoft.Json, React 19, TypeScript, Univer 0.25.1, Vitest, xUnit, Playwright/browser QA.

---

## File Structure

- Create `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocExchangeContracts.cs`: immutable exchange rules, diagnostics, limits, and result contracts.
- Create `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocXlsxExchangeConverter.cs`: ClosedXML import/export and configurable path/enum/scale mapping.
- Modify `Admin.NET/HD.ReportCenter.ReportEngine/HD.ReportCenter.ReportEngine.csproj`: add ClosedXML 0.105.0.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMapping.cs`: the single SqlSugar mapping entity.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMappingDefaults.cs`: deterministic Univer 0.25.1 rule rows.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMappingStore.cs`: seed-on-empty, latest-version selection, and duplicate validation.
- Create `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateExchangeService.cs`: file limits, rule loading, template validation, and converter orchestration.
- Modify `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`: multipart import and snapshot export endpoints.
- Create `Admin.NET/HD.ReportCenter.Tests/PocExchangeMappingStoreTests.cs`: SQLite persistence and rule selection tests.
- Create `Admin.NET/HD.ReportCenter.Tests/PocXlsxExchangeConverterTests.cs`: golden semantic round-trip tests.
- Create `Admin.NET/HD.ReportCenter.Tests/PocTemplateExchangeServiceTests.cs`: service limits and current template tests.
- Modify `ReportWeb/src/types.ts`: import response and diagnostic contracts.
- Modify `ReportWeb/src/api.ts`: multipart import and JSON snapshot download.
- Modify `ReportWeb/src/api.test.ts`: request/response/download tests.
- Modify `ReportWeb/src/App.tsx`: import/export commands, busy states, dirty confirmation, and diagnostics.
- Modify `ReportWeb/src/app.css`: stable command-button and diagnostic layout.
- Modify `docs/validation/2026-07-15-editable-template-operation-guide.md`: user instructions and supported subset.

### Task 1: Database Mapping Rule Set

**Files:**
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocExchangeContracts.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMapping.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMappingDefaults.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMappingStore.cs`
- Test: `Admin.NET/HD.ReportCenter.Tests/PocExchangeMappingStoreTests.cs`

- [ ] **Step 1: Write failing mapping-store tests**

Create SQLite tests that initialize only `PocExchangeMapping`, call `GetCurrentAsync("0.25.1")` twice, and assert that the first call inserts a non-empty rule set while the second call does not duplicate rows. Add tests that select the highest `MappingVersion`, ignore disabled rules, and reject duplicate `RuleCode` values.

```csharp
[Fact]
public async Task Empty_table_is_seeded_once_and_returns_current_rules()
{
    var first = await _store.GetCurrentAsync("0.25.1");
    var second = await _store.GetCurrentAsync("0.25.1");

    Assert.NotEmpty(first.Rules);
    Assert.Equal(first.MappingVersion, second.MappingVersion);
    Assert.Equal(first.Rules.Count, await _database.Queryable<PocExchangeMapping>().CountAsync());
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter PocExchangeMappingStoreTests`

Expected: build fails because `PocExchangeMappingStore` and its contracts do not exist.

- [ ] **Step 3: Add immutable engine contracts and the single table entity**

Define the engine-facing rule shape without referencing the application project:

```csharp
public sealed record PocExchangeRule(
    string RuleCode,
    string Direction,
    string Category,
    string SourcePath,
    string TargetPath,
    string TransformType,
    string? SourceValue,
    string? TargetValue,
    decimal? Factor,
    bool IsRequired,
    int Sort);

public sealed record PocExchangeRuleSet(
    string UniverVersion,
    int MappingVersion,
    IReadOnlyList<PocExchangeRule> Rules);
```

Create one `[SysTable]` entity with the exact design fields and a unique index over `UniverVersion`, `MappingVersion`, and `RuleCode`.

- [ ] **Step 4: Seed complete current-version rules**

Create deterministic import and export rules for `v`, `t`, `f`, `s`, `ff`, `fs`, `bl`, `it`, `ul.s`, `st.s`, `cl.rgb`, `bg.rgb`, `n.pattern`, `ht`, `vt`, `tb`, `tr.a`, `bd.t`, `bd.r`, `bd.b`, `bd.l`, row height, column width, merge ranges, hidden rows/columns, and freeze panes. Use stable codes such as `xlsx-to-univer-cell-formula` and `univer-to-xlsx-style-font-name` so every diagnostic can identify a database row.

- [ ] **Step 5: Implement seed-on-empty and current-version selection**

`GetCurrentAsync` must query the maximum mapping version for the requested Univer version, seed version 1 only when no rows exist, order enabled rules by `Sort` then `RuleCode`, and validate required fields and duplicate codes before returning.

- [ ] **Step 6: Run mapping tests and commit**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter PocExchangeMappingStoreTests`

Expected: all mapping-store tests pass.

Commit: `feat: add database exchange mappings`

### Task 2: Univer-to-XLSX Converter

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/HD.ReportCenter.ReportEngine.csproj`
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocXlsxExchangeConverter.cs`
- Test: `Admin.NET/HD.ReportCenter.Tests/PocXlsxExchangeConverterTests.cs`

- [ ] **Step 1: Add ClosedXML and a failing export test**

Add `<PackageReference Include="ClosedXML" Version="0.105.0" />`. The test loads the embedded seed snapshot, exports it, reopens the stream with `XLWorkbook`, and asserts workbook name, sheet name, `E6` formula, `A1:F1` merge, row height, column width, bold title, fill, border/alignment, and automatic calculation mode.

```csharp
using var workbook = new XLWorkbook(stream);
var sheet = workbook.Worksheet("科室工作量");
Assert.Equal("C6+D6", sheet.Cell("E6").FormulaA1);
Assert.Contains(sheet.MergedRanges, range => range.RangeAddress.ToString() == "A1:F1");
```

- [ ] **Step 2: Run the export test and verify failure**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "PocXlsxExchangeConverterTests.Export"`

Expected: build fails because the converter does not exist.

- [ ] **Step 3: Implement workbook, sheet, cell, and formula export**

Add:

```csharp
public sealed class PocXlsxExchangeConverter
{
    public void Export(JObject snapshot, PocExchangeRuleSet rules, Stream output);
}
```

Validate required rules up front. Iterate `sheetOrder`, then sparse `cellData`; write typed values and formulas without evaluating them. Strip one leading `=` before assigning `FormulaA1`, set automatic calculation, full calculation on load, and force full calculation.

- [ ] **Step 4: Implement style and layout export**

Resolve each cell `s` through the workbook `styles` dictionary and apply configured font, fill, borders, alignment, wrap, rotation, and number format mappings. Apply `mergeData`, `rowData`, `columnData`, hidden flags, and freeze settings. Ignore empty grid capacity outside sparse data.

- [ ] **Step 5: Run export tests and commit**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "PocXlsxExchangeConverterTests.Export"`

Expected: all export tests pass and the produced stream can be reopened by ClosedXML.

Commit: `feat: export Univer templates to xlsx`

### Task 3: XLSX-to-Univer Converter

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocExchangeContracts.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/Exchange/PocXlsxExchangeConverter.cs`
- Test: `Admin.NET/HD.ReportCenter.Tests/PocXlsxExchangeConverterTests.cs`

- [ ] **Step 1: Write failing import and round-trip tests**

Create an in-memory workbook containing two sheets, typed values, `{{params.hospitalId}}`, `{{detail.departmentName}}`, ordinary formulas, styles, merges, row height, column width, hidden rows/columns, and freeze panes. Assert the imported `JObject` contains Univer `0.25.1` paths and no numeric layout strings.

Add a semantic round-trip test that exports the current seed, imports it again, and compares values, formulas, style properties, merge coordinates, row heights, column widths, and freeze counts with explicit numeric tolerances.

- [ ] **Step 2: Run import tests and verify failure**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter "PocXlsxExchangeConverterTests.Import|PocXlsxExchangeConverterTests.Round_trip"`

Expected: tests fail because `Import` is missing.

- [ ] **Step 3: Implement typed sparse import**

Add:

```csharp
public PocXlsxImportResult Import(
    Stream input,
    PocExchangeRuleSet rules,
    string workbookName,
    PocXlsxExchangeLimits limits);
```

Build `IWorkbookData` as `JObject`, set application version to `0.25.1`, assign stable generated sheet IDs, and iterate only cells used by value, formula, or style. Preserve date cells as Excel serial numbers plus number formats instead of formatted text.

- [ ] **Step 4: Implement style deduplication and diagnostics**

Normalize supported styles into deterministic JSON, hash the normalized representation, and store each unique style once. Unsupported or lossy features add `PocExchangeDiagnostic` entries with `RuleCode`, sheet, cell address, and a Chinese message. Required mapping failures throw `PocExchangeConversionException`.

- [ ] **Step 5: Implement layout import and run tests**

Import merges, custom row heights, custom column widths, hidden flags, and freeze panes. Clamp `rowCount` and `columnCount` to reasonable minimums without materializing blank cells.

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter PocXlsxExchangeConverterTests`

Expected: all converter tests pass.

Commit: `feat: import xlsx templates into Univer`

### Task 4: Exchange Service and HTTP Endpoints

**Files:**
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateExchangeService.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`
- Test: `Admin.NET/HD.ReportCenter.Tests/PocTemplateExchangeServiceTests.cs`
- Modify: `Admin.NET/HD.ReportCenter.Tests/PocTemplateStoreTests.cs`

- [ ] **Step 1: Write failing service tests**

Test `.xlsx` extension validation, 10 MiB file limit, 200,000 used-cell limit, successful import returning the mapping version and canonical binding, export of the current stored template, and rejection of an export snapshot with invalid template tokens.

- [ ] **Step 2: Run service tests and verify failure**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj --filter PocTemplateExchangeServiceTests`

Expected: build fails because the service does not exist.

- [ ] **Step 3: Implement the application service**

Use constructor injection for `PocExchangeMappingStore` and `PocXlsxExchangeConverter`. Import must copy the upload to a bounded memory stream, invoke the converter, scan the returned snapshot with `PocTemplateValidator`, and return:

```csharp
public sealed record PocTemplateImportResult(
    JObject Snapshot,
    PocTemplateBinding Binding,
    string UniverVersion,
    int MappingVersion,
    IReadOnlyList<PocExchangeDiagnostic> Warnings);
```

Export must scan the submitted snapshot before conversion and return a positioned stream plus filename and mapping version.

- [ ] **Step 4: Add dynamic API endpoints and stable errors**

Add `POST template/import-xlsx` with `IFormFile` and `POST template/export-xlsx` with `JObject` request body. Convert file/limit/mapping/conversion exceptions to stable `TEMPLATE_XLSX_*` codes. Set `X-Univer-Version` and `X-Exchange-Mapping-Version` response headers on export.

- [ ] **Step 5: Run all .NET tests and commit**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj`

Expected: all existing and new tests pass.

Commit: `feat: expose template xlsx exchange APIs`

### Task 5: Frontend API Adapter

**Files:**
- Modify: `ReportWeb/src/types.ts`
- Modify: `ReportWeb/src/api.ts`
- Modify: `ReportWeb/src/api.test.ts`

- [ ] **Step 1: Write failing API tests**

Assert import posts a `FormData` containing the selected file without manually setting `Content-Type`, returns `TemplateImportResult`, and export posts the current snapshot as JSON then downloads the response filename.

- [ ] **Step 2: Run focused frontend tests and verify failure**

Run: `pnpm test -- api.test.ts`

Expected: tests fail because `importTemplateXlsx` and `exportTemplateXlsx` do not exist.

- [ ] **Step 3: Implement import/export API methods**

Add:

```ts
importTemplateXlsx(file: File): Promise<TemplateImportResult>;
exportTemplateXlsx(snapshot: IWorkbookData): Promise<void>;
```

Generalize `requestDownload` to accept a `RequestInit` and fallback filename while retaining existing normal/large export behavior.

- [ ] **Step 4: Run frontend API tests and commit**

Run: `pnpm test -- api.test.ts`

Expected: all API tests pass.

Commit: `feat: add template xlsx browser APIs`

### Task 6: Template Import/Export UI

**Files:**
- Modify: `ReportWeb/src/App.tsx`
- Modify: `ReportWeb/src/app.css`
- Test: `ReportWeb/src/api.test.ts`

- [ ] **Step 1: Add command state and hidden file input**

Add `importing` and `exporting` state, include them in `busy`, create a `.xlsx`-only hidden input, and use Lucide `FileUp` and `FileDown` icons. Keep stable button height and width so spinners do not shift the command bar.

- [ ] **Step 2: Implement export command**

In design mode, call `editor.saveDraft()` and pass the normalized snapshot to `reportApi.exportTemplateXlsx`. Do not change `dirty`, the database revision, or the active workbook.

- [ ] **Step 3: Implement import command**

If `dirty`, call `window.confirm('当前模板有未保存修改，导入将替换这些修改，是否继续？')`. On success, call `editor.loadDesign(result.snapshot)`, replace both draft refs/state, keep the current database revision, mark dirty, clear preview results, and show mapping version/warning count in a compact status line.

- [ ] **Step 4: Handle cancellation and repeated file selection**

No file selection must be a no-op. Always clear the input value after handling so selecting the same file again triggers `change`. Import failure must leave all draft refs and the active workbook unchanged.

- [ ] **Step 5: Run frontend tests/build and commit**

Run: `pnpm test`

Expected: all frontend tests pass.

Run: `pnpm build`

Expected: TypeScript and Vite build succeed.

Commit: `feat: add template xlsx exchange controls`

### Task 7: Current Template Round-Trip and Browser Verification

**Files:**
- Modify: `docs/validation/2026-07-15-editable-template-operation-guide.md`
- Test artifact: `.codex-run/editable-template-validation/template-exchange/*.xlsx` (ignored runtime output only)

- [ ] **Step 1: Rebuild and restart the .NET 8 backend**

Run the release build, stop only the process listening on port `5007`, and restart with the existing SQLite override pointing to `.codex-run/editable-template-validation/Admin.NET.poc.db`. Verify startup creates `RcPocExchangeMapping` and seeds current rules.

- [ ] **Step 2: Verify API round trip with the current stored template**

Fetch `dept-workload` revision 3, export its snapshot, save the downloaded bytes under the ignored runtime directory, import the same bytes, and compare the returned snapshot semantically. Confirm the mapping table contains one row per rule and no duplicate rule codes.

- [ ] **Step 3: Verify in a real browser**

At `http://localhost:5175`, export the current design, import that file, confirm the title, formulas, styles, merges, widths, heights, and freeze area, edit one cell, save, refresh, and enter data preview. Confirm the browser console has no new errors and the imported template remains editable.

- [ ] **Step 4: Update the operation guide**

Document the two buttons, replacement confirmation, supported `.xlsx` subset, warning behavior, local SQLite table, and the distinction between template exchange and data-result export.

- [ ] **Step 5: Run the complete verification suite and commit**

Run: `dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj`

Run: `pnpm test`

Run: `pnpm build`

Expected: all tests and builds pass.

Commit: `docs: verify template xlsx exchange`
