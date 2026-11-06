# 产品内 AI 助手（模板 AI + 数据同步 AI）架构说明

- 日期：2026-11-05
- 状态：已实现并通过真库 E2E 验证（见 `docs/validation/2026-11-05-ai-assistant-e2e-results.md`）
- 设计规格：`docs/superpowers/specs/2026-11-04-ai-report-assistant-design.md`（需求背景、决策记录、工具集规范全文见该文档，本文不重复）

## 1. 需求背景

报表中心的模板配置（绑定/参数/快照）与同步任务的字段映射都是高频重复劳动。产品内 AI 助手提供两条能力：

1. **模板 AI（报表设计助手）**：用户在报表列表页用自然语言描述需求，AI 通过受控工具主动查库摸数据（表结构、采样、验证性只读 SQL），生成报表 spec，后端 C# 编译器编译成报表包，用户在草稿卡片上完成校验/试跑/导入，全程不离开报表中心 UI。
2. **同步 AI（字段映射智能匹配）**：同步任务设计器"字段映射"步骤一键智能匹配源→目标字段（同名基线 + LLM 语义补齐 + 白名单转换链建议），结果回填映射表格，人工确认后走现有保存链。

## 2. 总体架构

```
ReportWeb（React）
   ├─ 模板列表页「AI 助手」浮钮/侧栏 AiAssistantPanel
   │      └─ POST /api/report-center/ai/chat ──▶ PocAiChatService（内存会话 + 工具循环 ≤ MaxToolRounds）
   │                                                │ POST {baseUrl}/chat/completions（OpenAI 兼容 tools）
   │                                                ▼
   │                                       PocAiToolExecutor（只读查数工具）
   │                                          ├─ list_data_sources / list_tables / describe_table / sample_rows
   │                                          │        └─ 复用 RcDataSource 配置 + PocDatabaseProvider（不暴露连接串）
   │                                          ├─ run_readonly_sql ─── 复用 PocReadOnlySqlValidator + 行数上限读取侧截断
   │                                          └─ submit_report_spec / dry_run_report_spec（对话服务直接处理）
   │                                                   └─ PocAiSpecCompiler（spec→包）→ PocReportPackageService 全链校验/受限试跑
   │                                                            │
   │                                       前端草稿卡片 → 复用 reportPackageApi（/report-packages/validate、/dry-run、/import）
   │
   ├─ AI 设置页 AiSettingsPage ── GET/POST /ai/settings、POST /ai/settings/test
   │
   └─ 同步设计器 DataSyncTaskPage「AI 智能匹配」按钮
          └─ POST /api/report-center/ai/sync-match ──▶ PocAiSyncMatchService
                同名基线（不依赖 LLM，与前端 buildInitialMappings 同语义）
                → LLM 歧义补齐（只处理同名规则未命中的目标列，返回 {matches:[...]} JSON）
                → 白名单转换链建议（PocDataSyncConverter.SupportedConverters 之外的转换器一律拒绝）
                输出与 SyncColumnMapping 同构的建议 + confidence/reason（仅 UI 标注，写回时丢弃），绝不落库
```

### 2.1 接口清单（路由前缀 `/api/report-center/ai`）

| 接口 | 方法 | 说明 |
| --- | --- | --- |
| `/settings` | GET | 读取配置（ApiKey 打码 `sk-***last4`）；未配置返回默认关闭配置。前端据此下发入口渲染 |
| `/settings` | POST | 保存配置；ApiKey 传空表示保留原值；启用时强制校验 baseUrl/model 完整 |
| `/settings/test` | POST | 连通性测试（对 baseUrl 发一次最小 chat 请求，30 秒超时） |
| `/chat` | POST | 模板 AI 对话；一次回复内完成工具循环；返回 `{sessionId, messages[], expired, draft?}` |
| `/sync-match` | POST | 同步 AI 智能匹配；入参可直接携带源字段/目标列元数据/当前映射，或只传 taskCode 由服务端装配 |
| `/sessions/{id}` | DELETE | 显式结束会话（清内存），幂等 |

AI 关闭或配置不完整时，除 `GET /settings` 外的所有接口统一 fail-fast 返回 `AI_DISABLED`（信封 code=403），绝不发起 LLM 请求或执行查数工具。

