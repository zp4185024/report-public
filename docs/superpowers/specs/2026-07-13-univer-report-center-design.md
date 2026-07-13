# Univer 报表中心设计

## 1. 文档状态

- 状态：待用户审阅
- 日期：2026-07-13
- 目标仓库：`hd-report-center`
- 后端基线：.NET 8、Admin.NET、Furion、SqlSugar、SQL Server
- 报表前端：独立 React + Vite 应用
- 表格引擎：Univer 开源核心，不引入 `@univerjs-pro/*`
- Excel 转换：.NET + ClosedXML；图片等绘图部件使用 Open XML SDK 补充处理

## 2. 背景与结论

本项目要将 `luck-report` 提供的主要报表能力迁移到独立报表中心。优先级依次为：

1. 快速形成可用的统计报表能力。
2. 复用成熟框架，减少自研编辑器和公式引擎。
3. 支持 Excel 模板导入、参数化查询、公式计算和结果导出。
4. 后续可以逐步增加数据源、数据集、模板和发布管理能力。

最终采用以下技术路线：

- 使用 Univer 开源核心承担表格设计、样式编辑、公式计算和页面预览。
- 报表设计、预览和嵌入页面使用独立 React 应用，避免 Vue 3 与 Univer 内部 React UI 的适配成本。
- .NET 8 后台承担模板、参数、数据集、SQL 执行、权限、审计和 Excel 导入导出。
- Excel 只作为模板导入来源；导入完成后以 Univer `IWorkbookData` 快照作为模板事实来源。
- Excel 结果导出使用 ClosedXML 和 Open XML SDK，不依赖 Univer Pro，因此不存在 Pro 水印或许可证限制。
- 公式在浏览器内由 Univer 计算，.NET 后台不重复实现 Excel 公式引擎。

## 3. 目标范围

### 3.1 第一阶段必须支持

- 导入 `.xlsx` 文件作为报表模板。
- 显示和编辑单元格、公式、样式、边框、合并、行高、列宽、冻结区域和普通图片。
- 将 Univer 模板快照保存到 SQL Server。
- 通过 `reportCode` 和 URL 参数打开报表。
- URL 传入医院、科室、人员、来源系统等内部上下文。
- 页面提供日期、状态、分类等查询条件。
- 查询参数支持 `now()`、`today()`、月初、月末等服务端默认函数。
- 后台使用参数化 SQL 查询业务数据库。
- 将后台数据绑定到模板并动态展开明细行。
- 由 Univer 计算 Excel 风格公式。
- 将最终结果导出为 `.xlsx`。
- 保存模板草稿、已发布版本和运行审计记录。

### 3.2 明确不做

- Univer Pro 插件和 Univer Server。
- 多人实时协作。
- 图表、数据透视表、迷你图和高级形状。
- Word、PDF 和复杂分页打印。
- Excel 宏、VBA、外部工作簿链接和 OLE 对象。
- 任意复杂 Excel 文件的百分之百无损往返。
- 在浏览器或接口中接收任意 SQL。
- 在数据库中保存并执行任意 C#、JavaScript 或脚本代码。

## 4. 总体架构

```text
内部业务系统
  └─ /report/view/{reportCode}?hospitalId=...&deptId=...&personId=...
                             │
                             ▼
独立 React 报表应用
  ├─ 参数表单
  ├─ Univer 模板设计器
  ├─ Univer 公式引擎
  └─ 只读预览与结果导出
                             │
                             ▼
HD.ReportCenter.Application
  ├─ 管理接口
  ├─ 开放运行接口
  ├─ 参数解析与默认函数
  ├─ 数据集执行
  ├─ 模板版本与发布
  └─ 导入导出与审计
                             │
             ┌───────────────┴───────────────┐
             ▼                               ▼
HD.ReportCenter.ReportEngine             SQL Server
  ├─ 模板模型                           ├─ 报表配置
  ├─ 参数模型                           ├─ 参数与数据集
  ├─ 绑定规则                           ├─ Univer 快照
  └─ Excel 转换模型                     └─ 运行日志
                                             │
                                             ▼
                                  HIS/LIS/PACS 等只读数据源
```

## 5. 模块职责

### 5.1 React 报表应用

React 应用负责：

- 初始化和销毁 Univer 实例。
- 加载已发布模板或设计中的草稿模板。
- 渲染数据库配置的查询表单。
- 将后台返回的数据集写入绑定区域。
- 复制明细模板行并调整相对公式引用。
- 触发并等待 Univer 公式重算。
- 检查可见区域中的公式错误。
- 保存模板快照。
- 提交最终工作簿结果用于导出。

