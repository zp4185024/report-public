# hd-report-center 快速部署指南

本指南描述从源码到可访问站点的一键部署路径，覆盖 IIS 部署（Windows）与 nginx 反向代理（Windows/Linux 均可）两种接入方式。所有脚本位于 `scripts/deploy/`，均为幂等设计，可重复执行。

## 总览

```text
Publish-ReportCenter.ps1          构建：后端 dotnet publish + 前端 vite build -> dist/report-center/
Deploy-ReportCenterIis.ps1        IIS：应用池/站点编排 + web.config 归一化 + 可选数据库初始化
New-ReportCenterNginxConfig.ps1   nginx：渲染 server{} 配置，供反代层 include
```

典型拓扑二选一或叠加：

- 直连 IIS：浏览器 -> IIS 前端站点（静态文件）+ IIS 后端站点（ASP.NET Core）。
- nginx 反代（推荐生产）：浏览器 -> nginx（80/443，托管前端静态文件并把 `/api` 反代到后端）-> 后端端口。

## 前置条件

| 组件 | 用途 | 说明 |
| --- | --- | --- |
| .NET 8 SDK | 发布后端 | 仅构建机需要；建议与 `global.json` 一致的 SDK 版本 |
| .NET 8 ASP.NET Core **Hosting Bundle** | 运行后端 | 运行机（IIS 服务器）必须；含 .NET Runtime + ASP.NET Core Runtime + ANCM。Hosting Bundle 安装后需 `net stop was /y && net start w3svc` 或重启机器让 IIS 感知 |
| IIS 功能 | 承载两个站点 | Windows 功能：`IIS-WebServerRole`、`IIS-WebServer`、`IIS-ApplicationDevelopment`；脚本会校验 ANCM（`aspnetcore.dll` + `AspNetCoreModuleV2` 注册） |
| Node.js + npm/pnpm | 构建前端 | **仅构建机需要**；运行机不需要 Node |
| PowerShell 7（`pwsh`） | 运行部署脚本 | 数据库初始化脚本同样要求 pwsh |
| SQL Server | 报表中心主库 | 只在执行 `-RunDatabaseInit` 或首次初始化时需要；账号需建库/建表权限 |

数据库密码红线：密码只通过进程环境变量 `HD_REPORT_CENTER_DB_PASSWORD` 或整串 `HD_REPORT_CENTER_DB_CONNECTION_STRING` 注入，不写入命令行参数、脚本或 Git。

## 第一步：构建发布产物

```powershell
# 仓库根目录执行；输出 dist/report-center/backend 与 dist/report-center/frontend
pwsh -NoProfile -File ./scripts/deploy/Publish-ReportCenter.ps1

# 常用变体
pwsh -NoProfile -File ./scripts/deploy/Publish-ReportCenter.ps1 -Configuration Release -CleanOutput
pwsh -NoProfile -File ./scripts/deploy/Publish-ReportCenter.ps1 -SkipFrontend   # 只发布后端
pwsh -NoProfile -File ./scripts/deploy/Publish-ReportCenter.ps1 -SkipBackend    # 只构建前端
```

脚本完成后会输出两个目录的文件数与体积摘要。注意：发布产物中的 `web.config` 保留了 VS 调试占位符（`%LAUNCHER_PATH%`），**不能直接用于 IIS**，需由下一步的部署脚本归一化覆盖。

## 第二步（路径 A）：IIS 一键部署

管理员 PowerShell 执行：

```powershell
# 干跑预检：不改动任何东西，输出将要执行的动作清单，建议先跑一次
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1 -WhatIf

# 正式部署：创建/更新应用池与站点，归一化 web.config
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1
```

默认行为：

- 应用池 `hd-report-center-api` / `hd-report-center-web`（均 No Managed Code）。
- 后端站点 `hd-report-center-api` 监听 5006 -> `dist/report-center/backend`，托管模型 InProcess。
- 前端站点 `hd-report-center-web` 监听 8080 -> `dist/report-center/frontend`，纯静态。
- 幂等：站点/应用池已存在时更新物理路径与属性，缺失的目标端口绑定时增量补写（不删除既有绑定），不重建不报错。

常用参数：

```powershell
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1 `
  -BackendSiteName rc-api -FrontendSiteName rc-web `
  -BackendPort 5106 -FrontendPort 8088 `
  -PhysicalRoot D:\deploy\report-center `
  -HostingModel OutOfProcess      # InProcess 优先；排障需要进程隔离时切 OutOfProcess

# 排障时打开 ANCM stdout 日志（写入后端目录 logs\stdout）
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1 -EnableStdoutLog
```

数据库相关：

```powershell
# 推荐：密码来自当前进程环境变量，持久化为机器级变量供 IIS 工作进程读取，不落任何配置文件
$env:HD_REPORT_CENTER_DB_PASSWORD = '<当前环境的密码>'
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1 -PersistDbEnvironmentToMachine

# 首次部署同时初始化数据库（按既有 scripts/database 脚本顺序执行）
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1 `
  -RunDatabaseInit -DbServer 188.188.2.5 -DbPort 1433 -DbName hd_report_center -DbUser hdhis

# 数据库初始化后追加 JNPF 患者费用明细报表导入预检（只读；写库见 scripts/database/README.md）
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1 `
  -RunDatabaseInit -ImportJnpfReport -JnpfTargetDataSourceCode jnpf_default
```

不推荐但支持的兜底：`-DbConnectionString "Server=...;Database=...;..."` 会把连接串（含密码）写入发布目录 `Configuration\Database.json`，明文落盘有泄露风险，生产请改用环境变量方式并在事后删除该值。

