# Core Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one hard-coded department workload report that proves URL parameters, server defaults, template-defined paging, Univer Web Worker formulas, normal Excel export, and large streaming Excel export.

**Architecture:** Keep the proof isolated from production persistence. `HD.ReportCenter.ReportEngine` owns deterministic report data, paging, formula-copy metadata, and Open XML generation; `HD.ReportCenter.Application` exposes anonymous POC endpoints and an in-memory large-export job store; a standalone `ReportWeb` React/Vite app renders the report with Univer 0.25.1. The POC deliberately does not implement report CRUD, SQL execution, template import, or durable `RcExportJob` persistence.

**Tech Stack:** .NET 8, Admin.NET/Furion dynamic APIs, DocumentFormat.OpenXml 3.3.0, xUnit, React 19, Vite 8, TypeScript 6, Univer OSS 0.25.1, Web Worker, Playwright.

---

## Scope Boundaries

The POC must prove:

- `hospitalId`, `deptId`, and `personId` arrive from the URL and are echoed into the template.
- `startDate` defaults to the first day of the current server month and `endDate` defaults to today.
- The hard-coded template is `Paged` with 20 rows per page; the server controls page size.
- Formula columns are calculated by Univer in a Web Worker for the current page.
- Normal export keeps formula text and enables full calculation when Excel/WPS opens the file.
- Large export writes at least 100,000 rows with `OpenXmlWriter`, reports progress, supports cancel/download, and does not build a full row list.

The POC must not add database entities, a generic SQL editor, template upload/import, authentication modes, or a second formula engine.

### Task 1: Add report-engine tests and POC contracts

**Files:**

- Modify: `Admin.NET/Admin.NET.Test/Admin.NET.Test.csproj`
- Create: `Admin.NET/Admin.NET.Test/ReportCenter/PocReportCatalogTests.cs`
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportContracts.cs`
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocReportCatalog.cs`

- [ ] **Step 1: Reference the report engine from the existing test project**

Add this project reference beside the existing `Admin.NET.Core` reference:

```xml
<ProjectReference Include="..\HD.ReportCenter.ReportEngine\HD.ReportCenter.ReportEngine.csproj" />
```

- [ ] **Step 2: Write failing catalog tests**

Create tests that require a paged definition, server defaults, stable paging, and server-owned page size:

```csharp
using HD.ReportCenter.ReportEngine.CoreValidation;
using Xunit;

namespace Admin.NET.Test.ReportCenter;

public sealed class PocReportCatalogTests
{
    [Fact]
    public void GetDefinition_ReturnsTemplateOwnedPagingAndDefaults()
    {
        var now = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.FromHours(8));

        var definition = PocReportCatalog.GetDefinition(now);

        Assert.Equal("dept-workload", definition.Code);
        Assert.Equal(PocPreviewMode.Paged, definition.Preview.Mode);
        Assert.Equal(20, definition.Preview.PageSize);
        Assert.Equal("2026-07-01", definition.Parameters.Single(x => x.Code == "startDate").DefaultValue);
        Assert.Equal("2026-07-14", definition.Parameters.Single(x => x.Code == "endDate").DefaultValue);
    }

    [Fact]
    public void Execute_UsesConfiguredPageSizeAndStableRows()
    {
        var request = new PocExecuteRequest(2, "H01", "D02", "P03", "2026-07-01", "2026-07-14");

        var result = PocReportCatalog.Execute(request);

        Assert.Equal(2, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(20, result.Rows.Count);
        Assert.Equal(21, result.Rows[0].Sequence);
        Assert.True(result.HasMore);
    }
}
```

- [ ] **Step 3: Run tests and verify failure**

Run:

```powershell
dotnet test Admin.NET/Admin.NET.Test/Admin.NET.Test.csproj -c Release --no-restore --filter PocReportCatalogTests --nologo
```

Expected: compilation fails because `CoreValidation` contracts do not exist.

