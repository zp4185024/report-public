# Univer 报表中心设计

## 1. 文档状态

- 状态：已完成方案评审，待用户复审
- 日期：2026-07-14
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
- 预览公式在浏览器内由 Univer Web Worker 计算，.NET 后台不重复实现 Excel 公式引擎。
- 导出保留模板中的 Excel 公式并设置打开时自动重算，不在模板之外计算公式结果。
- 是否分页由模板设计人员显式配置，系统不根据数据量自动改变展示语义。
- 性能是技术路线的一级约束，模板加载、分页预览、普通导出和大文件导出分别设定可验证的预算。

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
- 预览时由 Univer 计算当前已加载区域的 Excel 风格公式。
- 由模板设计人员配置预览为完整加载或后台分页。
- 提供普通同步导出和大文件异步导出，均保留 Excel 公式。
- 将运行结果导出为 `.xlsx`，由 Excel/WPS 打开后计算公式。
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
- 在 .NET 后台实现或引入第二套 Excel 公式计算引擎。
- 对分页预览中尚未加载的数据执行跨页公式计算。

## 4. 总体架构

```text
内部业务系统
  └─ /report/view/{reportCode}?hospitalId=...&deptId=...&personId=...
                             │
                             ▼
独立 React 报表应用
  ├─ 参数表单
  ├─ Univer 模板设计器
  ├─ Univer 公式引擎 Web Worker
  ├─ 完整或分页只读预览
  └─ 普通导出与大文件导出入口
                             │
                             ▼
HD.ReportCenter.Application
  ├─ 管理接口
  ├─ 开放运行接口
  ├─ 参数解析与默认函数
  ├─ 完整查询与分页查询
  ├─ 模板版本与发布
  ├─ 普通导出与大文件任务
  └─ 缓存、文件存储与审计
                             │
             ┌───────────────┴───────────────┐
             ▼                               ▼
HD.ReportCenter.ReportEngine             SQL Server
  ├─ 模板与绑定模型                     ├─ 报表与版本
  ├─ 参数与分页模型                     ├─ 参数与数据集
  ├─ 普通与流式 Excel 写入              ├─ Univer 快照与导出任务
  └─ 公式复制与引用偏移模型             └─ 运行日志
                                             │
                                             ▼
                                  HIS/LIS/PACS 等只读数据源
```

## 5. 模块职责

### 5.1 React 报表应用

React 应用负责：

- 初始化和销毁 Univer 实例，设计器与预览器按路由拆包。
- 预览器只加载已确定需要的 Univer 开源插件，并使用官方 Worker Preset 执行公式计算。
- 加载已发布模板或设计中的草稿模板。
- 渲染数据库配置的查询表单。
- 根据模板版本中的预览配置执行完整查询或分页查询。
- 将后台返回的数据集批量写入绑定区域。
- 复制明细模板行并调整相对公式引用。
- 每批数据写入完成后只触发一次公式重算。
- 检查可见区域中的公式错误。
- 保存模板快照。
- 发起普通导出或大文件导出任务，不向后台回传前端公式计算结果。

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
- 按模板配置执行完整查询或数据库分页查询，不根据运行行数自动切换模式。
- 使用 ClosedXML 完成单元格、样式和公式转换，使用 Open XML SDK 补充处理普通图片等绘图部件。
- 使用 `ZipArchive + XmlWriter` 直接流式写入 Open XML 包，维护导出任务、进度、取消、下载和过期清理；使用 Open XML SDK 做结构与公式回读校验。
- 缓存已发布模板和压缩响应，使用内容哈希区分缓存版本。
- 返回稳定的请求和响应 DTO，不直接暴露数据库实体。

### 5.3 `HD.ReportCenter.ReportEngine`

引擎层不依赖 HTTP、Admin.NET 页面和具体数据库表，负责：

