# SQL Server 报表中心迁移验证结果

日期：2026-08-07；验证分支：`codex/editable-template-validation`
验证报表：`dept-workload`（科室工作量统计）

## 1. 验证目标

本次验证确认报表中心从 SQLite 切换到 SQL Server 后，以下核心链路仍然可用：

1. Admin.NET 系统表、报表模板表和 Excel/Univer 映射表能够在目标库正常初始化。
2. 当前模板能够从 SQLite 迁移到 SQL Server，并保持 revision、token、公式和样式数据。
3. Vue/Univer 页面能够读取 SQL Server 数据，编辑模板并显示实际预览。
4. 模板导出和完成数据导出是两条独立链路，均能生成有效 XLSX。
5. 默认 `Full` 模式显示全部查询结果，只有报表定义为 `Paged` 时才分页。

## 2. 目标数据库

目标连接为 SQL Server `192.168.2.5:1433` 的 `hd_report_center` 数据库，排序规则为 `Chinese_PRC_CI_AS`，应用账号为 `hdhis`。连接串使用 `Encrypt=False` 和 `TrustServerCertificate=True`，符合当前内网验证环境的约定。

真实密码只从本机受控配置读取，并在启动命令的当前进程中设置为 `HD_REPORT_CENTER_DB_PASSWORD`。仓库内的 `Database.json` 只保留占位符，本文也不记录密码。

## 3. 数据库核对

| 核对项 | 结果 |
| --- | ---: |
| SQL Server 用户表总数 | 64 |
| `SysUser` 种子用户 | 6 |
| `RcReportTemplate` 模板 | 1 |
| 当前模板 revision | 8 |
| `RcExchangeMapping` 映射规则 | 50 |
| 当前映射版本 | `0.25.1 / mappingVersion 1` |
| 当前版本启用规则 | 50 |

本次只迁移当前 `dept-workload` 模板，没有迁移旧日志和历史记录。迁移工具在写入前生成带 SHA-256 的 SQLite 备份，目标写入使用参数化 SQL 和事务；写入后核对 revision、快照长度、绑定长度和 JSON 内容。

源模板基线为 revision 8，快照长度 3645，绑定长度 591。迁移后 SQL Server 中的值与基线一致。

## 4. 后端和页面验证

- 后端：`http://localhost:5007`
- 前端：`http://localhost:5175/?hospitalId=H01&deptId=D02&personId=P03`
- 定义接口返回 HTTP 200，`preview.mode=Full`，`export.defaultMode=Normal`。
- 模板接口返回 HTTP 200，报表编码为 `dept-workload`，revision 为 8。
- 页面显示 `revision 8 / 已保存`，Univer Worker 正常工作。
- 使用页面默认日期 `2026-08-01` 至 `2026-08-07` 点击“数据预览”，页面显示 `共 137 行`，没有分页按钮，渲染耗时约 435 ms。
- 页面选区范围为 `A6:L142`，说明 Full 预览已经展开到全部数据行，而不是只取前 25 条。

页面操作还验证了两种导出入口：

- 在模板设计模式点击“导出模板”，页面提示“当前模板草稿已导出为 XLSX”；
- 在数据预览模式点击“导出数据”，页面提示“完整数据已按保存模板导出”。

## 5. XLSX 文件核验

使用仓库配置的 `@oai/artifact-tool` 打开导出文件，而不是只检查文件扩展名。两个文件均为有效 ZIP/XLSX，工作表名称为 `科室工作量`，公式单元格没有 `#VALUE!`、`#DIV/0!` 等错误文本。

### 模板文件

- 文件：`.codex-run/editable-template-validation/sqlserver-template-export.xlsx`
- 使用单元格：6 行 × 6 列
- 公式数量：2
- 首个公式：`=C6+D6`
- 最后一个公式：`=AVERAGE(C6:D6)`

### 数据文件

- 文件：`.codex-run/editable-template-validation/sqlserver-data-export.xlsx`
- 固定验证查询日期：`2026-07-01` 至 `2026-07-16`
- 明细行：137（第 6 行至第 142 行）
- 使用单元格：142 行 × 6 列
- 公式数量：274
- 首个公式：`=C6+D6`
- 最后一个公式：`=AVERAGE(C142:D142)`

预览和导出使用的日期条件不同，但本次固定查询与页面验证都返回 137 条明细；工作表总行数为 142，包含标题、参数和 137 条明细，不应把工作表总行数误认为明细条数。

## 6. 已知的模板内容问题

迁移过程保持了源模板的原始内容，没有自动改写模板。当前 revision 8 的第 5 行表头是“序号、科室”，但第 6 行 token 实际为：

- A6：`{{detail.departmentName}}`
- B6：`{{detail.sequence}}`

所以预览中 A 列显示 `D02-01`，B 列显示 `1`。这属于模板设计内容顺序问题，不是 SQL Server 迁移、Excel 映射或公式转换错误。后续修订模板时，应交换 A6/B6 的 token 或同步调整表头，再通过“保存模板”形成新 revision。

## 7. 回归命令

已执行并通过：

```text
dotnet test Admin.NET/HD.ReportCenter.Tests/HD.ReportCenter.Tests.csproj -c Release --no-build --no-restore
cd ReportWeb
npm test -- --run
npm run build
cd ..
dotnet build Admin.NET/Admin.NET.Web.Entry/Admin.NET.Web.Entry.csproj -c Release --no-restore -o .codex-run/editable-template-validation/release-verify
```

结果：后端测试 98/98，前端测试 107/107，前端生产构建成功；后端 Release 独立目录构建为 0 个错误、15 个 Admin.NET 既有警告。一次性 SQLite 迁移工具已在迁移完成后移除。

## 8. 结论

当前技术路线在 SQL Server 目标环境下完成了核心可行性验证：数据库迁移、模板读取、Univer 编辑器加载、Full 全量预览、公式计算、模板导出和数据导出均已跑通。下一步可以在不改变模板交换协议的前提下，修正 revision 8 的 A/B 字段顺序，并再选一份真实业务模板验证多工作表和大文件导出。