- [ ] **Step 4: Implement immutable contracts and the fixed catalog**

Define `PocPreviewMode`, `PocPreviewSettings`, `PocParameterDefinition`, `PocReportDefinition`, `PocExecuteRequest`, `PocWorkloadRow`, and `PocExecuteResult`. Implement `PocReportCatalog` with these fixed rules:

```csharp
public static class PocReportCatalog
{
    public const string ReportCode = "dept-workload";
    public const int PageSize = 20;
    public const int TotalRows = 137;

    public static PocReportDefinition GetDefinition(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return PocReportDefinition.Create(monthStart, today);
    }

    public static PocExecuteResult Execute(PocExecuteRequest request)
    {
        var page = Math.Max(1, request.Page);
        var rows = EnumerateRows(request).Skip((page - 1) * PageSize).Take(PageSize + 1).ToArray();
        return new PocExecuteResult(page, PageSize, rows.Length > PageSize, rows.Take(PageSize).ToArray());
    }

    public static IEnumerable<PocWorkloadRow> EnumerateRows(PocExecuteRequest request, int rowCount = TotalRows)
    {
        for (var index = 1; index <= rowCount; index++)
        {
            yield return PocWorkloadRow.Create(index, request);
        }
    }
}
```

- [ ] **Step 5: Run catalog tests**

Run the command from Step 3.

Expected: both tests pass.

- [ ] **Step 6: Commit catalog slice**

```powershell
git add Admin.NET/Admin.NET.Test/Admin.NET.Test.csproj Admin.NET/Admin.NET.Test/ReportCenter Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation
git commit -m "feat: add fixed report validation catalog"
```

### Task 2: Generate formula-preserving Excel in memory and by streaming

**Files:**

- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/HD.ReportCenter.ReportEngine.csproj`
- Create: `Admin.NET/HD.ReportCenter.ReportEngine/CoreValidation/PocExcelExporter.cs`
- Create: `Admin.NET/Admin.NET.Test/ReportCenter/PocExcelExporterTests.cs`

- [ ] **Step 1: Add Open XML SDK**

Add:

```xml
<ItemGroup>
  <PackageReference Include="DocumentFormat.OpenXml" Version="3.3.0" />
</ItemGroup>
```

- [ ] **Step 2: Write failing export tests**

Tests must open the generated package and assert the first data formula and workbook calculation properties:

```csharp
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using HD.ReportCenter.ReportEngine.CoreValidation;
using Xunit;

namespace Admin.NET.Test.ReportCenter;

