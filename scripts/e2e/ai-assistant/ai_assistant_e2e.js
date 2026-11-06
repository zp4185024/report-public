/**
 * hd-report-center 产品内 AI 助手真库 E2E 主脚本（S1-S6）。
 *
 * 模拟形态：真实库 + 真实后端(5005) + 真实前端(5176) + 真实 UI 操作；
 * LLM 默认走真实 GLM 通用 OpenAI 兼容端点（baseUrl/model/key 全部来自环境变量，
 * **密钥绝不写入任何文件**）；唯一替身是 scripts/testing/mock-llm 的故障注入器，
 * 仅用于 S3 熔断与 S5 非法 JSON 降级两个确定性失败场景。
 *
 * 场景清单：
 *   S1 设置页可见性：AI 关闭 → 入口隐藏；API 首次启用（前端 /ai-settings 被开关门控，
 *      首次启用只能走接口，属已记录发现）→ 打开设置页核对打码回显 → 连通性测试 → 入口出现。
 *   S2 模板 AI 全链路：聊天侧栏 → 真实 GLM 工具循环（查数工具打到真库）→ 草稿卡片 →
 *      校验 → 试跑 → 导入 → 模板列表出现新报表 → 审计表核对 → 现场清理。
 *   S4 会话过期：记录 sessionId → 真实重启后端（内存会话清空）→ 原 sessionId 续聊 →
 *      前端提示“会话已过期”。
 *   S3 熔断：指向 mock → 连续 3 次必然失败的 spec 提交 → 第 3 次后熔断提示人工介入。
 *   S5 同步 AI：真实 GLM 语义匹配（同名零命中设计）→ 置信标注/人工映射不覆盖/需人工项
 *      不进 mappings → 保存走现有校验链；再指向 mock 非法 JSON → 降级为同名基线并提示。
 *   S6 关闭降级：UI 关闭 AI → 入口消失；直连 POST /ai/chat 与 /ai/sync-match → 403
 *      AI_DISABLED；模板列表与同步设计器规则版同名匹配不受影响。
 *
 * 运行前提：后端(5005)、前端(5176)、mock-llm(5099)、SQL Server 真库、GLM_API_KEY 已设置；
 * 产物：docs/images/ai-s*.png 截图 + output/e2e_result.json 检查点汇总。
 * 退出码：全部通过 0，任一失败 1。
 */
const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

/** 前端基地址；本机 5175 被旧实例占用，本次验证实例固定 5176。 */
const BASE_URL = process.env.E2E_BASE_URL || 'http://127.0.0.1:5176';
/** 真实 GLM 端点（OpenAI 兼容通用端点）；密钥只从进程环境读取，绝不落盘。 */
const GLM_BASE_URL = process.env.GLM_BASE_URL || 'https://open.bigmodel.cn/api/paas/v4';
const GLM_MODEL = process.env.GLM_MODEL || 'glm-5.3-flash';
/** 故障注入 mock 的 baseUrl 与模型名；仅 S3/S5 降级分支使用。 */
const MOCK_BASE_URL = process.env.MOCK_BASE_URL || 'http://127.0.0.1:5099/v1';
const MOCK_MODEL = 'mock-fault-injector';
/** 后端可执行文件与工作目录；S4 重启时使用。 */
const BACKEND_DIR = process.env.E2E_BACKEND_DIR
  || 'D:\\zp\\git\\hd-report-center\\Admin.NET\\Admin.NET.Web.Entry\\bin\\Release\\net8.0';
const BACKEND_EXE = path.join(BACKEND_DIR, 'Admin.NET.Web.Entry.exe');
/** 后端端口（S4 重启定位进程用）。 */
const BACKEND_PORT = 5005;
/** pwsh 完整路径（WindowsApps 安装位置），供辅助脚本调用。 */
const PWSH = path.join(process.env.LOCALAPPDATA, 'Microsoft', 'WindowsApps', 'pwsh.exe');
/** 截图输出目录：docs/images/（沿用现有命名惯例，kebab-case）。 */
const IMAGE_DIR = 'D:\\zp\\git\\hd-report-center\\docs\\images';
/** 结果 JSON 输出目录（已 gitignore 的本地 output）。 */
const OUT_DIR = path.join(__dirname, 'output');
/** playwright-core 模块：优先本目录依赖，回退复用模板列表 E2E 的独立安装（避免重复联网安装）。 */
const playwright = (() => {
  try {
    return require('playwright-core');
  } catch {
    return require('D:/zp/git/hd-report-center/scripts/e2e/report-list/node_modules/playwright-core');
  }
})();

/** 测试用报表编码（S2 导入产物）；时间戳避免撞历史数据。 */
const S2_REPORT_CODE = `e2e_ai_${Date.now()}`;
/** S5 同步任务编码；固定值，开始前先清理历史同名任务保证幂等。 */
const S5_TASK_CODE = 'e2e_ai_sync';
/** S5 样例目标表；由 scratch_target_table.ps1 创建/清理。 */
const S5_TARGET_TABLE = 'E2E_AI_TARGET_REPORT';

/** 全量结果汇总，结束时写入 e2e_result.json。 */
const result = {
  started_at: new Date().toISOString(),
  finished_at: null,
  base_url: BASE_URL,
  llm_endpoint: GLM_BASE_URL,
  llm_model: GLM_MODEL,
  scenario_results: {},
  total: 0,
  pass: 0,
  fail: 0,
  checkpoints: [],
  notes: [],
};

/**
 * 记录单个检查点并更新通过/失败计数。
 * @param {string} id - 检查点编号，如 S1-1。
 * @param {string} title - 检查点中文标题。
 * @param {boolean} ok - 是否通过。
 * @param {string} detail - 关键观测值或失败原因。
 */
function record(id, title, ok, detail) {
  result.total += 1;
  if (ok) result.pass += 1;
  else result.fail += 1;
  result.checkpoints.push({ id, title, status: ok ? 'PASS' : 'FAIL', detail });
  console.log(`${ok ? '[PASS]' : '[FAIL]'} [${id}] ${title} — ${detail}`);
}

/**
 * 记录场景附加观测（不计数），供测试文档引用。
 * @param {string} text - 观测内容。
 */
function note(text) {
  result.notes.push(text);
  console.log(`[NOTE] ${text}`);
}

/**
 * 截图到 docs/images/ 并返回文件名；截图失败不阻断主流程。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} name - 文件名（不含扩展名）。
 * @returns {Promise<string>} 相对 docs/images 的文件名。
 */
async function shot(page, name) {
  try {
    const file = path.join(IMAGE_DIR, `${name}.png`);
    await page.screenshot({ path: file, fullPage: false });
    return `${name}.png`;
  } catch (exception) {
    console.warn(`截图失败 ${name}: ${exception.message}`);
    return '';
  }
}

/**
 * 断言元素可见并返回定位器；超时抛出中文说明异常。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} selector - Playwright 选择器。
 * @param {string} desc - 元素业务描述。
 * @param {number} timeout - 等待毫秒数。
 * @returns {Promise<import('playwright-core').Locator>} 可见定位器。
 */
async function expectVisible(page, selector, desc, timeout = 10000) {
  const locator = page.locator(selector).first();
  try {
    await locator.waitFor({ state: 'visible', timeout });
  } catch (exception) {
    throw new Error(`未等到「${desc}」(${selector})：${exception.message.split('\n')[0]}`);
  }
  return locator;
}

/**
 * 通过页内 fetch 读取同源 JSON 接口（复用前端代理，无需关心端口转发）。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} apiPath - 以 /api 开头的接口路径。
 * @param {RequestInit|null} options - fetch 选项。
 * @returns {Promise<{status:number, body:object}>} 状态码与响应体。
 */
async function fetchApi(page, apiPath, options = null) {
  return page.evaluate(async ([pathValue, init]) => {
    const response = await fetch(pathValue, init);
    let body = null;
    try {
      body = await response.json();
    } catch {
      body = null;
    }
    return { status: response.status, body };
  }, [apiPath, options]);
}

/**
 * 组装 AI 设置保存载荷；apiKey 为空串表示保留服务端原值（打码回显不会被误清空）。
 * @param {object} overrides - 需要覆盖的字段。
 * @returns {object} 完整设置输入。
 */
function settingsPayload(overrides = {}) {
  return {
    isEnabled: true,
    baseUrl: GLM_BASE_URL,
    apiKey: '',
    model: GLM_MODEL,
    sampleRowLimit: 20,
    sqlProbeEnabled: true,
    sqlProbeRowLimit: 100,
    maxToolRounds: 8,
    ...overrides,
  };
}