React 应用不负责：

- 拼接或执行 SQL。
- 信任并直接使用未经后台校验的 URL 参数。
- 保存数据库连接串。
- 自行实现 Excel 公式计算。

### 5.2 `HD.ReportCenter.Application`

应用层负责：

- 复用 Admin.NET 的管理身份、权限、日志和依赖注入能力。
- 提供报表、模板版本、数据源、数据集和参数的管理接口。
- 提供内部开放的报表运行接口。
- 解析 URL 参数和页面参数。
- 执行默认函数、强类型转换和验证。
- 绑定数据库参数并执行只读 SQL。
- 使用 ClosedXML 完成单元格、样式和公式转换，使用 Open XML SDK 补充处理普通图片等绘图部件。
- 返回稳定的请求和响应 DTO，不直接暴露数据库实体。

### 5.3 `HD.ReportCenter.ReportEngine`

引擎层不依赖 HTTP、Admin.NET 页面和具体数据库表，负责：

- 参数定义、参数值和数据集结果模型。
- 安全默认函数求值。
- 模板数据绑定规则。
- Univer 快照与 Excel 转换所需的中立模型。
- 运行结果、公式错误和导入警告模型。
- 参数解析、绑定和导出相关的纯逻辑。

## 6. 数据库设计

报表中心配置表使用 `Rc` 前缀。由于开放运行接口没有 Admin.NET 登录上下文，核心配置实体不依赖机构自动过滤；管理接口单独执行权限检查。需要软删除的配置实体沿用 Admin.NET `EntityBaseDel` 审计字段。

### 6.1 `RcReport`

保存报表基本信息。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `Code` | nvarchar(64) | 全局唯一报表编码 |
| `Name` | nvarchar(128) | 报表名称 |
| `Description` | nvarchar(512) | 说明 |
| `AccessMode` | tinyint | `Open`、`Authenticated`、`Signed`；第一版只实现 `Open` |
| `PublishedVersionId` | bigint null | 当前已发布版本 |
| `Status` | tinyint | 启用、停用 |

约束：

- `Code` 建立唯一索引。
- 只有启用且存在已发布版本的报表可以通过开放运行接口访问。

### 6.2 `RcReportVersion`

保存模板版本。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `ReportId` | bigint | 所属报表 |
| `VersionNo` | int | 报表内递增版本号 |
| `SnapshotJson` | nvarchar(max) | Univer `IWorkbookData` 快照 |
| `BindingJson` | nvarchar(max) | 数据集重复区域和字段绑定规则 |
| `ParameterBindingJson` | nvarchar(max) | 参数到单元格或命名区域的绑定 |
| `SourceFileId` | bigint null | 原始 Excel 文件引用，仅用于追溯 |
| `SourceFileName` | nvarchar(256) null | 原始文件名 |
| `SourceFileHash` | varchar(64) null | SHA-256 |
| `Status` | tinyint | 草稿、已发布、已停用 |
| `PublishedTime` | datetime2 null | 发布时间 |

约束：

- `(ReportId, VersionNo)` 建立唯一索引。
- 同一报表同一时刻只有一个当前已发布版本。
- 发布操作在事务中更新版本状态和 `RcReport.PublishedVersionId`。
- 修改草稿不影响当前已发布版本。

### 6.3 `RcDataSource`

保存业务查询数据源。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `Code` | nvarchar(64) | 唯一编码 |
| `Name` | nvarchar(128) | 数据源名称 |
| `DbType` | nvarchar(32) | SQL Server、Oracle、MySQL 等 |
| `ConnectionConfigCipher` | nvarchar(max) | 加密后的连接配置 |
| `DefaultTimeoutSeconds` | int | 默认查询超时 |
| `DefaultMaxRows` | int | 默认最大返回行数 |
| `Status` | tinyint | 启用、停用 |

约束：

- 生产环境只配置只读数据库账号。
- 连接配置使用 ASP.NET Core Data Protection 加密，密钥持久化到受控位置。
- 管理和运行接口不得返回解密后的连接串。

### 6.4 `RcDataset`

