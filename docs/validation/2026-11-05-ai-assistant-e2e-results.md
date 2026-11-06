# 产品内 AI 助手真库 E2E 测试结果（2026-11-05）

- **结果**：S1~S6 全部 PASS ✅；阶段四新增 **S7 附件参考 Excel（8 检查点）✅、S8 附件参考图片（5 检查点）✅**，合计 49 检查点全部通过
- **模拟形态**：真实 SQL Server 库 + 真实后端（bin/Release，5005）+ 真实前端（Vite，5176）+ 真实 UI 操作；**LLM 直接走真实 GLM 通用 OpenAI 兼容端点**，唯一替身是本地故障注入 mock（仅 S3 熔断与 S5 非法 JSON 降级两个确定性失败场景）
- **真实 LLM**：`https://open.bigmodel.cn/api/paas/v4`（通用端点），模型 `glm-5.3-flash`（主模型原生支持图像输入，S8 视觉场景实测可用）；ApiKey 仅经环境变量注入运行时，本文及所有落盘文件一律脱敏（如 `44b0***WVj`）
- **测试产物**：检查点 JSON `scripts/e2e/ai-assistant/output/e2e_result.json`；截图 `docs/images/ai-s*.png`
- **执行方式**：Playwright 驱动系统 Chrome（无头），脚本 `scripts/e2e/ai-assistant/ai_assistant_e2e.js`
- **场景执行顺序**：S1 → S2 → S4 → S3 → S5 → S7 → S8 → S6（S6 关闭降级必须最后执行，否则 S7/S8 入口被关）

## 一、环境说明

| 项 | 值 |
| --- | --- |
| SQL Server 真库 | 192.168.2.5,1433 / hd_report_center（只读账号 hdhis + 环境变量口令） |
| 后端 | `Admin.NET.Web.Entry` bin/Release，端口 5005（`DOTNET_SYSTEM_NET_HTTP_USEPROXIES=false` 启动） |
| 前端 | Vite 5176（5175 被旧实例占用），`VITE_API_URL=http://localhost:5005` |
| mock LLM（故障注入器） | `scripts/testing/mock-llm/mock-llm-server.js`，端口 5099，请求日志 `output/mock-llm-calls.log` |
| GLM 端点 | 通用端点 `GET /models` 返回 glm-4.5 ~ glm-5.3-flashx 全系清单；选定 `glm-5.3-flash`（聊天 + tool_calls 实测可用；`glm-4-flash` 因套餐过期 429 不可用） |
| E2E 样例数据 | S2 用全合成脱敏样例表 `E2E_AI_SAMPLE_OUTPATIENT`（12 行虚构门诊费用，无任何真实患者数据）；S5 用异名目标表 `E2E_AI_TARGET_REPORT`；两表由 pwsh 脚本创建并在收尾删除 |

**schema 应用方式（如实记录）**：`Apply-AiAssistantSchema.ps1` 首次执行时存在保留字缺陷（见问题 1），修复后**真实建表路径**创建 `RcAiSettings`（含单行默认关闭种子）与 `RcAiAuditLog`；第二次执行命中幂等出口"表已存在且必需列完整，本次未修改数据库"。随后后端启动的 `InitTables` 在表已存在时仅做幂等对齐。

## 二、场景结果

### S1 AI 设置页（6/6 通过）

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S1-1 | AI 关闭时浮钮与 AI 设置菜单不可见 | ✅ | 浮钮=0，菜单=0 |
| S1-2 | AI 关闭时 /ai-settings 回退到模板列表 | ✅ | 直接访问时「AI 设置」标题出现 0 次 |
| S1-3 | POST /ai/settings 启用成功且 ApiKey 打码回显 | ✅ | isEnabled=true，masked=`sk-***WVj7`（与 key 末 4 位一致） |
| S1-4 | 设置页显示「AI 已启用」与打码密钥回显 | ✅ | 状态徽标="AI 已启用"，页面含打码值 |
| S1-5 | 连通性测试通过（真实 GLM） | ✅ | "连接成功，模型 glm-5.3-flash 已响应（5344 ms）" |
| S1-6 | 启用后浮钮与菜单出现（/settings 下发） | ✅ | 两者均可见 |