- 参数定义、参数值和数据集结果模型。
- 安全默认函数求值。
- 模板数据绑定规则。
- Univer 快照与 Excel 转换所需的中立模型。
- 运行结果、公式错误和导入警告模型。
- 模板公式复制和相对引用偏移，不执行公式求值。
- 普通导出与大文件流式导出的中立模型。
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
| `PublishedVersionId` | bigint null | 当前已发布版本 |
| `Status` | tinyint | 启用、停用 |
| `RowVersion` | rowversion | 并发更新控制 |

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
| `PreviewSettingsJson` | nvarchar(max) | 完整或分页预览配置 |
| `ExportSettingsJson` | nvarchar(max) | 普通和大文件导出配置 |
| `DefinitionSchemaVersion` | int | 报表定义结构版本 |
| `UniverVersion` | nvarchar(32) | 保存快照时使用的 Univer 版本 |
| `ContentHash` | varchar(64) | 模板、绑定和运行配置的 SHA-256 |
| `SourceFileId` | bigint null | 原始 Excel 文件引用，仅用于追溯 |
| `SourceFileName` | nvarchar(256) null | 原始文件名 |
| `SourceFileHash` | varchar(64) null | SHA-256 |
| `Status` | tinyint | 草稿、已发布、已停用 |
| `PublishedTime` | datetime2 null | 发布时间 |
| `RowVersion` | rowversion | 并发更新控制 |

约束：

- `(ReportId, VersionNo)` 建立唯一索引。
- 同一报表同一时刻只有一个当前已发布版本。
- 数据集、参数和参数映射都归属于版本；发布时根据完整版本图计算 `ContentHash`，并在事务中更新整个版本状态和 `RcReport.PublishedVersionId`。
- 修改草稿不影响当前已发布版本。
- 已发布版本不可直接修改；修改时复制出新的草稿版本。

### 6.3 `RcDataSource`

保存业务查询数据源。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `Code` | nvarchar(64) | 唯一编码 |
| `Name` | nvarchar(128) | 数据源名称 |
| `DbType` | nvarchar(32) | SQL Server、Oracle、MySQL 等 |
| `ConnectionString` | nvarchar(max) | 内部系统使用的明文连接串 |
| `DefaultTimeoutSeconds` | int | 默认查询超时 |
| `DefaultMaxRows` | int | 默认最大返回行数 |
| `Status` | tinyint | 启用、停用 |
| `RowVersion` | rowversion | 并发更新控制 |

约束：

- 生产环境只配置只读数据库账号。
- 按内部系统约束不对连接串加密。
- 运行接口和运行日志不得返回或记录连接串；管理页面显示时遮盖密码部分。

### 6.4 `RcDataset`

保存报表数据集和查询语句。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `ReportVersionId` | bigint | 所属报表版本 |
| `DataSourceId` | bigint | 数据源 |
| `Code` | nvarchar(64) | 版本内唯一数据集编码 |
| `Name` | nvarchar(128) | 名称 |
| `Purpose` | nvarchar(32) | `Main`、`ParameterOption` |
| `QueryText` | nvarchar(max) | 只读参数化 SQL |
| `FieldSchemaJson` | nvarchar(max) null | 最近验证的字段名和类型 |
| `LastValidatedTime` | datetime2 null | 最近字段验证时间 |
| `TimeoutSeconds` | int | 查询超时 |
| `MaxRows` | int | 最大返回行数 |
| `Status` | tinyint | 启用、停用 |
| `RowVersion` | rowversion | 并发更新控制 |

约束：

- `(ReportVersionId, Code)` 建立唯一索引。
- 只允许单条 `SELECT` 或以 `WITH` 开始的只读查询。
- 禁止多语句、DDL、DML 和事务控制语句。
- 只读数据库账号是最终安全边界；SQL 文本检查作为附加保护。

### 6.5 `RcReportParameter`

保存报表运行参数定义。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `ReportVersionId` | bigint | 所属报表版本 |
| `Code` | nvarchar(64) | 版本内唯一参数编码 |
| `DisplayName` | nvarchar(128) | 页面名称 |
| `DataType` | nvarchar(32) | string、long、decimal、date、datetime、bool、stringList、longList |
| `ControlType` | nvarchar(32) | input、select、date、dateRange、hidden |
| `SourceType` | nvarchar(16) | Url、Form、System、Fixed |
| `UrlKey` | nvarchar(64) null | URL 键名 |
| `DefaultExpression` | nvarchar(256) null | 默认函数表达式 |
| `FixedValue` | nvarchar(1024) null | 固定值 |
| `AllowMultiple` | bit | 选择参数是否允许多选 |
| `OptionDatasetId` | bigint null | 下拉选项数据集 |
| `OptionValueField` | nvarchar(64) null | 选项实际值字段 |
| `OptionLabelField` | nvarchar(64) null | 选项显示字段 |
| `IsVisible` | bit | 是否显示在查询表单 |
| `IsReadOnly` | bit | 是否只读 |
| `IsRequired` | bit | 是否必填 |
| `AllowUrlPrefill` | bit | Form 参数是否允许由 URL 预填 |
| `ValidationJson` | nvarchar(max) null | 长度、范围、正则和选项限制 |
| `DisplayOrder` | int | 表单顺序 |
| `Status` | tinyint | 启用、停用 |
| `RowVersion` | rowversion | 并发更新控制 |