保存报表数据集和查询语句。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `ReportId` | bigint | 所属报表 |
| `DataSourceId` | bigint | 数据源 |
| `Code` | nvarchar(64) | 报表内唯一数据集编码 |
| `Name` | nvarchar(128) | 名称 |
| `QueryText` | nvarchar(max) | 只读参数化 SQL |
| `TimeoutSeconds` | int | 查询超时 |
| `MaxRows` | int | 最大返回行数 |
| `Status` | tinyint | 启用、停用 |

约束：

- `(ReportId, Code)` 建立唯一索引。
- 只允许单条 `SELECT` 或以 `WITH` 开始的只读查询。
- 禁止多语句、DDL、DML 和事务控制语句。
- 只读数据库账号是最终安全边界；SQL 文本检查作为附加保护。

### 6.5 `RcReportParameter`

保存报表运行参数定义。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `ReportId` | bigint | 所属报表 |
| `Code` | nvarchar(64) | 报表内唯一参数编码 |
| `DisplayName` | nvarchar(128) | 页面名称 |
| `DataType` | nvarchar(32) | string、long、decimal、date、datetime、bool、stringList、longList |
| `SourceType` | nvarchar(16) | Url、Form、System、Fixed |
| `UrlKey` | nvarchar(64) null | URL 键名 |
| `DefaultExpression` | nvarchar(256) null | 默认函数表达式 |
| `FixedValue` | nvarchar(1024) null | 固定值 |
| `IsVisible` | bit | 是否显示在查询表单 |
| `IsReadOnly` | bit | 是否只读 |
| `IsRequired` | bit | 是否必填 |
| `AllowUrlPrefill` | bit | Form 参数是否允许由 URL 预填 |
| `ValidationJson` | nvarchar(max) null | 长度、范围、正则和选项限制 |
| `DisplayOrder` | int | 表单顺序 |
| `Status` | tinyint | 启用、停用 |

约束：

- `(ReportId, Code)` 建立唯一索引。
- 运行接口忽略数据库中未定义的 URL 和表单参数。
- 同一参数只能有一个来源类型，来源之间不能相互覆盖。

### 6.6 `RcDatasetParameter`

保存报表参数到 SQL 参数的映射。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `DatasetId` | bigint | 数据集 |
| `ReportParameterId` | bigint | 报表参数 |
| `DbParameterName` | nvarchar(64) | 例如 `@HospitalId` |
| `IsRequired` | bit | 数据集是否必须使用该参数 |

约束：

- `(DatasetId, DbParameterName)` 建立唯一索引。
- SQL 中的参数占位符必须全部存在映射。
- 列表参数按有限长度展开为多个数据库参数，禁止拼接列表值。

### 6.7 `RcRunLog`

保存运行审计，不保存完整业务结果。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `CorrelationId` | varchar(64) | 单次运行标识 |
| `ReportId` | bigint | 报表 |
| `ReportVersionId` | bigint | 使用的版本 |
| `ParameterSummaryJson` | nvarchar(max) | 脱敏后的参数快照 |
| `StartedTime` | datetime2 | 开始时间 |
| `ElapsedMilliseconds` | bigint | 耗时 |
| `ResultRowCount` | int | 返回行数 |
| `Status` | tinyint | 成功、失败、超时 |
| `ErrorCode` | nvarchar(64) null | 稳定错误码 |
| `ErrorMessage` | nvarchar(1024) null | 脱敏错误摘要 |

约束：

- 对患者姓名、证件号、手机号等敏感字段执行掩码或不落日志。
- 不记录连接串、完整 SQL 参数值和完整查询结果。
- 按 `ReportId`、`StartedTime` 建立查询索引。

## 7. 参数系统

### 7.1 参数来源

参数按来源分别解析，不使用一个可被任意覆盖的字典：

- `Url`：医院、科室、人员、来源系统等内部调用上下文；通常隐藏且只读。
- `Form`：开始日期、结束日期、状态、分类等用户查询条件。
- `System`：服务器当前时间等系统值，不接受客户端覆盖。
- `Fixed`：模板配置的固定常量。

示例 URL：

```text
/report/view/dept-statistics
  ?hospitalId=1
  &deptId=1001
  &personId=20001
  &source=his
```

### 7.2 默认函数

数据库只保存默认表达式文本，函数实现位于 .NET 代码的白名单求值器中。第一版允许：

```text
now()
today()
startOfDay(value)
endOfDay(value)
startOfMonth(value)
endOfMonth(value)
startOfYear(value)
endOfYear(value)
addDays(value, number)
addMonths(value, number)
```

规则：