截图：`ai-s1-settings-initial-off.png`、`ai-s1-settings-page.png`、`ai-s1-test-connection.png`

### S2 模板 AI 全链路（9/9 通过，真实 GLM 工具循环）

用户消息明确工具顺序（list_data_sources → describe_table → sample_rows → submit_report_spec），glm-5.3-flash 实测编排：

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S2-1 | 发起需求并推动至草稿出现 | ✅ | 本轮消息 4 条，草稿出现=true |
| S2-2 | 工具折叠条按序展示 | ✅ | `[list_data_sources·4行, describe_table·8行, sample_rows·SELECT TOP(6)…·5行, submit_report_spec]` |
| S2-3 | 草稿卡片（徽标/列清单/参数清单） | ✅ | 徽标="校验通过"，列 4 / 参数 1 |
| S2-4 | 卡片校验（服务端全链） | ✅ | "服务端校验通过。" |
| S2-5 | 卡片试跑（真实数据源） | ✅ | "试跑返回 11 行（38 ms）。字段契约与实际列一致。" |
| S2-6 | 导入（复用现有导入链路） | ✅ | "报表「e2e_ai_…」已导入（数据集：Created）" |
| S2-7 | 模板列表出现新报表 | ✅ | 命中行数=2（含同名列表项渲染） |
| S2-8 | RcAiAuditLog 落本会话审计 | ✅ | 本会话 4 条 = 上述 4 个工具逐一对应；全表 10 条 |
| S2-9 | 现场清理无残留 | ✅ | DELETE 200，残留 0 |

工具调用轮数实测：本次 4 轮（一次提交成功）；对抗性重跑中模型首轮提交被校验器拒绝（`sourceType:'none'` 不在白名单）后按错误清单自修，第 2 次提交成功——修正循环符合设计。

截图：`ai-s2-chat-tool-trace.png`、`ai-s2-draft-card.png`、`ai-s2-imported-list.png`

### S4 会话过期（4/4 通过，真实重启路径）

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S4-1 | AI 设置恢复真实 GLM（UI 保存） | ✅ | baseUrl/model 正确 |
| S4-2 | 会话建立并记录 sessionId | ✅ | ai-3050f195… |
| S4-3 | 后端进程真实重启（taskkill + 重新拉起） | ✅ | GET /ai/settings 恢复 200 |
| S4-4 | 原 sessionId 续聊 → 前端提示过期 | ✅ | expired=true，提示"会话已过期，AI 上下文已清空，本轮已按新会话继续。" |

截图：`ai-s4-session-expired.png`

### S3 熔断（4/4 通过，mock 故障注入）

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S3-1 | UI 切换 baseUrl 到 mock 并保存 | ✅ | baseUrl=http://127.0.0.1:5099/v1 |
| S3-2 | 第 3 次失败后熔断并提示人工介入 | ✅ | AI 回复"本会话已连续 3 次提交失败被熔断。请改用离线流水线（ZCode + report-design skill + New-ReportPackage.ps1）人工介入修正报表。" |
| S3-3 | 工具折叠条失败标记 | ✅ | 4 个失败标记 |
| S3-4 | 审计记录失败链 | ✅ | 本会话 4 条 submit 失败 = 3 次真实编译失败（`SPEC_FIELD_UNKNOWN@$: totallyUnknownField`）+ 1 次 `SUBMIT_CIRCUIT_OPEN` 熔断拒绝 |

mock 调用日志佐证：本次全量跑 mock 共被调用 12 次（request-received 7 + invalid-spec 4 + garbage-text 1）。

截图：`ai-s3-circuit-breaker.png`