约束：

- `(ReportVersionId, Code)` 建立唯一索引。
- 运行接口忽略数据库中未定义的 URL 和表单参数。
- 同一参数只能有一个来源类型，来源之间不能相互覆盖。
- 下拉参数的级联关系由选项数据集使用的参数映射推导，不额外维护级联关系表。

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

### 6.7 `RcExportJob`

保存大文件异步导出任务。

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | bigint | 主键 |
| `JobNo` | varchar(64) | 不可预测的任务编号 |
| `ReportId` | bigint | 报表 |
| `ReportVersionId` | bigint | 固定使用的已发布版本 |
| `ParameterSnapshotJson` | nvarchar(max) | 不可变的强类型参数快照 |
| `Status` | tinyint | 等待、执行中、成功、失败、已取消、已过期 |
| `ProgressRows` | bigint | 已写入行数 |
| `TotalRows` | bigint null | 可选总行数，不强制预先统计 |
| `FileId` | bigint null | Admin.NET 文件存储引用 |
| `FileName` | nvarchar(256) null | 下载文件名 |
| `FileSize` | bigint null | 文件字节数 |
| `RequestedTime` | datetime2 | 创建时间 |
| `StartedTime` | datetime2 null | 开始时间 |
| `CompletedTime` | datetime2 null | 完成时间 |
| `ExpiresTime` | datetime2 null | 文件和参数快照过期时间 |
| `ErrorCode` | nvarchar(64) null | 稳定错误码 |
| `ErrorMessage` | nvarchar(1024) null | 脱敏错误摘要 |
| `RowVersion` | rowversion | 状态抢占和并发控制 |

约束：

- `JobNo` 建立唯一索引。
- 任务固定使用创建时的已发布版本和参数快照，后续发布不影响任务。
- 大文件任务通过数据库状态持久化，应用重启后可以恢复等待任务并识别中断任务。
- 同一实例限制并发大文件任务数，避免同时占满数据库连接、CPU 和磁盘。

### 6.8 `RcRunLog`

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

运行接口采用内部开放模式：

- 运行和导出接口允许匿名调用。
- 不实现 URL 签名、调用方密钥和应用层来源授权。
- 模板管理、SQL 编辑、数据源管理和发布接口必须登录并授权。

开放模式的部署约束：

- 服务只部署在内部网络。
- 防火墙和反向代理禁止公网访问运行接口。
- URL 参数可被内部用户修改，因此开放报表被视为允许按已定义参数查询其覆盖范围内的数据。

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

### 10.3 预览配置

是否分页是模板语义的一部分，由模板设计人员在发布前明确选择。系统支持：

- `Full`：一次加载完整结果，适合总览、统计表和已知小数据量报表。
- `Paged`：后台分页查询，适合明细列表；Univer 只持有和计算当前页。

配置保存在 `RcReportVersion.PreviewSettingsJson`：

```json
{
  "mode": "Paged",
  "primaryDatasetCode": "deptStats",
  "pageSize": 200,
  "countMode": "None",
  "fullPreviewMaxRows": 5000
}
```

规则：

- 系统不根据实际行数把 `Full` 自动切换为 `Paged`，也不反向切换。
- `Full` 超过 `fullPreviewMaxRows` 时返回明确错误，由设计人员调整模板或上限。
- `Paged` 的 `pageSize` 由设计人员配置；第一版只允许一个主数据集驱动分页。
- `countMode=None` 只返回 `hasMore`，避免为大查询强制执行总数统计；`Exact` 才执行精确总数查询。
- 分页查询必须有稳定排序，同一参数下连续翻页不能重复或遗漏记录。
- 分页预览只计算当前页公式，不读取、模拟或汇总未加载页面。

预览运行时：

1. 按数据行数复制模板行。
2. 写入字段值。
3. 复制相对公式和样式。
4. 一批数据写入完成后触发一次 Univer Worker 公式重算。
5. 等待当前已加载区域计算完成后显示。

公式采用 Excel 语法。第一阶段重点验证：

- 四则运算。
- `SUM`、`COUNT`、`AVERAGE`、`IF`、`IFERROR`。
- 相对引用和绝对引用。
- 跨工作表引用。
- 动态明细行插入后的引用调整。

## 11. 性能与结果导出

### 11.1 传输和渲染