- 默认时区为 `Asia/Shanghai`。
- `now()` 和 `today()` 使用服务器时间。
- 支持嵌套表达式，例如 `startOfMonth(today())`。
- 不支持任意方法调用、属性访问、脚本或 SQL 片段。
- 系统启动和报表发布时预编译并验证表达式。

### 7.3 参数解析规则

1. 加载已发布报表和参数定义。
2. 按来源读取 URL、表单、系统或固定值。
3. 当值缺失时执行 `DefaultExpression`。
4. 转换为强类型值。
5. 执行必填、范围、长度、正则和选项验证。
6. 生成本次运行不可变的参数快照。
7. 按 `RcDatasetParameter` 生成数据库参数。

未知参数被忽略，不进入 SQL，也不写入模板。

## 8. 开放运行模式

第一版运行接口采用内部开放模式：

- `RcReport.AccessMode` 设置为 `Open`。
- 运行和导出接口允许匿名调用。
- 不实现 URL 签名、调用方密钥和应用层来源授权。
- 模板管理、SQL 编辑、数据源管理和发布接口必须登录并授权。

开放模式的部署约束：

- 服务只部署在内部网络。
- 防火墙和反向代理禁止公网访问运行接口。
- URL 参数可被内部用户修改，因此开放报表被视为允许按已定义参数查询其覆盖范围内的数据。
- 如果以后需要限制调用方范围，只需启用 `Authenticated` 或 `Signed` 模式，不修改模板和参数表结构。

开放模式不取消以下保护：

- 强类型参数和白名单参数。
- 参数化 SQL。
- 只读数据库账号。
- 查询超时、最大行数和列表长度限制。
- 请求体、工作表数、单元格数和导出大小限制。
- 脱敏运行日志。

## 9. 模板导入

### 9.1 支持范围

模板导入只接受 `.xlsx`，支持：

- 多工作表。
- 文本、数字、布尔值、日期和空值。
- Excel 公式。
- 字体、字号、颜色、背景、边框、对齐和自动换行。
- 数字、金额、百分比和日期格式。
- 合并单元格。
- 行高、列宽和隐藏状态。
- 冻结行列。
- 普通单元格图片和浮动图片。

不支持：

- `.xls`。
- 宏和 VBA。
- 图表、数据透视表和迷你图。
- 外部工作簿链接。
- OLE 对象和高级形状。

### 9.2 导入行为

1. 管理员上传 Excel 模板。
2. 后台校验扩展名、文件头、大小和压缩包安全限制。
3. ClosedXML 解析工作簿单元格、样式和公式，Open XML SDK 读取图片及其锚点。
4. 转换成受控范围内的 Univer `IWorkbookData`。
5. 返回导入警告和不支持项清单。
6. React 应用加载快照，由设计人员检查并配置数据绑定。
7. 保存为新的 `RcReportVersion` 草稿。
8. 通过发布校验后才能成为运行版本。

不支持项不能静默丢失。存在警告时允许保存草稿，但发布前必须由设计人员确认。

## 10. 模板绑定与公式

### 10.1 参数绑定

`ParameterBindingJson` 将参数写入指定单元格或命名区域，例如：

```json
{
  "bindings": [
    { "parameterCode": "startDate", "sheetId": "sheet1", "address": "B2" },
    { "parameterCode": "endDate", "sheetId": "sheet1", "address": "D2" },
    { "parameterCode": "hospitalName", "sheetId": "sheet1", "address": "B1" }
  ]
}
```

### 10.2 数据集绑定

`BindingJson` 定义数据集、模板行和字段列，例如：

```json
{
  "datasets": [
    {
      "datasetCode": "deptStats",
      "mode": "repeatRows",
      "sheetId": "sheet1",
      "templateRange": "A5:F5",
      "fields": {
        "A": "departmentName",
        "B": "visitCount",
        "C": "dischargeCount"
      },
      "formulaColumns": ["D", "E", "F"]
    }
  ]
}
```

第一阶段每个绑定块只支持一个连续模板行区域，不支持嵌套分组和交叉表。

运行时：

1. 按数据行数复制模板行。
2. 写入字段值。
3. 复制相对公式和样式。
4. 更新合计区域引用。
5. 触发 Univer 重新计算。
6. 等待计算完成后显示和导出。

公式采用 Excel 语法。第一阶段重点验证：