public sealed class PocExcelExporterTests
{
    [Fact]
    public void Export_WritesFormulaAndForcesCalculationOnOpen()
    {
        var request = new PocExecuteRequest(1, "H01", "D02", "P03", "2026-07-01", "2026-07-14");
        using var stream = new MemoryStream();

        PocExcelExporter.Write(stream, PocReportCatalog.EnumerateRows(request, 3));
        stream.Position = 0;

        using var document = SpreadsheetDocument.Open(stream, false);
        var sheetData = document.WorkbookPart!.WorksheetParts.Single().Worksheet.GetFirstChild<SheetData>()!;
        Assert.Equal("C6+D6", sheetData.Elements<Row>().Single(x => x.RowIndex == 6).Elements<Cell>().Single(x => x.CellReference == "E6").CellFormula!.Text);
        Assert.Equal(CalculateModeValues.Auto, document.WorkbookPart.Workbook.CalculationProperties!.CalculationMode!.Value);
        Assert.True(document.WorkbookPart.Workbook.CalculationProperties.FullCalculationOnLoad!.Value);
        Assert.True(document.WorkbookPart.Workbook.CalculationProperties.ForceFullCalculation!.Value);
    }
}
```

- [ ] **Step 3: Run export tests and verify failure**

Run:

```powershell
dotnet test Admin.NET/Admin.NET.Test/Admin.NET.Test.csproj -c Release --filter PocExcelExporterTests --nologo
```

Expected: compilation fails because `PocExcelExporter` does not exist.

- [ ] **Step 4: Implement one forward-only writer**

`PocExcelExporter.Write(Stream, IEnumerable<PocWorkloadRow>, Action<long>? progress = null, CancellationToken cancellationToken = default)` must:

- create one workbook and worksheet with `OpenXmlWriter`;
- write title rows, parameter headers, column headers, and data starting at row 6;
- write formulas `C{row}+D{row}` and `IFERROR(E{row}/(C{row}+D{row}),0)` without cached values;
- set calculation mode to automatic with full calculation on load;
- invoke `progress` every 1,000 rows;
- check cancellation every row;
- leave the caller-owned stream open.

- [ ] **Step 5: Run export tests**

Run the command from Step 3.

Expected: all export tests pass.

- [ ] **Step 6: Commit export engine**

```powershell
git add Admin.NET/HD.ReportCenter.ReportEngine Admin.NET/Admin.NET.Test/ReportCenter/PocExcelExporterTests.cs
git commit -m "feat: stream formula-preserving Excel exports"
```

### Task 3: Expose hard-coded runtime and export APIs

**Files:**

- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportApi.cs`
- Create: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocLargeExportJobStore.cs`
- Modify: `Admin.NET/HD.ReportCenter.ReportEngine/ReportCenterEngine.cs`

- [ ] **Step 1: Add definition and execute endpoints**

Create an anonymous dynamic API with explicit routes:

```csharp
[AllowAnonymous]
[ApiDescriptionSettings("报表中心验证", Name = "ReportCenterPoc", Order = 901)]
public sealed class PocReportApi : IDynamicApiController
{
    [HttpGet("/api/report-center/poc/{reportCode}/definition")]
    public PocReportDefinition Definition(string reportCode) => PocReportCatalog.GetDefinition(DateTimeOffset.Now);

    [HttpPost("/api/report-center/poc/{reportCode}/execute")]
    public PocExecuteResult Execute(string reportCode, PocExecuteRequest request) => PocReportCatalog.Execute(request);
}
```

Reject report codes other than `dept-workload` with a stable `REPORT_NOT_FOUND` message.

- [ ] **Step 2: Add normal export endpoint**

Add `POST /api/report-center/poc/{reportCode}/exports/normal`. Generate all 137 fixed rows into a `MemoryStream`, reset position, and return `FileStreamResult` with the Open XML MIME type and a Chinese-safe filename.

- [ ] **Step 3: Add in-memory large-export jobs**

Implement a POC-only `ConcurrentDictionary<string, PocExportJob>` store. `Start` must create a random job number, write 100,000 rows to a unique file under `Path.GetTempPath()/hd-report-center-poc`, update progress, and capture cancellation or failure. Expose:

```text
POST /api/report-center/poc/{reportCode}/exports/large
GET  /api/report-center/poc/export-jobs/{jobNo}
POST /api/report-center/poc/export-jobs/{jobNo}/cancel
GET  /api/report-center/poc/export-jobs/{jobNo}/download
```

The download endpoint must return `FileStreamResult`; it must never call `File.ReadAllBytes`.

- [ ] **Step 4: Extend health capabilities**

Replace obsolete `html-print` with `paged-preview`, `univer-worker`, `formula-preserving-export`, and `streaming-export`.

- [ ] **Step 5: Build the application project**

Run:

```powershell
dotnet build Admin.NET/HD.ReportCenter.Application/HD.ReportCenter.Application.csproj -c Release --no-restore --nologo
```

Expected: 0 errors. Existing Admin.NET warnings are allowed and recorded separately.

- [ ] **Step 6: Commit runtime APIs**

```powershell
git add Admin.NET/HD.ReportCenter.Application Admin.NET/HD.ReportCenter.ReportEngine/ReportCenterEngine.cs
git commit -m "feat: expose report core validation APIs"
```

### Task 4: Scaffold the standalone React/Univer application

**Files:**

- Create: `ReportWeb/package.json`
- Create: `ReportWeb/tsconfig.json`
- Create: `ReportWeb/tsconfig.app.json`
- Create: `ReportWeb/vite.config.ts`
- Create: `ReportWeb/index.html`
- Create: `ReportWeb/src/main.tsx`
- Create: `ReportWeb/src/types.ts`
- Create: `ReportWeb/src/api.ts`
- Create: `ReportWeb/src/app.css`

- [ ] **Step 1: Add pinned dependencies**

Use this exact dependency set and scripts:

```json
{
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "test": "vitest run"
  },
  "dependencies": {
    "@univerjs/preset-sheets-core": "0.25.1",
    "@univerjs/presets": "0.25.1",
    "lucide-react": "1.24.0",
    "react": "19.2.7",
    "react-dom": "19.2.7"
  },
  "devDependencies": {
    "@types/react": "19.2.17",
    "@types/react-dom": "19.2.3",
    "@vitejs/plugin-react": "6.0.3",
    "typescript": "7.0.2",
    "vite": "8.1.4",
    "vitest": "4.1.10"
  }
}
```

- [ ] **Step 2: Configure Vite and the backend proxy**

Use port `5174`; proxy `/api` to `VITE_API_URL` with fallback `http://localhost:5006`. Keep the Univer worker as an ES module worker and split Univer into its own production chunk.

