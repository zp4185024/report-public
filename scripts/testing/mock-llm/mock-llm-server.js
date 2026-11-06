/**
 * hd-report-center AI 助手 E2E 故障注入器（OpenAI 兼容 mock LLM）。
 *
 * 定位：真实模拟已改走真实 GLM 通用端点（见 docs/architecture/ai-assistant.md），
 * 本服务不再是通用对话替身，只保留两个确定性故障场景，供真库 E2E 稳定复现：
 *   1. S3 熔断场景：对携带 tools 的 /chat/completions 请求逐轮返回一个必然编译失败的
 *      submit_report_spec 工具调用；一旦请求历史中出现 SUBMIT_CIRCUIT_OPEN 工具回执，
 *      立即改返回“提示人工介入（离线流水线）”的结论文本，模拟模型放弃自修。
 *   2. S5 非法 JSON 降级场景：对不携带 tools 的请求（/ai/sync-match 的匹配提示词即此形态）
 *      返回非 JSON 乱文，驱动后端降级为同名规则结果并在响应中提示。
 *
 * 用法（真库 E2E 换 baseUrl 的复用方式）：
 *   node scripts/testing/mock-llm/mock-llm-server.js            # 默认端口 5099
 *   MOCK_LLM_PORT=5101 node scripts/testing/mock-llm/mock-llm-server.js
 * 然后在“AI 设置”页把服务地址配成 http://127.0.0.1:5099/v1（模型名任意非空），
 * 后端 PocLlmClient 会向 {baseUrl}/chat/completions 发送 OpenAI 兼容请求。
 * 若以后需要完整对话替身（非故障注入），换用任意 OpenAI 兼容 mock 或真实端点即可，
 * 本服务对 /v1/models 返回固定清单，兼容连通性测试形态。
 *
 * 请求日志：每次调用追加一行 JSON 到 output/mock-llm-calls.log（JSONL），
 * 只记录时间/是否带 tools/工具名/消息数等元数据，绝不落消息正文，避免误存业务数据。
 */
const http = require('http');
const fs = require('fs');
const path = require('path');

/** 监听端口；默认 5099，可用 MOCK_LLM_PORT 覆盖，避免与本地常用端口冲突。 */
const PORT = Number(process.env.MOCK_LLM_PORT || 5099);
/** 模型名回显：后端不做校验，原样返回配置值即可。 */
const ECHO_MODEL = process.env.MOCK_LLM_MODEL || 'mock-fault-injector';
/** JSONL 调用日志路径；放在脚本同级 output/ 下，跟随脚本目录便于查找与清理。 */
const LOG_FILE = path.join(__dirname, 'output', 'mock-llm-calls.log');

/**
 * 构造一个必然编译失败的报表 spec：specVersion=1 下携带未知顶层字段，
 * C# SpecCompiler 严格拒绝未知字段（与 ps1 有意分歧项），保证每轮 submit 都失败。
 * @returns {object} 非法 spec 对象。
 */
function buildInvalidSpec() {
  return {
    specVersion: 1,
    report: { code: 'e2e_ai_circuit', name: '熔断场景草稿' },
    totallyUnknownField: true,
  };
}

/**
 * 追加一行调用日志；写日志失败只打控制台告警，不影响 HTTP 响应。
 * @param {{hasTools:boolean, toolName:string|null, messageCount:number, note:string}} entry - 日志元数据。
 * @returns {void}
 */
function appendLog(entry) {
  const line = JSON.stringify({ time: new Date().toISOString(), ...entry });
  try {
    fs.mkdirSync(path.dirname(LOG_FILE), { recursive: true });
    fs.appendFileSync(LOG_FILE, `${line}\n`, 'utf8');
  } catch (exception) {
    console.warn(`mock-llm 写日志失败：${exception.message}`);
  }
}

/**
 * 读取请求体并解析 JSON；解析失败返回空对象，由分发逻辑按默认分支处理。
 * @param {http.IncomingMessage} request - 请求流。
 * @returns {Promise<object>} 解析后的请求体。
 */
function readJsonBody(request) {
  return new Promise((resolve) => {
    let raw = '';
    request.on('data', (chunk) => { raw += chunk; });
    request.on('end', () => {
      try {
        resolve(JSON.parse(raw || '{}'));
      } catch {
        resolve({});
      }
    });
  });
}