- 四则运算。
- `SUM`、`COUNT`、`AVERAGE`、`IF`、`IFERROR`。
- 相对引用和绝对引用。
- 跨工作表引用。
- 动态明细行插入后的引用调整。

## 11. 结果导出

结果导出与模板导出分离。第一版只提供“运行结果导出”：

- React 等待 Univer 公式计算完成。
- 前端生成结果工作簿快照，包含最终单元格值和受支持样式。
- 后台验证工作表、单元格、图片和请求大小限制。
- ClosedXML 生成 `.xlsx` 主体，Open XML SDK 写入需要保留的普通图片及锚点。
- 结果导出默认写入计算后的值，避免依赖 Excel/WPS 打开后的重新计算行为。
- 保留标题、样式、边框、合并、行高、列宽、数字格式、冻结区域和页面设置。

模板中的公式仍保存在 `RcReportVersion.SnapshotJson`，不会因为结果导出而被覆盖。

## 12. HTTP 接口

### 12.1 管理接口

管理接口要求 Admin.NET 登录和对应权限：

```text
GET    /api/report-center/manage/reports
POST   /api/report-center/manage/reports
PUT    /api/report-center/manage/reports/{id}
DELETE /api/report-center/manage/reports/{id}
GET    /api/report-center/manage/reports/{id}/versions
POST   /api/report-center/manage/reports/{id}/versions/import
PUT    /api/report-center/manage/reports/{id}/versions/{versionId}/snapshot
POST   /api/report-center/manage/reports/{id}/versions/{versionId}/validate
POST   /api/report-center/manage/reports/{id}/versions/{versionId}/publish
GET    /api/report-center/manage/data-sources
POST   /api/report-center/manage/data-sources
PUT    /api/report-center/manage/data-sources/{id}
DELETE /api/report-center/manage/data-sources/{id}
POST   /api/report-center/manage/data-sources/{id}/test
GET    /api/report-center/manage/reports/{id}/datasets
POST   /api/report-center/manage/reports/{id}/datasets
PUT    /api/report-center/manage/reports/{id}/datasets/{datasetId}
DELETE /api/report-center/manage/reports/{id}/datasets/{datasetId}
GET    /api/report-center/manage/reports/{id}/parameters
POST   /api/report-center/manage/reports/{id}/parameters
PUT    /api/report-center/manage/reports/{id}/parameters/{parameterId}
DELETE /api/report-center/manage/reports/{id}/parameters/{parameterId}
```

### 12.2 开放运行接口

```text
GET  /api/report-center/runtime/{reportCode}/definition
POST /api/report-center/runtime/{reportCode}/execute
POST /api/report-center/runtime/{reportCode}/export
```

`definition` 返回：

- 已发布的 Univer 快照。
- 数据集和参数绑定规则。
- 可见查询表单定义。
- 结合 URL 和默认函数解析出的初始参数。

`execute` 接收：

- URL 来源参数继续放在查询字符串中。
- Form 来源参数放在 JSON 请求体中。

`execute` 返回：

- 按数据集编码组织的结构化数据。
- 不可变参数快照。
- 运行标识和版本号。

`export` 接收前端最终结果工作簿，返回 `.xlsx` 文件流。

## 13. 错误处理

接口返回稳定错误码和关联标识，不向开放页面泄露 SQL、连接串和堆栈：

| 错误码 | 含义 |
|---|---|
| `REPORT_NOT_FOUND` | 报表不存在 |
| `REPORT_NOT_PUBLISHED` | 没有已发布版本 |
| `REPORT_DISABLED` | 报表已停用 |
| `PARAM_REQUIRED` | 必填参数缺失 |
| `PARAM_INVALID` | 参数类型或范围错误 |
| `DEFAULT_EXPRESSION_INVALID` | 默认函数配置错误 |
| `DATASET_TIMEOUT` | 查询超时 |
| `DATASET_QUERY_FAILED` | 查询失败 |
| `TEMPLATE_IMPORT_UNSUPPORTED` | 模板包含不支持内容 |
| `FORMULA_ERROR` | 公式存在错误 |
| `EXPORT_LIMIT_EXCEEDED` | 导出大小超过限制 |

所有运行错误写入 `RcRunLog`，前端显示用户可理解的中文提示和 `CorrelationId`。

## 14. 发布校验

模板发布前必须通过：

