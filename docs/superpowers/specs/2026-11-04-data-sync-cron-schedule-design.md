# 数据同步调度增加 Cron 表达式类型 — 设计文档

日期：2026-11-04
状态：已与用户确认方向（轻量前端方案 + Furion 原生 Cron 触发器）；已按一轮规范审查修复（2026-11-04）

## 1. 背景与目标

数据同步任务的调度目前只支持三种类型：`Manual`（手动）、`Daily`（每天定时 HH:mm）、`Interval`（固定间隔 N 分钟）。本设计新增第四种类型 `Cron`：用户填写标准 Cron 表达式，任务按表达式周期自动执行。

约束与决策（已与用户确认）：

- 前端选择器采用轻量方案：表达式输入框 + cronstrue 中文释义 + cron-parser 未来执行时间预览，不引入 antd / 不做可视化构建器。
- 后端不手搓 Cron 到期判定，直接使用 Furion 定时调度的动态作业 + Cron 触发器能力（仓库 Admin.NET.Core 已有成熟用法可参照）。
- 现有 Manual / Daily / Interval 三种类型的扫描逻辑完全不动。

## 2. 现状（关键代码位置）

| 位置 | 说明 |
| --- | --- |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncEntities.cs` | `PocSyncTask` 实体（ScheduleType/ScheduleTime/IntervalMinutes 列） |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncContracts.cs` | `PocSyncScheduleDefinition(Type, Time, IntervalMinutes)`（28 行）、`PocSyncTaskDocument`（198-211）、`PocSyncTaskInput`（218-231）、同步包契约 `PocDataSyncPackage`（102-115） |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncService.cs` | `ScheduleTypes` 白名单（42 行）、保存 `SaveAsync` 校验/组装（119 行起，白名单判断约 1114 行）、`IsTaskDue`（约 894 行）、`ExecuteTaskNowAsync`（约 844 行）、`ToTaskDocument`（约 1411 行） |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncStore.cs` | `PocSyncTaskMutation`（4-16 行）、`SaveTaskAsync` 的 insert 逐列赋值（约 88-104）与 update `SetColumns` 逐列更新（约 108-120+） |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncJob.cs` | 每分钟扫描作业（`job_rcDataSync`，`[PeriodMinutes(1)]`，`RunOnStart=false`），`ExecuteAsync` 三层异常处理（约 60-77 行） |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncPackageService.cs` | 同步包 `ExportAsync` 用实体平铺列组装包（92-114 行，调度取 111-113）；导入自有路径落库（约 365-382，`IsEnabled:true`）；导入 `ValidateSchedule`（约 580，三参签名） |
| `ReportWeb/src/DataSyncTaskPage.tsx` | 设计器第 3 步"目标设置与调度"、列表页调度摘要 |
| `ReportWeb/src/syncTaskDesign.ts` | `SCHEDULE_TYPE_OPTIONS` 白名单 |
| `Admin.NET/Admin.NET.Core/Service/Job/SysJobService.cs` | Furion `ISchedulerFactory` 动态作业/触发器参照用法（AddJob 117 / RemoveJob 175 / GetJob / AddTrigger 204 / UpdateTrigger 228） |
| `Admin.NET/Admin.NET.Core/Service/Job/DbJobPersistence.cs` | 调度持久化：动态作业写入 SysJobDetail/SysJobTrigger；启动 PreloadAsync 清理"代码中不存在"的 BuiltIn 作业（43-53）；`NotSupportedException` 只针对非 BuiltIn 非 Script（175-184） |
| `Admin.NET/Admin.NET.Core/Entity/SysJobDetail.cs` / `SysJobTrigger.cs` | JobId/TriggerId 列长均 64 |
| `Admin.NET/Admin.NET.Web.Core/Startup.cs` | `AddSchedule` + `AddPersistence<DbJobPersistence>`（约 81）；`[AppStartup(int.MaxValue)]` 最后执行 |