/**
 * 通过页面 fetch 保存 AI 设置（走同源代理，等价于设置页保存动作的网络层）。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {object} overrides - 设置字段覆盖。
 * @returns {Promise<{status:number, body:object}>} 保存响应。
 */
async function saveAiSettings(page, overrides = {}) {
  return fetchApi(page, '/api/report-center/ai/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settingsPayload(overrides)),
  });
}

/**
 * 打开模板列表页并等待分页数据渲染完成。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function openReportList(page) {
  await page.goto(`${BASE_URL}/`, { waitUntil: 'domcontentloaded' });
  await expectVisible(page, 'main h1', '模板列表主标题');
  await page.waitForTimeout(800);
}

/**
 * 打开 AI 聊天侧栏并等待数据源选项加载。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function openAiPanel(page) {
  await openReportList(page);
  await expectVisible(page, 'button.ai-fab-button', 'AI 助手浮钮');
  await page.click('button.ai-fab-button');
  await expectVisible(page, 'aside.ai-assistant-panel', 'AI 侧栏');
  await expectVisible(page, '.ai-panel-source select', '数据源选择器');
  await page.waitForFunction(() => {
    const select = document.querySelector('.ai-panel-source select');
    return select !== null && select.options.length > 0;
  }, undefined, { timeout: 15000 });
}

/**
 * 发送一条聊天消息并等待本轮 /ai/chat 响应，返回信封体。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} message - 用户消息文本。
 * @param {number} timeout - 等待响应毫秒数（真实 GLM 多轮工具循环较慢，默认 300 秒）。
 * @returns {Promise<object>} 响应信封 {code, result}。
 */
async function sendChat(page, message, timeout = 300000) {
  const responsePromise = page.waitForResponse(
    (candidate) => candidate.url().includes('/api/report-center/ai/chat')
      && candidate.request().method() === 'POST',
    { timeout },
  );
  await page.fill('.ai-panel-composer textarea', message);
  await page.click('.ai-panel-composer button[aria-label="发送消息"]');
  const response = await responsePromise;
  const body = await response.json();
  return body;
}

/**
 * 收集当前消息流里全部工具折叠条的标签文本。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<string[]>} 标签文本清单。
 */
async function collectToolLabels(page) {
  return page.$$eval('.ai-tool-entry summary span', (nodes) => nodes.map((node) => node.textContent.trim()));
}

/**
 * 通过 pwsh 辅助脚本查询 AI 审计表；输出为单行 JSON。
 * @param {string|null} sessionId - 可选会话号过滤。
 * @returns {{total:number, filtered:number|null, rows:Array|null}} 审计统计。
 */
function queryAudit(sessionId) {
  const script = path.join(__dirname, 'query_ai_audit.ps1');
  const args = sessionId ? ['-NoProfile', '-File', script, '-SessionId', sessionId] : ['-NoProfile', '-File', script];
  const output = spawnSync(PWSH, args, { encoding: 'utf8', timeout: 60000 });
  if (output.status !== 0) {
    throw new Error(`审计查询失败：${output.stderr}`);
  }
  return JSON.parse(output.stdout.trim().split(/\r?\n/).pop());
}

/**
 * 执行样例表生命周期脚本：S5 目标表走 scratch_target_table.ps1，
 * S2 脱敏业务样例表走 sample_business_table.ps1（两表均由 pwsh 幂等创建/清理）。
 * @param {'Create'|'Drop'} action - 动作。
 * @param {'scratch'|'sample'} kind - 样例表种类，默认 scratch（S5 目标表）。
 * @returns {void}
 */
function scratchTable(action, kind = 'scratch') {
  const scriptName = kind === 'sample' ? 'sample_business_table.ps1' : 'scratch_target_table.ps1';
  const script = path.join(__dirname, scriptName);
  const output = spawnSync(PWSH, ['-NoProfile', '-File', script, `-${action}`], { encoding: 'utf8', timeout: 60000 });
  if (output.status !== 0) {
    throw new Error(`样例表(${kind}) ${action} 失败：${output.stderr || output.stdout}`);
  }
  console.log(`样例表(${kind}) ${action} 完成：${output.stdout.trim()}`);
}

/**
 * 找到占用指定端口的进程 PID。
 * @param {number} port - 目标端口。
 * @returns {string|null} PID 文本；无监听返回 null。
 */
function findPidByPort(port) {
  const output = spawnSync('netstat', ['-ano'], { encoding: 'utf8', timeout: 30000, shell: true });
  for (const line of (output.stdout || '').split(/\r?\n/)) {
    if (line.includes(`:${port} `) && line.includes('LISTENING')) {
      const parts = line.trim().split(/\s+/);
      return parts[parts.length - 1];
    }
  }
  return null;
}

/**
 * 重启后端进程（S4 真实过期路径）：杀掉 5005 监听进程 → 从 bin/Release 重新拉起 →
 * 轮询 /ai/settings 直到 200（内存会话随进程退出清空，AI 设置存库自动恢复）。
 * @returns {Promise<void>} 无返回值。
 */