### 2.2 后端模块（`Admin.NET/HD.ReportCenter.Application/CoreValidation/AiAssistant/`）

| 模块 | 职责 |
| --- | --- |
| `PocAiAssistantApi` | 路由入口 + 统一异常翻译（AI_DISABLED→403、LLM 失败→502、配置错误→400） |
| `PocAiSettingsService/Store` | 单行配置读写、ApiKey 打码与"空值保留原值"、限额校验、AI 启用守卫（所有调用型接口第一道闸） |
| `PocAiChatService` | "模型↔工具"循环；产物工具（submit/dry_run）的编译、校验、草稿状态与 3 次失败熔断 |
| `PocAiChatSessionStore` | 进程内会话（ConcurrentDictionary），空闲 30 分钟过期、历史上限 50 条裁剪；不落库 |
| `PocAiToolExecutor` | 只读查数工具：表名标识符校验、方言化采样 SQL、只读 SQL 校验、读取侧行数截断、单工具超时 |
| `PocAiToolRegistry` | 工具 schema 静态定义（OpenAI tools 格式） |
| `PocLlmClient` | OpenAI 兼容客户端：手写 `/chat/completions` 调用，解析 content/tool_calls；请求体 ASCII 转义序列化、响应显式 UTF-8 解码（见 §5 安全设计） |
| `PocAiSpecCompiler` | C# 版 spec→报表包编译器（对齐 `New-ReportPackage.ps1`）；specVersion=1 严格拒绝未知字段（与 ps1 有意分歧，见 §6） |
| `PocAiSyncMatchService` | 三层匹配策略（同名基线 → LLM 歧义补齐 → 白名单转换链）；LLM 不可用/非法 JSON 降级为同名结果并提示 |
| `PocAiAuditStore` | 每次工具调用/匹配请求写一行 `RcAiAuditLog`（SQL 截断 2000、错误摘要 512，不含密钥/连接串） |
| `PocAiSystemPrompt` | 编译嵌入的系统提示词（角色契约、SQL 硬规则、查数策略、安全红线、动态注入数据源摘要） |

### 2.3 前端模块（`ReportWeb/src/`）

| 模块 | 职责 |
| --- | --- |
| `AiAssistantPanel.tsx` | 聊天侧栏：数据源选择器、消息流（工具折叠条=工具名+SQL 摘要+行数）、草稿卡片（校验/试跑/导入复用 `reportPackageApi`）、外发提示常驻 |
| `AiSettingsPage.tsx` | 启用开关、baseUrl、ApiKey（打码回显、空值=保留）、各项限额、连通性测试 |
| `aiAssistantChat.ts` / `aiDraftCard.ts` / `aiSettingsForm.ts` / `syncTaskDesign.ts(applyAiSyncMatchSuggestions)` | 纯函数模块，均配套同名 `*.test.ts` |

入口下发：`main.tsx` 启动时读 `/settings`，`aiEnabled=false` 时不渲染浮钮/不注册"AI 设置"菜单/`/ai-settings` 路由回退模板列表——前端不写死任何 AI 入口。

## 3. 与既有模块的分界

### 3.1 新增目录（不改动既有职责）

- 后端 `CoreValidation/AiAssistant/`（整个目录为新增）；实体 `RcAiSettings`/`RcAiAuditLog` 走 SqlSugar `[SysTable]` + `ReportCenterStartup` 启动 `InitTables`。
- 前端 `AiAssistantPanel.tsx`、`AiSettingsPage.tsx`、`ai*.ts(x)` 及其测试。
- 数据库脚本 `scripts/database/Apply-AiAssistantSchema.ps1`。

### 3.2 最小追加清单（对既有文件的改动）

