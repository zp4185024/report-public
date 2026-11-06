# 阶段四：模板 AI 附件参考（图片 / Excel / PDF → 标准骨架模板）

- 日期：2026-11-05
- 状态：需求方已确认方向，待实现（前置：阶段一~三 E2E 完成后开工）
- 关联：`docs/superpowers/specs/2026-11-04-ai-report-assistant-design.md`（阶段一~三主 spec）、`docs/architecture/ai-assistant.md`

## 1. 需求与已确认决策

用户需求：很多模板有现成参考物料（截图、Excel 报表、PDF 文件），AI 要能"看"这些
参考生成模板，而不是只凭自然语言+查库凭空设计。

已与需求方确认的两项决策（2026-11-05）：

| 决策点 | 结论 |
| --- | --- |
| 版式还原程度 | **标准骨架内参考复刻**：附件只作内容参考（列名/列序/标题/参数/合计提示），模板仍由 C# SpecCompiler 按既有标准骨架（标题行/参数行/表头行/明细模板行/合计行）生成。不做自由版式复刻（合并单元格任意布局），后者如需要另行立 schema v2 设计 |
| 入库方式 | **维持人工确认**：AI 产出草稿卡片，人工预览/试跑后点导入；不放开"AI 直接入库" |

沿用铁律：AI 只出意图（spec），代码出模板；`submit_report_spec`/草稿卡片/导入链路全部不变。

## 2. 核心设计：附件理解与工具循环解耦

视觉模型不一定支持 tools 协议（GLM 视觉模型实测为准），因此**附件理解不进工具循环**：

```
用户消息 + 附件
   ├─ Excel(xlsx) ──▶ PocAiExcelReferenceParser（ClosedXML 纯后端解析，无需 LLM）
   ├─ PDF          ──▶ PocAiPdfReferenceParser（PdfPig 文本层提取 + 坐标启发列切分）
   └─ 图片         ──▶ PocAiVisionAnalyzer（一次性和视觉模型对得到结构化 JSON，不走 tools）
                ▼
   统一参考材料 JSON：{title?, columns:[{name,note?}], groups?, paramsHint?, totalsHint?, warnings[]}
                ▼
   作为「参考材料」段注入主模型系统上下文 → 主模型（工具循环照旧）产出 spec
```

- 主模型提示词新增"参考材料"段约束：**列名/列序/标题必须从参考材料提炼**，
  参考列与库字段对齐仍靠 `describe_table`/`sample_rows`，不得凭空编造参考里没有的列；
  参考与库字段名不一致时以库字段为准并在回复中说明映射。
- 视觉模型单次调用即返回（不给 tools、不进循环），失败/超时降级为"仅文本继续"并提示。

## 3. 各类型解析规则

| 类型 | 解析方式 | 说明 |
| --- | --- | --- |
| xlsx | ClosedXML（后端已有依赖） | 取首个非空工作表；前 3 行探测表头行；输出列名+列序；合并单元格输出摘要（`A1:C1 已合并`）；数据行采样 ≤5 行仅判类型不外发全量 |
| pdf | PdfPig（新增轻量依赖，Apache-2.0 纯托管） | 提取文本层+字符坐标，按行聚类、x 间隔启发切列；**无文本层的扫描版 PDF 不做 OCR**，直接提示"请转传图片" |
| 图片（png/jpg/jpeg/webp） | 视觉模型（OpenAI 兼容 image_url content part，base64 data URL） | 提示词要求输出上述参考 JSON，禁止编造数据值 |

`PocAiSettings` 新增可选列 `VisionModel`（Apply 脚本幂等加列；空则回退 `Model`，主模型本身具备视觉能力时单模型即可）。

## 4. 限制与安全

- 单条消息附件 ≤3 个；图片 ≤5MB、xlsx/pdf ≤10MB；超限前端拦截+后端兜底拒绝。
- 附件内容**不落库**：随内存会话存活，30 分钟过期即清（与"会话不落库"一致）；
  审计只记元信息（文件名/类型/大小），不记内容。
- 外发红线延续：图片会发给视觉模型、解析摘要会发给主模型——面板常驻提示补充
  "上传的附件内容将发送至所配置的 LLM 服务"；敏感凭证类图片属用户责任。
- 前端附件条展示（缩略图/文件名+大小），发送后随消息渲染。

## 5. 改动边界（延续分界原则）

- 新文件：后端 `AiAssistant/PocAiExcelReferenceParser.cs`、`PocAiPdfReferenceParser.cs`、
  `PocAiVisionAnalyzer.cs`、`PocAiReferenceMaterial.cs`（统一 JSON 契约）；前端 `aiAttachment.ts` + 面板内附件区。
- 追加修改：`PocAiLlmClient`（content 支持 parts 数组，纯追加重载）、`PocAiChatService`
  （发送前附件解析+注入）、`PocAiAssistantApi`（若走独立上传端点则新增，倾向 base64 随消息内联不新增端点）、
  设置页（VisionModel 字段）、系统提示词（参考材料段）、Apply 脚本（VisionModel 幂等加列）。
- 依赖变化：仅新增 PdfPig 一项轻量包（记录为对"零新增依赖"约束的一次明示例外，理由：无文本解析库则 PDF 能力不可达）。
- SpecCompiler / 工具集 / 导入链路 / 数据库主结构：**零改动**。

## 6. 测试与验收

- 单测：Excel 解析（表头探测/列序/合并摘要）、PDF 文本提取（含无文本层拒绝）、
  图片超限/类型拒绝、视觉模型失败降级为纯文本、参考 JSON 注入提示词断言。
- 前端：附件条渲染、超限拦截、VisionModel 设置回显打码沿用既有打码逻辑。
- E2E 补场景 S7：上传样例 Excel（表头乱序+改名列）→ AI 生成列序对齐参考的模板并导入成功；
  S8：上传报表截图（视觉模型实测）→ 生成模板列名与截图一致。
- 真实视觉模型可用性以阶段一 E2E 实测的 `/models` 清单为准；无可用视觉模型时
  图片功能在设置页标灰并提示，Excel/PDF 不受影响（不依赖视觉）。