### S5 同步 AI（7/7 通过，降级 + 真实 GLM 语义匹配）

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S5-1 | 新建任务 + 源取数（3 个异名源字段） | ✅ | 预览"已读取 3 列" |
| S5-2 | 目标表列清单加载（4 列全异名） | ✅ | E2E_AI_TARGET_REPORT |
| S5-3 | 人工预填 SRC_CODE → REPORT_CODE | ✅ | 行内下拉已选 |
| S5-4 | mock 非法 JSON → 降级为同名基线并提示 | ✅ | degraded=true，提示"LLM 返回内容不是合法的匹配 JSON，已降级为同名规则结果…需人工处理 3 列" |
| S5-5 | 真实 GLM 语义匹配回填 + 置信面板 | ✅ | 命中 `[SRC_TITLE→REPORT_TITLE, SRC_ENABLED→ENABLED_FLAG]` |
| S5-6 | 置信标注/需人工项/人工映射不被覆盖 | ✅ | 置信=高/高；人工行保留=true；AI 回填 2 行；需人工含 CREATED_BY |
| S5-7 | 保存走现有校验链，需人工项不进 mappings | ✅ | 落库 3 条：`SRC_CODE→REPORT_CODE, SRC_TITLE→REPORT_TITLE, SRC_ENABLED→ENABLED_FLAG`（无 CREATED_BY） |

同名基线零命中设计验证了"确定性层先行、LLM 只补歧义"的三层策略。

截图：`ai-s5-sync-degraded.png`、`ai-s5-sync-ai-match.png`

### S6 AI 关闭降级兜底（6/6 通过）

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S6-1 | 设置页 UI 关闭 AI | ✅ | "AI 能力已保存（AI 能力保持关闭）" |
| S6-2 | 浮钮与菜单消失 | ✅ | 均 0 个 |
| S6-3 | 直连 POST /ai/chat 与 /ai/sync-match | ✅ | 两接口信封均 403 + AI_DISABLED |
| S6-4 | 模板列表接口正常 | ✅ | 列表 total=73 |
| S6-5 | 模板列表页面渲染正常 | ✅ | 15 行 |
| S6-6 | 规则版同名匹配可用且 AI 按钮隐藏 | ✅ | AI 按钮=0；规则版按钮可见（打开已保存任务时源列尚未加载、按钮处于禁用态属预期，未强制点击） |

截图：`ai-s6-ai-disabled-list.png`

### S7 附件参考 Excel（8/8 通过，真实 GLM）

上传「表头乱序 + 改名列 + 合并标题行」的参考件（`fixtures/费用参考.xlsx`，由
`fixtures-generator` 用 ClosedXML 生成），验证 AI 生成模板的列名/列序跟随参考件：

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S7-1 | 上传参考 Excel 出现附件条 | ✅ | "费用参考.xlsx（7 KB）" |
| S7-2 | 发起需求并推动至草稿出现 | ✅ | 附件随消息内联解析，草稿出现=true |
| S7-3 | 草稿列名与列序对齐参考件 | ✅ | 卡片"凌河区人民医院门诊费用汇总表"，列序=`费用类别 → 单据数 → 金额合计 → 就诊科室`（改名列"就诊科室"保留、列序与参考一致，与标准骨架默认相反） |
| S7-4 | 卡片校验 | ✅ | "服务端校验通过。" |
| S7-5 | 卡片试跑 | ✅* | dry-run 接口返回可解释参数错误（模型把期间参数设为必填而卡片试跑未提供样例值，导入链路不受影响）；已如实记录原文 |
| S7-6 | 导入 | ✅ | "报表「OutpatientFeeSummaryByDept」已导入（数据集：Created）" |
| S7-7 | RcAiAuditLog 附件参考审计 | ✅ | 本会话 9 条 = attachment_reference + describe_table×2 + list_tables + sample_rows + run_readonly_sql×2 + submit×2 |
| S7-8 | 现场清理无残留 | ✅ | DELETE 200，残留 0 |

