# scripts 目录索引

本目录收纳运维与质量工具脚本。全部脚本使用 PowerShell 7（`pwsh`）执行。

## deploy/ — 安装与快速部署

从源码到可访问站点的一键部署能力，详细步骤见 [docs/deploy/README.md](../docs/deploy/README.md)。

| 脚本 | 职责 |
| --- | --- |
| `deploy/Publish-ReportCenter.ps1` | dotnet publish 后端入口（Admin.NET.Web.Entry）+ 构建前端（Vite），统一输出到 `dist/report-center/backend` 与 `dist/report-center/frontend`，结束后打印产物摘要。支持 `-SkipBackend`/`-SkipFrontend`/`-Configuration`/`-CleanOutput` |
| `deploy/Deploy-ReportCenterIis.ps1` | 创建/更新 IIS 应用池与站点（幂等），校验 Hosting Bundle/ANCM，归一化后端 `web.config`（InProcess/OutOfProcess、stdout 日志），可选写入 `Database.json` 连接串、持久化 `HD_REPORT_CENTER_DB_*` 机器级环境变量、按序执行数据库初始化（`-RunDatabaseInit`，可叠加 `-ImportJnpfReport -JnpfTargetDataSourceCode` 做 JNPF 报表导入预检）；支持 `-WhatIf` 干跑 |
| `deploy/New-ReportCenterNginxConfig.ps1` | 渲染 `scripts/deploy/nginx/report-center.conf.template`，生成可直接 include 的 `server{}` 配置：前端静态托管 + history 路由回退 + `/api` 反代 + 大文件超时/体积放开 + gzip；主机/端口全部参数注入，模板不含具体内网 IP |

快速上手：

```powershell
# 发布（构建机）
pwsh -NoProfile -File ./scripts/deploy/Publish-ReportCenter.ps1

# IIS 一键部署（目标机，管理员；先 -WhatIf 预检）
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1 -WhatIf
pwsh -NoProfile -File ./scripts/deploy/Deploy-ReportCenterIis.ps1

# 生成 nginx 反代配置
pwsh -NoProfile -File ./scripts/deploy/New-ReportCenterNginxConfig.ps1 -ListenPort 80
```

## database/ — 数据库初始化与迁移

`hd_report_center` 的一次性建库、表结构升级与 JNPF 报表迁移工具，用法与保护策略见 [database/README.md](database/README.md)。数据库密码只通过环境变量 `HD_REPORT_CENTER_DB_PASSWORD` 注入。

## quality/ — 代码质量门禁

`Test-CodeComments.ps1`：AGENTS.md 强制的代码注释质量检查（方法级注释覆盖、长方法阶段注释），提交前必须通过。

## remote/ — 远程运维

远程主机运维脚本集合。