/**
 * 组装 OpenAI 兼容补全响应：文本与 tool_calls 二选一。
 * @param {string|null} content - 回复文本；为 null 时必须提供 toolCalls。
 * @param {Array<{id:string,name:string,args:object}>|null} toolCalls - 工具调用清单。
 * @returns {object} OpenAI chat.completion 形态响应体。
 */
function completion(content, toolCalls) {
  const message = { role: 'assistant', content };
  if (toolCalls && toolCalls.length > 0) {
    message.tool_calls = toolCalls.map((call) => ({
      id: call.id,
      type: 'function',
      function: { name: call.name, arguments: JSON.stringify(call.args) },
    }));
    message.content = null;
  }
  return {
    id: `mock-${Date.now()}`,
    object: 'chat.completion',
    created: Math.floor(Date.now() / 1000),
    model: ECHO_MODEL,
    choices: [{ index: 0, message, finish_reason: toolCalls && toolCalls.length > 0 ? 'tool_calls' : 'stop' }],
  };
}

/**
 * 依据请求形态分发故障场景：带 tools 走熔断链，不带 tools 返回非法 JSON。
 * @param {object} body - OpenAI 兼容请求体。
 * @returns {{payload:object, note:string}} 响应体与日志备注。
 */
function dispatch(body) {
  const hasTools = Array.isArray(body.tools) && body.tools.length > 0;
  const messages = Array.isArray(body.messages) ? body.messages : [];
  if (hasTools) {
    const circuitOpen = messages.some(
      (item) => item.role === 'tool' && typeof item.content === 'string' && item.content.includes('SUBMIT_CIRCUIT_OPEN'),
    );
    if (circuitOpen) {
      // 后端已熔断：返回结论文本，引导人工走离线流水线，模型不再尝试提交。
      return {
        payload: completion(
          '本会话已连续 3 次提交失败被熔断。请改用离线流水线（ZCode + report-design skill + New-ReportPackage.ps1）人工介入修正报表。',
          null,
        ),
        note: 'circuit-open-final-text',
      };
    }
    return {
      payload: completion(null, [
        { id: `call_${Date.now()}`, name: 'submit_report_spec', args: { spec: buildInvalidSpec() } },
      ]),
      note: 'invalid-spec-tool-call',
    };
  }
  // 不带 tools（同步匹配提示词/连通性测试形态）：返回非法 JSON 乱文，驱动降级分支。
  return {
    payload: completion('@@@not-a-json{{{断裂文本', null),
    note: 'garbage-text',
  };
}

/** 创建并启动故障注入 HTTP 服务；启动失败直接退出并打印原因。 */
const server = http.createServer(async (request, response) => {
  const url = request.url || '';
  if (request.method === 'GET' && url.includes('/models')) {
    // 必须显式 charset=utf-8：缺省时 .NET HttpClient 会按系统码页解码，中文响应直接乱码报错。
    response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify({ object: 'list', data: [{ id: ECHO_MODEL, object: 'model', owned_by: 'mock' }] }));
    return;
  }
  if (request.method === 'POST' && url.includes('/chat/completions')) {
    // 请求一到达就记一行（区别于解析完成后的 dispatch 日志），用于诊断“请求是否真的到达 mock”。
    appendLog({ hasTools: null, toolName: null, messageCount: null, note: 'request-received' });
    const body = await readJsonBody(request);
    const tools = Array.isArray(body.tools) ? body.tools : [];
    const toolName = tools.length > 0 ? 'submit_report_spec' : null;
    const { payload, note } = dispatch(body);
    appendLog({
      hasTools: tools.length > 0,
      toolName,
      messageCount: Array.isArray(body.messages) ? body.messages.length : 0,
      note,
    });
    // 必须显式 charset=utf-8：缺省时 .NET HttpClient 会按系统码页解码，中文响应直接乱码报错。
    response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify(payload));
    return;
  }
  response.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
  response.end(JSON.stringify({ error: { message: `mock-llm 未实现的路径 ${url}` } }));
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`mock-llm 故障注入器已启动：http://127.0.0.1:${PORT}/v1（日志：${LOG_FILE}）`);
});
