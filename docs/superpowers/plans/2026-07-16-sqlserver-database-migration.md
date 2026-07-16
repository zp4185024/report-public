# SQL Server Database Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `192.168.2.5` 创建 `hd_report_center`，使用 Admin.NET CodeFirst 建立并初始化全部系统表，将报表表改为正式名称并迁移当前模板，最后让应用使用 SQL Server 运行。

**Architecture:** 提交的正常配置使用 SQL Server 且关闭自动初始化；一次性 PowerShell 工具负责新库存在性保护、建库和临时启用 Admin.NET CodeFirst/种子，Python 工具负责 SQLite 备份和参数化模板迁移。应用现有 SqlSugar 存储层继续工作，只调整数据库表和索引映射，不把迁移逻辑放进 HTTP API。

**Tech Stack:** .NET 8、ASP.NET Core/Furion、SqlSugar、SQL Server 2012、PowerShell 7、Python 3、sqlite3、pymssql、xUnit

---

### Task 1: 报表表改为正式数据库名称

**Files:**
- Modify: `Admin.NET/HD.ReportCenter.Tests/PocTemplateStoreTests.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportTemplate.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMapping.cs`
- Modify: `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateStore.cs`

- [ ] **Step 1: 写入失败测试**

在 `PocTemplateStoreTests` 初始化后断言实体实际映射和唯一约束错误使用正式名称：

```csharp
[Fact]
public void Report_entities_use_production_table_names()
{
    Assert.True(_database.DbMaintenance.IsAnyTable("RcReportTemplate", false));
    Assert.True(_database.DbMaintenance.IsAnyTable("RcExchangeMapping", false));
    Assert.False(_database.DbMaintenance.IsAnyTable("RcPocReportTemplate", false));
    Assert.False(_database.DbMaintenance.IsAnyTable("RcPocExchangeMapping", false));
}
```

把重复键模拟消息改为 `RcReportTemplate.ReportCode`，验证存储层仍翻译成 revision 冲突；非 ReportCode 约束继续原样抛出。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PocTemplateStoreTests"
```

Expected: 正式表不存在，旧 `RcPoc*` 表仍存在；正式约束名尚未被存储层识别。

- [ ] **Step 3: 实现正式映射**

修改实体特性：

```csharp
[SugarTable("RcReportTemplate", "报表中心模板表")]
[SugarIndex("uk_RcReportTemplate_ReportCode", nameof(ReportCode), OrderByType.Asc, IsUnique = true)]
```

```csharp
[SugarTable("RcExchangeMapping", "报表中心Excel与Univer映射表")]
[SugarIndex(
    "uk_RcExchangeMapping_Version_Rule",
    nameof(UniverVersion), OrderByType.Asc,
    nameof(MappingVersion), OrderByType.Asc,
    nameof(RuleCode), OrderByType.Asc,
    IsUnique = true)]
```

`PocTemplateStore.IsReportCodeConstraint` 同时识别新约束名和新表列名；不再依赖旧 POC 名称。

- [ ] **Step 4: 验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PocTemplateStoreTests"
```

Expected: 所有 `PocTemplateStoreTests` 通过。

Commit:

```powershell
git add Admin.NET/HD.ReportCenter.Tests/PocTemplateStoreTests.cs Admin.NET/HD.ReportCenter.Application/CoreValidation/PocReportTemplate.cs Admin.NET/HD.ReportCenter.Application/CoreValidation/PocExchangeMapping.cs Admin.NET/HD.ReportCenter.Application/CoreValidation/PocTemplateStore.cs
git commit -m "refactor: use production report table names"
```

### Task 2: SQL Server 正常配置与一次性初始化工具

**Files:**
- Modify: `Admin.NET/Admin.NET.Application/Configuration/Database.json`
- Create: `scripts/database/Initialize-ReportCenterSqlServer.ps1`
- Create: `scripts/database/README.md`

- [ ] **Step 1: 写入配置和脚本静态失败检查**

运行以下检查并确认当前失败：

```powershell
rg -n 'Database=hd_report_center|Encrypt=False' Admin.NET/Admin.NET.Application/Configuration/Database.json
Test-Path scripts/database/Initialize-ReportCenterSqlServer.ps1
```

Expected: 配置仍指向 `lis_admin` 且脚本不存在。

- [ ] **Step 2: 更新正常运行配置**

连接串使用不可用占位密码，自动初始化保持关闭：

