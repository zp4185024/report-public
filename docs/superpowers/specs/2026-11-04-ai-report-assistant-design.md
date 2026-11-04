# 产品内 AI 报表助手设计（含 AI 查数据能力）

- 日期：2026-11-04
- 状态：已与需求方确认，待实现
- 关联：`docs/superpowers/specs/2026-11-04-ai-report-assistant-design.md`（本文）、`.zcode/skills/report-design/SKILL.md`、`scripts/report-design/README.md`

## 1. 背景与目标

报表中心当前只支持 Excel 表格形态报表（前端 Univer 预览 + 后端 ClosedXML 导出）。
手工配置模板/绑定/参数门槛高（GoView 类编辑器过于复杂，已明确不引入）。
AI agent 能力成熟，希望：

1. **产品内 AI 助手**（部署环境可连 LLM API 时）：用户用自然语言描述需求，
   AI 自动查库摸数据 → 生成报表草稿 → 校验 → 试跑 → 导入，全程在报表中心 UI 内完成。
2. **AI 查数据能力**：AI 助手可通过受控工具主动查询已配置数据源的表结构、采样数据、
   验证性只读 SQL，而不是靠人工贴表结构。
3. **图表报表**（本期不实现，仅定方向）：模板报表可叠加图表视图，数据沿用现有
   execute 通道，图表配置独立成块，便于分期开发。

### 非目标（本期不做）

- 不引入 GoView / 大屏拖拽编辑器。
- 不做图表渲染、Excel 内嵌图表（见 §10 预留方向）。
- 不做 AI 会话的数据库持久化（会话仅内存 + 前端展示恢复）。
- 不做流式输出（SSE），首期一次性返回 + 加载动画，接口预留。
- 不改变现有离线 AI 流水线（ZCode + report-design skill + ps1 编译器），两套并存。

## 2. 核心决策记录

| 决策点 | 结论 | 理由 |
| --- | --- | --- |
| AI 生成产物 | **AI 只输出 spec JSON（意图层），后端 C# SpecCompiler 编译成报表包** | spec 小（几百 token）不易错；模板快照与绑定由代码机器构造，与既有"AI 只出意图，代码出模板"原则一致；校验复用 `ValidatePackageCore` 全链兜底。AI 直接手写 Univer 快照是现有 skill 明令禁止项 |
| LLM 接入 | **OpenAI 兼容自定义端点**（baseUrl + apiKey + model 三项可配置） | DeepSeek / 通义 / GLM / OneAPI 网关 / 自部署 vLLM 通用；换模型不发版 |
| 交互形态 | **聊天侧栏 + 工具调用（agent 循环）** | AI 可多轮查数后再生草稿，用户可追问修改；一次性向导无法按查到的数据调整 |
| 数据安全 | 采样/SQL 结果会外发 LLM：面板明示 + 行数上限 + 审计 + 管理员可禁用 run_sql 工具 | 医疗数据外发必须显式可控 |
| 图表 | 后置；方向 = 模板可选图表视图、数据沿用 execute、配置独立成节 | 需求方明确"先做 AI 通道"，schema 预留方向写入 §10 防返工 |

## 3. 总体架构

```
ReportWeb 聊天侧栏（ReportListPage 浮钮）
   │  POST /api/report-center/ai/chat  （sessionId + 消息）
   ▼
PocAiChatService（内存会话存储 / 工具调用循环 / 轮数上限）
   │  OpenAI 兼容 POST {baseUrl}/chat/completions  (tools + tool_choice)
   ▼
PocAiToolExecutor（只读查数工具 + 草稿提交工具）
   │
   ├── list_data_sources / list_tables / describe_table / sample_rows
   │        └─ 复用 RcDataSource 配置 + PocDatabaseProvider（不暴露连接串）
   ├── run_readonly_sql
   │        └─ 复用 PocReadOnlySqlValidator + 行数/超时硬上限（管理员可禁用）
   └── submit_report_spec / dry_run_report_spec
            └─ PocAiSpecCompiler（spec→包）→ ValidatePackageCore / DryRunAsync
                        │
          前端草稿卡片 → 复用现有 reportPackageApi（validate / dryRun / import）
```