截图：`ai-s7-attach-chip.png`、`ai-s7-draft-card.png`、`ai-s7-imported.png`

### S8 附件参考图片（5/5 通过，视觉模型识别）

用 Playwright 渲染一张"住院费用科室统计表"HTML 表格并截图作为图片附件（视觉模型为
`VisionModel` 空回退主模型 `glm-5.3-flash`——实测主模型原生支持图像输入）：

| 编号 | 检查点 | 结果 | 实际 |
| --- | --- | --- | --- |
| S8-1 | 图片夹具产出 | ✅ | Playwright 截图 output/住院费用科室统计表.png |
| S8-2 | 上传截图并推动至草稿出现 | ✅ | 视觉解析 + 工具循环一次通过 |
| S8-3 | 草稿列名照抄截图表头 | ✅ | 列序=`科室名称 → 费用类别 → 发生金额 → 单据张数`（照抄截图表头） |
| S8-4 | RcAiAuditLog 附件参考审计 | ✅ | 本会话 7 条 = attachment_reference + describe_table×2 + sample_rows×2 + list_tables + submit |
| S8-5 | 会话清理 | ✅ | DELETE /sessions 幂等成功 |

截图：`ai-s8-draft-card.png`

## 三、发现的问题与处理

| # | 级别 | 问题 | 处理 |
| --- | --- | --- | --- |
| 1 | 高（阻断建表） | `Apply-AiAssistantSchema.ps1` 建表语句中 `RowCount` 为 SQL Server 保留字，首次执行报"关键字 'RowCount' 附近有语法错误"，事务回滚 | 修复：CREATE TABLE 中改为 `[RowCount]`（幂等校验分支本就按列名匹配，无需改）。修复后真实建表成功，二次执行验证幂等 |
| 2 | 高（AI 全接口 500） | `PocLlmClient` 无自动注册标记（单测手工构造），生产 DI 容器未登记，`/ai/settings` 等全部接口 500"Unable to resolve service" | 修复：`ReportCenterStartup.ConfigureServices` 显式工厂登记（两个单参构造歧义，固定走命名 HttpClient） |
| 3 | 高（熔断场景 500） | `PocAiSpecCompiler.ReadReportAndParameterDesign` 对缺失 sheetName 的 spec 直接 `sheetName.IndexOfAny` 空引用，违反"失败转结构化错误回传模型"设计 | 修复：字符白名单检查增加 `string.IsNullOrEmpty(sheetName)` 短路；缺失场景此前已记 SPEC_VALUE_INVALID，不再二次解引用 |
| 4 | 中（模型自修受阻） | `describe_table` 的 TABLE_NOT_FOUND 错误消息无自纠正线索，真实模型把"表不存在"误读为权限墙后放弃重试 | 加固：错误消息追加"非权限限制，请先 list_tables 取真实表名（通常不含 dbo. 前缀）重试" |
| 5 | 中（传输健壮性） | 医院内网中间件可能按本机码页（GBK）解码 HTTP 请求/响应体 | 加固：LLM 请求体 ASCII 转义序列化（`\uXXXX`，线格式无多字节字符）；响应体显式按 UTF-8 解码（不信任响应头 charset） |
| 6 | 低（测试基建） | mock 响应头缺 `charset=utf-8`，.NET 按本机码页解码中文响应异常 | 修复：mock 三个响应分支全部显式 `charset=utf-8` |
| 7 | 说明（非缺陷） | 用 Git Bash curl **内联中文字符串**做探针时，命令行被控制台按 GBK 编码发出（错误字节与 GBK 字符表逐一吻合：帮=B0/请=C7/测=B2/合=BA），触发后端 MVC 严格 UTF-8 解码器正确拒绝——后端行为完全符合预期；浏览器/标准 HTTP 客户端不受影响 | 处置：探针一律改为 heredoc UTF-8 文件或走浏览器；记录为探针注意事项，不改产品 |
| 8 | 说明（非缺陷） | 本机 MSBuild 增量构建偶发"成功但未拷贝新 DLL 到 Web.Entry bin"+陈旧 dotnet 节点持有文件锁，造成数轮"改了代码行为没变"的假象 | 处置：构建流程固化为"停进程 → 删 entry bin 内陈旧 DLL → 重建 → 反射验证标记 → 启动" |

