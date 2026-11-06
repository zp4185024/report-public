---
name: report-design
description: AI 报表设计流水线：按"需求 + 表结构快照"编写报表 spec，用生成器编译成 hd-report-center 报表包 JSON，再走报表中心导入向导（校验→试跑→导入）落地。当用户说"设计报表 / 生成报表包 / 把XX做成报表 / 写报表spec / 出一个报表"或提出新的取数报表需求时使用。
---

# 报表设计流水线（spec → 报表包 → 导入向导）

分工原则：**AI 只出意图（spec JSON），代码出模板 JSON**。不要手写 Univer 快照，
不要直接写库；最终以导入向导的服务端校验和试跑为准。

## 流程

1. 明确需求：报表名称、展示列、筛选参数、数据来源库。
2. 获取表结构：如果没有表结构，向用户索要——
   内网侧执行 `scripts/report-design/Export-ReportSourceSchema.ps1`（密码经环境变量
   `REPORT_SOURCE_DB_PASSWORD` 注入）导出的快照 JSON，或等价的表字典文档。
   绝不主动连接生产/内网库。
3. 编写 spec：写到 `design/report-specs/<报表编码>.spec.json`，格式见下文与
   参考范例 `scripts/report-design/samples/report-asset-inventory.spec.json`。
4. 编译报表包：
   ```bash
   pwsh ./scripts/report-design/New-ReportPackage.ps1 -SpecPath design/report-specs/<编码>.spec.json
   ```
   默认产出 `output/report-design/<编码>.report-package.json`；失败按提示修 spec 重跑。
5. 交付用户：告知"启动本地后端后，在报表中心模板列表侧边栏打开『报表导入』向导，
   依次完成 校验 → 试跑（填样例参数）→ 预览，满意后导入；正式环境再从模板列表导出正式包"。

## 硬规则（生成器会校验，违反即失败）

SQL：
- 只允许**单条**只读 `SELECT` 或 `WITH` 查询；禁止分号多语句、`#`、DML/DDL 关键字、`@@`。
- 参数一律用 `@paramCode` 占位（SQL 参数对象绑定，绝不拼接值）。
- `preview.mode = "Paged"` 时 SQL 必须含稳定的 `ORDER BY`。
- SQL 中出现的每个 `@code` 必须都在 `dataset.variables` 声明；
  每个变量必须有**同名同 dataType** 的 `parameters` 项承接（与后端
  `ValidateDatasetVariableBindings` 对齐）。

模板：
- `{{detail.<field>}}` 由生成器构造（每列独占单元格、同一模板行），spec 里不要出现 detail token。
- `template.title` / `parameterLine` 里可混排 `{{params.<code>}}`，编码必须已定义、区分大小写。
- `template.columns[].field` / `totals[].field` 必须精确（区分大小写）存在于 `dataset.fields[].code`；
  合计项不得占用首列（首列放合计标签）。

命名：
- `report.code` / `dataset.code` / 参数与变量及字段 `code`：`^[A-Za-z][A-Za-z0-9_]{0,63}$`。
- `sheetName` ≤ 31 字符，不含 `[ ] : * ? / \`。
- `fields[].code` 不得重复；`dataSourceType` 只允许 SqlServer / Oracle / MySql / PostgreSQL。

安全：
- spec 与包里**绝不出现**连接串、密码、IP+凭据；密码只走环境变量，与本流水线无关。
- 绝不直接连生产库取数验证；SQL 是否正确、性能是否可接受，以向导"试跑"结果为准。

## spec 字段参考

| 字段 | 说明 |
| --- | --- |
| `specVersion` | 固定 `1`。 |
| `report.code` / `report.name` | 报表编码（导入主键）/ 报表名称。 |
| `report.sheetName` | Excel 工作表名，≤31 字符且不含 `[]:*?/\`。 |
| `report.preview` | `mode`(Full/Paged)、`pageSize`(1-10000)、`showExactTotal`，可省略用默认。 |
| `report.export` | 导出方式与限额：`defaultMode`(Normal/Large)、`normalMaxRows/normalMaxCells/normalTimeoutSeconds`、`largeExportEnabled`、`rowsPerSheet`、`fileRetentionHours`，可省略。 |
| `report.fieldPresentations` | 可选。字段值映射 `[{code, valueMappings:[{value,label}]}]`（如 0/1→停用/启用），仅作用于导出文件。 |
| `dataset.code` / `dataset.name` | 数据集编码/名称；报表通过它引用数据集。 |
| `dataset.dataSourceCode` / `dataSourceType` | 目标环境数据源编码提示 / 方言（SqlServer、Oracle、MySql、PostgreSQL）。 |
| `dataset.timeoutSeconds` / `maxRows` | 查询超时（1-600）与行数上限（1-1000000），可省略。 |
| `dataset.queryText` | 单条只读 SELECT/WITH；`@var` 占位；Paged 必须带 ORDER BY。 |
| `dataset.variables` | SQL 入参声明 `[{code, displayName, dataType, required}]`，与 parameters 同名同类型对齐。 |
| `dataset.fields` | 返回字段契约 `[{code, displayName, dataType}]`，dataType 限 string/number/date/datetime/bool。 |
| `parameters[]` | 报表参数：`code/displayName/dataType/controlType/sourceType/isRequired/isReadOnly/isVisible/allowUrlPrefill/urlKey/defaultExpression/fixedValue/options/displayOrder`。 |
| `template.title` | 标题行文本，可混排 `{{params.*}}`。 |
| `template.parameterLine` | 参数说明行（可省略），如 `"开始日期：{{params.startDate}}"`。 |
| `template.columns` | 明细列 `[{field, header}]`，field 对应 dataset.fields[].code（区分大小写）。 |
| `template.totals` | 可选合计 `[{field, label}]`；field 必须是已声明列且不在首列，生成 `=SUM(列4:列4)` 随导出自动展开。 |

参考范例：`scripts/report-design/samples/report-asset-inventory.spec.json`
（生成的包：`scripts/report-design/samples/report-asset-inventory.report-package.json`，
该包已被 `HD.ReportCenter.Tests` 的回归测试钉住与后端校验器同步）。
脚本用法详见 `scripts/report-design/README.md`。

## 禁止事项

- 不要手写 Univer 模板 JSON 或直接改动生成器产出的包内容（绑定与快照必须保持机器一致）。
- 不要写 SQL/脚本直接往报表中心库表插入配置，一律走导入向导。
- 不要绕过向导校验/试跑直接导入；试跑失败先修 spec 或确认目标库数据源类型一致。
- 不要在 spec、包或会话输出中出现任何连接串或密码。
