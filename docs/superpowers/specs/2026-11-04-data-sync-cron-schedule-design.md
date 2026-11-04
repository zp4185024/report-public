# 数据同步调度增加 Cron 表达式类型 — 设计文档

日期：2026-11-04
状态：已与用户确认方向（轻量前端方案 + Furion 原生 Cron 触发器）

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
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncContracts.cs` | `PocSyncScheduleDefinition(Type, Time, IntervalMinutes)`、`PocSyncTaskInput`、`PocSyncTaskDocument` |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncService.cs` | `ScheduleTypes` 白名单（约 42 行）、保存校验（约 1114 行）、`IsTaskDue`（约 894 行）、`ExecuteTaskNowAsync`（约 844 行） |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncJob.cs` | 每分钟扫描作业（`job_rcDataSync`，`[PeriodMinutes(1)]`） |
| `Admin.NET/HD.ReportCenter.Application/CoreValidation/PocDataSyncPackageService.cs` | 同步包导入的 `ValidateSchedule`（约 580 行） |
| `ReportWeb/src/DataSyncTaskPage.tsx` | 设计器第 3 步"目标设置与调度"、列表页调度摘要 |
| `ReportWeb/src/syncTaskDesign.ts` | `SCHEDULE_TYPE_OPTIONS` 白名单 |
| `Admin.NET/Admin.NET.Core/Service/Job/SysJobService.cs` | Furion `ISchedulerFactory` 动态作业/触发器的参照用法（AddJob/RemoveJob/AddTrigger/UpdateTrigger） |
| `Admin.NET/Admin.NET.Core/Service/Job/DbJobPersistence.cs` | 调度持久化：动态作业写入 SysJobDetail/SysJobTrigger 表；启动 PreloadAsync 会清理"代码中不存在"的 BuiltIn 作业 |
| `Admin.NET/Admin.NET.Web.Core/Startup.cs` | `AddSchedule` + `AddPersistence<DbJobPersistence>`（约 81 行）；该 Startup 是 `[AppStartup(int.MaxValue)]`，最后执行 |

## 3. 方案总览

```
前端设计器(第3步) ──保存──▶ 后端校验表达式 ──▶ 落库(RcSyncTask.ScheduleCron)
                                   │
                                   ├─ 启用 + Cron ─▶ 动态注册/更新 Furion 作业与 Cron 触发器
                                   └─ 否则         ─▶ 移除对应动态作业