- [ ] **Step 3: Implement typed API calls**

`api.ts` must unwrap Admin.NET's `{ code, result }` envelope, throw the server message on non-200 results, post execute parameters, download normal exports as a blob, and expose large-job start/status/cancel/download methods.

- [ ] **Step 4: Add the quiet work-focused shell styles**

Use a neutral white/gray work surface, one green status accent, blue command accent, square 4-6px controls, fixed toolbar heights, responsive query controls, and no decorative cards or gradients.

- [ ] **Step 5: Install and build**

Run:

```powershell
pnpm --dir ReportWeb install
pnpm --dir ReportWeb build
```

Expected: production build succeeds with no missing worker or CSS imports.

- [ ] **Step 6: Commit frontend scaffold**

```powershell
git add ReportWeb
git commit -m "feat: scaffold Univer validation app"
```

### Task 5: Render the paged report with Univer Web Worker formulas

**Files:**

- Create: `ReportWeb/src/univer.worker.ts`
- Create: `ReportWeb/src/reportWorkbook.ts`
- Create: `ReportWeb/src/ReportSheet.tsx`
- Create: `ReportWeb/src/App.tsx`
- Create: `ReportWeb/src/reportWorkbook.test.ts`

- [ ] **Step 1: Write failing workbook-data tests**

Test `buildWorkbookData(definition, result)` for title values, URL parameter cells, 20 data rows, and formulas in columns E/F. The first formula row must be `=C6+D6`.

- [ ] **Step 2: Run tests and verify failure**

Run:

```powershell
pnpm --dir ReportWeb test
```

Expected: failure because `buildWorkbookData` does not exist.

- [ ] **Step 3: Implement the worker**

Use the official open-source worker preset:

```ts
import UniverPresetSheetsCoreZhCN from '@univerjs/preset-sheets-core/locales/zh-CN';
import { UniverSheetsCoreWorkerPreset } from '@univerjs/preset-sheets-core/worker';
import { createUniver, LocaleType, mergeLocales } from '@univerjs/presets';

createUniver({
  locale: LocaleType.ZH_CN,
  locales: { zhCN: mergeLocales(UniverPresetSheetsCoreZhCN) },
  presets: [UniverSheetsCoreWorkerPreset()],
});
```

- [ ] **Step 4: Implement workbook data and React lifecycle**

Create the main-thread Univer with `UniverSheetsCorePreset({ workerURL })`. Keep exactly one Univer instance per mounted `ReportSheet`; update the active sheet in one batch when page data changes; terminate the worker and dispose Univer on unmount.