新增数据库对象（沿用现有 schema 脚本模式，新增 `scripts/database/Apply-AiAssistantSchema.ps1`）：

- `RcAiSettings`：单行配置。`IsEnabled`（**默认 false，AI 整体默认关闭**）、`BaseUrl`、`ApiKey`、`Model`、
  `SampleRowLimit`（默认 20，上限 100）、`SqlProbeEnabled`（默认 true）、
  `SqlProbeRowLimit`（默认 100）、`MaxToolRounds`（默认 8）、`UpdateTime`。
  ApiKey 存库，接口回显一律打码（只返回 `sk-***last4`）。ApiKey 与现有
  `PocDataSource.ConnectionString` 同为明文存库（内网部署、与现状实践一致）。
- `RcAiAuditLog`：审计。`Id`、`Time`、`SessionId`、`ToolName`、`DataSourceCode`、
  `SqlText`（截断存储）、`RowCount`、`DurationMs`、`Success`、`ErrorSummary`。

聊天会话不落库：`PocAiChatSessionStore`（内存 ConcurrentDictionary，空闲 30 分钟过期，
消息历史上限 50 条，超出裁剪最早的非系统消息）。

## 4. API 设计

新增 `PocAiAssistantApi`（Furion IDynamicApiController，路由前缀 `/api/report-center/ai`）：

| 接口 | 方法 | 说明 |
| --- | --- | --- |
| `/settings` | GET | 读取配置（ApiKey 打码）；未配置返回默认值 |
| `/settings` | POST | 保存配置；ApiKey 传空表示保留原值；保存时可选 `testConnection` 同步连通性测试 |
| `/settings/test` | POST | 连通性测试（对 baseUrl 发一次 models 或最小 chat 请求） |
| `/chat` | POST | 发送消息。请求体：`sessionId`（新会话可空）、`dataSourceCode`（发起会话时前端数据源选择器的选择，决定上下文注入与工具默认数据源）、`message`。返回 `{sessionId, messages[], draft?, expired?}`；非流式，一次回复内完成工具循环（≤ MaxToolRounds 轮） |
| `/sessions/{id}` | DELETE | 显式结束会话（清内存） |

`draft` 结构：`{spec, package(编译产物), validateResult, status(submitted/validated/dryRunOk/failed)}`。
导入动作不经过 AI 接口，由前端草稿卡片直接调用现有
`reportPackageApi.validateReportPackage / dryRunReportPackage / importReportPackage`——
导入权限、信封解包、Replace 语义完全沿用现有向导链路。

**AI 可关闭（硬约束）**：报表中心可部署在完全无法访问 LLM API 的环境，AI 必须是纯可选能力——

- `IsEnabled=false`（默认）或配置不完整（缺 BaseUrl/Model）时，**所有 `/ai/*` 接口
  统一 fail-fast**（明确错误码 `AI_DISABLED`），不发起任何 LLM 请求、不建会话、不执行查数工具；
- 前端入口（浮钮/侧栏/设置页入口）由 `/settings` 接口下发开关控制渲染，前端不写死；
  AI 关闭时其余功能零感知、零新增依赖（echarts/LLM SDK 等一律不进主包）；
- LLM 端点也允许指向内网自部署的 OpenAI 兼容服务（vLLM/Ollama/OneAPI），
  "可关闭"与"端点可指向哪里"是两件独立的事。

## 5. AI 工具集规范

LLM 侧工具 schema（OpenAI tools 格式，后端 `PocAiToolRegistry` 静态定义）：

| 工具 | 参数 | 返回 | 硬约束 |
| --- | --- | --- | --- |
| `list_data_sources` | 无 | 数据源列表：code/name/dbType | 永不含连接串 |
| `list_tables` | `dataSourceCode` | 表清单（表名+注释） | 数据源必须存在 |
| `describe_table` | `dataSourceCode`,`tableName` | 列名/类型/可空/注释 | 表名标识符校验（防注入） |
| `sample_rows` | `dataSourceCode`,`tableName`,`limit≤SampleRowLimit` | 前 N 行数据+列名 | 表名校验；`TOP/LIMIT` 包装生成，不让 AI 写 SQL |
| `run_readonly_sql` | `dataSourceCode`,`sql`,`limit≤SqlProbeRowLimit` | 行数据 | `PocReadOnlySqlValidator` 全量校验；`SqlProbeEnabled=false` 时整体拒绝；行数上限用**读取侧截断**（读到上限即停止读取，不回传超出部分，不包装改写 SQL，避免方言包装破坏 CTE/ORDER BY 语义） |
| `submit_report_spec` | `spec`(JSON 对象) | 编译+校验结果（错误列表含字段路径） | 修正循环：同一会话最多 3 次 failed 提交后提示用户人工介入 |
| `dry_run_report_spec` | `spec`,`sampleParameters`(样例参数值) | 前 50 行试跑结果 + 列清单 | 复用 `DryRunAsync` 限额（50 行/30 秒） |