| 文件 | 追加内容 |
| --- | --- |
| `ReportCenterStartup.cs` | 注册名为 `ReportCenterAiLlm` 的 HttpClient（超时放开）；显式登记 `PocLlmClient` 到 DI（该类无自动注册标记，单测手工构造，生产容器不登记会导致 `/ai/*` 全部 500） |
| `PocReportApi.cs` 等既有 API | 无改动（草稿卡片的校验/试跑/导入直接复用既有 `/report-packages/*` 三接口） |
| 前端 `main.tsx` / `AppFrame.tsx` | AI 开关下发与菜单显隐；`aiEnabled=false` 时零感知 |
| `ReportListPage.tsx` / `DataSyncTaskPage.tsx` | 各追加一个由开关控制的入口（浮钮 / AI 智能匹配按钮），关闭时不渲染 |
| `scripts/database/README.md` | 新增 AI 助手表结构升级说明 |

## 4. AI 可关闭语义（硬约束）

1. `RcAiSettings.IsEnabled` **默认 false**（建表脚本与 InitTables 播种的初始行都是关闭态）——AI 是纯可选能力。
2. 未启用或缺 BaseUrl/Model 时所有调用型 `/ai/*` 接口 fail-fast（`AI_DISABLED`，信封 403），不发起任何 LLM 请求、不建会话、不执行查数工具。
3. 前端入口由 `GET /settings` 下发控制渲染；AI 关闭时浮钮、菜单、同步"AI 智能匹配"按钮全部不渲染，其余功能零新增依赖。
4. 同步设计器的**规则版同名匹配保留可用**（无 AI 环境的基线能力，与 AI 按钮并存）。
5. LLM 端点允许指向内网自部署的 OpenAI 兼容服务（vLLM/Ollama/OneAPI），"可关闭"与"端点指向哪里"是两件独立的事。

## 5. 安全设计

1. **只读铁律**：查数工具全部只读；`run_readonly_sql` 复用 `PocReadOnlySqlValidator`（禁多语句/DML/DDL/`@@`/可执行注释），叠加行数上限（读取侧截断，不改写 SQL）与超时（数据源 `DefaultTimeoutSeconds`，硬上限 30 秒）。
2. **外发控制**：侧栏与设置页常驻"对话内容与采样数据将发送至所配置的 LLM 服务"提示；管理员可整体禁用（IsEnabled）、可禁用 run_sql（SqlProbeEnabled）、可把采样上限调 0（等效禁采样）。
3. **ApiKey**：仅存库；日志与审计一律不含 key；接口回显打码 `sk-***last4`；保存接口空值=保留原值。
4. **审计**：每次工具调用与同步匹配请求落 `RcAiAuditLog`（SessionId/ToolName/DataSourceCode/SqlText/RowCount/DurationMs/Success/ErrorSummary），供按会话或时间回溯。
5. **传输加固（本次真库 E2E 期间加固）**：
   - 请求体按 **ASCII 转义 JSON** 序列化（非 ASCII 一律 `\uXXXX`）：线格式不含多字节字符，内网常见的按本机码页（GBK）解码请求体的中间件不可能解码失败，目标端点按 JSON 规范还原后语义等价；
   - 响应体**显式按 UTF-8 解码**（`ReadAsByteArrayAsync` + `Encoding.UTF8`）：不信任响应头 charset，部分网关声明本机码页会导致 UTF-8 中文响应解码异常。
6. **行数上限**：采样 ≤ `SampleRowLimit`（默认 20，硬上限 100，0=禁用）；只读 SQL ≤ `SqlProbeRowLimit`（默认 100，上限 1000）。

## 6. SpecCompiler 与 ps1 编译器的有意分歧

- **一致性钉测**：样例 `report-asset-inventory.spec.json` 分别经 ps1 与 C# 编译，关键结构（bindings 坐标、快照 cellData、四节字段）一致，仅允许 `ExportedAt` 等时间戳差异。
- **有意分歧**：未知字段处理——ps1 宽松忽略，C# 对 specVersion=1 **严格拒绝**（根级与各节未知字段都记入 issues 回传模型，防止 AI 输出的新字段被静默丢弃）。ps1 编译器保留不动，离线 ZCode 流水线继续使用。
- 缺失节（dataset/parameters/template）返回结构化 `SPEC_SECTION_MISSING` 错误而非异常；任何编译/校验失败都转成带字段路径的错误清单回传模型自修，同一会话连续 3 次提交失败后熔断 submit 工具并提示人工走离线流水线。

## 7. 真实 LLM 端点接入验证（GLM 通用 OpenAI 兼容端点）

