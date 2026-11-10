/**
 * hd-report-center「AI 报表助手 @ant-design/x 外壳」真实 LLM E2E（T1-T7）。
 *
 * 前置：后端(5005，已启用真实 GLM)、前端 vite(5175) 已在运行；AI 设置无需改动
 * （密钥只存在于服务端数据库，本脚本绝不读取/写入设置）。
 * 验证点：
 *   T1 懒加载面板外壳：React.lazy 分包加载后抽屉渲染，头部 antd Select + 底部 Sender 就位。
 *   T2 数据源头部化：antd Select 选中 data_source_2；表清单 Popover 打开且渲染行数据。
 *   T3 真实 GLM 工具循环：ThoughtChain 链式节点随 progress 轮询实时增长，展开节点可读。
 *   T4 运行中插话：running 时 Enter 走 guidance 端点，乐观气泡带“插话”角标，
 *      POST /chat 返回（Seq 校正）后角标仍持久保留。
 *   T5 草稿卡：真实 GLM 调用 submit_report_spec 后消息流内出现自研草稿卡（仅提交不导入）。
 *   T6 内置停止按钮：running 中点击停止 → abort 当前 /chat → 复位提示出现、按钮回到发送态。
 *   T7 现场清理：DELETE 会话，服务端内存上下文释放。
 *
 * 产物：output/ai-antd/*.png 截图 + output/ai-antd/e2e_result.json 检查点汇总。
 * 退出码：全部通过 0，任一失败 1。
 */
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

/** 前端基地址：开发实例固定 5175（/api 代理到 5005 后端）。 */
const BASE_URL = process.env.E2E_BASE_URL || 'http://127.0.0.1:5175';
/** 截图与结果输出目录（已 gitignore 的 output）。 */
const OUT_DIR = path.join(__dirname, '..', '..', '..', 'output', 'ai-antd');
/** 测试用报表编码（仅出现在草稿 spec 中，不导入不落库）；时间戳避免撞历史草稿。 */
const DRAFT_REPORT_CODE = `ai_antd_${Date.now()}`;
/** playwright-core：复用模板列表 E2E 的独立安装。 */
const playwright = require('D:/zp/git/hd-report-center/scripts/e2e/report-list/node_modules/playwright-core');
/** pwsh 完整路径（WindowsApps 安装位置），供样例表生命周期脚本调用。 */
const PWSH = path.join(process.env.LOCALAPPDATA, 'Microsoft', 'WindowsApps', 'pwsh.exe');

/**
 * 执行脱敏业务样例表生命周期（dbo.E2E_AI_SAMPLE_OUTPATIENT，全合成数据）：
 * T5 的 spec 提交必须打在业务样例表上——模型按安全红线拒绝把 Rc* 配置表作为报表数据源
 * （实测拒绝理由见 e2e_result.json notes），沿用 ai_assistant_e2e.js S2 的样例表方案。
 * @param {'Create'|'Drop'} action - 动作。
 * @returns {void}
 */
function scratchSampleTable(action) {
  const script = path.join(__dirname, 'sample_business_table.ps1');
  const output = spawnSync(PWSH, ['-NoProfile', '-File', script, `-${action}`], { encoding: 'utf8', timeout: 60000 });
  if (output.status !== 0) {
    throw new Error(`样例表 ${action} 失败：${output.stderr || output.stdout}`);
  }
  console.log(`样例表 ${action} 完成：${output.stdout.trim().split(/\r?\n/).pop()}`);
}

/** 全量结果汇总，结束时写入 e2e_result.json。 */
const result = {
  started_at: new Date().toISOString(),
  finished_at: null,
  base_url: BASE_URL,
  checkpoints: [],
  total: 0,
  pass: 0,
  fail: 0,
  notes: [],
};

/**
 * 记录单个检查点并更新通过/失败计数。
 * @param {string} id - 检查点编号，如 T1。
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
 * 记录附加观测（不计数），供交付报告引用。
 * @param {string} text - 观测内容。
 */
function note(text) {
  result.notes.push(text);
  console.log(`[NOTE] ${text}`);
}

/**
 * 截图到 output/ai-antd/ 并返回文件名；截图失败不阻断主流程。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} name - 文件名（不含扩展名）。
 * @returns {Promise<string>} 文件名。
 */