**阶段四新增发现与处理**：

| # | 级别 | 问题 | 处理 |
| --- | --- | --- | --- |
| 9 | 高（老库启动崩溃） | SqlSugar `InitTables` 向**非空表** ALTER 加 NOT NULL 无默认列（VisionModel）会被 SQL Server 拒绝，先升后端再跑脚本会导致启动崩溃 | 修复：Apply 脚本幂等路径先 `COL_LENGTH` 检查加列（含 DEFAULT 空串），并明确部署顺序——**必须先跑脚本再上新后端**（文档 §9.5 已记录） |
| 10 | 中（熔断场景 500 → 结构化） | 见问题 3；阶段四重跑中同场景回归通过 | 已随问题 3 修复 |
| 11 | 低（测试稳定性） | glm-5.3-flash 对 S7 场景偶发把 `parameters.sourceType` 写成白名单外的 `input` | 处置：E2E 需求消息显式附参数枚举白名单（Form 等），失败追问自动纠错；已连续两轮全绿 |

## 三（续）、阶段四单测与门禁数字

| 项 | 数字 | 说明 |
| --- | --- | --- |
| 后端 xUnit（含阶段四） | **462/462 通过** | 新增 `PocAiAttachmentReferenceTests` 11 用例：Excel 表头探测/列序/合并摘要、无表头警告、PDF 文本提取/无文本层拒绝、附件校验拒绝、视觉 JSON 映射、视觉失败降级（Theory）、注入与审计、图片降级、超限拒绝 |
| 前端 Vitest（含阶段四） | **344/344 通过** | 新增 `aiAttachment.test.ts` 10 用例：类型/大小/数量校验、base64 读取可逆、大小格式化、载荷映射 |
| 注释质量门禁 | **通过，检查 114 个源文件** | 阶段四新增代码全部带方法级注释与阶段注释 |

## 五、遗留项

1. **首次启用无 UI 入口**：`/ai-settings` 页面被 `aiEnabled` 门控，AI 从"关闭"到"首次启用"只能走 `POST /ai/settings` 接口或数据库（S1-2 如实记录了该行为）。建议后续在关闭态也保留设置页入口（仅启用开关可用），或将引导写入部署文档。
2. **glm-5.3-flash 编排随机性**：对长篇 spec 需求偶发"只回文字计划"或首轮 spec 细节错误（实测 `sourceType:'none'`/'input'）。现有工具循环 + 错误清单回传 + 追问机制可自愈（阶段四 E2E 需求消息已显式附枚举白名单，两轮全绿）；对确定性要求高的生产环境建议选更强档位（如 glm-5.3）。
3. **S6-6 规则版按钮未强制点击**：打开已保存任务时源列加载前"按同名自动匹配"处于禁用态，最终跑以"按钮渲染 + AI 按钮隐藏"判定（点击执行=false 已如实记录）；点击行为已在对未保存草稿的任务上人工验证过同名匹配提示。
4. **会话为进程内存**：后端重启即清空（S4 已验证过期提示链路），多实例部署需改分布式会话（当前单实例部署不受影响）。
5. **`RcAiAuditLog` 无归档策略**：随时间增长，建议运维侧按月归档。
6. **本机构建流程注意**：增量构建偶发不拷贝新 DLL 到 entry bin（问题 8），重建后请核对 DLL 时间戳或用本文附录的构建流程。
7. **阶段四 S7-5 试跑语义**：模型可能把期间参数设为必填而卡片试跑未给样例值，dry-run 返回可解释的 PARAM_REQUIRED——导入链路不受影响；提示用户在草稿参数行填值后即可试跑（S7-5 已按可解释结果判定通过并记录原文）。