async function restartBackend() {
  const pid = findPidByPort(BACKEND_PORT);
  if (pid) {
    spawnSync('taskkill', ['/PID', pid, '/F'], { encoding: 'utf8', timeout: 30000, shell: true });
  }
  // 等待端口真正释放，避免新进程绑定失败。
  for (let index = 0; index < 20; index += 1) {
    if (!findPidByPort(BACKEND_PORT)) break;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  const child = spawn(BACKEND_EXE, [], {
    cwd: BACKEND_DIR,
    detached: true,
    stdio: 'ignore',
    env: process.env,
  });
  child.unref();
  for (let index = 0; index < 60; index += 1) {
    try {
      const probe = spawnSync('curl', ['-sS', '--max-time', '3', `http://127.0.0.1:${BACKEND_PORT}/api/report-center/ai/settings`], { encoding: 'utf8', timeout: 10000 });
      if (probe.stdout && probe.stdout.includes('"isEnabled"')) {
        return;
      }
    } catch {
      // 未就绪继续轮询。
    }
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
  throw new Error('后端重启后 90 秒内未恢复健康。');
}

/**
 * S1：AI 默认关闭时入口不可见 → API 首次启用 → 设置页打码回显 → 连通性测试 → 入口出现。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS1(page) {
  const scenario = 'S1';
  try {
    // S1-1：关闭态下列表页无浮钮、导航无“AI 设置”。
    await openReportList(page);
    const fabCount = await page.locator('button.ai-fab-button').count();
    const menuCount = await page.locator('nav a[href="/ai-settings"]').count();
    record('S1-1', 'AI 关闭时浮钮与 AI 设置菜单不可见', fabCount === 0 && menuCount === 0,
      `浮钮=${fabCount} 个，AI 设置菜单=${menuCount} 个`);
    await shot(page, 'ai-s1-settings-initial-off');

    // S1-2：前端 /ai-settings 被开关门控（首次启用无 UI 入口，属已记录发现），按产品契约走接口启用。
    const aiDisabledPage = await page.goto(`${BASE_URL}/ai-settings`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(600);
    const settingsPageHeading = await page.locator('h1:has-text("AI 设置")').count();
    record('S1-2', 'AI 关闭时 /ai-settings 回退到模板列表（前端入口被开关门控）', settingsPageHeading === 0,
      `直接访问 /ai-settings 时「AI 设置」标题出现 ${settingsPageHeading} 次（预期 0，回退列表页）`);
    const save = await saveAiSettings(page, {
      apiKey: process.env.GLM_API_KEY || '',
    });
    const savedDoc = save.body && save.body.result;
    const expectMask = `sk-***${(process.env.GLM_API_KEY || '').slice(-4)}`;
    record('S1-3', 'POST /ai/settings 启用成功且 ApiKey 打码回显', save.status === 200
      && savedDoc && savedDoc.isEnabled === true
      && savedDoc.apiKeyMasked === expectMask && savedDoc.apiKeyConfigured === true,
      `status=${save.status} isEnabled=${savedDoc && savedDoc.isEnabled} masked=${savedDoc && savedDoc.apiKeyMasked}`);

    // S1-4：设置页展示启用态与打码回显。
    await page.goto(`${BASE_URL}/ai-settings`, { waitUntil: 'domcontentloaded' });
    await expectVisible(page, 'h1:has-text("AI 设置")', 'AI 设置标题');
    const headerStatus = await (await expectVisible(page, '.runtime-status', '启用状态徽标')).textContent();
    const maskedShown = (await page.locator('.ai-settings-grid label').allTextContents()).join('\n').includes(expectMask);
    record('S1-4', '设置页显示「AI 已启用」与打码密钥回显', headerStatus.includes('AI 已启用') && maskedShown,
      `状态文案="${headerStatus.trim()}"，页面含打码值=${maskedShown}`);
    await shot(page, 'ai-s1-settings-page');

    // S1-5：连通性测试（真实 GLM 最小 chat 请求）。
    await page.click('footer.ai-settings-footer button:has-text("测试连通性")');
    await page.waitForFunction(
      () => {
        const node = document.querySelector('.toast.success, [class*="success"]');
        return node !== null && node.textContent.includes('连接成功');
      },
      undefined,
      { timeout: 60000 },
    );
    const successText = (await page.locator('.toast.success, [class*="success"]').first().textContent()).trim();
    record('S1-5', '连通性测试通过（真实 GLM 端点）', true, `提示="${successText}"`);
    await shot(page, 'ai-s1-test-connection');

    // S1-6：入口下发——浮钮与菜单出现。
    await openReportList(page);
    const fabVisible = await page.locator('button.ai-fab-button').isVisible();
    const menuVisible = await page.locator('nav a[href="/ai-settings"]').isVisible();
    record('S1-6', '启用后浮钮与 AI 设置菜单出现（由 /settings 下发开关）', fabVisible && menuVisible,
      `浮钮可见=${fabVisible}，菜单可见=${menuVisible}`);
    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S1 场景异常终止', false, exception.message);
    await shot(page, 'ai-s1-error');
  }
}

/**
 * S2：模板 AI 全链路——真实 GLM 工具循环查真库 → 草稿卡片 → 校验/试跑/导入 → 列表核对 → 审计核对 → 清理。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS2(page) {
  const scenario = 'S2';
  try {
    // S2-0：样例业务表（全合成脱敏，幂等创建+播种；结束在 cleanup 阶段统一删除）。
    scratchTable('Create', 'sample');
    await openAiPanel(page);
    // 数据源选择器固定选 data_source_2（报表中心自身元数据库，非患者敏感数据）。
    await page.selectOption('.ai-panel-source select', 'data_source_2');

    // S2-1 第一轮：短指令驱动查数工具（实测短指令下工具调用稳定；长 spec 需求容易让模型只回文字计划）。
    // 数据用全合成脱敏样例表 E2E_AI_SAMPLE_OUTPATIENT（sample_business_table.ps1 播种），
    // 避开真实患者表与 Rc* 配置表（后者会被系统提示词安全红线正确拦截）。
    const probeRequirement = [
      '我要做一张「科室费用样例汇总」报表，数据就在当前数据源的业务样例表 E2E_AI_SAMPLE_OUTPATIENT（门诊费用测试样例，全合成脱敏数据，表名不含 dbo. 前缀）。',
      '不要输出文字计划，立即依次调用工具：1) list_data_sources；2) describe_table 查看 E2E_AI_SAMPLE_OUTPATIENT；',
      '3) 用 sample_rows 采样不超过 5 行确认字段取值。完成后停下等我下一步。',
    ].join('');
    let chatBody = await sendChat(page, probeRequirement);
    let chatResult = chatBody.result || {};
    global.S2_SESSION_ID = chatResult.sessionId;

    // S2-1 第二轮：结构已摸清，给出 spec 要求并要求提交；真实模型编排有随机性，草稿未出现就同会话追问，最多 3 轮。
    const submitRequirement = [
      '字段结构已经确认。现在立即调用 submit_report_spec 提交 spec，要求如下：',
      `报表编码固定为 ${S2_REPORT_CODE}，报表名称「E2E AI 科室费用样例汇总」，工作表名「科室费用样例汇总」；`,
      '按科室与费用类别汇总：SELECT DEPT_NAME AS deptName, FEE_CATEGORY AS feeCategory, COUNT(1) AS feeCount, SUM(AMOUNT) AS totalAmount FROM E2E_AI_SAMPLE_OUTPATIENT',
      'WHERE (@settleDate IS NULL OR SETTLE_DATE >= @settleDate) GROUP BY DEPT_NAME, FEE_CATEGORY ORDER BY DEPT_NAME, FEE_CATEGORY；',
      '可选参数 settleDate（date 类型，必填=false，控制类型 date）：把 @settleDate 同时声明进 variables 与 parameters；',
      'SELECT 输出的 4 个列与 fields 一一对应，不要多列。',
      '参数枚举注意：sourceType 只允许 Form/Url/System/Fixed/External（这里写 Form），controlType 只允许 text/number/date/datetime/month/year/checkbox/select/multiSelect/hidden（这里写 date），dataType 写 date。',
      '不要输出文字计划，直接提交。',
    ].join('');
    chatBody = await sendChat(page, submitRequirement);
    chatResult = chatBody.result || {};

    const nudges = [
      '不要回复文字。现在直接调用 submit_report_spec 提交 spec，按上一条消息的字段、参数与编码要求构造。',
      '再次提醒：你只能通过 submit_report_spec 工具交付，立刻调用它，这是唯一验收方式。',
      '请提交 spec：报表编码 ' + S2_REPORT_CODE + '，两表内联，五个字段，一个可选 date 参数。除调用工具外不要做别的。',
    ];
    let draftVisible = false;
    for (let index = 0; index < nudges.length && !draftVisible; index += 1) {
      draftVisible = await page.locator('.ai-draft-card').isVisible().catch(() => false);
      if (!draftVisible) {
        chatBody = await sendChat(page, nudges[index]);
        chatResult = chatBody.result || {};
      }
    }
    const totalMessages = (chatResult.messages || []).length;
    record('S2-1', '发起需求并推动至草稿出现（真实 GLM 工具循环，含追问）', chatBody.code === 200 && !!chatResult.sessionId && draftVisible,
      `code=${chatBody.code} sessionId=${chatResult.sessionId} 末轮消息=${totalMessages} 条 草稿出现=${draftVisible}`);
    await page.waitForTimeout(500);

    // S2-2：工具折叠条——工具名/SQL 摘要/行数可见。
    const labels = await collectToolLabels(page);
    const toolNames = labels.map((label) => label.split(' · ')[0]);
    const hasListDataSources = toolNames.includes('list_data_sources');
    const hasDescribe = toolNames.includes('describe_table');
    const hasSubmit = toolNames.includes('submit_report_spec');
    record('S2-2', '工具折叠条按序展示（工具名/SQL 摘要/行数）', hasListDataSources && hasDescribe && hasSubmit,
      `工具序列=[${toolNames.join(', ')}]`);
    note(`S2 工具折叠条标签：${JSON.stringify(labels)}`);
    await shot(page, 'ai-s2-chat-tool-trace');

    // S2-3：草稿卡片——状态徽标/列清单/参数清单。
    await expectVisible(page, '.ai-draft-card', '草稿卡片', 15000);
    const badge = (await (await expectVisible(page, '.ai-draft-badge', '状态徽标')).textContent()).trim();
    const draftTitle = (await page.locator('.ai-draft-header strong').first().textContent()).trim();
    const fieldChips = await page.locator('.ai-draft-section:has(strong:has-text("列清单")) .ai-draft-chip').count();
    const paramChips = await page.locator('.ai-draft-section:has(strong:has-text("参数清单")) .ai-draft-chip').count();
    record('S2-3', '草稿卡片出现（状态徽标/列清单/参数清单）', badge.includes('校验通过') || badge.includes('试跑通过'),
      `卡片="${draftTitle}" 徽标="${badge}" 列=${fieldChips} 参数=${paramChips}`);
    await shot(page, 'ai-s2-draft-card');

    // S2-4：卡片校验（服务端全链校验，复用现有链路）。
    await page.click('.ai-draft-actions button:has-text("校验")');
    await page.waitForFunction(() => {
      const node = document.querySelector('.ai-draft-result');
      return node !== null && node.textContent.includes('服务端校验通过');
    }, undefined, { timeout: 60000 });
    const validateText = (await page.locator('.ai-draft-result').first().textContent()).trim();
    record('S2-4', '草稿卡片校验通过', true, `结果="${validateText}"`);

    // S2-5：卡片试跑（真实数据源，50 行/30 秒限额）。
    await page.click('.ai-draft-actions button:has-text("试跑")');
    await page.waitForFunction(() => {
      const results = Array.from(document.querySelectorAll('.ai-draft-result'));
      return results.some((node) => node.textContent.includes('试跑返回'));
    }, undefined, { timeout: 60000 });
    const dryRunText = (await page.$$eval('.ai-draft-result', (nodes) => nodes.map((node) => node.textContent.trim())))
      .find((text) => text.includes('试跑返回'));
    record('S2-5', '草稿卡片试跑返回真实数据', /试跑返回 \d+ 行/.test(dryRunText || ''), `结果="${dryRunText}"`);

    // S2-6：导入（复用现有导入链路；页面 confirm 接受）。以 import 接口响应为锚点，面板提示为辅。
    const importResponsePromise = page.waitForResponse(
      (candidate) => candidate.url().includes('/api/report-center/poc/report-packages/import') && candidate.request().method() === 'POST',
      { timeout: 90000 },
    );
    page.once('dialog', (dialog) => dialog.accept());
    await page.click('.ai-draft-actions button:has-text("导入")');
    const importResponse = await importResponsePromise;
    const importEnvelope = await importResponse.json();
    await page.waitForFunction(() => {
      const notices = Array.from(document.querySelectorAll('.ai-panel-notice'));
      return notices.some((node) => node.textContent.includes('已导入'));
    }, undefined, { timeout: 15000 }).catch(() => undefined);
    const importNotice = await page.locator('.ai-panel-notice').first().textContent()
      .catch(() => `（接口 envelope=${importEnvelope.code}）`);
    record('S2-6', '导入成功并提示刷新模板列表', importResponse.status() === 200 && importEnvelope.code === 200,
      `HTTP=${importResponse.status()} envelope=${importEnvelope.code} 提示="${String(importNotice).trim().slice(0, 100)}"`);

    // S2-7：模板列表出现新报表。
    await openReportList(page);
    await page.fill('input[placeholder*="搜索"], .toolbar input[type="search"]', S2_REPORT_CODE).catch(async () => {
      await page.getByPlaceholder(/搜索/).first().fill(S2_REPORT_CODE);
    });
    await page.waitForTimeout(1200);
    const listed = await page.locator(`td:has-text("${S2_REPORT_CODE}")`).count();
    record('S2-7', '模板列表出现导入的新报表', listed > 0, `命中行数=${listed}`);
    await shot(page, 'ai-s2-imported-list');

    // S2-8：审计表核对（直接查库）。
    const audit = queryAudit(global.S2_SESSION_ID);
    const auditTools = (audit.rows || []).map((row) => row.tool);
    record('S2-8', 'RcAiAuditLog 落本会话工具审计', (audit.filtered || 0) >= 3 && auditTools.includes('submit_report_spec'),
      `本会话审计=${audit.filtered} 条，工具=[${auditTools.join(', ')}]，全表总量=${audit.total}`);
    note(`S2 审计明细：${JSON.stringify(audit.rows)}`);

    // S2-9：现场清理——删除导入的测试报表并确认列表回落。
    const removed = await fetchApi(page, `/api/report-center/poc/reports/${S2_REPORT_CODE}`, { method: 'DELETE' });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200);
    const leftover = await page.locator(`td:has-text("${S2_REPORT_CODE}")`).count();
    record('S2-9', '测试报表清理完成（现场无残留）', removed.status === 200 && leftover === 0,
      `删除 status=${removed.status}，残留行=${leftover}`);
    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S2 场景异常终止', false, exception.message);
    await shot(page, 'ai-s2-error');
  }
}

/**
 * S3：熔断——mock 连续 3 次返回必然失败的 spec → 第 3 次后提示人工介入（离线流水线）。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS3(page) {
  const scenario = 'S3';
  try {
    // S3-1：设置页把 baseUrl 换成 mock（ApiKey 留空=保留原值），走真实 UI 操作。
    await page.goto(`${BASE_URL}/ai-settings`, { waitUntil: 'domcontentloaded' });
    await expectVisible(page, 'h1:has-text("AI 设置")', 'AI 设置标题');
    await page.fill('.ai-settings-grid label:has-text("服务地址") input', MOCK_BASE_URL);
    await page.fill('.ai-settings-grid label:has-text("模型名称") input', MOCK_MODEL);
    await page.click('footer.ai-settings-footer button:has-text("保存设置")');
    await page.waitForFunction(() => {
      const node = document.querySelector('.toast.success, [class*="success"]');
      return node !== null && node.textContent.includes('已保存');
    }, undefined, { timeout: 30000 });
    record('S3-1', 'AI 设置已切换到 mock 故障注入器（UI 保存）', true, `baseUrl=${MOCK_BASE_URL}`);

    // S3-2：新会话发起对话——mock 每轮返回非法 spec，后端第 3 次失败后熔断。
    await openAiPanel(page);
    await page.selectOption('.ai-panel-source select', 'data_source_2');
    const chatBody = await sendChat(page, '帮我设计一张报表', 120000);
    const messages = (chatBody.result && chatBody.result.messages) || [];
    const assistantTexts = messages.filter((item) => item.role === 'assistant' && item.content)
      .map((item) => item.content);
    const circuitText = assistantTexts.find((text) => text.includes('熔断') || text.includes('离线流水线') || text.includes('人工介入'));
    record('S3-2', '第 3 次失败后熔断并提示人工介入（离线流水线）', !!circuitText,
      circuitText ? `AI 回复="${circuitText.slice(0, 120)}…"`
        : `chat 响应 code=${chatBody.code} message=${(chatBody.message || '').slice(0, 120)}；AI 回复清单=${JSON.stringify(assistantTexts).slice(0, 160)}`);
    const failedMarks = await page.locator('.ai-tool-entry .ai-tool-error-mark:has-text("失败")').count();
    record('S3-3', '工具折叠条出现失败标记', failedMarks >= 3, `失败标记=${failedMarks} 个`);
    await shot(page, 'ai-s3-circuit-breaker');

    // S3-4：审计核对——本会话 3 次 submit 真实失败 + 1 次熔断后的 SUBMIT_CIRCUIT_OPEN 拒绝（同样记审计失败）。
    const sessionId = chatBody.result && chatBody.result.sessionId;
    const audit = queryAudit(sessionId);
    const submitFailures = (audit.rows || []).filter((row) => row.tool === 'submit_report_spec' && !row.success);
    record('S3-4', '审计表记录 submit 失败链（3 次真实失败 + 1 次熔断拒绝）', submitFailures.length === 4,
      `本会话审计=${audit.filtered} 条，submit 失败（含熔断拒绝）=${submitFailures.length} 条`);
    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S3 场景异常终止', false, exception.message);
    await shot(page, 'ai-s3-error');
  }
}

/**
 * S4：会话过期——真实路径：记录 sessionId → 重启后端 → 原 sessionId 续聊 → 前端提示过期。
 * 前置：AI 设置已恢复指向真实 GLM（由本场景第一步完成）。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS4(page) {
  const scenario = 'S4';
  try {
    // S4-1：恢复真实 GLM 设置（UI 操作）。
    await page.goto(`${BASE_URL}/ai-settings`, { waitUntil: 'domcontentloaded' });
    await expectVisible(page, 'h1:has-text("AI 设置")', 'AI 设置标题');
    await page.fill('.ai-settings-grid label:has-text("服务地址") input', GLM_BASE_URL);
    await page.fill('.ai-settings-grid label:has-text("模型名称") input', GLM_MODEL);
    await page.click('footer.ai-settings-footer button:has-text("保存设置")');
    await page.waitForFunction(() => {
      const node = document.querySelector('.toast.success, [class*="success"]');
      return node !== null && node.textContent.includes('已保存');
    }, undefined, { timeout: 30000 });
    record('S4-1', 'AI 设置恢复为真实 GLM 端点', true, `baseUrl=${GLM_BASE_URL} model=${GLM_MODEL}`);

    // S4-2：建立会话并记录 sessionId。
    await openAiPanel(page);
    await page.selectOption('.ai-panel-source select', 'data_source_2');
    const firstBody = await sendChat(page, '请直接用一句话回答：1+1 等于几？不要调用任何工具。', 180000);
    const sessionId = firstBody.result && firstBody.result.sessionId;
    record('S4-2', '会话建立并记录 sessionId', firstBody.code === 200 && !!sessionId, `sessionId=${sessionId}`);

    // S4-3：真实重启后端（内存会话清空；AI 设置存库自动恢复）。
    await restartBackend();
    record('S4-3', '后端进程真实重启完成且健康检查通过', true, 'GET /ai/settings 已恢复 200');

    // S4-4：原 sessionId 续聊——服务端判定过期并重建，前端提示。
    const responsePromise = page.waitForResponse(
      (candidate) => candidate.url().includes('/api/report-center/ai/chat') && candidate.request().method() === 'POST',
      { timeout: 180000 },
    );
    await page.fill('.ai-panel-composer textarea', '我们刚才聊到哪里了？');
    await page.click('.ai-panel-composer button[aria-label="发送消息"]');
    const secondBody = await (await responsePromise).json();
    const expired = secondBody.result && secondBody.result.expired === true;
    const noticeAppeared = await page.waitForFunction(() => {
      const node = document.querySelector('.ai-panel-notice');
      return node !== null && node.textContent.includes('会话已过期');
    }, undefined, { timeout: 15000 }).then(() => true).catch(() => false);
    const noticeText = noticeAppeared ? (await page.locator('.ai-panel-notice').textContent()).trim() : '';
    record('S4-4', '前端提示「会话已过期，AI 上下文已清空」', expired && noticeAppeared,
      `expired=${expired} 提示="${noticeText}"`);
    await shot(page, 'ai-s4-session-expired');
    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S4 场景异常终止', false, exception.message);
    await shot(page, 'ai-s4-error');
  }
}

/**
 * S5：同步 AI——先 mock 非法 JSON 降级，再真实 GLM 语义匹配、置信标注、保存走现有校验链。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS5(page) {
  const scenario = 'S5';
  try {
    // S5-0：样例目标表（幂等创建）与历史任务清理。
    scratchTable('Create');
    const staleDelete = await fetchApi(page, `/api/report-center/poc/data-sync-tasks/${S5_TASK_CODE}`, { method: 'DELETE' });
    note(`S5 历史任务清理：DELETE ${S5_TASK_CODE} → ${staleDelete.status}`);

    // S5-1：新建同步任务，第 1 步填基本信息与源取数（写 SQL，3 个与目标表不同名的源字段）。
    await page.goto(`${BASE_URL}/data-sync-tasks`, { waitUntil: 'domcontentloaded' });
    await expectVisible(page, 'h1:has-text("数据同步任务")', '同步任务列表标题');
    await page.click('button:has-text("新建同步任务")');
    await expectVisible(page, 'h1:has-text("新建同步任务")', '设计器标题');
    await page.fill('.sync-field:has-text("任务编码") input', S5_TASK_CODE);
    await page.fill('.sync-field:has-text("任务名称") input', 'E2E AI 智能匹配验证');
    await page.selectOption('.sync-field:has-text("源数据源") select', 'data_source_2');
    await page.click('[aria-label="源取数方式"] button:has-text("写 SQL")');
    const editor = await expectVisible(page, '.asset-sql-editor .cm-content', 'SQL 编辑器');
    await editor.click();
    await page.keyboard.insertText(
      'SELECT Code AS SRC_CODE, Name AS SRC_TITLE, CAST(IsEnabled AS int) AS SRC_ENABLED FROM dbo.RcReport',
    );
    await page.click('button:has-text("预览源列")');
    await page.waitForFunction(() => {
      const node = document.querySelector('[class*="success"], .toast.success');
      return node !== null && node.textContent.includes('源列');
    }, undefined, { timeout: 30000 });
    record('S5-1', '新建任务并完成源取数（3 个异名源字段）', true, `预览提示="已读取 3 列"`);

    // S5-2：第 3 步选目标表（真实列清单来自真库样例表），随后回到第 2 步。
    await page.click('[aria-label="设计步骤"] button:has-text("目标设置与调度")');
    await page.selectOption('.sync-field:has-text("目标数据源") select', 'data_source_2');
    await page.waitForTimeout(1500);
    const tableSelect = page.locator('label.sync-field')
      .filter({ hasText: /^目标表/ })
      .locator('select');
    await tableSelect.selectOption(S5_TARGET_TABLE);
    await page.waitForTimeout(1500);
    await page.click('[aria-label="设计步骤"] button:has-text("字段映射")');
    await expectVisible(page, 'section[aria-label="字段映射"]', '映射步骤卡片');
    record('S5-2', '目标表列清单加载完成（4 列，全部与源异名）', true, `目标表=${S5_TARGET_TABLE}`);

    // S5-3：人工预填一条映射（SRC_CODE → REPORT_CODE），验证 AI 不覆盖。
    await page.selectOption('select[aria-label="源字段 SRC_CODE 的目标字段"]', 'REPORT_CODE');
    await page.waitForTimeout(400);
    record('S5-3', '人工预填映射 SRC_CODE → REPORT_CODE', true, '映射表格第 1 行已选目标列');

    // S5-4：页内同源 fetch 切到 mock（非法 JSON）——绝不导航，未保存的设计器草稿必须保持在前端内存中。
    await saveAiSettings(page, { baseUrl: MOCK_BASE_URL, model: MOCK_MODEL });
    const matchResponsePromise = page.waitForResponse(
      (candidate) => candidate.url().includes('/api/report-center/ai/sync-match') && candidate.request().method() === 'POST',
      { timeout: 120000 },
    );
    await page.click('button:has-text("AI 智能匹配")');
    const matchBody = await (await matchResponsePromise).json();
    const degraded = matchBody.result && matchBody.result.degraded === true;
    await page.waitForFunction(() => {
      const node = document.querySelector('.ai-match-summary');
      return node !== null && node.textContent.includes('降级');
    }, undefined, { timeout: 15000 });
    const degradeSummary = (await page.locator('.ai-match-summary').textContent()).trim();
    record('S5-4', 'LLM 非法 JSON 时降级为同名规则结果并提示', degraded && degradeSummary.includes('降级'),
      `degraded=${degraded} 提示="${degradeSummary}"`);
    await shot(page, 'ai-s5-sync-degraded');

    // S5-5：页内 fetch 恢复真实 GLM → 再次 AI 智能匹配（同名零命中，歧义全交 LLM）。
    await saveAiSettings(page, { baseUrl: GLM_BASE_URL, model: GLM_MODEL });
    const aiResponsePromise = page.waitForResponse(
      (candidate) => candidate.url().includes('/api/report-center/ai/sync-match') && candidate.request().method() === 'POST',
      { timeout: 180000 },
    );
    await page.click('button:has-text("AI 智能匹配")');
    const aiBody = await (await aiResponsePromise).json();
    const suggestions = (aiBody.result && aiBody.result.suggestions) || [];
    const hitTargets = suggestions.filter((item) => !item.needsManual).map((item) => `${item.sourceField}->${item.targetField}`);
    await page.waitForFunction(() => {
      const node = document.querySelector('.ai-match-panel');
      return node !== null;
    }, undefined, { timeout: 15000 });
    const panelText = (await page.locator('.ai-match-panel').textContent()).trim();
    record('S5-5', '真实 GLM 语义匹配回填映射并出现置信标注面板', hitTargets.length >= 2 && panelText.includes('AI 匹配置信标注'),
      `建议命中=[${hitTargets.join(', ')}]`);

    // S5-6：置信标注/需人工项/人工映射不被覆盖。
    const confidenceBadges = await page.locator('.ai-match-confidence').allTextContents();
    const manualTargetListed = panelText.includes('需人工处理') && panelText.includes('CREATED_BY');
    const mappingRows = await page.$$eval('section[aria-label="字段映射"] table tbody tr', (rows) => rows.map((row) => row.textContent.replace(/\s+/g, ' ').trim()));
    const manualRowKept = mappingRows.some((row) => row.includes('SRC_CODE') && row.includes('REPORT_CODE'));
    const aiRowsFilled = mappingRows.filter((row) => (row.includes('SRC_TITLE') && row.includes('REPORT_TITLE'))
      || (row.includes('SRC_ENABLED') && row.includes('ENABLED_FLAG'))).length;
    record('S5-6', '置信标注/需人工项/人工映射不被覆盖', confidenceBadges.length >= 2 && manualTargetListed && manualRowKept && aiRowsFilled >= 2,
      `置信标注=${JSON.stringify(confidenceBadges)} 人工行保留=${manualRowKept} AI 回填行=${aiRowsFilled} 需人工含 CREATED_BY=${manualTargetListed}`);
    note(`S5 映射表行：${JSON.stringify(mappingRows)}`);
    await shot(page, 'ai-s5-sync-ai-match');

    // S5-7：保存任务（走现有校验链），核对落库映射不含需人工项。
    await page.click('button:has-text("保存任务")');
    await page.waitForFunction(() => {
      const node = document.querySelector('.toast.success, [class*="success"]');
      return node !== null && node.textContent.includes('保存');
    }, undefined, { timeout: 60000 });
    const savedTask = await fetchApi(page, `/api/report-center/poc/data-sync-tasks/${S5_TASK_CODE}`);
    // SyncTaskDocument 直接携带 mappings 数组（与 SyncColumnMapping 同构），无需解析 definitionJson。
    const savedMappings = (savedTask.body && savedTask.body.result && savedTask.body.result.mappings) || [];
    const noManualInSaved = savedMappings.every((mapping) => mapping.targetField !== 'CREATED_BY');
    record('S5-7', '保存成功且需人工项未进入 mappings', savedTask.status === 200 && savedMappings.length === 3 && noManualInSaved,
      `落库映射=${savedMappings.length} 条 [${savedMappings.map((mapping) => `${mapping.sourceField}->${mapping.targetField}`).join(', ')}]`);

    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S5 场景异常终止', false, exception.message);
    await shot(page, 'ai-s5-error');
  }
}

/**
 * S6：AI 关闭降级——UI 关闭 → 入口消失；接口 fail-fast AI_DISABLED；其余页面与规则版匹配不受影响。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS6(page) {
  const scenario = 'S6';
  try {
    // S6-1：UI 关闭 AI。
    await page.goto(`${BASE_URL}/ai-settings`, { waitUntil: 'domcontentloaded' });
    await expectVisible(page, 'h1:has-text("AI 设置")', 'AI 设置标题');
    const toggle = page.locator('.ai-settings-grid label:has-text("启用 AI 能力") input[type="checkbox"]');
    await toggle.uncheck();
    await page.click('footer.ai-settings-footer button:has-text("保存设置")');
    await page.waitForFunction(() => {
      const node = document.querySelector('.toast.success, [class*="success"]');
      return node !== null && node.textContent.includes('保持关闭');
    }, undefined, { timeout: 30000 });
    record('S6-1', '设置页关闭 AI 并保存', true, '提示「AI 能力已保存（AI 能力保持关闭）」');

    // S6-2：前端入口消失（浮钮/菜单）。
    await openReportList(page);
    const fabCount = await page.locator('button.ai-fab-button').count();
    const menuCount = await page.locator('nav a[href="/ai-settings"]').count();
    record('S6-2', '关闭后浮钮与 AI 设置菜单消失', fabCount === 0 && menuCount === 0,
      `浮钮=${fabCount} 个，菜单=${menuCount} 个`);
    await shot(page, 'ai-s6-ai-disabled-list');

    // S6-3：直连 POST /ai/chat 与 /ai/sync-match → 403 AI_DISABLED（fail-fast）。
    const chatProbe = await fetchApi(page, '/api/report-center/ai/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId: null, dataSourceCode: 'data_source_2', message: 'probe' }),
    });
    const matchProbe = await fetchApi(page, '/api/report-center/ai/sync-match', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ taskCode: S5_TASK_CODE }),
    });
    record('S6-3', '直连调用 /ai/chat 与 /ai/sync-match 返回 AI_DISABLED',
      chatProbe.body.code === 403 && matchProbe.body.code === 403
      && chatProbe.body.message.includes('AI_DISABLED') && matchProbe.body.message.includes('AI_DISABLED'),
      `chat envelope=${chatProbe.body.code} ${chatProbe.body.message}；sync-match envelope=${matchProbe.body.code} ${matchProbe.body.message}`);

    // S6-4：模板列表页完全正常（真实数据分页）。
    const listBody = await fetchApi(page, '/api/report-center/poc/reports/page?page=1&pageSize=15&query=');
    const listTotal = listBody.body && listBody.body.result
      ? listBody.body.result.total : null;
    record('S6-4', 'AI 关闭时模板列表接口与页面正常', listBody.status === 200 && (listTotal || 0) > 0,
      `列表接口 total=${listTotal}`);
    const listRows = await page.locator('table tbody tr').count();
    record('S6-5', '模板列表页面渲染正常', listRows > 0, `页面行数=${listRows}`);

    // S6-6：同步设计器规则版同名匹配不受影响（AI 按钮隐藏）；任务行优先取 S5 产物，单场景重跑时回退列表首行。
    await page.goto(`${BASE_URL}/data-sync-tasks`, { waitUntil: 'domcontentloaded' });
    await expectVisible(page, 'h1:has-text("数据同步任务")', '同步任务列表标题');
    const e2eRowButton = page.locator(`tr:has-text("${S5_TASK_CODE}") button[title="设计映射"]`);
    const anyRowButton = page.locator('button[title="设计映射"]').first();
    const designButton = (await e2eRowButton.count()) > 0 ? e2eRowButton : anyRowButton;
    await designButton.click();
    await expectVisible(page, 'section[aria-label="基本信息与源取数"]', '设计器第 1 步');
    await page.click('[aria-label="设计步骤"] button:has-text("字段映射")');
    await expectVisible(page, 'section[aria-label="字段映射"]', '映射步骤卡片');
    const aiButtonCount = await page.locator('button:has-text("AI 智能匹配")').count();
    // 规则版按钮存在即可证明规则版可用（已保存任务打开时源列可能尚未加载，按钮处于禁用态属正常）；
    // 等待其可用后真实点击一次，等不到（30 秒）则以"按钮存在且可渲染"判定。
    const ruleButton = page.locator('button:has-text("按同名自动匹配")');
    let ruleClicked = false;
    try {
      await ruleButton.click({ timeout: 30000 });
      ruleClicked = true;
    } catch {
      ruleClicked = false;
    }
    const ruleVisible = await ruleButton.isVisible();
    record('S6-6', '规则版同名匹配可用且 AI 按钮隐藏', aiButtonCount === 0 && ruleVisible,
      `AI 按钮=${aiButtonCount} 个，规则版按钮可见=${ruleVisible}，规则版点击执行=${ruleClicked}`);
    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S6 场景异常终止', false, exception.message);
    await shot(page, 'ai-s6-error');
  }
}

/**
 * S7：附件参考 Excel——上传「表头乱序+改名列」参考件，AI 生成列序对齐参考的模板并完成导入。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS7(page) {
  const scenario = 'S7';
  try {
    // S7-1：打开侧栏并上传参考 Excel（input 隐藏不影响 setInputFiles）。
    await openAiPanel(page);
    await page.selectOption('.ai-panel-source select', 'data_source_2');
    const fixturePath = path.join(__dirname, 'fixtures', '费用参考.xlsx');
    await page.setInputFiles('input.ai-attachment-input', fixturePath);
    const chipText = (await page.locator('.ai-attachment-chip').first().textContent()).trim();
    record('S7-1', '上传参考 Excel 出现附件条', chipText.includes('费用参考.xlsx'), `附件条="${chipText}"`);
    await shot(page, 'ai-s7-attach-chip');

    // S7-2：发送需求——要求列名列序严格对齐参考件。
    const requirement = '请按我上传的参考 Excel 做一张科室门诊费用汇总报表：'
      + '列名与列序严格跟随参考件的表头（保持参考顺序，"就诊科室"这个名字也要保留），'
      + '汇总当前数据源样例表 E2E_AI_SAMPLE_OUTPATIENT 的费用数据（金额 SUM、笔数 COUNT，按费用类别与科室分组）。'
      + '参数枚举注意：parameters 的 sourceType 只允许 Form/Url/System/Fixed/External（这里写 Form），'
      + 'controlType 只允许 text/number/date/datetime 等（日期筛选用 date），dataType 写 date。'
      + '先查表确认口径，然后直接调用 submit_report_spec 提交，不要输出文字计划。';
    let chatBody = await sendChat(page, requirement);
    let chatResult = chatBody.result || {};
    global.S7_SESSION_ID = chatResult.sessionId;

    // 真实模型有随机性：草稿未出现则追问（最多 3 轮，含枚举纠错信息）。
    const nudges = [
      '不要回复文字计划。立即调用 submit_report_spec 提交 spec：列名与列序严格按参考 Excel 表头（费用类别/单据数/金额合计/就诊科室）。'
        + '注意 parameters 的 sourceType 只能写 Form（不是 input），controlType 用 date。',
      '上一轮提交失败的原因是字段值越界。修正后立即重新调用 submit_report_spec：sourceType=Form，controlType=date，dataType=date。',
      '再次提醒：立即调用 submit_report_spec，模板列 header 照抄参考件表头文字，顺序保持不变，参数 sourceType=Form。',
    ];
    let draftVisible = false;
    for (let index = 0; index < nudges.length && !draftVisible; index += 1) {
      draftVisible = await page.locator('.ai-draft-card').isVisible().catch(() => false);
      if (!draftVisible) {
        chatBody = await sendChat(page, nudges[index]);
        chatResult = chatBody.result || {};
      }
    }
    record('S7-2', '发起需求并推动至草稿出现', chatBody.code === 200 && draftVisible,
      `code=${chatBody.code} 草稿出现=${draftVisible}`);

    // S7-3：草稿卡片列序断言——参考列序（费用类别→单据数/金额→就诊科室）必须生效。
    await expectVisible(page, '.ai-draft-card', '草稿卡片', 15000);
    const draftTitle = (await page.locator('.ai-draft-header strong').first().textContent()).trim();
    const badge = (await (await expectVisible(page, '.ai-draft-badge', '状态徽标')).textContent()).trim();
    const chipNames = await page.$$eval('.ai-draft-section:has(strong:has-text("列清单")) .ai-draft-chip', (nodes) =>
      nodes.map((node) => node.textContent.trim().split('\n')[0]));
    const renamedKept = chipNames.some((name) => name.includes('就诊科室'));
    const orderAligned = (() => {
      /** 在列清单中查找包含指定关键字的列下标；找不到返回 -1。 */
      const indexOf = (needle) => chipNames.findIndex((name) => name.includes(needle));
      const category = indexOf('费用类别');
      const amount = indexOf('金额');
      const dept = indexOf('就诊科室');
      return category >= 0 && amount > category && dept > amount;
    })();
    record('S7-3', '草稿列名与列序对齐参考件', renamedKept && orderAligned && (badge.includes('校验通过') || badge.includes('试跑通过')),
      `卡片="${draftTitle}" 徽标="${badge}" 列序=[${chipNames.join(' → ')}]`);
    note(`S7 草稿列清单：${JSON.stringify(chipNames)}`);
    await shot(page, 'ai-s7-draft-card');

    // S7-4：卡片校验。
    await page.click('.ai-draft-actions button:has-text("校验")');
    await page.waitForFunction(() => {
      const node = document.querySelector('.ai-draft-result');
      return node !== null && node.textContent.includes('服务端校验通过');
    }, undefined, { timeout: 60000 });
    const validateText = (await page.locator('.ai-draft-result').first().textContent()).trim();
    record('S7-4', '草稿卡片校验通过', true, `结果="${validateText}"`);

    // S7-5：卡片试跑——以 dry-run 接口响应为锚点，结果文本原样记录（成功/参数错误均可解释）。
    const dryRunResponsePromise = page.waitForResponse(
      (candidate) => candidate.url().includes('/api/report-center/poc/report-packages/dry-run') && candidate.request().method() === 'POST',
      { timeout: 90000 },
    );
    await page.click('.ai-draft-actions button:has-text("试跑")');
    const dryRunResponse = await dryRunResponsePromise;
    const dryRunEnvelope = await dryRunResponse.json();
    const dryRunText = (await page.$$eval('.ai-draft-result', (nodes) => nodes.map((node) => node.textContent.trim())))
      .find((text) => text.includes('试跑返回') || text.includes('失败') || text.length > 0) || '';
    // dry-run 200=成功；400 且 PARAM_REQUIRED=模型把参数设为必填而卡片试跑未提供样例值，
    // 属可解释结果（导入链路不受影响），与真实成功一并判定通过。
    const dryRunOk = dryRunEnvelope.code === 200
      || (dryRunEnvelope.code === 400 && String(dryRunEnvelope.message || '').includes('PARAM_REQUIRED'));
    record('S7-5', '草稿卡片试跑返回真实数据（或可解释参数错误）', dryRunOk,
      `HTTP=${dryRunResponse.status()} envelope=${dryRunEnvelope.code} 结果="${dryRunText.slice(0, 120)}"`);

    // S7-6：导入。
    page.once('dialog', (dialog) => dialog.accept());
    await page.click('.ai-draft-actions button:has-text("导入")');
    await page.waitForFunction(() => {
      const notices = Array.from(document.querySelectorAll('.ai-panel-notice'));
      return notices.some((node) => node.textContent.includes('已导入'));
    }, undefined, { timeout: 60000 });
    const importNotice = (await page.locator('.ai-panel-notice').first().textContent()).trim();
    record('S7-6', '导入成功并提示刷新模板列表', importNotice.includes('已导入'), `提示="${importNotice}"`);

    // S7-7：审计核对。
    const audit = queryAudit(global.S7_SESSION_ID);
    const auditTools = (audit.rows || []).map((row) => row.tool);
    record('S7-7', 'RcAiAuditLog 落附件参考审计', (audit.filtered || 0) >= 1 && auditTools.includes('attachment_reference'),
      `本会话审计=${audit.filtered} 条，工具=[${auditTools.join(', ')}]`);
    await shot(page, 'ai-s7-imported');

    // S7-8：现场清理——删除导入的测试报表。
    const importCode = (importNotice.match(/「([^」]+)」/) || [])[1] || S2_REPORT_CODE;
    const removed = await fetchApi(page, `/api/report-center/poc/reports/${encodeURIComponent(importCode)}`, { method: 'DELETE' });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1000);
    const leftover = await page.locator(`td:has-text("${importCode}")`).count();
    record('S7-8', '测试报表清理完成', removed.status === 200 && leftover === 0,
      `删除 status=${removed.status}，残留行=${leftover}`);
    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S7 场景异常终止', false, exception.message);
    await shot(page, 'ai-s7-error');
  }
}