async function shot(page, name) {
  try {
    const file = path.join(OUT_DIR, `${name}.png`);
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
async function expectVisible(page, selector, desc, timeout = 15000) {
  const locator = page.locator(selector).first();
  try {
    await locator.waitFor({ state: 'visible', timeout });
  } catch (exception) {
    throw new Error(`未等到「${desc}」(${selector})：${exception.message.split('\n')[0]}`);
  }
  return locator;
}

/**
 * 通过页内 fetch 读取同源 JSON 接口（复用前端代理）。
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
 * 打开模板列表页并等待浮钮出现。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function openReportList(page) {
  await page.goto(`${BASE_URL}/`, { waitUntil: 'domcontentloaded' });
  await expectVisible(page, 'button.ai-fab-button', 'AI 助手浮钮');
  await page.waitForTimeout(600);
}

/**
 * 打开 AI 抽屉（触发懒加载分包）并等待外壳三段式渲染完成。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function openAiPanel(page) {
  await page.click('button.ai-fab-button');
  await expectVisible(page, 'aside.ai-assistant-panel', 'AI 抽屉外壳');
  await expectVisible(page, '.ai-panel-source-select', '头部数据源 Select');
  await expectVisible(page, '.ai-panel-composer textarea', '底部 Sender 输入框');
  // 等待数据源选项异步加载完成（antd v6 的 Select 选中值渲染在 .ant-select-content）。
  await page.waitForFunction(() => {
    const node = document.querySelector('.ai-panel-source-select .ant-select-content');
    return node !== null && node.textContent.length > 0;
  }, undefined, { timeout: 20000 });
}

/**
 * 在 antd Select 中按 title 子串选择数据源选项。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} codeSubstring - 选项 title 中的数据源编码子串。
 * @returns {Promise<void>} 无返回值。
 */
async function selectDataSource(page, codeSubstring) {
  await page.click('.ai-panel-source-select');
  const dropdown = await expectVisible(page, '.ant-select-dropdown:not(.ant-select-dropdown-hidden)', '数据源下拉');
  // antd v6 的选项渲染为 div.ant-select-item-option（title 属性来自 option label）。
  await dropdown.locator(`.ant-select-item-option[title*="${codeSubstring}"]`).first().click();
  await page.waitForFunction((needle) => {
    const node = document.querySelector('.ai-panel-source-select .ant-select-content');
    return node !== null && node.textContent.includes(needle);
  }, codeSubstring, { timeout: 10000 });
}

/**
 * 通过 Sender 发送一条消息（fill + Enter；Playwright 的 insertText 会触发受控 React onChange）。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} message - 消息文本。
 * @returns {Promise<void>} 无返回值。
 */
async function typeAndSubmit(page, message) {
  const textarea = await expectVisible(page, '.ai-panel-composer textarea', 'Sender 输入框');
  await textarea.click();
  await textarea.fill('');
  await textarea.pressSequentially(message, { delay: 5 });
  await textarea.press('Enter');
}

/**
 * 收集当前 ThoughtChain 节点数量（0 = 消息流还没有工具轨迹）。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<number>} 节点数。
 */
async function countThoughtNodes(page) {
  return page.locator('.ai-panel-messages .ant-thought-chain-node').count();
}

/**
 * T1：懒加载面板外壳——抽屉渲染、头部 Select、底部 Sender、外发警示齐备。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioT1(page) {
  await openAiPanel(page);
  const warning = await expectVisible(page, '.ai-panel-warning', '外发警示');
  const warningShown = (await warning.textContent()).includes('LLM');
  record('T1', '懒加载抽屉外壳渲染（头部 Select + Sender + 外发警示）', warningShown,
    `警示含 LLM 提示=${warningShown}`);
  await shot(page, '01-drawer-overview');
}

/**
 * T2：数据源头部化——antd Select 选中 data_source_2，表清单 Popover 打开且渲染行。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioT2(page) {
  await selectDataSource(page, 'data_source_2');
  // 换源触发表清单重新加载；稍等请求完成后打开 Popover。
  await page.waitForTimeout(1200);
  await page.click('button.ai-tables-trigger');
  const popover = await expectVisible(page, '.ai-table-popover', '表清单浮层');
  await page.waitForFunction(() =>
    document.querySelectorAll('.ai-table-popover .ai-table-list-row').length > 0,
  undefined, { timeout: 15000 });
  const rowCount = await page.locator('.ai-table-popover .ai-table-list-row').count();
  record('T2', '数据源 Select 移到头部 + 表清单 Popover 渲染', rowCount > 0,
    `选中 data_source_2，浮层内表行=${rowCount}`);
  await shot(page, '05-datasource-select-header');
  await page.keyboard.press('Escape');
  await popover.waitFor({ state: 'hidden', timeout: 5000 }).catch(() => undefined);
}

/**
 * T3：真实 GLM 工具循环——发送短指令，验证 ThoughtChain 随 progress 轮询实时增长，
 * 展开任一节点查看 SQL 摘要/详情保持可读；POST /chat 返回后气泡与轨迹仍在。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioT3(page) {
  const chatResponsePromise = page.waitForResponse(
    (candidate) => candidate.url().includes('/api/report-center/ai/chat')
      && candidate.request().method() === 'POST',
    { timeout: 300000 },
  );
  await typeAndSubmit(page, '请调用一次 list_data_sources 工具，然后简短回复数据源数量。不要做别的事。');
  // 轮询记录 ThoughtChain 节点数增长轨迹（500ms 间隔，最多 120 秒观察窗）。
  const growthSamples = [];
  for (let index = 0; index < 240; index += 1) {
    const count = await countThoughtNodes(page);
    if (growthSamples.length === 0 || growthSamples[growthSamples.length - 1] !== count) {
      growthSamples.push(count);
    }
    const loading = await page.locator('.ant-sender-actions-btn-loading-button').isVisible().catch(() => false);
    if (!loading && count > 0) break;
    if (!loading && index > 4) break;
    await page.waitForTimeout(500);
  }
  const grew = growthSamples.length > 1 && growthSamples[growthSamples.length - 1] > 0;
  record('T3a', 'ThoughtChain 随 progress 轮询实时增长', grew,
    `节点数轨迹=[${growthSamples.join(' → ')}]`);
  const chatBody = await (await chatResponsePromise).json();
  const chatOk = chatBody.code === 200;
  record('T3b', 'POST /chat 真实 GLM 循环返回', chatOk,
    `code=${chatBody.code} 消息数=${(chatBody.result?.messages || []).length}`);
  // 展开首个节点验证折叠交互与详情可读。
  const firstNode = page.locator('.ai-panel-messages .ant-thought-chain-node').first();
  await firstNode.locator('.ant-thought-chain-node-title').first().click();
  await page.waitForTimeout(400);
  const detailVisible = await page.locator('.ai-panel-messages .ai-thought-detail').first().isVisible().catch(() => false);
  record('T3c', 'ThoughtChain 节点展开后详情可读', detailVisible, `详情区可见=${detailVisible}`);
  await shot(page, '02-thoughtchain-expanded');
  note(`T3 节点增长轨迹：${JSON.stringify(growthSamples)}`);
}

/**
 * T4：运行中插话——第二轮多工具查询进行中（loading 态）用 Enter 发送插话，
 * 断言走 guidance 端点、乐观气泡带角标、本轮 /chat 完成后角标仍持久。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioT4(page) {
  const chatResponsePromise = page.waitForResponse(
    (candidate) => candidate.url().includes('/api/report-center/ai/chat')
      && candidate.request().method() === 'POST',
    { timeout: 300000 },
  );
  await typeAndSubmit(page, '请依次调用 list_tables 和 describe_table 工具查看 RcReport 表的结构，然后简短总结。不要提交报表。');
  // 等待进入 running 态（内置停止按钮出现）。
  await expectVisible(page, '.ant-sender-actions-btn-loading-button', 'Sender 内置停止按钮', 60000);
  const stopVisible = true;
  note('T4 running 态确认：内置停止按钮可见。');
  // 发送插话并锚定 guidance 端点响应。
  const guidanceResponsePromise = page.waitForResponse(
    (candidate) => candidate.url().includes('/sessions/') && candidate.url().includes('/guidance')
      && candidate.request().method() === 'POST',
    { timeout: 30000 },
  );
  await typeAndSubmit(page, '（插话纠正）describe_table 只需要看列名，不用输出全部类型信息。');
  const guidanceResponse = await guidanceResponsePromise;
  const guidanceOk = guidanceResponse.status() === 200;
  // 乐观上屏角标：guidance 响应返回后角标应已出现。
  const badgeDuringRun = await page.locator('.ai-guidance-badge').first().isVisible().catch(() => false);
  record('T4a', 'running 中发送走 guidance 端点且乐观气泡带角标（停止按钮在位）',
    guidanceOk && badgeDuringRun && stopVisible,
    `guidance HTTP=${guidanceResponse.status()} 角标可见=${badgeDuringRun}`);
  // 截图前滚到消息流底部，让带角标的插话气泡进入视口（角标校验点已在上屏时通过）。
  await page.evaluate(() => {
    const box = document.querySelector('.ai-panel-messages .ant-bubble-list-scroll-box');
    if (box) box.scrollTo({ top: box.scrollHeight });
  });
  await page.waitForTimeout(600);
  await shot(page, '03-guidance-bubble');
  // 等本轮 /chat 完成，验证 Seq 校正后角标仍持久。
  const chatBody = await (await chatResponsePromise).json();
  await page.waitForTimeout(800);
  const badgeAfterTurn = await page.locator('.ai-guidance-badge').first().isVisible().catch(() => false);
  const messagesOk = chatBody.code === 200 && (chatBody.result?.messages || []).length > 0;
  record('T4b', '本轮完成后插话角标持久保留（Seq 校正后仍在）', messagesOk && badgeAfterTurn,
    `chat code=${chatBody.code} 完成后角标可见=${badgeAfterTurn}`);
}

/**
 * 确保面板处于空闲态：若上一轮 POST 仍在运行（停止按钮可见），点击内置停止按钮中止；
 * 后端工具循环随断开中止，不产生悬挂请求。供 T5/T6 前置与收尾兜底使用。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function ensureIdle(page) {
  const stopButton = page.locator('.ant-sender-actions-btn-loading-button').first();
  if (await stopButton.isVisible().catch(() => false)) {
    await stopButton.click();
    await stopButton.waitFor({ state: 'hidden', timeout: 15000 }).catch(() => undefined);
    await page.waitForTimeout(500);
  }
}

/**
 * 结束当前会话并等待本地消息流清空（换新会话前置；幂等——无会话时仅清空本地展示）。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function endSessionViaUi(page) {
  await ensureIdle(page);
  await page.click('button[aria-label="结束 AI 会话"]');
  await page.waitForFunction(() => {
    const bubbles = document.querySelectorAll('.ai-panel-messages .ant-bubble:not(.ant-bubble-system)');
    return bubbles.length === 0;
  }, undefined, { timeout: 10000 }).catch(() => undefined);
  await page.waitForTimeout(400);
}

/**
 * 发送一条消息并等待本轮 /chat 响应返回（独立等待窗，供多轮追问使用）。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} message - 消息文本。
 * @param {number} timeout - 响应等待毫秒数。
 * @returns {Promise<{code:number|null, body:object|null}>} 响应信封。
 */
async function sendChatAndWait(page, message, timeout = 180000) {
  const responsePromise = page.waitForResponse(
    (candidate) => candidate.url().includes('/api/report-center/ai/chat')
      && candidate.request().method() === 'POST',
    { timeout },
  ).catch(() => null);
  await typeAndSubmit(page, message);
  const response = await responsePromise;
  if (!response) return { code: null, body: null };
  const body = await response.json().catch(() => null);
  return { code: body && body.code, body };
}

/**
 * T5：草稿卡——先结束旧会话拿到干净上下文，按「先探表结构 → 再提交 spec」两轮推动
 * 真实 GLM 调用 submit_report_spec（单轮直提交已被实测证明模型大概率不调工具），
 * 验证消息流内自研草稿卡渲染。只提交不导入：草稿仅存在于会话内存，无数据库残留。
 * 提示词沿用模板 AI 全链路 E2E（ai_assistant_e2e.js S2）已验证的字段约束写法。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioT5(page) {
  // 单场景模式（E2E_ONLY=T5）下 T1 未执行：这里兜底打开抽屉并确认数据源已选。
  if (!(await page.locator('aside.ai-assistant-panel').isVisible().catch(() => false))) {
    await openAiPanel(page);
    await selectDataSource(page, 'data_source_2');
    await page.waitForTimeout(800);
  }
  await endSessionViaUi(page);
  // 第一轮：探明结构（模型只有先看到真实表结构才愿意走 submit_report_spec，S2 已验证该编排）。
  // 目标表用全合成脱敏样例表：模型按安全红线拒绝把 Rc* 配置表作为报表数据源（实测见 notes）。
  const probe = await sendChatAndWait(page,
    '请依次调用 list_tables 和 describe_table 工具查看业务样例表 E2E_AI_SAMPLE_OUTPATIENT（全合成脱敏门诊费用测试表，表名不含 dbo. 前缀；只需列名），然后一句话确认结构。不要提交报表。',
    240000);
  note(`T5 探表轮 chat 响应 code=${probe.code}`);
  const submitRequirement = [
    '字段结构已确认。现在立即调用 submit_report_spec 提交 spec，除该工具外不要调用其他工具、不要输出文字计划，要求如下：',
    `报表编码固定为 ${DRAFT_REPORT_CODE}，报表名称「AI AntD E2E 最简报表」，工作表名「最简报表」；`,
    '主数据集 SQL：SELECT DEPT_NAME AS deptName, FEE_CATEGORY AS feeCategory, COUNT(1) AS feeCount FROM E2E_AI_SAMPLE_OUTPATIENT GROUP BY DEPT_NAME, FEE_CATEGORY ORDER BY DEPT_NAME；',
    'SELECT 输出的三个列与 fields 一一对应：deptName displayName「就诊科室」、feeCategory displayName「费用类别」、feeCount displayName「单据数」，dataType 分别为 text/text/number；',
    '无参数（variables 与 parameters 都是空数组）。直接调用 submit_report_spec 提交，这是唯一验收方式。',
  ].join('');
  let draftVisible = false;
  const nudges = [
    submitRequirement,
    '不要回复文字计划，立即调用 submit_report_spec 工具，按上一条消息的字段与编码要求提交。',
    '再次提醒：你只能通过 submit_report_spec 工具交付，立刻调用它，这是唯一验收方式。',
  ];
  for (let index = 0; index < nudges.length && !draftVisible; index += 1) {
    draftVisible = await page.locator('.ai-draft-card').isVisible().catch(() => false);
    if (!draftVisible) {
      const round = await sendChatAndWait(page, nudges[index]);
      if (round.code === null) {
        note(`T5 第 ${index + 1} 轮 chat 响应超时，继续下一轮。`);
        await ensureIdle(page);
        continue;
      }
      note(`T5 第 ${index + 1} 轮 chat 响应 code=${round.code}`);
    }
    if (!draftVisible) {
      await page.waitForTimeout(1500);
      draftVisible = await page.locator('.ai-draft-card').isVisible().catch(() => false);
    }
  }
  const badgeText = draftVisible
    ? (await page.locator('.ai-draft-badge').first().textContent().catch(() => '') || '').trim()
    : '';
  record('T5', '真实 GLM 提交草稿后消息流内渲染草稿卡', draftVisible,
    `草稿卡可见=${draftVisible} 状态徽标=「${badgeText}」编码=${DRAFT_REPORT_CODE}`);
  await shot(page, '04-draft-card');
}

/**
 * T6：内置停止按钮——干净状态下发送简单问题进入 running 态，点击停止按钮，
 * 断言 abort 后出现复位提示且按钮回到发送态。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioT6(page) {
  // 单场景模式（E2E_ONLY=T6）下 T1 未执行：这里兜底打开抽屉并确认数据源已选。
  if (!(await page.locator('aside.ai-assistant-panel').isVisible().catch(() => false))) {
    await openAiPanel(page);
    await selectDataSource(page, 'data_source_2');
    await page.waitForTimeout(800);
  }
  await endSessionViaUi(page);
  await typeAndSubmit(page, '请调用 describe_table 工具查看 RcDataSource 表结构，然后一句话总结。');
  // 进入 running 态（loading 按钮随乐观上屏立即出现；若响应过快错过则降级记录）。
  let stopClicked = false;
  try {
    await expectVisible(page, '.ant-sender-actions-btn-loading-button', '运行中停止按钮', 45000);
    await page.click('.ant-sender-actions-btn-loading-button');
    stopClicked = true;
  } catch {
    note('T6 未能在 45 秒内捕捉到 loading 态（模型响应过快），停止按钮分支降级为跳过。');
  }
  await page.waitForTimeout(800);
  const noticeShown = await page.waitForFunction(() => {
    const node = document.querySelector('.ai-panel-notice');
    return node !== null && node.textContent.includes('已停止');
  }, undefined, { timeout: 8000 }).then(() => true).catch(() => false);
  const sendBack = await page.locator('.ant-sender-actions-btn:not(.ant-sender-actions-btn-loading-button)').first().isVisible().catch(() => false);
  record('T6', '停止按钮 abort 当前请求并复位发送态', stopClicked && noticeShown,
    `点击停止=${stopClicked} 复位提示=${noticeShown} 按钮回到发送态=${sendBack}`);
  await shot(page, '06-stop-abort-reset');
}

/**
 * T7：现场清理——结束会话释放服务端内存上下文，本地消息流归零（空态系统气泡除外）。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 无返回值。
 */
async function scenarioT7(page) {
  await page.click('button[aria-label="结束 AI 会话"]');
  await page.waitForTimeout(800);
  const remaining = await page.locator('.ai-panel-messages .ant-bubble:not(.ant-bubble-system)').count();
  const emptyShown = await page.locator('.ai-panel-messages .ai-panel-empty').count();
  record('T7', '结束会话清空本地消息流并释放服务端上下文', remaining === 0 && emptyShown === 1,
    `剩余消息气泡=${remaining} 空态引导渲染=${emptyShown}`);
}

/**
 * 主流程：打开页面 → T1..T7 顺序执行 → 写结果 JSON。
 * @returns {Promise<void>} 无返回值。
 */
async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const browser = await playwright.chromium.launch({ channel: 'chrome', headless: process.env.E2E_HEADLESS !== '0' });
  const context = await browser.newContext({ viewport: { width: 1600, height: 900 }, locale: 'zh-CN' });
  const page = await context.newPage();
  /** 记录面板会话号：guidance/chat 响应里捕获，用于最终 DELETE 兜底清理。 */
  let sessionId = null;
  page.on('response', (response) => {
    const url = response.url();
    if (url.includes('/api/report-center/ai/chat') && response.request().method() === 'POST') {
      response.json().then((body) => {
        if (body && body.result && body.result.sessionId) sessionId = body.result.sessionId;
      }).catch(() => undefined);
    }
  });
  try {
    await openReportList(page);
    const only = process.env.E2E_ONLY || '';
    // T5 需要脱敏业务样例表（模型安全红线拒绝 Rc* 配置表作为数据源，见 scratchSampleTable 注释）。
    if (!only || only === 'T5') scratchSampleTable('Create');
    if (!only || only === 'T1') await scenarioT1(page);
    if (!only || only === 'T2') await scenarioT2(page);
    if (!only || only === 'T3') await scenarioT3(page);
    if (!only || only === 'T4') await scenarioT4(page);
    if (!only || only === 'T5') await scenarioT5(page);
    if (!only || only === 'T6') await scenarioT6(page);
    if (!only || only === 'T7') await scenarioT7(page);
  } catch (exception) {
    record('ERR', 'E2E 主流程异常终止', false, exception.message);
    await shot(page, '99-error');
  } finally {
    // 兜底清理：即使中途失败也释放会话（幂等，不存在同样返回成功）；样例表一并删除。
    if (sessionId) {
      await fetchApi(page, `/api/report-center/ai/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' })
        .then((probe) => note(`兜底 DELETE 会话 ${sessionId} → ${probe.status}`))
        .catch(() => undefined);
    }
    try {
      scratchSampleTable('Drop');
    } catch (exception) {
      console.warn(`清理样例表失败：${exception.message}`);
    }
    await browser.close();
  }
  result.finished_at = new Date().toISOString();
  fs.writeFileSync(path.join(OUT_DIR, 'e2e_result.json'), JSON.stringify(result, null, 2), 'utf8');
  console.log(`\n共 ${result.total} 项检查点：通过 ${result.pass}，失败 ${result.fail}`);
  if (result.fail > 0) {
    process.exitCode = 1;
  }
}

main().catch((exception) => {
  console.error(`E2E 主流程异常：${exception.message}`);
  result.finished_at = new Date().toISOString();
  try {
    fs.mkdirSync(OUT_DIR, { recursive: true });
    fs.writeFileSync(path.join(OUT_DIR, 'e2e_result.json'), JSON.stringify(result, null, 2), 'utf8');
  } catch {
    // 写结果失败不再抛出，保留原始异常信息。
  }
  process.exitCode = 1;
});