- 已发布模板按 `ReportId + VersionNo + ContentHash` 缓存，模板未变化时使用 ETag 避免重复传输。
- 复用 Admin.NET 已有 Brotli/Gzip 响应压缩，不重复建设压缩中间件。
- 分页数据响应使用字段数组加二维值数组，避免每一行重复传输字段名。
- 翻页时复用 Univer 实例，只替换绑定区域；不重新创建整个工作簿。
- 数据批量写入后只触发一次重算，不逐单元格触发公式链。
- 设计器和预览器使用不同前端入口，预览器不加载图表、协作、评论和其他未使用插件。

### 11.2 普通导出

普通导出是同步请求，适合设计人员确认的数据量范围：

- 后台重新执行完整查询，不复用当前分页数据，也不接收前端计算结果。
- ClosedXML 生成主体，Open XML SDK 补充图片、计算属性和必要部件。
- 保留模板样式、合并、行高、列宽、数字格式、冻结区域和公式。
- 默认限制为 `20000` 行、`500000` 个单元格和 `60` 秒，实际值从模板版本配置读取。
- 超过限制返回 `EXPORT_REQUIRES_LARGE_MODE`，不在同一请求中重复查询或自动转换。

### 11.3 大文件导出

大文件导出是持久化后台任务：

1. 创建 `RcExportJob`，固定报表版本和强类型参数快照。
2. 后台使用 `DbDataReader` 顺序读取数据，不构造完整结果集合。
3. 使用 `ZipArchive + XmlWriter` 逐部件、逐行写入工作表；不要仅依赖 `OpenXmlWriter`，实测 Open XML SDK 的包层仍会缓存工作表部件并显著抬高峰值内存。
4. 按模板行复制样式和公式，并根据目标行调整相对引用。
5. 定期更新已写入行数，支持进度查询和取消。
6. 完成后写入 Admin.NET 文件存储，通过文件流和 HTTP Range 下载。
7. 超过 Excel 单工作表 `1048576` 行时按 `rowsPerSheet` 拆分工作表。
8. 到期后清理导出文件和任务中的参数快照。

第一版不重写跨拆分工作表的全局统计公式。需要这类公式的模板应控制单表数据量，或由 Excel 文件打开后的公式自行引用对应工作表。

### 11.4 公式导出规则

导出只复制公式，不在浏览器、.NET 后台或独立服务中计算公式结果。生成工作簿时设置：

```text
CalculationMode = Auto
ForceFullCalculation = true
FullCalculationOnLoad = true
```

Excel/WPS 打开文件后完成公式计算。普通和大文件导出使用同一套公式复制、相对引用偏移和发布校验规则，区别只在同步/异步和内存/流式写入方式。

### 11.5 模板导出配置

配置保存在 `RcReportVersion.ExportSettingsJson`：

```json
{
  "defaultMode": "Normal",
  "normalMaxRows": 20000,
  "normalMaxCells": 500000,
  "normalTimeoutSeconds": 60,
  "largeExportEnabled": true,
  "rowsPerSheet": 1000000,
  "fileRetentionHours": 24
}
```

普通和大文件导出由用户或模板默认值选择，系统不需要为了判断模式先执行一次完整总数查询。

### 11.6 核心性能预算

验证版在目标部署机器上至少记录并达到以下初始目标：

| 链路 | 数据规模 | 初始目标 |
|---|---|---|
| 已缓存模板加载 | 不含业务查询 | 局域网 P95 不超过 300ms |
| 分页写入与公式重算 | 200 行、50 列 | API 返回后 1 秒内完成显示 |
| 普通导出 | 20000 行、20 列 | 30 秒内完成，进程增量内存不超过 512MB |
| 大文件导出 | 500000 行、20 列 | 全程无内存溢出，进程增量内存不超过 512MB，持续报告进度 |