Furion 版本 4.9.9.13（`Admin.NET.Core.csproj`），Cron 解析内核是内嵌的 **TimeCrontab**（不是 Cronos）：默认格式 5 段（分 时 日 月 周），天/周域支持 `?`、`L`、`W`、`#` 等拓展，秒位需显式 `CronStringFormat.WithSeconds`（默认拒绝 6 段）。`CronTrigger` 构造函数内部立即 `Crontab.Parse`，构造即校验可行。

## 3. 方案总览

```
前端设计器(第3步) ──保存──▶ 后端校验表达式 ──▶ 落库(RcSyncTask.ScheduleCron)
                                   │
                                   ├─ 启用 + Cron ─▶ 动态注册/更新 Furion 作业与 Cron 触发器
                                   └─ 否则         ─▶ 移除对应动态作业
同步包导入成功 ──▶ 走同一注册/摘除钩子
服务启动 ──▶ PocDataSyncJob 首轮扫描前一次性对账：补挂所有"启用且 Cron"任务（幂等）
触发器到点 ─▶ PocSyncCronJob ─▶ 复用 ExecuteTaskNowAsync（沿用 Running 防重）
```

明确不做的事（YAGNI）：

- 不改 Manual/Daily/Interval 的扫描行为，不加可视化 cron 构建器，不引入 antd。
- 不做分布式/多实例协调（当前单实例部署）。
- 不适配框架自带"定时任务"管理页对这类作业的编辑（仅作为运行观察入口，见 §5 边界）。

## 4. 详细设计

### 4.1 数据模型与契约

- `PocSyncTask` 新增列：`ScheduleCron`（`string?`，Length = 64，`IsNullable = true`，描述"Cron 调度表达式"）。CodeFirst 启动自动补列，无需迁移脚本。
- `PocSyncScheduleDefinition` 末尾追加可选参数 `string? CronExpression = null`（保持 DefinitionJson 与实体列一致；旧 JSON 反序列化天然兼容，`SerializeDefinition` 保留 null 不影响）。
- `PocSyncTaskInput`（末参可选 `Source = null` 之后）、`PocSyncTaskMutation`、`PocSyncTaskDocument` 追加 `ScheduleCron`（Document 对外 camelCase `scheduleCron`）。新增参数放参数列表末尾并带默认值；仓内既有位置参数构造点（`PocDataSyncService.cs`、`PocDataSyncPackageService.cs`、测试等）可编译兼容，受影响处同步更新。
- **同步包契约**：`PocDataSyncPackage` 新增可选字段 `ScheduleCron`（见 §4.5——包走平铺列，不经 `PocSyncScheduleDefinition`）。
- 调度类型白名单：`PocDataSyncService.ScheduleTypes` 与同步包校验处均扩为 `Manual/Daily/Interval/Cron`。
- `IsTaskDue`：为 `Cron` 增加显式 `case "Cron": return false;`（带注释：Cron 类型由 Furion 触发器直接驱动，扫描作业永不触发它）；未知类型仍走 default 返回 false。
- `ToTaskDocument` 从实体列取值，追加列即透出 `scheduleCron`。

### 4.2 保存与导入校验

- 表达式口径（前后端一致）：**只支持标准 5 段（分 时 日 月 周）**，天/周域的 `?`、`L`、`W`、`#` 等拓展以 Furion 内嵌 TimeCrontab 默认格式为准；6 段（含秒）默认被 TimeCrontab 拒绝，不支持。前端提示文案："标准 5 段：分 时 日 月 周（支持天/周域的 ?）"。
- 保存任务（`PocDataSyncService.SaveAsync` 校验段）与同步包导入（`PocDataSyncPackageService.ValidateSchedule`）都增加 Cron 分支：
  - `ScheduleType == "Cron"` 时表达式必填、非空白，且必须能通过 Furion 触发器解析：用 `Triggers.Cron(表达式)`（或 `new CronTrigger(表达式)`）构造并 try/catch，异常转成含原始表达式与原因的业务错误文案；不直接引第三方 Cron 库。
  - 非 Cron 类型时 `ScheduleCron` 置空（与现有 `ScheduleTime`/`IntervalMinutes` 按类型裁剪的规则一致）。
