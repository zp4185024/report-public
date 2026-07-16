# SQL Server 数据库迁移设计

日期：2026-07-16

## 目标

在 `192.168.2.5` 的 SQL Server 上创建独立数据库 `hd_report_center`，使用 Admin.NET 自带的 SqlSugar CodeFirst 初始化机制建立全部系统表、插件表和报表表，然后把当前 SQLite 中的报表模板迁移到新库。迁移完成后，应用不再依赖 SQLite。

## 已确认环境

- SQL Server：`192.168.2.5:1433`
- SQL Server 版本：11.0.6607.3（SQL Server 2012 Standard）
- 服务器排序规则：`Chinese_PRC_CI_AS`
- 数据库账号：`hdhis`，已验证具备 `sysadmin` 和创建数据库权限
- 目标数据库：`hd_report_center`，当前不存在
- 连接不启用传输加密：`Encrypt=False`
- 实际密码不写入仓库，通过 `HD_REPORT_CENTER_DB_PASSWORD` 或 `HD_REPORT_CENTER_DB_CONNECTION_STRING` 提供

## 数据库范围

新库使用 Admin.NET 主数据库配置，包含当前有效程序集内属于主配置的全部 `[SugarTable]` 实体：

- Admin.NET 系统表；
- 已启用插件的系统表；
- 报表中心表。

现有报表验证表改为正式数据库表名：

- `RcPocReportTemplate` 改为 `RcReportTemplate`；
- `RcPocExchangeMapping` 改为 `RcExchangeMapping`。

C# 类型名和 POC API 路由本次不做无关重构，数据库表名、索引名和表说明去掉验证含义。存储层仍通过实体映射访问正式表。

## 建库与建表

仓库中的 `Admin.NET.Web.Entry/UpdateScripts/1.0.0.sql` 只是版本更新占位文件，不是完整建表脚本。Admin.NET 当前正式初始化入口位于 `SqlSugarSetup.InitDatabase`：

1. 使用 SQL Server 管理连接显式创建 `hd_report_center`，排序规则采用 `Chinese_PRC_CI_AS`。
2. 使用目标库连接启动 Admin.NET，并仅在本次初始化进程中覆盖配置：
   - `EnableInitDb=false`，因为数据库已经显式创建；
   - `EnableInitTable=true`；
   - `EnableInitSeed=true`；
   - `EnableIncreTable=false`；
   - `EnableIncreSeed=false`。
3. Admin.NET 使用 SqlSugar CodeFirst 扫描实体并建立表、索引、字段类型和说明，再执行框架种子初始化。
4. 初始化进程结束后，正常运行配置恢复为禁止自动建库、自动建表和自动种子，避免每次启动修改结构。

初始化输出重定向到本地运行日志；连接密码不写入日志摘要、文档或 Git。

## 报表数据迁移

迁移前保留 SQLite 文件备份，并读取源数据进行校验。当前需保留的模板数据为一条 `dept-workload` 模板记录。

迁移程序使用参数化 SQL，在一个事务内完成：

1. 确认目标 `RcReportTemplate` 中不存在相同 `ReportCode`；若存在则停止，不覆盖。
2. 复制 `Id`、`ReportCode`、`SnapshotJson`、`BindingJson`、`Revision` 及实体基类审计字段。
3. 提交事务后重新读取，比较报表编码、修订号、快照长度和绑定长度。

Excel/Univer 映射不从 SQLite 手工复制。表建立完成后，通过现有 `PocExchangeMappingStore` 首次读取逻辑初始化当前 `0.25.1 / mappingVersion 1` 的 50 条规则，并验证唯一索引和启用规则数量。

SQLite 中的系统日志、用户操作历史、异常日志和验证运行历史不迁移。Admin.NET 系统基础数据由框架种子产生。

## 应用配置

仓库默认配置更新为：

```text
Server=192.168.2.5;Database=hd_report_center;User Id=hdhis;Password=CHANGE_ME;Encrypt=False;TrustServerCertificate=True;
```

`CHANGE_ME` 只作为不可用占位符。运行时优先读取完整连接串环境变量，其次读取密码环境变量并替换占位值。不得提交真实密码。

自动初始化开关在提交的正常配置中保持关闭。一次性初始化由可审计脚本设置进程级环境变量，不修改机器级环境变量。

## 验证标准

- `DB_ID('hd_report_center')` 非空，排序规则为 `Chinese_PRC_CI_AS`。
- Admin.NET 初始化进程成功退出，新库存在全部预期系统表和插件表。
- `RcReportTemplate` 存在且 `dept-workload` 恰好一条，修订号、快照和绑定与 SQLite 源一致。
- `RcExchangeMapping` 存在 50 条当前映射规则，无重复规则编码。
- 应用使用 SQL Server 正常启动，健康检查返回成功。
- 页面可加载迁移模板，并完成保存、Full 预览和模板/数据导出。
- 自动化测试继续使用隔离测试数据库，不连接或修改 `192.168.2.5`。

## 失败与回滚

- 若目标数据库在建库前已经存在，初始化脚本立即停止，不复用或删除已有库。
- 若 Admin.NET 建表或种子初始化失败，停止后续模板迁移，保留新库供排查，不自动删除。
- 若模板迁移校验失败，事务回滚，不覆盖目标数据。
- SQLite 源文件和迁移前备份保持不变，直到 SQL Server 页面验证完成。
- 所有数据库创建、建表和迁移操作仅限 `hd_report_center`，不得对 `lis_admin` 或其他库执行 DDL/DML。

## 范围外事项

本次不迁移 SQLite 日志和验证历史，不调整 Admin.NET 权限模型，不增加数据库管理页面，不启用连接串加密，不删除本地 SQLite 文件，也不修改 `lis_admin`。