服务启动 ──▶ 对账：把所有"启用且 Cron"的任务补挂触发器（幂等）
触发器到点 ─▶ PocSyncCronJob ─▶ 复用 ExecuteTaskNowAsync（沿用 Running 防重）
```

明确不做的事（YAGNI）：

- 不改 Manual/Daily/Interval 的扫描行为，不加可视化 cron 构建器，不引入 antd。
- 不做分布式/多实例协调（当前单实例部署）。
- 不适配框架自带"定时任务"管理页对这类作业的编辑（仅作为运行观察入口，见 §5 边界）。

## 4. 详细设计

### 4.1 数据模型与契约

- `PocSyncTask` 新增列：`ScheduleCron`（`string?`，Length = 64，`IsNullable = true`，描述"Cron 调度表达式"）。CodeFirst 启动自动补列，无需迁移脚本。
- `PocSyncScheduleDefinition` 末尾追加可选参数 `string? CronExpression = null`——旧 DefinitionJson / 旧同步包反序列化天然兼容。
- `PocSyncTaskInput`、`PocSyncTaskMutation`（store 侧变更记录）、`PocSyncTaskDocument` 追加 `ScheduleCron`（Document 中字段名 `ScheduleCron`，DTO 对外 camelCase `scheduleCron`）。新增参数一律放参数列表末尾并带默认值，保持旧构造调用点编译兼容或同步更新仓内调用点。
- 调度类型白名单：`PocDataSyncService.ScheduleTypes` 与同步包校验处均扩为 `Manual/Daily/Interval/Cron`。
- `IsTaskDue`：为 `Cron` 增加显式 `case "Cron": return false;`（带注释说明：Cron 类型由 Furion 触发器直接驱动，扫描作业永不触发它），未知类型仍走 default 返回 false。

### 4.2 保存与导入校验

- 保存任务（`SaveTaskAsync` 校验段）与同步包导入（`PocDataSyncPackageService.ValidateSchedule`）都增加 Cron 分支：
  - `ScheduleType == "Cron"` 时表达式必填、非空白，且必须能通过 Furion Cron 触发器的解析（用 `Triggers.Cron(表达式)` 构造/`CronTrigger` 校验，try/catch 转成业务错误文案；不要直接引 Cronos 包）。非法时保存/导入失败，错误信息给出表达式与原因。
  - 非 Cron 类型时 `ScheduleCron` 置空（与现有 `ScheduleTime`/`IntervalMinutes` 按类型裁剪的规则一致）。
- 表达式格式约定：标准 5 段（分 时 日 月 周）。Furion 底层是 Cronos，不认识 Quartz 风格 `?`；前端提示文案必须写明"标准 5 段，不支持 Quartz 的 ?"。后端不额外限制 6 段（含秒）表达式——Furion 能解析就放行，前端预览对 6 段降级为仅显示释义（cron-parser 需要时可开 seconds 选项）。

### 4.3 调度执行（Furion 动态作业）

新增（建议放在 `CoreValidation/`，如 `PocDataSyncCronScheduler.cs`，命名随实现）：

1. **作业类 `PocSyncCronJob : IJob`**（不加 `[JobDetail]` 特性，避免被 `App.EffectiveTypes.ScanToBuilders()` 当编译期作业扫描）。
   - 每个任务一个动态作业实例：`JobBuilder.Create(typeof(PocSyncCronJob)).SetJobId($"job_rcSync_{taskCode}")`，Description 存任务名，`AddTrigger(Triggers.Cron(表达式).SetTriggerId($"trigger_rcSync_{taskCode}"))`。
   - 作业体从 `context.JobDetail.JobId` 解出任务编码（前缀 `job_rcSync_`），在独立作用域解析同步服务调用 `ExecuteTaskNowAsync(taskCode)`——模式照抄 `PocDataSyncJob.ExecuteAsync`。Running 防重已有，人工触发与定时触发互斥安全。
2. **调度网关接口**：把对 `ISchedulerFactory` 的 AddJob/RemoveJob/GetJob 封装成一个小接口（便于单测伪造），实现类里 try/catch 并记日志。
3. **生命周期维护点**：
   - 保存成功后（`SaveTaskAsync` 服务层收尾）：`启用 && Cron` → 以"先 RemoveJob 后 AddJob"的幂等方式注册当前表达式；其余情况 → RemoveJob（作业不存在时吞掉异常）。
   - 删除任务：RemoveJob。
   - 注册失败不回滚保存、不抛给前端（DB 已是事实源，下次保存/重启会纠正），记 Error 日志。
4. **启动对账**：应用启动后把所有"启用且 Cron"任务批量补挂。时序要求：必须在调度器宿主服务启动之后执行。推荐实现为在 `PocDataSyncJob` 首轮扫描开头执行一次性对账（该作业由调度器自身驱动，天然保证就绪，且用标志位保证只跑一次）；如实现者选择独立 IHostedService，则必须验证注册顺序或自带就绪重试。验收标准是"重启后启用的 Cron 任务自动恢复触发"。

**与 `DbJobPersistence` 的交互（已核实，安全）**：运行时 `AddJob` 会把作业持久化到 SysJobDetail/SysJobTrigger（CreateType 默认 BuiltIn）。下次重启时 `PreloadAsync` 的陈旧清理会把"代码中不存在的 BuiltIn 作业"整行删除，不会落入 `NotSupportedException` 分支（该分支只处理非 BuiltIn 非 Script），因此重启会先清掉旧行、再由启动对账重建——自洽，无残留风险。框架"定时任务"管理页能看到这些作业及其运行记录（SysJobTriggerRecord），作为观察入口。

### 4.4 前端（ReportWeb）

新增依赖：`cronstrue`（含 zh_CN locale）与 `cron-parser`（均为小体积纯函数库）。

- `types.ts`：`SyncScheduleDefinition` 及三处含 `scheduleType/scheduleTime/intervalMinutes` 的接口追加 `scheduleCron?: string | null`。
- `syncTaskDesign.ts`：`SCHEDULE_TYPE_OPTIONS` 增加 `{ value: 'Cron', label: 'Cron 表达式' }`。
- `DataSyncTaskPage.tsx`：
  - 草稿状态增加 `scheduleCron`，编辑回填与新建默认空。
  - 设计器第 3 步：类型选 Cron 后显示表达式输入框，下方实时显示 cronstrue 中文释义（解析失败显示错误样式与提示）+ 未来 3 次执行时间（本地时区，cron-parser 计算）；输入旁提示"标准 5 段：分 时 日 月 周，不支持 Quartz 的 ?"。
  - 本地校验：Cron 类型必须非空且前端可解析；错误文案与后端口径一致。
  - 保存组装：仅 Cron 类型提交 `scheduleCron`，其余类型置 null（沿用现有裁剪写法）。
  - 列表页调度摘要：Cron 显示表达式原文；`scheduleSummary` 增加分支。
- `api.ts`：保存输入与文档类型透传 `scheduleCron`。

### 4.5 同步包兼容

- 导出：定义随 `PocSyncScheduleDefinition.CronExpression` 自然携带。
- 导入：`ValidateSchedule` 增加 Cron 分支（校验表达式，同 §4.2）；旧包（无 CronExpression 字段或类型非 Cron）不受影响，保持现状。

## 5. 错误处理与边界

- 表达式非法：保存/导入即失败，不允许带病上线（无"保存成功但永不触发"的静默态）。
- 触发器注册失败：保存仍成功，Error 日志可查；下次保存或重启自愈。
- 服务停机期间错过的 cron 点：不补跑（与现有 Daily/Interval 语义一致——只执行停机后到期的时间点）。
- 用户在框架"定时任务"页暂停/删除了同步 Cron 作业：报表中心不感知，下次重启对账恢复；在任务页重新保存也会恢复。文档/提示不承诺该页可管理同步作业。
- 重启后到首轮对账之间的短窗口内正好到点的 cron 不补触发，可接受。

## 6. 测试与验收

后端（HD.ReportCenter.Tests，沿用现有测试风格）：

1. `IsTaskDue`：Cron 类型在任何时点返回 false（扫描排除）。
2. 保存校验：合法表达式通过；`0 2 * * ?`（Quartz 风格）、空白、乱串、缺段被拒且错误文案可读。
3. 非 Cron 保存后 `ScheduleCron` 被裁剪为空；Cron 保存后落库值与白名单状态正确。
4. 同步包：含 Cron 的新包导入成功；旧包（无 CronExpression）导入回归不变。
5. 调度网关：启用/停用/改表达式/删除时注册与摘除调用正确（用伪造的网关接口断言）。

前端（vitest）：

1. cronstrue 释义与 cron-parser 未来执行时间的格式化（含解析失败分支）。
2. 保存组装裁剪（仅 Cron 提交 scheduleCron）。
3. `scheduleSummary` Cron 分支、类型选项文案。

验收命令与门禁：

- `dotnet test`（HD.ReportCenter.Tests）全绿。
- `pnpm test`（ReportWeb）全绿；注意 bash 下先把 nvm node v22.21.1 加入 PATH。
- `pwsh -NoProfile -File ./scripts/quality/Test-CodeComments.ps1` 通过（注释规范按 AGENTS.md，测试代码同样要写方法注释）。
- 本地起后端 + 前端冒烟：建一条 Cron 任务（如 `*/2 * * * *`）观察启动日志注册与到点触发、停用后摘除。

## 7. 风险与注意事项

- Furion 动态 API 的确切方法名以包内源码/上游 `SysJobService` 用法为准（AddJob/RemoveJob/GetJob/Triggers.Cron），实现时先小验证再铺开。
- 仓库当前有大量其他功能的未提交改动：实现只新增/修改本特性相关文件，禁止 `git add -A`，未获明确要求不执行任何 git 提交。
- 所有新增 C#/TS 具名方法按 AGENTS.md 写足方法级注释，长方法分阶段块内注释。
