# AI 助手 E2E 故障注入 mock LLM

真实模拟已改走真实 GLM 通用 OpenAI 兼容端点（见 `docs/architecture/ai-assistant.md`），本目录只保留一个**纯故障注入器**，用于真库 E2E 中需要确定性失败的两个场景：

| 场景 | 触发条件 | 注入行为 |
| --- | --- | --- |
| S3 熔断 | 请求携带 `tools` | 每轮返回必然编译失败的 `submit_report_spec` 工具调用（spec 含未知字段，C# 编译器严格拒绝）；历史出现 `SUBMIT_CIRCUIT_OPEN` 回执后改返回“人工介入（离线流水线）”结论文本 |
| S5 非法 JSON 降级 | 请求不携带 `tools`（`/ai/sync-match` 提示词形态） | 返回非 JSON 乱文，后端降级为同名规则结果并提示 |

## 用法

```bash
# 默认端口 5099
node scripts/testing/mock-llm/mock-llm-server.js

# 自定义端口
MOCK_LLM_PORT=5101 node scripts/testing/mock-llm/mock-llm-server.js
```

然后在「AI 设置」页把服务地址（baseUrl）配成 `http://127.0.0.1:5099/v1`、模型名任意非空（如 `mock-fault-injector`）、ApiKey 留空（保留原值）。后端 `PocLlmClient` 会向 `{baseUrl}/chat/completions` 发送标准 OpenAI 兼容请求，无需任何代码改动。

## 请求日志

每次调用追加一行 JSON 到 `output/mock-llm-calls.log`（JSONL），只记录时间/是否带 tools/工具名/消息数等元数据，**不落消息正文**。测试后可直接统计行数核对调用次数。

## 复用说明

以后做真库 E2E 若需要完整对话替身（非故障注入），无需改本服务：把「AI 设置」的 baseUrl 指向任意 OpenAI 兼容端点（真实 GLM/内网 vLLM/Ollama）即可，`/v1/models` 也已实现供连通性测试使用。
