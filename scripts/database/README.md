# 报表中心数据库工具

本目录放置 `hd_report_center` 的一次性初始化与模板迁移工具。工具不是应用启动流程的一部分，正常运行时 `EnableInitDb`、`EnableInitTable`、`EnableInitSeed` 都必须保持 `false`。

## 环境要求

- PowerShell 7（`pwsh`）。
- .NET 8 SDK/Runtime。
- Python 3 和 `pymssql`（仅模板迁移需要）。
- SQL Server 账号对服务器具有建库、建表和写入权限。
- 在当前 PowerShell 进程设置 `HD_REPORT_CENTER_DB_PASSWORD`。

密码只能通过进程级环境变量传入，不得放入命令参数、脚本、配置文件或 Git 提交。

```powershell
$env:HD_REPORT_CENTER_DB_PASSWORD = '<当前环境的密码>'
```

## 首次建库和建表

先停止当前后端，然后构建 Release 输出：

```powershell
dotnet build Admin.NET/Admin.NET.Web.Entry/Admin.NET.Web.Entry.csproj -c Release --no-restore
pwsh -File scripts/database/Initialize-ReportCenterSqlServer.ps1
```

脚本执行以下保护：

1. 如果 `hd_report_center` 已存在，立即失败，不会覆盖。
2. 新库固定使用 `Chinese_PRC_CI_AS`。
3. 只在临时 Admin.NET 子进程中开启 CodeFirst 建表和种子初始化。
4. 只有 `SysUser`、`RcReportTemplate`、`RcExchangeMapping` 存在、系统用户种子已写入且应用完成启动时才判定成功。
5. 如果建表或种子失败，保留新库供人工排查，不会继续迁移模板。

如果首次运行只完成了建库、库中仍为 0 张表，可以在排除问题后续跑：

```powershell
pwsh -File scripts/database/Initialize-ReportCenterSqlServer.ps1 -ResumeEmptyDatabase
```

`ResumeEmptyDatabase` 会先查询 `sys.tables`，只要存在任何用户表就拒绝执行，因此不能用它覆盖或升级现有库。

## 正常启动

正常启动只需在启动后端的同一进程环境中提供密码：

```powershell
$env:HD_REPORT_CENTER_DB_PASSWORD = '<当前环境的密码>'
dotnet Admin.NET/Admin.NET.Web.Entry/bin/Release/net8.0/Admin.NET.Web.Entry.dll --urls http://127.0.0.1:5007
```

正常配置不会自动建库、改表或重放系统种子。

## 从 JNPF 试迁移患者费用明细

`Import-JnpfPatientFeeReport.ps1` 是早期单报表验证工具，不被“JNPF迁移”菜单调用。
它只处理 JNPF 中启用的“患者费用明细”当前版本，
读取其 MySQL 元数据、HIS Oracle 数据源、数据接口和 Univer 模板。脚本默认只做只读预检，
不会写 JNPF，也不会写报表中心库。密码只从当前 PowerShell 进程环境变量读取：

```powershell
$env:JNPF_MYSQL_PASSWORD = '<JNPF MySQL 密码>'
pwsh -File scripts/database/Import-JnpfPatientFeeReport.ps1 -TargetDataSourceCode '<已有数据源编码>'
```

确认预检摘要后，设置报表中心 SQL Server 密码并显式增加 `-WriteTarget`：

```powershell
$env:HD_REPORT_CENTER_DB_PASSWORD = '<报表中心 SQL Server 密码>'
pwsh -File scripts/database/Import-JnpfPatientFeeReport.ps1 -TargetDataSourceCode '<已有数据源编码>' -WriteTarget
```

写入固定限制在 `hd_report_center`，并在一个事务内新增 `RcDataSource`、`RcDataset`、
`RcReport`、`RcReportParameter` 和 `RcReportTemplate`。任一目标编码已经存在时会拒绝覆盖。
JNPF 查询区中基于结果字段的二次筛选不属于当前 SQL 变量模型，本次试迁移不复制，
预检会明确显示未迁移控件数量。

## JNPF 报表配置小批量迁移程序

`Invoke-JnpfHisReportMigration.ps1` 的文件名为历史兼容名称，实际用途是把 JNPF
中的报表配置迁移到当前报表中心。它只读取 JNPF 元数据中的启用报表、数据集 SQL、
参数、返回字段和模板快照，再按小批次写入 `hd_report_center` 的报表配置表。

这里有三个边界必须分清：

* `SourceServer/SourcePort/SourceDatabase/SourceUser` 是 JNPF 元数据库连接，只用于读报表配置，通常是 MySQL。
* `ReportDataSourceCode/ReportDataSourceType` 是迁移后报表运行时要引用的已有数据源编码和类型，由“数据源管理”下拉选择，不是写入目标连接，也不是固定的 HIS/Oracle 连接；程序不会创建新业务数据源。
* 迁移不读取业务行、不执行报表 SQL、不复制 JNPF 或 HIS 密码。报表 SQL 只保存为配置，写入前会带上 `SOURCE_SQL_NOT_EXECUTED` 和必要的方言复核标记。

先设置 JNPF MySQL 密码，并显式传入所选 JNPF 来源数据源，只读盘点启用报表：