同时记录模板字节数、查询耗时、序列化耗时、响应字节数、前端写入耗时、公式耗时、导出行数、导出耗时和峰值内存。目标硬件基线确定后，核心链路性能回退超过 20% 视为回归。

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
GET    /api/report-center/manage/reports/{id}/versions/{versionId}/datasets
POST   /api/report-center/manage/reports/{id}/versions/{versionId}/datasets
PUT    /api/report-center/manage/reports/{id}/versions/{versionId}/datasets/{datasetId}
DELETE /api/report-center/manage/reports/{id}/versions/{versionId}/datasets/{datasetId}
GET    /api/report-center/manage/reports/{id}/versions/{versionId}/parameters
POST   /api/report-center/manage/reports/{id}/versions/{versionId}/parameters
PUT    /api/report-center/manage/reports/{id}/versions/{versionId}/parameters/{parameterId}
DELETE /api/report-center/manage/reports/{id}/versions/{versionId}/parameters/{parameterId}
```

### 12.2 开放运行接口

```text
GET  /api/report-center/runtime/{reportCode}/definition
POST /api/report-center/runtime/{reportCode}/execute
POST /api/report-center/runtime/{reportCode}/exports/normal
POST /api/report-center/runtime/{reportCode}/exports/large
GET  /api/report-center/runtime/export-jobs/{jobNo}
POST /api/report-center/runtime/export-jobs/{jobNo}/cancel
GET  /api/report-center/runtime/export-jobs/{jobNo}/download
```

`definition` 返回：

- 已发布的 Univer 快照。
- 数据集和参数绑定规则。
- 可见查询表单定义。
- 模板确定的完整或分页预览配置。
- 普通和大文件导出配置。
- 结合 URL 和默认函数解析出的初始参数。

`execute` 接收：

- URL 来源参数继续放在查询字符串中。
- Form 来源参数放在 JSON 请求体中。
- `Paged` 模板传入页码；页大小使用已发布模板配置，客户端不能任意放大。

`execute` 返回：

- 按数据集编码组织的字段数组和二维值数组。
- 分页模板返回页码、页大小、`hasMore`，只有 `countMode=Exact` 才返回总行数。
- 不可变参数快照。
- 运行标识和版本号。

普通导出重新执行完整查询并同步返回 `.xlsx` 文件流。大文件导出创建 `RcExportJob` 并立即返回 `jobNo`，由任务查询接口提供进度和最终下载状态。两种导出都不接收前端工作簿或前端公式结果。

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
| `PREVIEW_LIMIT_EXCEEDED` | 完整预览超过模板配置上限 |
| `PAGING_ORDER_REQUIRED` | 分页主数据集缺少稳定排序 |
| `TEMPLATE_IMPORT_UNSUPPORTED` | 模板包含不支持内容 |
| `FORMULA_ERROR` | 公式存在错误 |
| `EXPORT_LIMIT_EXCEEDED` | 导出大小超过限制 |
| `EXPORT_REQUIRES_LARGE_MODE` | 普通导出超过模板阈值，应使用大文件导出 |
| `EXPORT_JOB_FAILED` | 大文件导出任务失败 |
| `EXPORT_JOB_EXPIRED` | 大文件导出文件已过期 |

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
- `PreviewSettingsJson` 明确选择 `Full` 或 `Paged`。
- `Paged` 模式配置了主数据集、合法页大小和稳定排序。
- `Full` 模式配置了大于零的完整预览上限。
- `ExportSettingsJson` 的普通导出阈值、拆表行数和文件保留时间合法。
- 工作簿不存在阻断发布的 `#REF!`、`#NAME?` 等公式错误。
- 公式属于 Univer 预览和 Excel/WPS 打开重算都支持的交集范围。
- 导入警告已经由设计人员确认。

## 15. 测试策略

### 15.1 后端单元测试

- 默认函数和嵌套表达式。
- 参数来源隔离和强类型转换。
- 必填、范围、正则和列表长度验证。
- URL 中的未知参数不会进入 SQL。
- SQL 参数映射和列表参数展开。
- 数据绑定配置验证。
- 完整和分页预览配置验证。
- 公式复制和相对引用偏移，不执行公式求值。
- 普通与大文件导出阈值判断。
- 敏感运行参数脱敏。

### 15.2 后端集成测试

- 报表草稿、版本和发布事务。
- 已发布版本不会被草稿修改影响。
- 开放运行接口只加载启用且已发布报表。
- 参数化 SQL 可以执行，注入字符串不能改变 SQL 结构。
- 查询超时和最大行数生效。
- 分页查询顺序稳定，`countMode=None` 不执行总数查询。
- `Full` 超过模板预览上限时失败且不会自动切换分页。
- Excel 导入产生预期快照和警告。
- 普通导出保留公式并设置打开重算属性。
- 大文件导出使用顺序读取和前向写入，任务可恢复、取消、下载和过期。
- 结果导出可以被 ClosedXML 重新打开，公式、普通图片及锚点可以由 Open XML SDK 重新读取。

### 15.3 前端测试