- **实现第一步先做小样验证**：确认 `Triggers.Cron("*/2 * * * *")`、`"0 2 * * ?"`、6 段串的实际解析行为，再固化测试预期与前端文案（TimeCrontab 的拓展字符支持范围以实测为准）。
- `PocDataSyncStore.SaveTaskAsync` 的 insert 逐列赋值与 update `SetColumns` 映射必须补 `ScheduleCron`——漏改会造成"保存成功但列永远为空"的静默丢数，列入验收断言。

### 4.3 调度执行（Furion 动态作业）

新增（建议放在 `CoreValidation/`，如 `PocDataSyncCronScheduler.cs`，命名随实现）：

1. **作业类 `PocSyncCronJob : IJob`，显式声明固定宿主 `[JobDetail("job_rcSyncCronHost")]`**。
   - 依据：Furion `ScanToBuilders` 对所有 `IJob` 实现类扫描构建（无特性也走默认 `JobBuilder.Create(jobType)`），不加特性会在每次重启生成一行"幽灵作业"；显式特性让它成为已知宿主作业（无触发器、永不运行、框架定时任务页可见一行，属预期）。
   - 每个任务一个动态作业实例：`JobBuilder.Create(typeof(PocSyncCronJob)).SetJobId(...).SetDescription(任务名)` 组装后，经 `ISchedulerFactory.AddJob(jobBuilder, 触发器)` 重载注册（或 AddJob 后走 `scheduler.AddTrigger(...)`，参照 `SysJobService.cs:204` 模式）。注意 `JobBuilder` 本身没有 `AddTrigger` 方法（已反编译核实），触发器用 `Triggers.Cron(表达式).SetTriggerId(...)` 构建后作为 AddJob 参数传入。
   - **截断派生 ID 的反查**：任务码被截断后无法从 JobId 直接反解完整任务码，作业体按"对全部启用 Cron 任务重算派生 ID 反查匹配"实现（每次触发任务数量有限，开销可忽略）。
   - **派生 ID 总长必须 ≤ 64**（SysJobDetail/SysJobTrigger 列长）：任务编码最长 64，前缀预算不足时会持久化超长报错。约束：前缀取 `job_rcSync_`/`trigger_rcSync_`，任务码部分超预算时截断并追加 8 位稳定哈希（如 SHA256 前 8 位）；注册前自行校验长度，超预算走截断规则。
   - **作业体异常处理必须照抄 `PocDataSyncJob.ExecuteAsync` 的三层结构**：`SYNC_TASK_ALREADY_RUNNING` 静默跳过、`OperationCanceledException` 直接结束、其余兜底捕获（一次异常不得打断后续调度）。作业体从 `context.JobDetail.JobId` 解出任务编码（按派生规则还原），独立作用域解析同步服务调用 `ExecuteTaskNowAsync(taskCode)`；Running 防重已有，人工触发与定时触发互斥安全。
   - 备选（实现时可小验证后替换）：`JobBuilder.Create(委托)` 委托作业直接闭包捕获任务编码，免去 JobId 解码约定与宿主类；持久化行为需同样验证（重启即清理重建，自洽）。