真实模拟（2026-11-05）未使用任何 LLM 替身跑主链路，直接接 **GLM 通用 OpenAI 兼容端点**：

- **BaseUrl**：`https://open.bigmodel.cn/api/paas/v4`（通用端点；`GET /models` 返回 glm-4.5 至 glm-5.3-flashx 全系清单，与 coding 端点 `/api/coding/paas/v4` 清单一致）
- **Model**：`glm-5.3-flash`（清单中最新的 5.3 系列 flash 档；聊天、tool_calls、多轮循环全部验证可用）
- **鉴权**：`Authorization: Bearer <key>`，key 仅经环境变量注入运行时，绝不落盘、不入文档（文档一律脱敏如 `44b0***WVj`）
- **连通性**：`POST /settings/test` 实测 1.3~5.2 秒返回"连接成功"
- **工具循环**：S2 场景实测 glm-5.3-flash 按提示词引导完成 `list_data_sources → describe_table → sample_rows → submit_report_spec` 全链；flash 档对"长篇 spec 需求"可能只回文字计划，靠同会话追问推动即可（详见测试文档 S2 说明）

内网自部署同理：任何 OpenAI 兼容端点（vLLM/Ollama/OneAPI 网关）只需在 AI 设置页填 `{baseUrl}/model/key` 三项，`PocLlmClient` 拼 `{baseUrl}/chat/completions`，无需改代码。注意内网中间件若按本机码页解码请求/响应体，本模块的 ASCII 转义 + 显式 UTF-8 解码（§5.5）已做了兼容。

## 8. 部署注意事项

1. **schema**：先跑 `scripts/database/Apply-AiAssistantSchema.ps1` 建 `RcAiSettings`/`RcAiAuditLog`（幂等；`RcAuditLog.RowCount` 为 SQL Server 保留字，脚本内已用 `[RowCount]` 括号——不要去掉）。后端启动 `InitTables` 幂等对齐，不隐式建库。
2. **内网 LLM 端点**：AI 设置页配置 baseUrl/model/key；连通性测试发一次最小 chat 请求（30 秒超时）。无法访问外网 LLM 的环境保持 AI 关闭即可，其余功能零影响。
3. **启用流程**：AI 默认关闭；启用需 `POST /ai/settings`（`isEnabled=true` + 合法 baseUrl + model）。注意：前端 `/ai-settings` 页面被 `aiEnabled` 门控——**首次启用没有 UI 入口，只能走接口或数据库**（已记录的体验缺口，见测试文档遗留项）；启用后即可全程 UI 操作。
4. **会话与审计**：会话在进程内存（重启即清空，前端以 `expired` 标记提示）；审计在 `RcAiAuditLog`，按需归档。
5. **行数与轮数限额**：按部署实际收紧（采样/SQL 行数、MaxToolRounds ≤ 20）。

## 9. 阶段四：附件参考（图片 / Excel / PDF → 标准骨架模板）

设计规格：`docs/superpowers/specs/2026-11-05-ai-attachment-reference-design.md`。已确认决策：
**标准骨架内参考复刻**（附件只作内容参考——列名/列序/标题/参数/合计提示，模板仍由 SpecCompiler
按标准骨架生成）与**维持人工确认导入**（草稿卡片链路不变）。

### 9.1 核心解耦：附件理解不进工具循环

```
用户消息 + 附件（base64 内联，≤3 个）
   ├─ Excel(xlsx) ──▶ PocAiExcelReferenceParser（ClosedXML 纯后端，已有依赖）
   ├─ PDF          ──▶ PocAiPdfReferenceParser（PdfPig 文本层 + 空格连击切列）
   └─ 图片         ──▶ PocAiVisionAnalyzer（单次 image_url content part 调用，无 tools）
                ▼
   PocAiReferenceMaterial：{sourceKind, fileName, title?, columns:[{name,note?}],
                            paramsHint?, totalsHint?, warnings[]}
                ▼
   随内存会话存储（≤6 份裁剪）→「参考材料」段注入系统提示词 → 主模型工具循环照旧产出 spec
```

关键模块（`CoreValidation/AiAssistant/`）：