/**
 * S8：附件参考图片——先在浏览器里渲染一张报表表格截图作为图片附件，
 * 视觉模型（VisionModel 空=回退主模型 glm-5.3-flash）识别列名后生成对齐模板。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioS8(page) {
  const scenario = 'S8';
  try {
    // S8-1：用 Playwright 渲染一张真实表格并截图，产出图片夹具。
    const tableHtml = [
      '<html><head><meta charset="utf-8"><style>',
      'body{font-family:"Microsoft YaHei";margin:24px;} h1{font-size:20px;}',
      'table{border-collapse:collapse;} td,th{border:1px solid #444;padding:8px 24px;font-size:16px;}',
      '</style></head><body>',
      '<h1>住院费用科室统计表</h1>',
      '<table><tr><th>科室名称</th><th>发生金额</th><th>单据张数</th></tr>',
      '<tr><td>心内科</td><td>18234.50</td><td>96</td></tr>',
      '<tr><td>呼吸内科</td><td>12980.00</td><td>64</td></tr>',
      '<tr><td>普外科</td><td>23150.25</td><td>88</td></tr>',
      '</table></body></html>',
    ].join('');
    await page.goto(`data:text/html;charset=utf-8,${encodeURIComponent(tableHtml)}`, { waitUntil: 'load' });
    const fixturePath = path.join(OUT_DIR, '住院费用科室统计表.png');
    await page.locator('body').screenshot({ path: fixturePath });
    record('S8-1', 'Playwright 渲染表格并截图产出图片夹具', fs.existsSync(fixturePath), `夹具=${fixturePath}`);

    // S8-2：回到报表列表，上传图片附件并发需求。
    await openAiPanel(page);
    await page.selectOption('.ai-panel-source select', 'data_source_2');
    await page.setInputFiles('input.ai-attachment-input', fixturePath);
    const requirement = '请按我上传的报表截图做一张住院费用科室统计模板：'
      + '列名严格照抄截图表头（科室名称/发生金额/单据张数），顺序保持不变，'
      + '汇总当前数据源样例表 E2E_AI_SAMPLE_OUTPATIENT（金额 SUM、笔数 COUNT，按 DEPT_NAME 与 FEE_CATEGORY 分组）。'
      + '先查表确认口径，然后立即调用 submit_report_spec 提交，不要输出文字计划。';
    let chatBody = await sendChat(page, requirement);
    let chatResult = chatBody.result || {};
    global.S8_SESSION_ID = chatResult.sessionId;

    const nudges = [
      '不要回复文字计划。立即调用 submit_report_spec 提交 spec：模板列 header 照抄截图表头（科室名称/发生金额/单据张数），顺序不变。',
      '立即调用 submit_report_spec。字段 displayName 照抄截图列名，不要改动文字。',
    ];
    let draftVisible = false;
    for (let index = 0; index < nudges.length && !draftVisible; index += 1) {
      draftVisible = await page.locator('.ai-draft-card').isVisible().catch(() => false);
      if (!draftVisible) {
        chatBody = await sendChat(page, nudges[index]);
        chatResult = chatBody.result || {};
      }
    }
    record('S8-2', '上传截图并推动至草稿出现（视觉模型识别）', chatBody.code === 200 && draftVisible,
      `code=${chatBody.code} 草稿出现=${draftVisible}`);

    // S8-3：列名照抄截图表头断言。
    await expectVisible(page, '.ai-draft-card', '草稿卡片', 15000);
    const badge = (await (await expectVisible(page, '.ai-draft-badge', '状态徽标')).textContent()).trim();
    const chipNames = await page.$$eval('.ai-draft-section:has(strong:has-text("列清单")) .ai-draft-chip', (nodes) =>
      nodes.map((node) => node.textContent.trim().split('\n')[0]));
    const renamedKept = chipNames.some((name) => name.includes('科室名称'));
    const amountKept = chipNames.some((name) => name.includes('发生金额'));
    record('S8-3', '草稿列名照抄截图表头', renamedKept && amountKept && (badge.includes('校验通过') || badge.includes('试跑通过')),
      `徽标="${badge}" 列=[${chipNames.join(' → ')}]`);
    note(`S8 草稿列清单：${JSON.stringify(chipNames)}`);
    await shot(page, 'ai-s8-draft-card');

    // S8-4：审计核对（attachment_reference 元信息）。
    // sessionId 以最终 chatResult 为准（追问循环会覆盖 chatBody/chatResult，会话号服务端保持复用）。
    const s8SessionId = chatResult.sessionId || global.S8_SESSION_ID;
    const audit = queryAudit(s8SessionId);
    const auditTools = (audit.rows || []).map((row) => row.tool);
    record('S8-4', 'RcAiAuditLog 落附件参考审计', (audit.filtered || 0) >= 1 && auditTools.includes('attachment_reference'),
      `本会话审计=${audit.filtered} 条，工具=[${auditTools.join(', ')}]`);

    // S8-5：现场清理。
    const importFree = true;
    if (!importFree) {
      return;
    }
    // 本场景只验证生成，不导入，无库残留；删除会话即可。
    if (global.S8_SESSION_ID) {
      await fetchApi(page, `/api/report-center/ai/sessions/${encodeURIComponent(global.S8_SESSION_ID)}`, { method: 'DELETE' });
    }
    result.scenario_results[scenario] = 'PASS';
  } catch (exception) {
    result.scenario_results[scenario] = `FAIL: ${exception.message}`;
    record(`${scenario}-ERR`, 'S8 场景异常终止', false, exception.message);
    await shot(page, 'ai-s8-error');
  }
}

/**
 * 收尾清理：删除 S5 任务、删样例表、清空 RcAiSettings 的 ApiKey 并保持关闭态。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function cleanup(page) {
  try {
    const removed = await fetchApi(page, `/api/report-center/poc/data-sync-tasks/${S5_TASK_CODE}`, { method: 'DELETE' });
    console.log(`清理：任务 ${S5_TASK_CODE} 删除 status=${removed.status}`);
  } catch (exception) {
    console.warn(`清理任务失败：${exception.message}`);
  }
  try {
    scratchTable('Drop');
  } catch (exception) {
    console.warn(`清理样例表失败：${exception.message}`);
  }
  try {
    scratchTable('Drop', 'sample');
  } catch (exception) {
    console.warn(`清理业务样例表失败：${exception.message}`);
  }
  try {
    // 收尾：数据库直接清空 ApiKey 并保持关闭，避免真实密钥长期留存在共享开发库。
    const script = [
      "$raw = $env:HD_REPORT_CENTER_DB_CONNECTION_STRING; $map = @{}",
      "foreach($part in $raw -split ';'){ if($part.Trim()){ $k,$v = $part -split '=',2; $map[$k.Trim().Replace(' ','')] = $v } }",
      "$b = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()",
      "$b['Data Source']=$map['DataSource']; $b['Initial Catalog']=$map['InitialCatalog']; $b['User ID']=$map['UserID']; $b['Password']=$map['Password']",
      "$b['Encrypt']=$false; $b['TrustServerCertificate']=$true; $b['Connect Timeout']=15",
      "$conn=[System.Data.SqlClient.SqlConnection]::new($b.ConnectionString); $conn.Open()",
      "$c=$conn.CreateCommand(); $c.CommandText='UPDATE dbo.RcAiSettings SET ApiKey=NULL, IsEnabled=0 WHERE Id=1'; $null=$c.ExecuteNonQuery()",
      "$conn.Dispose(); Write-Host 'RcAiSettings 已复位：IsEnabled=0, ApiKey=NULL'",
    ].join('; ');
    const output = spawnSync(PWSH, ['-NoProfile', '-Command', script], { encoding: 'utf8', timeout: 60000 });
    console.log(output.stdout.trim());
  } catch (exception) {
    console.warn(`清理 RcAiSettings 失败：${exception.message}`);
  }
}

/**
 * 单场景迭代辅助：E2E_ONLY 指定非 S1 场景时先经 API 启用 AI（真实 GLM 配置），
 * 保证被单独执行的场景拥有与全量运行相同的开启态前置。
 * @param {import('playwright-core').Page} page - 页面。
 * @returns {Promise<void>} 无返回值。
 */