```powershell
$env:JNPF_MYSQL_PASSWORD = '<JNPF MySQL 密码>'
pwsh -File scripts/database/Invoke-JnpfHisReportMigration.ps1 `
  -SourceServer '192.168.2.5' `
  -SourcePort 13306 `
  -SourceDatabase 'jnpf_test_520' `
  -SourceUser 'root' `
  -ReportDataSourceCode report_center `
  -ReportDataSourceType SqlServer `
  -InventoryOnly
```

盘点结果会写入 `output/jnpf-migration/<时间>/inventory.csv` 和 `inventory.json`。
只迁移指定报表时使用源端 `report_id` 或完整名称；默认迁移全部可自动迁移的报表。
使用 `-MaxReports` 可主动限制本批数量，`0` 表示全部：

```powershell
pwsh -File scripts/database/Invoke-JnpfHisReportMigration.ps1 `
  -SourceServer '192.168.2.5' `
  -SourcePort 13306 `
  -SourceDatabase 'jnpf_test_520' `
  -SourceUser 'root' `
  -ReportDataSourceCode report_center `
  -ReportDataSourceType SqlServer `
  -SourceReportId '<report_id_1>','<report_id_2>' `
  -MaxReports 2
```

确认 `selection.json` 后，设置目标库密码并显式增加 `-WriteTarget`：

```powershell
$env:HD_REPORT_CENTER_DB_PASSWORD = '<报表中心 SQL Server 密码>'
pwsh -File scripts/database/Invoke-JnpfHisReportMigration.ps1 `
  -SourceServer '192.168.2.5' `
  -SourcePort 13306 `
  -SourceDatabase 'jnpf_test_520' `
  -SourceUser 'root' `
  -ReportDataSourceCode report_center `
  -ReportDataSourceType SqlServer `
  -SourceReportId '<report_id_1>','<report_id_2>' `
  -MaxReports 2 `
  -TargetServer '192.168.2.5' `
  -TargetPort 1433 `
  -TargetDatabase hd_report_center `
  -TargetUser hdhis `
  -WriteTarget
```

脚本不会预填固定的 JNPF 或目标库地址。直接运行时必须显式提供来源连接和当前报表中心
数据源编码；写入目标库时还必须提供目标 SQL Server 参数或
`HD_REPORT_CENTER_DB_CONNECTION_STRING`。程序默认不覆盖已存在报表
（`-ExistingReportAction Skip`），每张报表独立事务，失败项会继续处理并写入
`migration-results.json`。

JNPF SQL 参数标记会转换为报表中心的 `@参数名` 协议。当前转换器识别 `{name}`、
`{{name}}`、`#{name}`、`#{name}#`、`${name}`、`#name#`、`$name$`、`:name` 和
已有的 `@name`；字符串、注释和引用标识符中的相同文本不会被误转。无法识别或未闭合
的标记会阻断该报表，并在盘点清单中显示原因，不会悄悄写入一条运行时报错的 SQL。

### 从报表中心菜单执行

报表中心已增加“**JNPF迁移**”菜单。页面的“读取清单”调用 `-InventoryOnly`，勾选可迁移
报表后点击“执行迁移”才会传入 `-WriteTarget`。页面只让用户从数据源管理中选择一个已启用、
已配置密码的 MySQL 数据源作为 JNPF 元数据库来源；写入目标由服务当前报表中心连接确定，
并校验该连接在数据源管理中有对应的已启用编码。数据库密码不会从浏览器提交；环境变量
`JNPF_MYSQL_PASSWORD`、`HD_REPORT_CENTER_DB_PASSWORD` 只供直接运行 PowerShell 命令行程序时使用。发布时项目会把
`scripts/database` 下的迁移程序复制到应用输出目录；若部署目录另有安排，可用
`JNPF_MIGRATION_SCRIPT_PATH` 指定入口脚本，用 `JNPF_MIGRATION_PWSH_PATH` 指定 `pwsh` 路径。

当前接口与报表中心验证接口保持匿名设置；上线前必须接入现有登录权限，并限制
“JNPF迁移”菜单和写入接口只允许迁移负责人使用。JNPF 迁移默认关闭，只有在
`AppSettings:ReportCenter:JnpfMigrationMenuEnabled=true` 或设置
`JNPF_MIGRATION_ENABLED=true` 时才显示菜单并放行接口；关闭时菜单接口和迁移接口均返回 403。

## 报表配置表升级

现有 `hd_report_center` 数据库启用自定义数据源、主数据集和查询参数时，只执行增量脚本：

```powershell
pwsh -File scripts/database/Apply-ReportConfigurationSchema.ps1
```

脚本只新增 `RcReport`、`RcDataSource`、`RcDataset`、`RcReportParameter`。4 张表全都存在时只校验必需列并跳过；只存在一部分或列结构不完整时拒绝继续，不会自动改列、删表或覆盖配置。

如果数据库中的报表中心表已经存在，但表说明或字段说明为空，使用 SQL Server 查询窗口执行
`Apply-ReportSchemaComments.sql`。脚本只新增或更新 `MS_Description` 扩展属性，不修改表结构和业务数据，可重复执行；
覆盖 `RcReport`、`RcDataSource`、`RcDataset`、`RcReportParameter`、`RcExternalParameterSource`、
`RcReportTemplate` 和 `RcExchangeMapping`。