2. **调度网关接口**：把对 `ISchedulerFactory` 的 AddJob/RemoveJob/GetJob/ContainsJob 封装成小接口（便于单测伪造），实现类里 try/catch 并记日志。
3. **生命周期维护点（全部走同一个注册/摘除服务方法）**：
   - 保存成功后（`PocDataSyncService.SaveAsync` 收尾）：`启用 && Cron` → 以"先 RemoveJob 后 AddJob"的幂等方式注册当前表达式；其余情况 → RemoveJob（作业不存在时吞掉异常）。
   - **同步包导入成功后**（`PocDataSyncPackageService` 导入落库路径，`IsEnabled:true`）→ 同一钩子注册；缺失此步会导致导入的 Cron 任务静默不跑。
   - 删除任务：RemoveJob。
   - 注册失败不回滚保存/导入、不抛给前端（DB 已是事实源，下次保存/导入/重启自愈），记 Error 日志。
4. **启动对账**：在 `PocDataSyncJob` 首轮扫描开头执行一次性对账（该作业由调度器自身驱动，`[PeriodMinutes(1), RunOnStart=false]`，启动后 ≤1 分钟必跑，天然保证调度器就绪；用标志位保证只跑一次）：把所有"启用且 Cron"任务批量补挂。验收标准是"重启后启用的 Cron 任务自动恢复触发"。

**与 `DbJobPersistence` 的交互（已核实，安全）**：运行时 `AddJob` 把作业持久化到 SysJobDetail/SysJobTrigger（`JobDetail` 无 CreateType 属性，`Adapt` 落库取实体默认值 BuiltIn）。下次重启 `PreloadAsync` 的陈旧清理删除"代码中不存在的 BuiltIn 作业"，不会落入 `NotSupportedException` 分支，随后由启动对账重建——自洽，无残留风险。框架"定时任务"管理页能看到这些作业及运行记录（SysJobTriggerRecord），作为观察入口。

### 4.4 前端（ReportWeb）

新增依赖：`cronstrue`（含 zh_CN locale）与 `cron-parser`（小体积纯函数库）。

- `types.ts`：`SyncScheduleDefinition` 及三处含 `scheduleType/scheduleTime/intervalMinutes` 的接口追加 `scheduleCron?: string | null`；`DataSyncPackage` 增加 `scheduleCron?`；625 行注释"Type 为 Manual/Daily/Interval"更新为含 Cron。
- `syncTaskDesign.ts`：`SCHEDULE_TYPE_OPTIONS` 增加 `{ value: 'Cron', label: 'Cron 表达式' }`。
- `DataSyncTaskPage.tsx`：
  - 草稿状态增加 `scheduleCron`，编辑回填与新建默认空。
  - 设计器第 3 步：类型选 Cron 后显示表达式输入框，下方实时显示 cronstrue 中文释义 + 未来 3 次执行时间（本地时区，cron-parser 计算，默认 5 段格式）；解析失败显示错误样式与提示。
  - **预览尽力而为**：cron-parser 算不了的表达式（如含拓展字符解析失败）只展示输入值并提示"以服务端校验为准"，不阻塞输入；后端是唯一裁决者。
  - 本地校验：Cron 类型必须非空；错误文案与后端口径一致（标准 5 段）。
  - 保存组装：仅 Cron 类型提交 `scheduleCron`，其余类型置 null（沿用现有裁剪写法）。
  - 列表页调度摘要：Cron 显示表达式原文；`scheduleSummary` 增加分支。
- `api.ts`：保存输入与文档类型透传 `scheduleCron`。

### 4.5 同步包兼容（按平铺列契约）

同步包的调度字段是**实体平铺列**（`ExportAsync` 组装 `task.ScheduleType/ScheduleTime/IntervalMinutes`，导入同样只读包平铺字段），不经 `PocSyncScheduleDefinition`：

- `PocDataSyncPackage` 新增可选字段 `ScheduleCron`（旧包缺字段反序列化为 null，天然兼容）。
- 导出 `ExportAsync` 从 `task.ScheduleCron` 带出。
- 导入 `ValidateSchedule` 扩参接收表达式（签名从三参扩展），增加 Cron 分支：类型 Cron 时按 §4.2 校验表达式，落库携带；类型非 Cron 或旧包无字段时保持现状。
- 前端 `types.ts` 的 `DataSyncPackage` 同步加字段（导入向导 UI 不展示调度字段，无需改界面）。