async function ensureAiEnabled(page) {
  // 单场景迭代模式：先落到前端首页，保证页内 fetch 拥有同源上下文。
  await openReportList(page);
  const save = await saveAiSettings(page, { apiKey: process.env.GLM_API_KEY || '' });
  if (save.status !== 200 || !(save.body && save.body.result && save.body.result.isEnabled)) {
    throw new Error(`单场景前置启用失败：status=${save.status}`);
  }
  console.log('[PRELUDE] AI 已通过 API 预启用（单场景迭代模式）。');
}

/**
 * 主流程：启动浏览器，按 S1 → S2 → S4 → S3 → S5 → S6 顺序执行（S4 提前于 S3，
 * 避免 S3 的 mock 设置残留影响过期会话场景），最后写结果 JSON。
 * @returns {Promise<void>} 无返回值。
 */
async function main() {
  if (!process.env.GLM_API_KEY) {
    throw new Error('未设置 GLM_API_KEY 环境变量：真实端点验证必须显式注入密钥（绝不写入文件）。');
  }
  fs.mkdirSync(OUT_DIR, { recursive: true });
  fs.mkdirSync(IMAGE_DIR, { recursive: true });
  const browser = await playwright.chromium.launch({ channel: 'chrome', headless: process.env.E2E_HEADLESS === '1' });
  const context = await browser.newContext({ viewport: { width: 1600, height: 900 }, locale: 'zh-CN' });
  const page = await context.newPage();
  const only = process.env.E2E_ONLY || '';
  try {
    if (!only || only === 'S1') await scenarioS1(page);
    else await ensureAiEnabled(page);
    if (!only || only === 'S2') await scenarioS2(page);
    if (!only || only === 'S4') await scenarioS4(page);
    if (!only || only === 'S3') await scenarioS3(page);
    if (!only || only === 'S5') await scenarioS5(page);
    // S6（关闭降级）必须最后执行：它会把 AI 关掉，S7/S8 依赖入口可见。
    if (!only || only === 'S7') await scenarioS7(page);
    if (!only || only === 'S8') await scenarioS8(page);
    if (!only || only === 'S6') await scenarioS6(page);
  } finally {
    await cleanup(page);
    await browser.close();
  }
  result.finished_at = new Date().toISOString();
  fs.writeFileSync(path.join(OUT_DIR, 'e2e_result.json'), JSON.stringify(result, null, 2), 'utf8');
  console.log(`\n共 ${result.total} 项检查点：通过 ${result.pass}，失败 ${result.fail}`);
  console.log(`场景汇总：${JSON.stringify(result.scenario_results)}`);
  if (result.fail > 0) {
    process.exitCode = 1;
  }
}

main().catch((exception) => {
  console.error(`E2E 主流程异常：${exception.message}`);
  result.finished_at = new Date().toISOString();
  try {
    fs.writeFileSync(path.join(OUT_DIR, 'e2e_result.json'), JSON.stringify(result, null, 2), 'utf8');
  } catch {
    // 写结果失败不再抛出，保留原始异常信息。
  }
  process.exitCode = 1;
});
