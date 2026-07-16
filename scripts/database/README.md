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

## 正常启动

正常启动只需在启动后端的同一进程环境中提供密码：

```powershell
$env:HD_REPORT_CENTER_DB_PASSWORD = '<当前环境的密码>'
dotnet Admin.NET/Admin.NET.Web.Entry/bin/Release/net8.0/Admin.NET.Web.Entry.dll --urls http://127.0.0.1:5007
```

正常配置不会自动建库、改表或重放系统种子。