## 5. 错误处理与边界

- 表达式非法：保存/导入即失败，不允许带病上线（无"保存成功但永不触发"的静默态）。
- 触发器注册失败（含派生 ID 超长兜底后的意外失败）：保存仍成功，Error 日志可查；下次保存/导入/重启自愈。
- 服务停机期间错过的 cron 点：不补跑（与现有 Daily/Interval 语义一致——只执行停机后到期的时间点）。
- 用户在框架"定时任务"页暂停/删除了同步 Cron 作业：报表中心不感知，下次重启对账恢复；在任务页重新保存或重新导入也会恢复。该页会多一行 `job_rcSyncCronHost` 宿主作业（无触发器不运行），属预期，不承诺该页可管理同步作业。
- 重启后到首轮对账之间的短窗口内正好到点的 cron 不补触发，可接受。

## 6. 测试与验收

后端（HD.ReportCenter.Tests，沿用现有测试风格）：

1. `IsTaskDue`：Cron 类型在任何时点返回 false（扫描排除）。
2. 保存校验：合法表达式（含 `*/2 * * * *` 与天/周域 `?` 用例）通过；乱串、缺段（4 段）、6 段（含秒）被拒且错误文案含原始表达式；`?` 与拓展字符的确切预期以 §4.2 小样实测为准后固化。
3. 落库断言：Cron 保存后 `ScheduleCron` 列有值（防 store 列映射遗漏的静默丢数）；非 Cron 保存后裁剪为空。
4. 同步包：含 Cron 的新包导出→导入闭环（表达式不丢失）；旧包（无 ScheduleCron 字段）导入回归不变；导入成功后注册钩子被调用。
5. 调度网关：启用/停用/改表达式/删除/导入路径的注册与摘除调用正确（用伪造网关接口断言）；派生 ID 截断规则对超长任务码生效且 ≤64。
6. 既有测试回归：`PocDataSyncExecutorTests`、`PocDataSyncStoreTests`、`PocDataSyncPackageTests`、`PocDataSyncColumnDescriptionTests` 中受契约扩展影响的构造点/断言同步更新。

前端（vitest）：

1. cronstrue 释义与 cron-parser 未来执行时间的格式化（含解析失败分支与"以服务端校验为准"降级分支）。
2. 保存组装裁剪（仅 Cron 提交 scheduleCron）。
3. `scheduleSummary` Cron 分支、类型选项文案。

验收命令与门禁：

- `dotnet test`（HD.ReportCenter.Tests）全绿。
- `pnpm test`（ReportWeb）全绿；bash 下先把 nvm node v22.21.1 加入 PATH。
- `pwsh -NoProfile -File ./scripts/quality/Test-CodeComments.ps1` 通过（注释规范按 AGENTS.md，测试代码同样要写方法注释）。
- 本地起后端 + 前端冒烟：建一条 Cron 任务（如 `*/2 * * * *`）观察启动对账注册、到点触发、停用后摘除；重启后端验证触发器自动恢复。

## 7. 风险与注意事项

- **实现第一步**：先跑 Furion `Triggers.Cron` 小样验证表达式口径（`?`、`L/W/#`、6 段默认拒绝），固化测试预期后再铺开。
- Furion 动态 API 确切方法名以包内源码/上游 `SysJobService` 用法为准（AddJob/RemoveJob/GetJob/ContainsJob/Triggers.Cron）。
- 仓库当前有大量其他功能的未提交改动：实现只新增/修改本特性相关文件，禁止 `git add -A`，未获明确要求不执行任何 git 提交。
- 所有新增 C#/TS 具名方法按 AGENTS.md 写足方法级注释，长方法分阶段块内注释，测试代码同样写注释。