- Univer 初始化、重载和销毁不泄漏实例。
- 公式计算运行在 Web Worker，重算期间页面仍可响应滚动和取消操作。
- URL 参数与表单默认值正确显示。
- 数据集写入和动态行展开正确。
- 模板的 `Full` 和 `Paged` 配置分别生效，系统不会自动切换。
- 翻页只替换绑定区域，不重建整个 Univer 实例。
- 相对公式复制后引用正确。
- 公式错误能够定位到工作表和单元格。
- 只读预览不能修改模板。

### 15.4 黄金文件测试

仓库保存一份受控 Excel 模板和期望输出：

- 导入后比较工作表、单元格、公式、样式、合并和尺寸。
- 固定参数和固定数据执行后比较公式结果。
- 导出后重新读取并比较公式文本、样式和工作簿打开重算属性。

## 16. 核心技术验证版

验证版只实现一张“科室工作量统计报表”，但必须贯通真实架构：

1. 通过 Excel 导入模板。
2. 模板和参数定义保存在 SQL Server。
3. URL 传入 `hospitalId`、`deptId`、`personId`。
4. 页面日期默认值使用 `startOfMonth(today())` 和 `now()`。
5. 数据集 SQL 使用命名数据库参数。
6. 后台先返回固定可控的测试数据或只读测试库数据。
7. 模板显式选择 `Paged`，配置主数据集和每页行数；另用同一模板验证 `Full` 配置。
8. 前端批量展开当前页明细行，Univer Web Worker 计算当前页公式。
9. 保存草稿、发布并通过开放 URL 重新加载，数据集和参数随版本冻结。
10. 普通导出保留公式并设置 Excel/WPS 打开重算。
11. 大文件任务流式导出至少 500000 行，持续记录进度且内存保持稳定。

验收标准：

- React 页面可以稳定加载 Univer 开源核心，无 Pro 包、水印和许可证请求。
- Excel 模板导入后，支持范围内的格式和公式正确显示。
- URL、页面和系统默认参数解析正确。
- SQL 全程使用数据库参数，无值拼接。
- 修改查询条件后数据和公式正确重算。
- 分页由模板配置决定，系统不因实际行数自动切换。
- 分页切换后只计算当前页；完整模式计算全部已加载数据。
- 普通和大文件导出都保留正确公式，后台不计算公式结果。
- 导出文件能被 Excel/WPS 正常打开，并在打开后自动重算公式。
- 保存、发布、重新加载后模板和绑定不丢失。
- 大文件导出不阻塞普通 HTTP 请求，支持进度、取消、下载和过期。
- 模板加载、当前页渲染、普通导出和大文件内存达到第 11.6 节预算。
- 错误公式、缺失参数、查询超时、完整预览超限和普通导出超限都有明确错误。

## 17. 后续迁移顺序

核心验证通过后按以下顺序迁移：

1. 报表、版本、发布、复制和模板性能配置管理。
2. 数据源、版本化数据集和字段结构缓存管理。
3. 参数表单、下拉数据集和默认函数。
4. 可视化参数绑定、数据区域绑定和完整/分页预览配置。
5. 普通导出、大文件任务、进度和文件清理。
6. 多数据集和多工作表支持。
7. Excel 导入兼容规则扩展。
8. 性能指标、运行日志和运维监控贯穿每一阶段，不作为末期补充。

每一阶段都必须产出可运行、可测试、可回滚的功能，不以完整复制 Luck-Report 全部能力作为单次交付目标。

## 18. 最终决策摘要

- 报表 UI 使用 React + Vite，不要求使用 Vue 3。
- 使用 Univer 开源核心，不使用 Univer Pro。
- 预览公式由 Univer Web Worker 计算；分页预览只计算当前页。
- 是否分页由模板设计人员配置，系统不自动判断或切换。
- 数据、参数、SQL 和默认函数配置由 .NET 后台和 SQL Server 管理。
- 数据集、参数和参数映射归属于报表版本，发布后整体不可变。
- 数据源连接串按内部系统要求明文保存，但不通过运行接口和日志返回。
- URL 采用内部开放模式，不做签名。
- SQL 只接受数据库中已发布的数据集，并强制参数化和只读账号。
- Excel 导入只作为模板来源，支持明确的受控子集。
- 普通导出同步生成，大文件导出异步流式生成。
- 导出保留模板公式，由 Excel/WPS 打开后自动重算；后台不实现公式计算。
- 性能预算覆盖模板存储与传输、分页渲染、公式 Worker、普通导出和大文件内存。
- 第一阶段不考虑图表、数据透视、协作、Word 和 PDF。