- 报表编码唯一且状态启用。
- 至少存在一个启用数据集。
- 所有 SQL 参数都有 `RcDatasetParameter` 映射。
- 所有必填报表参数有来源或默认值。
- 默认函数表达式可以解析。
- 数据源测试成功且为只读账号。
- SQL 为单条只读查询。
- 数据绑定引用的数据集和字段存在。
- 参数绑定引用的单元格或命名区域存在。
- 工作簿不存在阻断发布的 `#REF!`、`#NAME?` 等公式错误。
- 导入警告已经由设计人员确认。

## 15. 测试策略

### 15.1 后端单元测试

- 默认函数和嵌套表达式。
- 参数来源隔离和强类型转换。
- 必填、范围、正则和列表长度验证。
- URL 中的未知参数不会进入 SQL。
- SQL 参数映射和列表参数展开。
- 数据绑定配置验证。
- 敏感运行参数脱敏。

### 15.2 后端集成测试

- 报表草稿、版本和发布事务。
- 已发布版本不会被草稿修改影响。
- 开放运行接口只加载启用且已发布报表。
- 参数化 SQL 可以执行，注入字符串不能改变 SQL 结构。
- 查询超时和最大行数生效。
- Excel 导入产生预期快照和警告。
- 结果导出可以被 ClosedXML 重新打开，普通图片及锚点可以由 Open XML SDK 重新读取。

### 15.3 前端测试

- Univer 初始化、重载和销毁不泄漏实例。
- URL 参数与表单默认值正确显示。
- 数据集写入和动态行展开正确。
- 相对公式复制后引用正确。
- 公式重算完成前不能导出。
- 公式错误能够定位到工作表和单元格。
- 只读预览不能修改模板。

### 15.4 黄金文件测试

仓库保存一份受控 Excel 模板和期望输出：

- 导入后比较工作表、单元格、公式、样式、合并和尺寸。
- 固定参数和固定数据执行后比较公式结果。
- 导出后重新读取并比较单元格值和格式。

## 16. 核心技术验证版

验证版只实现一张“科室工作量统计报表”，但必须贯通真实架构：

1. 通过 Excel 导入模板。
2. 模板和参数定义保存在 SQL Server。
3. URL 传入 `hospitalId`、`deptId`、`personId`。
4. 页面日期默认值使用 `startOfMonth(today())` 和 `now()`。
5. 数据集 SQL 使用命名数据库参数。
6. 后台先返回固定可控的测试数据或只读测试库数据。
7. 前端动态展开明细行。
8. Univer 计算明细和合计公式。
9. 保存草稿、发布并通过开放 URL 重新加载。
10. ClosedXML 和 Open XML SDK 导出最终 `.xlsx`。

验收标准：

- React 页面可以稳定加载 Univer 开源核心，无 Pro 包、水印和许可证请求。
- Excel 模板导入后，支持范围内的格式和公式正确显示。
- URL、页面和系统默认参数解析正确。
- SQL 全程使用数据库参数，无值拼接。
- 修改查询条件后数据和公式正确重算。
- 页面结果和导出 Excel 的值逐项一致。
- 导出文件能被 Excel/WPS 正常打开。
- 保存、发布、重新加载后模板和绑定不丢失。
- 错误公式、缺失参数、查询超时和超限导出都有明确错误。

## 17. 后续迁移顺序

核心验证通过后按以下顺序迁移：

1. 报表、版本、发布和复制管理。
2. 数据源和数据集管理。
3. 参数表单配置和默认函数。
4. 可视化参数绑定和数据集区域绑定插件。
5. 多数据集和多工作表支持。
6. Excel 导入兼容规则扩展。
7. 运行日志、性能保护和运维监控。
8. 按实际需求增加登录访问模式；不预先建设签名模式。

每一阶段都必须产出可运行、可测试、可回滚的功能，不以完整复制 Luck-Report 全部能力作为单次交付目标。

## 18. 最终决策摘要

- 报表 UI 使用 React + Vite，不要求使用 Vue 3。
- 使用 Univer 开源核心，不使用 Univer Pro。
- 公式在前端由 Univer 计算。
- 数据、参数、SQL 和默认函数配置由 .NET 后台和 SQL Server 管理。
- URL 采用内部开放模式，不做签名。
- SQL 只接受数据库中已发布的数据集，并强制参数化和只读账号。
- Excel 导入只作为模板来源，支持明确的受控子集。
- 结果导出由 ClosedXML 和 Open XML SDK 生成，默认输出最终计算值。
- 第一阶段不考虑图表、数据透视、协作、Word 和 PDF。