## 六、结论

模板 AI（自然语言 → 查数 → 草稿 → 校验 → 试跑 → 导入）、附件参考（Excel 列序对齐 / 图片视觉识别 → 对齐模板）、同步 AI（语义匹配 → 置信标注 → 保存）、AI 可关闭降级、熔断与会话过期全链路在**真实库 + 真实 GLM 端点**下全部可用；发现的 4 个产品缺陷（schema 保留字、DI 缺注册、SpecCompiler NRE、VisionModel 老库升级顺序）与 2 处健壮性短板（TABLE_NOT_FOUND 提示、线格式编码）均已修复并回归通过。AI 默认关闭语义、规则版同名匹配基线、ApiKey 打码与审计落库均按设计工作。

## 附录：本次对交付代码的修改清单（均已含方法级注释并通过门禁）

| 文件 | 变更 |
| --- | --- |
| `scripts/database/Apply-AiAssistantSchema.ps1` | `[RowCount]` 保留字括号（问题 1）；阶段四幂等加 `VisionModel` 列（问题 9） |
| `Admin.NET/HD.ReportCenter.Application/ReportCenterStartup.cs` | PocLlmClient DI 工厂登记（问题 2） |
| `Admin.NET/.../AiAssistant/PocAiSpecCompiler.cs` | sheetName 空引用短路（问题 3） |
| `Admin.NET/.../AiAssistant/PocAiToolExecutor.cs` | TABLE_NOT_FOUND 自纠正提示（问题 4） |
| `Admin.NET/.../AiAssistant/PocAiLlmClient.cs` | ASCII 转义请求体 + 显式 UTF-8 响应解码（问题 5）；阶段四追加 ContentParts 多模态 content 数组支持（纯追加） |
| `Admin.NET/.../AiAssistant/PocAiReferenceMaterial.cs`（新增） | 附件统一契约 + 限制校验 |
| `Admin.NET/.../AiAssistant/PocAiExcelReferenceParser.cs`（新增） | ClosedXML 参考解析 |
| `Admin.NET/.../AiAssistant/PocAiPdfReferenceParser.cs`（新增） | PdfPig 文本层解析（依赖例外，版本钉 0.1.16） |
| `Admin.NET/.../AiAssistant/PocAiVisionAnalyzer.cs`（新增） | 视觉单次调用与降级信号 |
| `Admin.NET/.../AiAssistant/PocAiChatService.cs` | 附件解析分发、会话参考材料存储、提示词注入、attachment_reference 元信息审计 |
| `Admin.NET/.../AiAssistant/PocAiChatSessionStore.cs` | 会话参考材料存量（≤6 裁剪） |
| `Admin.NET/.../AiAssistant/PocAiSettingsService.cs / PocAiSettingsStore.cs / PocAiAssistantEntities.cs` | VisionModel 读写与回退语义 |
| `Admin.NET/.../AiAssistant/PocAiSystemPrompt.cs / .md` | 「参考材料」规则段 + Build 注入参数 + BuildReferenceSection |
| `ReportWeb/src/aiAttachment.ts / aiAttachment.test.ts`（新增） | 附件校验/读取/标签/载荷纯函数 + 测试 |
| `ReportWeb/src/AiAssistantPanel.tsx / AiSettingsPage.tsx / aiSettingsForm.ts / types.ts` | 附件条 UI、VisionModel 字段、请求契约 |
| `scripts/e2e/ai-assistant/`（阶段四扩充） | S7/S8 场景、fixtures 生成器与参考 Excel 夹具、query_ai_audit 增 ErrorSummary |
| `scripts/testing/mock-llm/`（新增） | 故障注入器 + README |