```text
Server=192.168.2.5;Database=hd_report_center;User Id=hdhis;Password=CHANGE_ME;Encrypt=False;TrustServerCertificate=True;
```

`DbNickName` 改为“报表中心库”，`EnableInitDb`、`EnableInitTable`、`EnableInitSeed` 均为 `false`。

- [ ] **Step 3: 实现安全初始化脚本**

脚本参数固定默认服务器、数据库、用户和排序规则，密码只读取 `$env:HD_REPORT_CENTER_DB_PASSWORD`。脚本执行：

```powershell
if (DB_ID(N'hd_report_center') IS NOT NULL) {
    throw '目标数据库已存在，拒绝继续。'
}

CREATE DATABASE [hd_report_center] COLLATE Chinese_PRC_CI_AS
```

随后设置当前进程环境：

```powershell
$env:HD_REPORT_CENTER_DB_CONNECTION_STRING = $targetConnectionString
$env:DbConnection__ConnectionConfigs__0__DbSettings__EnableInitDb = 'false'
$env:DbConnection__ConnectionConfigs__0__TableSettings__EnableInitTable = 'true'
$env:DbConnection__ConnectionConfigs__0__SeedSettings__EnableInitSeed = 'true'
```

隐藏启动 `Admin.NET.Web.Entry.dll --urls http://127.0.0.1:0`，轮询 `sys.tables` 直到 `SysUser`、`RcReportTemplate`、`RcExchangeMapping` 均存在且 `SysUser` 有种子数据，然后停止初始化进程。失败时保留新库并返回非零退出码，不执行数据迁移。

- [ ] **Step 4: 写明运行和安全边界**

`scripts/database/README.md` 记录：PowerShell 7、.NET 8、SQL Server 权限、密码环境变量、目标库存在保护、初始化后自动开关恢复关闭、禁止把密码写入参数或 Git。

- [ ] **Step 5: 语法与静态验证并提交**

Run:

```powershell
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path scripts/database/Initialize-ReportCenterSqlServer.ps1),
    [ref]$null,
    [ref]$errors) | Out-Null
if ($errors.Count -gt 0) { throw $errors[0] }
rg -n 'Database=hd_report_center|Encrypt=False' Admin.NET/Admin.NET.Application/Configuration/Database.json
git diff --check
```

Expected: PowerShell 无语法错误，SQL Server 配置命中，差异检查通过。

Commit:

```powershell
git add Admin.NET/Admin.NET.Application/Configuration/Database.json scripts/database/Initialize-ReportCenterSqlServer.ps1 scripts/database/README.md
git commit -m "feat: add sql server database bootstrap"
```

### Task 3: 参数化模板迁移工具

**Files:**
- Create: `scripts/database/migrate_report_template.py`
- Create: `scripts/database/test_migrate_report_template.py`

- [ ] **Step 1: 写入失败测试**

使用临时 SQLite 创建 `RcPocReportTemplate`，测试源记录读取和校验：

```python
def test_load_source_template_requires_exactly_one_row(self):
    row = load_source_template(self.sqlite_path, "dept-workload")
    self.assertEqual("dept-workload", row.report_code)
    self.assertEqual(8, row.revision)
    self.assertEqual('{"id":"workbook"}', row.snapshot_json)
```

增加空结果和重复结果拒绝测试、目标数据库名必须等于 `hd_report_center` 的测试，以及备份文件创建测试。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
python -m unittest scripts/database/test_migrate_report_template.py -v
```

Expected: FAIL，迁移模块尚不存在。

- [ ] **Step 3: 实现读取、备份和参数化迁移**

脚本使用标准库 `sqlite3` 读取源，使用 `pymssql` 连接目标。密码只读取环境变量。目标保护：

```python
if database != "hd_report_center":
    raise MigrationError("目标数据库必须是 hd_report_center。")