执行侧通用约束：每次工具调用记 `RcAiAuditLog`；单工具超时取目标**数据源 `DefaultTimeoutSeconds`**，硬上限 30 秒；工具执行异常转为结构化错误文本回传 LLM（模型可自行重试或改写），
不中断会话。

## 6. 上下文构建（系统提示词）

后端资源文件 `PocAiSystemPrompt`（编译嵌入，改提示词需发版——可接受，格式规范与后端校验器必须强一致）：

1. **角色与产物契约**：只能通过 `submit_report_spec` 交付；spec 格式规范
   （字段表、命名正则、dataType 枚举、controlType 枚举）——内容取材自
   `.zcode/skills/report-design/SKILL.md` 的"硬规则/字段参考"两节，与后端
   SpecCompiler 校验逐条对应。
2. **SQL 硬规则**：单条只读 SELECT/WITH、禁止多语句/DML/DDL、`@paramCode` 占位、
   Paged 必带稳定 ORDER BY、SQL 中每个 `@code` 必须在 variables 声明且与 parameters
   同名同类型。
3. **查数策略指引**：先 `list_data_sources` → `list_tables` → `describe_table`，
   需要确认值分布/口径时用 `sample_rows` 或 `run_readonly_sql` 小批量验证聚合逻辑后再写 spec。
4. **安全红线**：不得在回复中复述采样出的敏感明细；不得尝试查询数据源配置表本身。
5. **动态注入段**：用户会话选择数据源后，注入该源表清单摘要（表名列表，不带结构），
   结构靠按需 `describe_table`，避免上下文爆炸。

## 7. SpecCompiler（C# 版 spec→包编译器）

移植 `New-ReportPackage.ps1`（1046 行）核心路径，输出与 ps1 同构的 `PocReportPackage`：

- 校验层：`Assert-Design`/`Assert-SpecCode`/SQL 参数引用提取（`Convert-SqlToSanitized` +
  `Get-SqlParameterReferences`）等价实现；错误带 spec 字段路径，直接回传 LLM。
- 模板构造：固定布局快照（行0 标题合并 / 行1 参数行 / 行2 表头 / 行3 明细模板行 /
  行4 合计行）+ 规范 `PocTemplateBinding`，与 ps1 `New-TemplateSnapshot`/`New-TemplateBinding`
  逐字段对齐。
- 组装：`ConvertTo-PackageJson` 等价实现（PackageKind/PackageVersion=1/ExportedAt/
  Report/Parameters/Template/Dataset 四节）。
- **一致性钉测**：仓库样例 `report-asset-inventory.spec.json` 分别经 ps1 与 C# 编译，
  断言关键结构一致（bindings 坐标、快照 cellData、四节字段）。允许 `ExportedAt` 等时间戳差异。
  注意：钉测只针对样例 spec 的**结构一致**；未知字段处理上两条编译器**有意分歧**——
  ps1 宽松忽略，C# 对 specVersion=1 严格拒绝（AI 通道加强项，见 §10）。ps1 编译器保留不动
  （离线 ZCode 流水线继续用）。

编译成功后立即走 `ValidatePackageCore`（内存态，不落库），校验错误同样回传 LLM 自修。

## 8. 前端设计（纯 React，无新增重量依赖）

- `ReportListPage` 右下角浮钮"AI 助手"（`/settings`.IsEnabled 才渲染）→
  `AiAssistantPanel.tsx` 侧栏：消息流（用户/AI/工具折叠条——工具名+SQL 摘要+行数）、
  草稿卡片（列清单、参数清单、状态徽标 + 校验/试跑/导入按钮）、
  数据源选择器（发起对话时选择，决定上下文注入与工具默认数据源）、
  顶部固定提示"对话内容与采样数据将发送至所配置的 LLM 服务"。
