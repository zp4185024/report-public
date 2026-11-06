# AI 报表设计流水线（开发机侧工具链）

把「需求 + 表结构」变成可导入的报表包：**AI 只出意图（spec），代码出模板 JSON**，
最终在报表中心的『报表导入』向导里完成 校验 → 试跑 → 导入。

```
内网库 ──Export-ReportSourceSchema.ps1──▶ 表结构快照 JSON
                                          │（带出内网）
需求 + 快照 ──AI 编写 spec──▶ design/report-specs/<code>.spec.json
                                          │
            New-ReportPackage.ps1 ──▶ output/report-design/<code>.report-package.json
                                          │
                        报表中心『报表导入』向导（validate → dry-run → import）
```

## 1. Export-ReportSourceSchema.ps1 —— 表结构快照（内网侧执行）

只读元数据（INFORMATION_SCHEMA + sys.extended_properties 注释），绝不导出业务数据行。
密码只从环境变量 `REPORT_SOURCE_DB_PASSWORD` 读取，缺失即拒绝；不进参数、命令行或输出。

```powershell
$env:REPORT_SOURCE_DB_PASSWORD = '***'
./scripts/report-design/Export-ReportSourceSchema.ps1 -Server 192.168.1.10 -Database HisReport -User report_reader
# 可选：-Port 1433  -IncludeRowCounts  -TableFilter 'dbo.Rc%'  -OutputPath <路径>
```

默认输出 `output/schema-snapshots/<库名>-<yyyyMMdd-HHmmss>.json`（已被仓库 .gitignore 的 `output/` 覆盖）。
快照结构：`{ exportedAt, server, database, tables: [{ schema, name, type, comment, rowCount?, columns: [...] }] }`。

## 2. New-ReportPackage.ps1 —— spec 编译为报表包

```powershell
pwsh ./scripts/report-design/New-ReportPackage.ps1 -SpecPath design/report-specs/report_asset_inventory.spec.json
# 可选：-OutputPath <路径>（默认 output/report-design/<报表编码>.report-package.json）
```

生成器在本地完成与后端同规则的校验（编码/SheetName、变量↔参数同名同类型、SQL 剥离注释后的
`@变量` 提取、Paged 必须 ORDER BY、模板列与字段精确对齐等），按固定布局构造 Univer 快照
（行0 标题合并 / 行1 参数行 / 行2 表头 / 行3 明细模板行 / 行4 合计行）和规范绑定；
任何违规以非零退出并给中文原因，不猜测修正。

## 3. 样板（作为 spec 编写参考）

- `samples/report-asset-inventory.spec.json` ——「报表资产清单」：列出租表及其数据集，
  `startDate` 参数（可空，默认 `startofmonth(today())`），`isEnabled` 输出原始 0/1、
  由 `report.fieldPresentations.valueMappings` 在导出时映射为 启用/停用
  （工作台预览暂不套用映射，预览见 0/1 属预期）。
- `samples/report-asset-inventory.report-package.json` —— 生成器对上述 spec 的实际产出。
- `Admin.NET/HD.ReportCenter.Tests/PocReportPackageTests.cs` 中的
  `Validate_AcceptsGeneratorSamplePackageFromRepository` 用后端 `ValidateAsync` 钉住该包：
  生成器布局/绑定一旦与后端契约脱节，测试即失败。

## 4. AI 使用方式

安装技能后（`.zcode/skills/report-design/SKILL.md`），对 ZCode 说
"设计报表 / 把XX做成报表 / 写报表spec" 即按流程走：索要快照 → 写 spec → 跑生成器 →
引导用户走向导导入。spec 字段含义与硬规则清单见 SKILL.md。