- [ ] **Step 5: Implement the query and paging flow**

Read `hospitalId`, `deptId`, and `personId` from `location.search`; load definition defaults; render date inputs; execute page 1; provide previous/next icon buttons; disable next when `hasMore=false`; display query/render timing without instructional text.

- [ ] **Step 6: Run unit tests and build**

```powershell
pnpm --dir ReportWeb test
pnpm --dir ReportWeb build
```

Expected: all tests pass and production build succeeds.

- [ ] **Step 7: Commit working preview**

```powershell
git add ReportWeb
git commit -m "feat: render paged report with Univer worker"
```

### Task 6: Add normal and large export interactions

**Files:**

- Modify: `ReportWeb/src/App.tsx`
- Modify: `ReportWeb/src/api.ts`
- Modify: `ReportWeb/src/app.css`

- [ ] **Step 1: Add export mode menu**

Provide two explicit commands: normal export downloads immediately; large export starts a job. Use download/database icons, accessible names, and tooltips.

- [ ] **Step 2: Add large-job progress and cancellation**

Poll every second while status is `Pending` or `Running`. Show written rows and a cancel icon. On success, expose download; stop polling on failure, cancellation, or unmount.

- [ ] **Step 3: Verify frontend tests and build**

```powershell
pnpm --dir ReportWeb test
pnpm --dir ReportWeb build
```

Expected: tests and build pass.

- [ ] **Step 4: Commit export UI**

```powershell
git add ReportWeb
git commit -m "feat: add normal and large export controls"
```

### Task 7: Run end-to-end and performance validation

**Files:**

- Create: `docs/validation/2026-07-14-core-validation-results.md`

- [ ] **Step 1: Start a new backend instance without stopping the existing one**

Run Release output on `http://localhost:5006`. If that port is occupied, use the next free port and pass it to Vite with `VITE_API_URL`.

- [ ] **Step 2: Start the React dev server**

Run `pnpm --dir ReportWeb dev --host 0.0.0.0 --port 5174` and keep it running for browser verification.

- [ ] **Step 3: Verify APIs**

Call definition and execute endpoints with URL/form parameters. Download normal export, open it with Open XML SDK, and assert formulas plus full-calculation properties. Start a large export and wait for success without holding the HTTP request open.

- [ ] **Step 4: Verify in a real browser**

Use Playwright at desktop `1440x900` and mobile `390x844`. Verify the workbook is nonblank, formulas produce visible numeric results, page 2 starts at sequence 21, controls do not overlap, and normal export downloads.

- [ ] **Step 5: Record performance evidence**

Record cached definition latency, execute latency, browser render/formula time, normal export time and size, large export rows/time/size, and process memory before/peak/after. Do not claim the design budget passes unless measured values support it.

- [ ] **Step 6: Run final automated verification**

```powershell
dotnet test Admin.NET/Admin.NET.Test/Admin.NET.Test.csproj -c Release --no-restore --filter ReportCenter --nologo
dotnet build Admin.NET/HD.ReportCenter.Application/HD.ReportCenter.Application.csproj -c Release --no-restore --nologo
pnpm --dir ReportWeb test
pnpm --dir ReportWeb build
git diff --check
```

Expected: every command exits 0. Existing unrelated Admin.NET warnings may remain; no new report-center warnings are accepted.

- [ ] **Step 7: Commit validation evidence**

```powershell
git add docs/validation/2026-07-14-core-validation-results.md
git commit -m "docs: record report core validation results"
```

## Execution Notes

- The currently running Admin.NET process on port 5005 locks Debug DLLs. Use Release builds and port 5006; do not terminate the existing process without explicit user approval.
- The large export store is intentionally in-memory for this POC. Production work replaces it with `RcExportJob` and durable file storage from the approved design.
- The fixed data source is deterministic and contains no patient or customer data.
- Every commit must contain only the files from its task; unrelated user changes remain untouched.