- `AiSettingsPage.tsx`（AppFrame 新增菜单"AI 设置"）：启用开关、baseUrl、apiKey
  （保存后打码回显）、model、各项限额、SqlProbeEnabled 开关、连通性测试按钮。
- 草稿卡片的校验/试跑/导入复用现有 `reportPackageApi`，导入成功后提示刷新模板列表。
- 会话过期（内存 30 分钟）时前端提示"会话已过期，AI 上下文已清空"，历史仅本地展示。

## 9. 安全与错误处理

- **只读铁律**：所有查数工具强制只读；`run_readonly_sql` 复用现有校验器（禁多语句/DML/
  DDL/`@@`/可执行注释等），并叠加行数上限与超时。
- **外发控制**：管理页可整库禁用（IsEnabled=false 时接口 fail-fast + 前端入口隐藏，
  见 §4"AI 可关闭"）、可禁用 run_sql、可把采样上限调到 0（等效禁采样）；面板常驻外发提示。
- **ApiKey**：仅存库；日志与审计一律不含 key；回显打码；保存接口空值=保留原值。
- **审计**：每次工具调用落 `RcAiAuditLog`，含 SQL 与影响行数，供事后追溯。
- **错误策略**：LLM 请求失败 → 会话保留，前端提示重试；工具执行失败 → 结构化错误回传
  LLM 自修；编译/校验失败 → 错误列表回传 LLM（最多 3 轮），仍失败则草稿标记 failed
  并建议人工走离线流水线；会话轮数超限 → 强制要求 AI 给出结论性回复。
- **权限**：首期与现有 Poc API 一致不接权限体系（内网部署），依赖审计与配置开关约束；
  后续接入 Admin.NET 权限时不改接口形状。

## 10. 图表预留方向（本期不实现）

- 报表包 schema：将来升 `PackageVersion=2`，包/spec 增加可选 `charts[]`
  （`{code, title, type(bar/line/pie/area), xField, yFields[], agg(sum/count/avg), sort?}`），
  与 `template` 并列；渲染数据复用现有 `/execute` 行数据，不新增取数链路。
- 前端：预览页加"表格/图表"Tab，图表视图用 ECharts（届时新增唯一重量依赖）。
- 模板编辑器：在现有多步视图中加"图表"步骤，选择类型与字段映射。
- AI：spec 增补 `charts` 字段规范与提示词段落，AI 生成图表配置复用同一编译校验链。
- 本期代码不动 schema；SpecCompiler 与提示词中不出现 charts 字段（specVersion=1
  严格校验未知字段，防止 AI 提前输出图表配置被静默丢弃）。

## 11. 测试策略

| 层 | 内容 |
| --- | --- |
| SpecCompiler 单测 | 样例 spec 与 ps1 编译产物结构比对（钉契约）；非法 spec 错误路径（命名/SQL/字段对齐） |
| 工具执行器单测 | 只读校验拦截（DML/多语句）、表名注入拒绝、行数上限、数据源不存在、SqlProbeEnabled=false 拒绝 |
| LLM 客户端单测 | mock HTTP：tool_calls 解析、多轮循环收敛、MaxToolRounds 超限、超时/5xx 重试边界 |
| 会话服务单测 | 30 分钟过期、50 条历史裁剪、3 次 failed 提交熔断 |
| 前端组件测试 | 聊天侧栏消息渲染、工具折叠条、草稿卡片状态流转、设置页打码回显 |
| 真库 E2E（后置） | 连真实 LLM 端点：需求→查数→草稿→校验→试跑→导入全链路（沿用真库验收习惯，另行排期） |

## 12. 实施分期建议

1. **阶段一（本期主体）**：schema 脚本 + 配置管理页 + LLM 客户端 + 会话/工具循环 +
   只读查数工具 + 审计。
2. **阶段二**：SpecCompiler + submit/dry_run 工具 + 草稿卡片与导入闭环 + 一致性钉测。
3. **阶段三（另立设计）**：图表视图（schema v2 + ECharts Tab + AI charts 字段）。