| 模块 | 职责 |
| --- | --- |
| `PocAiReferenceMaterial` | 统一 JSON 契约 + 附件限制常量（≤3 个、图片 ≤5MB、xlsx/pdf ≤10MB）与 `Validate` |
| `PocAiExcelReferenceParser` | ClosedXML 取首个非空工作表；前 3 行探测表头；列名+列序；合并单元格摘要；≤5 行采样仅推断类型倾向不外发数据 |
| `PocAiPdfReferenceParser` | PdfPig 提取文本层+字符坐标；Y 聚类成行（y 降序=自上而下）、行内 ≥2 连续空格切列；**无文本层不做 OCR**，提示转传图片 |
| `PocAiVisionAnalyzer` | 单次视觉调用（`VisionModel` 空则回退主 `Model`），要求输出参考 JSON；失败/超时/非法 JSON 抛降级信号，对话服务按"仅文本继续"处理并注入降级说明 |

`PocLlmChatMessage` 追加 `ContentParts` 属性（纯追加，原构造签名不变）：非空时 `content`
按 OpenAI content 数组形态序列化（image_url + text），承载多模态视觉请求。

`PocAiChatRequest.Attachments`（base64 内联，不新增上传端点）→ `PocAiChatService` 发送前解析：
逐条校验 → 按类型分发 → 材料入会话（`AppendReferenceMaterials`，上限 6 份裁剪最早）→
审计一行 `attachment_reference`（仅文件名/类型/大小元信息，**不落附件内容**）→
`BuildSystemPromptAsync` 把 `PocAiSystemPrompt.BuildReferenceSection` 渲染的「参考材料」段拼入提示词。

### 9.2 提示词契约（参考材料段规则）

系统提示词新增「参考材料」静态规则段 + 动态注入段（`{1}` 占位）：

- 列名/列序/标题必须从参考材料提炼（`displayName`/`template.columns.header` 跟随参考原文）；
- 参考列与库字段对齐靠 `describe_table`/`sample_rows`；**参考与库字段冲突以库为准**并说明映射；
- 不凭空编造参考里没有的列；参考与用户最新文字需求冲突时以文字需求为准；
- 模板版式仍由服务端按标准骨架生成，不复刻合并单元格/自由版式。

### 9.3 视觉模型（VisionModel）

- `RcAiSettings.VisionModel` 可选列（Apply 脚本幂等加列，见 §8）；**空则回退主 `Model`**——
  主模型具备视觉能力时单模型即可。
- 真实接入验证（2026-11-05，GLM 通用端点）：**`glm-5.3-flash` 主模型原生支持图像输入**
  （image_url content part 实测 200 且正确识图），`glm-4.5v` 亦可作为独立视觉模型；
  `glm-4v-plus`/`glm-4v-flash` 因账号套餐不可用（429）。因此当前部署**无需配置 VisionModel**，
  图片功能随主模型开箱即用。
- 视觉失败降级语义：解析失败不中断会话，参考材料携带"视觉模型解析失败，已按纯文本继续"
  说明注入提示词，模型据此告知用户。

### 9.4 依赖例外（明示记录）

**PdfPig 0.1.16**（Apache-2.0 纯托管）是对"零新增依赖"约束的一次明示例外：PDF 文本层解析
无此库则能力不可达（不做 OCR 是有意的安全边界——扫描件引导用户转传图片）。除此之外
阶段四零新增依赖（Excel 复用已有 ClosedXML；前端附件区无新增包）。

### 9.5 部署补充（阶段四）

1. 老库升级：重跑 `Apply-AiAssistantSchema.ps1`（幂等给 `RcAiSettings` 加 `VisionModel` 列，含 DEFAULT 空串），
   或依赖后端启动 `InitTables` 自动补列——但**空表限制**：SqlSugar 的 ALTER 不允许向非空表加无默认的
   NOT NULL 列，因此**必须先跑脚本再加新后端**，顺序不能反。
2. 前端附件随消息 base64 内联（单条消息 ≤3 个；图片 ≤5MB、xlsx/pdf ≤10MB），前后端双重拦截；
   附件内容不落库随会话过期，审计仅记元信息。
3. 面板常驻外发提示已补充"上传的附件内容将发送至所配置的 LLM 服务"。