```

迁移事务先确认 `RcReportTemplate` 存在且同 `ReportCode` 为 0 条，再使用 `%s` 参数插入全部模板列；提交后比较 `Revision`、`LEN(SnapshotJson)` 和 `LEN(BindingJson)`。任何异常回滚。

- [ ] **Step 4: 运行测试和静态编译并提交**

Run:

```powershell
python -m unittest scripts/database/test_migrate_report_template.py -v
python -m py_compile scripts/database/migrate_report_template.py
```

Expected: 全部测试通过，Python 编译通过。

Commit:

```powershell
git add scripts/database/migrate_report_template.py scripts/database/test_migrate_report_template.py
git commit -m "feat: add report template migration tool"
```

### Task 4: 构建并创建 SQL Server 数据库

**Files:**
- Runtime source: `.codex-run/editable-template-validation/Admin.NET.poc.db`
- Runtime backup: `.codex-run/editable-template-validation/backups/`
- Runtime target: SQL Server `192.168.2.5/hd_report_center`

- [ ] **Step 1: 完整测试和 Release 构建**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-restore
dotnet build Admin.NET/Admin.NET.Web.Entry/Admin.NET.Web.Entry.csproj -c Release --no-restore
```

Expected: 测试零失败，构建零错误。

- [ ] **Step 2: 保存当前模板基线**

读取 SQLite 并记录：源文件哈希、`dept-workload` 行数、Revision、SnapshotJson/BindingJson 长度。复制 SQLite 到时间戳备份文件，验证备份哈希与源一致。

- [ ] **Step 3: 安全取得运行密码**

从本机已有受控配置读取可用 `hdhis` 密码，只写入当前 PowerShell 进程的 `HD_REPORT_CENTER_DB_PASSWORD`；不输出值，不写入文件，不设置机器级环境变量。

- [ ] **Step 4: 停止 SQLite 验证后端并运行初始化**

Run:

```powershell
pwsh -File scripts/database/Initialize-ReportCenterSqlServer.ps1 \
  -AppDllPath Admin.NET/Admin.NET.Web.Entry/bin/Release/net8.0/Admin.NET.Web.Entry.dll
```

Expected: 新建 `hd_report_center`，Admin.NET CodeFirst 和系统种子完成，目标三张关键表存在。

### Task 5: 迁移模板、初始化映射并切换运行时

**Files:**
- Runtime target: SQL Server `192.168.2.5/hd_report_center`

- [ ] **Step 1: 迁移模板**

Run:

```powershell
python scripts/database/migrate_report_template.py \
  --source-sqlite .codex-run/editable-template-validation/Admin.NET.poc.db \
  --server 192.168.2.5 \
  --database hd_report_center \
  --user hdhis \
  --report-code dept-workload
```

Expected: 迁移一条模板，Revision、快照长度和绑定长度与源一致。

- [ ] **Step 2: 使用 SQL Server 正常配置启动后端**

设置当前进程密码环境变量，保持所有初始化开关关闭，在 `5007` 启动 Release 后端。健康检查必须成功，定义接口返回 `Full`。

- [ ] **Step 3: 触发现有映射规则初始化**

调用数据导出或模板导出链路，使 `PocExchangeMappingStore` 在空表中插入当前 50 条规则。随后查询 `RcExchangeMapping`：总数 50，启用数 50，`UniverVersion=0.25.1`、`MappingVersion=1`。

- [ ] **Step 4: 数据库和 API 校验**

验证：

```text
DB_ID(hd_report_center) != null
RcReportTemplate(dept-workload) = 1
RcExchangeMapping = 50
模板 Revision/JSON 长度与 SQLite 源一致
健康接口 HTTP 200
普通数据导出工作表 142 行、公式 274 个
```

### Task 6: 浏览器验证和文档

**Files:**
- Modify: `docs/validation/2026-07-15-editable-template-operation-guide.md`
- Create: `docs/validation/2026-07-16-sqlserver-migration-results.md`

- [ ] **Step 1: 真实浏览器验证**

打开 `http://localhost:5175/?hospitalId=H01&deptId=D02&personId=P03`，确认从 SQL Server 加载迁移模板、Full 预览 137 行、保存后 Revision 增加、模板导出和数据导出成功。保存操作会写 SQL Server，因此验证前记录 Revision，验证后记录新 Revision。

- [ ] **Step 2: 更新操作说明和迁移结果**

记录数据库地址、库名、正式表名、表数、报表/映射行数、源/目标校验值、自动化测试、构建和浏览器结果。不得记录密码或完整连接串。

- [ ] **Step 3: 最终验证并提交**

Run:

```powershell
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-build --no-restore
pnpm --dir ReportWeb vitest run
git diff --check
```

Expected: 测试零失败，差异检查通过，工作树只包含预期文档。

Commit:

```powershell
git add docs/validation/2026-07-15-editable-template-operation-guide.md docs/validation/2026-07-16-sqlserver-migration-results.md
git commit -m "docs: record sql server migration"
```