## 第二步（路径 B）：nginx 反向代理

nginx 直接托管前端静态文件，并把 `/api` 原样反代到后端（后端路由自带 `/api` 前缀，nginx 不做 rewrite）：

```powershell
# Windows 构建机上生成 conf（产物默认 dist/report-center/nginx/report-center.conf）
pwsh -NoProfile -File ./scripts/deploy/New-ReportCenterNginxConfig.ps1 `
  -ListenPort 80 `
  -FrontendRoot 'D:/deploy/report-center/frontend' `
  -BackendHost 127.0.0.1 -BackendPort 5006
```

Linux 主机示例（把构建产物同步过去后）：

```bash
pwsh ./scripts/deploy/New-ReportCenterNginxConfig.ps1 \
  -ListenPort 80 \
  -FrontendRoot /opt/report-center/frontend \
  -BackendHost 127.0.0.1 -BackendPort 5006 \
  -OutputPath /etc/nginx/conf.d/report-center.conf

nginx -t && systemctl reload nginx
```

模板已内置：history 路由回退（`try_files ... /index.html`）、`/api` 反代、`client_max_body_size`（默认 1g）与 `proxy_*_timeout`（默认 300s，可用 `-ProxyTimeout 600s` 调整）、`proxy_buffering off`（流式大导出不缓冲）、gzip。所有主机/端口均为参数注入，模板不包含具体内网 IP。

## 数据库初始化顺序

`-RunDatabaseInit` 等价于手工按序执行以下既有脚本（脚本本身不重写）：

1. `scripts/database/Initialize-ReportCenterSqlServer.ps1` — 一次性建库 + CodeFirst 建表 + 种子；库已存在时部署脚本自动跳过（该脚本自身拒绝覆盖已有库）。部署脚本会用本次发布产物 `Admin.NET.Web.Entry.dll` 作为其 `-AppDllPath`。
2. `scripts/database/Apply-ReportConfigurationSchema.ps1` — 报表配置表结构升级，幂等。
3. `scripts/database/Apply-ReusableDataAssetSchema.ps1` — 数据源/数据集表结构升级，幂等。
4. （可选）`scripts/database/Import-JnpfPatientFeeReport.ps1` — 加 `-ImportJnpfReport -JnpfTargetDataSourceCode <编码>` 时执行只读导入预检；该脚本写库需自行带 `-WriteTarget` 调用（见 `scripts/database/README.md`）。

生产建议后续结构升级仍单独按 `scripts/database/README.md` 操作，部署脚本只覆盖首次安装与常用升级路径。

## 常见故障排查

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 站点启动报“端口被占用” | 目标端口被其他站点/进程监听 | `netstat -ano | findstr :5006` 找到占用进程；换 `-BackendPort`/`-FrontendPort` 或释放端口后重跑部署脚本（幂等更新绑定） |
| 后端 502.5（ANCM Process Failure） | Hosting Bundle 未装或损坏、32/64 位不匹配 | `dotnet --list-runtimes` 确认存在 `Microsoft.AspNetCore.App 8.x`；重装 Hosting Bundle 后 `net stop was /y && net start w3svc`；打开 `-EnableStdoutLog` 看 `logs\stdout` 具体异常 |
| 500.19 / 提示 AspNetCoreModuleV2 缺失 | IIS 未注册 ANCM | 安装 Hosting Bundle（不是裸 .NET Runtime）；脚本 `-WhatIf` 会预检该项 |
| 后端反复重启 / 起不来提示“未配置主数据库”或数据库连不上 | 连接串缺失或库不可达导致启动循环 | 确认 `HD_REPORT_CENTER_DB_CONNECTION_STRING` 或 `HD_REPORT_CENTER_DB_PASSWORD` 对应用池生效（机器级环境变量设置后需回收应用池或重启 WAS）；用 `-RunDatabaseInit` 补齐库结构；检查 SQL Server 网络/账号 |
| 前端页面刷新 404 | 未走部署脚本的站点或未配置 history 回退 | 确认前端根目录正确；nginx 路径确认 `try_files $uri $uri/ /index.html` 存在 |
| 前端能开但接口全 404/超时 | `/api` 未反代到后端 | 检查 nginx `location /api/` 的 `proxy_pass` 主机端口；确认后端站点独立访问 `http://<后端>:5006/api/report-center/features` 正常 |
| 大文件导出/上传 413 或中途断开 | 请求体限制或超时过小 | nginx 调大 `-ClientMaxBodySize`、`-ProxyTimeout` 后重新生成配置；后端 `web.config` 的 `requestLimits` 已放开到 1GB |
| 排障后磁盘膨胀 | stdout 日志未关 | 排障完成后不带 `-EnableStdoutLog` 重跑部署脚本即可关闭，并清理后端 `logs\stdout.*` |

## 未经实测说明

- 本机无 IIS 角色与管理员环境时，`Deploy-ReportCenterIis.ps1` 的应用池/站点编排（`Ensure-*`、`Start-*`）与机器级环境变量持久化未经真实站点创建验证，仅通过 `-WhatIf` 干跑与参数校验覆盖；首次上机请先 `-WhatIf` 再实跑。
- nginx 配置为静态模板渲染，未在本机起 nginx 实例做 `nginx -t` 验证；上机后请先 `nginx -t`。
- SQL Server 相关分支（建库存在性探测、初始化脚本串联）依赖真实实例，需在目标环境确认。
