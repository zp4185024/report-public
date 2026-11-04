/**
 * hd-report-center 模板列表页全自动 E2E 测试主脚本。
 *
 * 职责：驱动系统 Chrome 以真实用户视角完整走通模板列表页（加载 → 搜索 → 分页 →
 * 新增模板 → 导出报表包 → 删除 → 现场恢复），每个检查点实时判定并截图留证；
 * 产物 output/e2e_result.json 与 output/shots/*.png 供 gen_report.js 生成图文报告。
 *
 * 等待策略：界面文案存在渲染时序，任何断言都以"本次操作触发的分页接口响应"为锚点，
 * 先按 query/page/pageSize 参数捕获响应拿到真实 total，再等 DOM 渲染出同一数值，
 * 避免读到上一轮请求遗留的陈旧文案（实测曾造成搜索/总数检查点假失败）。
 *
 * 运行前提：本地前端(默认 http://localhost:5175)与后端(5005/5006)已启动且数据库可达；
 * 退出码：全部检查点通过返回 0，任一失败返回 1，供 skill 判断是否继续后续流程。
 */
const { chromium } = require('playwright-core');
const fs = require('fs');
const path = require('path');

/** 前端基地址，可用环境变量 E2E_BASE_URL 覆盖，便于多实例验证。 */
const BASE_URL = process.env.E2E_BASE_URL || 'http://localhost:5175';
/** 是否无头运行；默认有头模式便于人工旁观，CI 或后台可设 E2E_HEADLESS=1。 */
const HEADLESS = process.env.E2E_HEADLESS === '1';
/** 本次测试使用的模板编码（时间戳保证可重复执行不撞历史数据）。 */
const TEST_CODE = `e2e_tpl_${Date.now()}`;
/** 测试模板的展示名称，删除断言与现场恢复检查都依赖它。 */
const TEST_NAME = 'E2E模板列表测试';
/** 截图与结果 JSON 的输出目录（已 gitignore，不入库）。 */
const OUT_DIR = path.join(__dirname, 'output');
/** 截图输出子目录。 */
const SHOT_DIR = path.join(OUT_DIR, 'shots');

/** 测试结果汇总对象，最后整体写入 e2e_result.json。 */
const result = {
  started_at: new Date().toISOString(),
  finished_at: null,
  base_url: BASE_URL,
  headless: HEADLESS,
  test_code: TEST_CODE,
  total: 0,
  pass: 0,
  fail: 0,
  checkpoints: [],
};

/**
 * 记录单个检查点的判定结果并同步更新通过/失败计数。
 * @param {string} id - 检查点编号，如 P1-1，报告按此排序。
 * @param {string} title - 检查点中文标题，说明验证的业务点。
 * @param {boolean} ok - 是否通过。
 * @param {string} detail - 通过时给出关键观测值，失败时给出原因。
 */
function record(id, title, ok, detail) {
  result.total += 1;
  if (ok) result.pass += 1;
  else result.fail += 1;
  result.checkpoints.push({ id, title, status: ok ? 'PASS' : 'FAIL', detail });
  const mark = ok ? '✅' : '❌';
  console.log(`${mark} [${id}] ${title} — ${detail}`);
}

/**
 * 截取当前页面截图并返回相对路径；截图失败不阻断测试主流程。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} name - 截图文件名（不含扩展名）。
 * @returns {Promise<string>} 相对脚本目录的截图路径，失败返回空串。
 */
async function shot(page, name) {
  try {
    const file = path.join(SHOT_DIR, `${name}.png`);
    await page.screenshot({ path: file, fullPage: false });
    return path.relative(__dirname, file);
  } catch (exception) {
    console.warn(`截图失败 ${name}: ${exception.message}`);
    return '';
  }
}

/**
 * 断言元素可见，不满足时抛出携带中文说明的异常。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {string} selector - Playwright 选择器。
 * @param {string} desc - 元素的业务描述，用于失败信息。
 * @param {number} timeout - 等待可见的超时毫秒数。
 * @returns {Promise<import('playwright-core').Locator>} 可见后的定位器。
 */
async function expectVisible(page, selector, desc, timeout = 8000) {
  const locator = page.locator(selector).first();
  try {
    await locator.waitFor({ state: 'visible', timeout });
  } catch (exception) {
    throw new Error(`未等到「${desc}」(${selector})：${exception.message.split('\n')[0]}`);
  }
  return locator;
}

/**
 * 读取文本内容并去除空白；元素不存在时返回空串而不是抛错。
 * @param {import('playwright-core').Locator} locator - 目标定位器。
 * @returns {Promise<string>} 元素文本。
 */
async function textOf(locator) {
  return (await locator.textContent().catch(() => ''))?.trim() ?? '';
}

/**
 * 捕获下一次符合条件的模板分页接口响应，返回其结果体。
 * 必须在触发界面操作之前调用，否则可能错过快速返回的响应。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {{query?: string, page?: number, pageSize?: number}} expect - 请求参数匹配条件。
 * @param {number} timeout - 等待响应的超时毫秒数。
 * @returns {Promise<{total: number, page: number, pageSize: number, items: Array<object>}>} 接口结果体。
 */
function waitForReportsResponse(page, expect, timeout = 15000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      page.off('response', handler);
      reject(new Error(`等待分页接口响应超时（${JSON.stringify(expect)}）`));
    }, timeout);
    /** 校验响应 URL 的查询参数是否与期望一致，命中即解析并解除监听。 */
    const handler = async (response) => {
      if (!response.url().includes('/api/report-center/poc/reports/page')) return;
      const params = new URL(response.url()).searchParams;
      // 空关键字时前端会整体省略 query 参数，缺失必须视同空串参与匹配。
      if (expect.query !== undefined && (params.get('query') ?? '') !== expect.query) return;
      if (expect.page !== undefined && params.get('page') !== String(expect.page)) return;
      if (expect.pageSize !== undefined && params.get('pageSize') !== String(expect.pageSize)) return;
      try {
        const body = await response.json();
        clearTimeout(timer);
        page.off('response', handler);
        resolve(body?.result ?? {});
      } catch {
        // 解析失败视为非目标响应，继续等待下一次。
      }
    };
    page.on('response', handler);
  });
}

/**
 * 在搜索框输入关键字并等待"该关键字触发的接口响应 + DOM 渲染一致"后再继续。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @param {import('playwright-core').Locator} search - 搜索框定位器。
 * @param {string} query - 要输入的搜索关键字。
 * @returns {Promise<{total: number, items: Array<object>}>} 本次搜索的接口结果体。
 */
async function fillSearchAndWait(page, search, query) {
  const payloadPromise = waitForReportsResponse(page, { query });
  await search.fill(query);
  const payload = await payloadPromise;
  const expectedMeta = `${query.trim() ? '匹配' : '共'} ${payload.total} 个模板`;
  await page.waitForFunction(
    (expected) => document.querySelector('.report-table-meta span')?.textContent?.trim() === expected,
    expectedMeta,
    { timeout: 8000 },
  );
  return payload;
}

/**
 * 启动前预检：前端可达、后端 features API 正常、模板分页 API 返回真实数据。
 * 数据库不可达或服务未启动时直接终止，避免后续检查点被环境问题污染。
 * @returns {Promise<{reportsTotal: number}>} 预检观测值，reportsTotal 为当前模板总数。
 */
async function preflight() {
  const featuresResponse = await fetch(`${BASE_URL}/api/report-center/features`);
  record('P0-1', '前端与后端网关可达', featuresResponse.ok, `GET /api/report-center/features → ${featuresResponse.status}`);
  const features = await featuresResponse.json();

  const pageResponse = await fetch(`${BASE_URL}/api/report-center/poc/reports/page?page=1&pageSize=5&query=`);
  const pageBody = await pageResponse.json();
  const reportsTotal = pageBody?.result?.total ?? 0;
  record(
    'P0-2',
    '模板分页 API 返回真实数据（数据库连通）',
    pageResponse.ok && reportsTotal > 0,
    `GET /poc/reports/page → ${pageResponse.status}，模板总数=${reportsTotal}`,
  );
  record('P0-3', '功能开关接口可用', features?.code === 200, `features.code=${features?.code}`);
  return { reportsTotal };
}

/**
 * P1 阶段：打开模板列表页，验证标题、表头、行数与分页汇总文案。
 * @param {import('playwright-core').Page} page - 浏览器页面。
 * @returns {Promise<{reportsTotal: number}>} 首屏接口观测的模板总数。
 */
async function phaseList(page) {
  // 阶段：挂上首屏响应等待后打开页面，等待表格渲染完成。
  const firstLoad = waitForReportsResponse(page, { page: 1 });
  await page.goto(`${BASE_URL}/reports`, { waitUntil: 'domcontentloaded' });
  const payload = await firstLoad;
  const heading = await expectVisible(page, 'main.report-list-page h1', '页面主标题');
  record('P1-1', '列表页打开且主标题正确', (await textOf(heading)) === '模板列表', `h1="${await textOf(heading)}"`);

  // 阶段：核对表头业务列，防止列被误删或换序（操作列为空表头，不参与比对）。
  const expectedHeaders = ['模板', '模板编码', '数据集', '状态', '配置版本', '模板版本', '更新时间'];
  const headerTexts = await page.locator('.report-table thead th').allTextContents();
  const missing = expectedHeaders.filter((name) => !headerTexts.includes(name));
  record('P1-2', '表头包含全部业务列', missing.length === 0, missing.length === 0 ? '7 个业务列齐全（另有操作列）' : `缺少列：${missing.join('、')}`);

  // 阶段：核对行数与接口 total、meta 文案三者一致。
  const rowCount = await page.locator('.report-table tbody tr').count();
  const metaText = await textOf(page.locator('.report-table-meta span').nth(1));
  const expectedPageRows = Math.min(payload.total, payload.pageSize);
  record('P1-3', '列表渲染真实模板数据', rowCount === expectedPageRows, `接口 total=${payload.total}，页面 ${rowCount} 行`);
  record('P1-4', '当前页计数与分页汇总一致', metaText.includes(`当前页 ${rowCount} 个`), `meta="${metaText}"`);
  await shot(page, 'P1_list');
  return { reportsTotal: payload.total };
}

/**
 * P2 阶段：验证搜索框过滤（无匹配提示、命中过滤、清空恢复）。
 * @param {import('playwright-core').Page} page - 浏览器页面。
 * @returns {Promise<void>} 检查点失败时抛出异常。
 */
async function phaseSearch(page) {
  const search = page.locator('input[aria-label="搜索模板"]');

  // 阶段：输入必然无命中的关键字，列表应显示空态文案。
  const missPayload = await fillSearchAndWait(page, search, 'zzz_no_such_template_zzz');
  const emptyCell = page.locator('.report-table-empty', { hasText: '没有匹配的模板' });
  record('P2-1', '无命中关键字显示空态提示', missPayload.total === 0 && await emptyCell.isVisible().catch(() => false), `接口 total=${missPayload.total}，空态文案已显示`);
  await shot(page, 'P2_search_empty');

  // 阶段：输入真实存在的编码关键字，断言命中行且计数切换为"匹配"口径。
  const hitPayload = await fillSearchAndWait(page, search, 'report_asset_inventory');
  const hitRow = page.locator('.report-table tbody tr', { hasText: 'report_asset_inventory' });
  const hitCount = await hitRow.count();
  record('P2-2', '有效关键字命中并切换匹配口径', hitPayload.total === 1 && hitCount === 1, `接口 total=${hitPayload.total}，命中 ${hitCount} 行`);
  await shot(page, 'P2_search_hit');

  // 阶段：清空关键字，列表恢复全量数据且行数与每页上限一致。
  const restorePayload = await fillSearchAndWait(page, search, '');
  const restoredRows = await page.locator('.report-table tbody tr').count();
  record('P2-3', '清空搜索恢复全量列表', restorePayload.total >= 1 && restoredRows === Math.min(restorePayload.total, 15), `接口 total=${restorePayload.total}，当前页 ${restoredRows} 行`);
}

/**
 * P3 阶段：验证分页条——每页条数切换与前后翻页的真实导航。
 * @param {import('playwright-core').Page} page - 浏览器页面。
 * @returns {Promise<void>} 检查点失败时抛出异常。
 */
async function phasePagination(page) {
  // 阶段：切换每页条数 15 → 30，接口与汇总文案应同步为 30 条口径。
  const sizeSelect = page.locator('select[aria-label="模板每页条数"]');
  const sizeResponse = waitForReportsResponse(page, { pageSize: 30 });
  await sizeSelect.selectOption('30');
  const sizePayload = await sizeResponse;
  const summary = await textOf(page.locator('.pagination-summary'));
  record('P3-1', '每页条数切换生效', sizePayload.pageSize === 30 && summary.includes(`1-30 / ${sizePayload.total}`), `接口 pageSize=${sizePayload.pageSize}，汇总="${summary}"`);

  // 阶段：数据多于 30 条时，第 1 页只有"下一页"可用，"上一页"应禁用。
  const prevDisabled = await page.locator('button[aria-label="上一页"]').isDisabled();
  const nextDisabled = await page.locator('button[aria-label="下一页"]').isDisabled();
  record('P3-2', '第 1 页翻页按钮状态正确', prevDisabled && !nextDisabled, `上一页禁用=${prevDisabled}，下一页禁用=${nextDisabled}`);

  // 阶段：点下一页真实翻页，接口应返回第 2 页数据且页码文案同步。
  const nextPageResponse = waitForReportsResponse(page, { page: 2 });
  await page.locator('button[aria-label="下一页"]').click();
  const nextPagePayload = await nextPageResponse;
  const pageText = await textOf(page.locator('.pagination-page'));
  record('P3-3', '下一页翻到第 2 页', nextPagePayload.page === 2 && pageText.includes('第 2 /'), `接口 page=${nextPagePayload.page}，文案="${pageText}"`);
  await shot(page, 'P3_pagination');

  // 阶段：翻回第 1 页并恢复默认每页条数，避免影响后续阶段。
  const backResponse = waitForReportsResponse(page, { page: 1 });
  await page.locator('button[aria-label="上一页"]').click();
  await backResponse;
  const restoreResponse = waitForReportsResponse(page, { pageSize: 15 });
  await sizeSelect.selectOption('15');
  await restoreResponse;
}

/**
 * P4 阶段：新增模板弹窗——数据集加载、编码校验负向、必填校验、合法创建跳转编辑器。
 * @param {import('playwright-core').Page} page - 浏览器页面。
 * @returns {Promise<void>} 检查点失败时抛出异常。
 */
async function phaseCreate(page) {
  // 阶段：P3 已恢复第 1 页且每页 15 条，直接打开弹窗并等待数据集选项加载。
  await page.locator('button.report-create-button').click();
  await expectVisible(page, '.create-report-dialog', '新增模板弹窗');
  const datasetSelect = page.locator('.create-report-grid label:has-text("数据集") select');
  await datasetSelect.waitFor({ state: 'visible', timeout: 8000 });
  await page.waitForFunction(() => {
    const select = document.querySelector('.create-report-grid select');
    return select instanceof HTMLSelectElement && select.options.length > 0;
  }, null, { timeout: 10000 });
  const datasetCount = await datasetSelect.locator('option').count();
  record('P4-1', '新增弹窗加载数据集选项', datasetCount > 0, `可选数据集 ${datasetCount} 个`);
  await shot(page, 'P4_dialog');

  const codeInput = page.locator('.create-report-grid label:has-text("模板编码") input');
  const nameInput = page.locator('.create-report-grid label:has-text("模板名称") input');
  const sheetInput = page.locator('.create-report-grid label:has-text("工作表名称") input');

  // 阶段：负向校验——非法编码被前端拦截并给出明确提示。
  await codeInput.fill('Bad_Code!');
  await page.locator('.create-report-dialog button[type="submit"]').click();
  const codeError = page.locator('.status-toast.error', { hasText: '模板编码必须以字母开头' });
  await codeError.waitFor({ state: 'visible', timeout: 5000 }).catch(() => {});
  record('P4-2', '非法编码被拦截并提示', await codeError.isVisible().catch(() => false), '提示="模板编码必须以字母开头…"');
  await page.locator('.status-toast.error .status-toast-close').click().catch(() => {});
  await shot(page, 'P4_invalid_code');

  // 阶段：负向校验——编码合法但名称为空时提示必填。
  await codeInput.fill(TEST_CODE);
  await nameInput.fill('');
  await sheetInput.fill('');
  await page.locator('.create-report-dialog button[type="submit"]').click();
  const requiredError = page.locator('.status-toast.error', { hasText: '模板名称和工作表名称不能为空' });
  await requiredError.waitFor({ state: 'visible', timeout: 5000 }).catch(() => {});
  record('P4-3', '名称/工作表为空被拦截', await requiredError.isVisible().catch(() => false), '提示="模板名称和工作表名称不能为空"');
  await page.locator('.status-toast.error .status-toast-close').click().catch(() => {});

  // 阶段：正向创建——合法输入提交后应跳转到模板编辑器。
  await nameInput.fill(TEST_NAME);
  await sheetInput.fill('E2E测试');
  await page.locator('.create-report-dialog button[type="submit"]').click();
  await page.waitForURL(`**/reports/${TEST_CODE}`, { timeout: 15000 });
  record('P4-4', '合法创建成功并跳转编辑器', page.url().endsWith(`/reports/${TEST_CODE}`), `URL=${page.url()}`);

  // 阶段：编辑器渲染——配置为异步加载，标题最终应从占位变为模板名称。
  await expectVisible(page, 'header[aria-label="模板命令栏"]', '模板编辑器命令栏', 15000);
  await page.waitForFunction(
    (name) => document.querySelector('header[aria-label="模板命令栏"] h1')?.textContent?.trim() === name,
    TEST_NAME,
    { timeout: 15000 },
  );
  const editorTitle = await textOf(page.locator('header[aria-label="模板命令栏"] h1'));
  record('P4-5', '编辑器正常渲染', editorTitle === TEST_NAME, `编辑器标题="${editorTitle}"`);
  await shot(page, 'P4_editor');
}

/**
 * P5 阶段：从模板列表导出一个已保存模板的报表包并断言下载触发。
 * @param {import('playwright-core').Page} page - 浏览器页面。
 * @returns {Promise<void>} 检查点失败时抛出异常。
 */
async function phaseExportPackage(page) {
  // 阶段：返回列表，等待首屏数据渲染完成。
  await page.locator('a[aria-label="返回模板列表"], a[href="/reports"]').first().click();
  await expectVisible(page, 'main.report-list-page h1', '返回后的列表页', 10000);
  await page.waitForFunction(() => {
    const rows = document.querySelectorAll('.report-table tbody tr');
    return rows.length > 0 && !rows[0].querySelector('.report-table-empty');
  }, null, { timeout: 10000 });

  // 阶段：遍历行找未禁用的导出按钮；禁用态说明模板从未保存，不能用于导出断言。
  const rows = page.locator('.report-table tbody tr');
  const rowCount = await rows.count();
  let exportTarget = null;
  let targetName = '';
  for (let index = 0; index < rowCount; index += 1) {
    const row = rows.nth(index);
    const exportButton = row.locator('button[title^="导出报表包"]');
    if (await exportButton.count() === 0) continue;
    if (!(await exportButton.isDisabled())) {
      exportTarget = exportButton;
      targetName = await textOf(row.locator('td').first().locator('strong'));
      break;
    }
  }
  if (exportTarget === null) {
    record('P5-1', '存在可导出的已保存模板', false, `当前页 ${rowCount} 行均无已保存模板`);
    return;
  }
  record('P5-1', '存在可导出的已保存模板', true, `目标模板="${targetName}"`);

  // 阶段：确认弹窗自动接受后应触发浏览器下载，文件名应为报表包 JSON。
  const downloadPromise = page.waitForEvent('download', { timeout: 20000 });
  await exportTarget.click();
  const download = await downloadPromise;
  const filename = download.suggestedFilename();
  record('P5-2', '导出报表包触发下载', filename.endsWith('.json'), `下载文件="${filename}"`);
  await shot(page, 'P5_export');
}

/**
 * P6 阶段：删除本次新建的测试模板，断言确认弹窗、成功提示、行移除与总数回落。
 * @param {import('playwright-core').Page} page - 浏览器页面。
 * @param {number} totalBefore - 删除前的模板总数（来自预检），删除后应恢复该值。
 * @returns {Promise<void>} 检查点失败时抛出异常。
 */
async function phaseDelete(page, totalBefore) {
  // 阶段：先挂删除后刷新的接口等待，再定位测试模板行并点击删除（confirm 自动接受）。
  const refreshPromise = waitForReportsResponse(page, { query: '' });
  const testRow = page.locator('.report-table tbody tr', { hasText: TEST_CODE });
  await testRow.waitFor({ state: 'visible', timeout: 10000 });
  await testRow.locator('button[aria-label^="删除模板"]').click();

  // 阶段：等待成功 toast 与行消失，并核对接口 total 恢复到测试前。
  const successToast = page.locator('.status-toast.success', { hasText: '已删除' });
  await successToast.waitFor({ state: 'visible', timeout: 10000 }).catch(() => {});
  record('P6-1', '删除弹出确认且成功提示', await successToast.isVisible().catch(() => false), '提示="模板「E2E模板列表测试」已删除"');
  await testRow.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  const rowGone = (await testRow.count()) === 0;
  record('P6-2', '删除后列表行移除', rowGone, `测试模板行已${rowGone ? '移除' : '仍在'}`);
  const refreshPayload = await refreshPromise;
  const totalText = await textOf(page.locator('.report-table-meta span').first());
  record('P6-3', '模板总数恢复到测试前', refreshPayload.total === totalBefore && totalText.includes(String(totalBefore)), `接口 total=${refreshPayload.total}，页面="${totalText}"`);
  await shot(page, 'P6_deleted');
}

/**
 * P7 阶段：现场恢复核验——按测试编码搜索确认无残留数据。
 * @param {import('playwright-core').Page} page - 浏览器页面。
 * @returns {Promise<void>} 检查点失败时抛出异常。
 */
async function phaseCleanupCheck(page) {
  const search = page.locator('input[aria-label="搜索模板"]');
  const missPayload = await fillSearchAndWait(page, search, TEST_CODE);
  const emptyCell = page.locator('.report-table-empty', { hasText: '没有匹配的模板' });
  record('P7-1', '测试数据无残留', missPayload.total === 0 && await emptyCell.isVisible().catch(() => false), `搜索 ${TEST_CODE} 接口 total=${missPayload.total}`);
  await fillSearchAndWait(page, search, '');
}

/**
 * 兜底清理：任一阶段失败后，若测试模板已创建则直接调后端删除接口移除。
 * @returns {Promise<boolean>} 清理是否成功（未创建过返回 true）。
 */
async function cleanupTestTemplate() {
  try {
    const response = await fetch(`${BASE_URL}/api/report-center/poc/reports/${encodeURIComponent(TEST_CODE)}`, { method: 'DELETE' });
    return response.ok || response.status === 404;
  } catch {
    return false;
  }
}

/**
 * 主流程：预检 → 打开浏览器 → 依序执行各阶段 → 失败兜底清理 → 落盘结果 JSON。
 * @returns {Promise<number>} 进程退出码：全绿 0，否则 1。
 */
async function main() {
  // 阶段：清空并重建截图目录，避免上一轮（尤其失败轮）的旧截图混入本次报告。
  fs.rmSync(SHOT_DIR, { recursive: true, force: true });
  fs.mkdirSync(SHOT_DIR, { recursive: true });
  let exitCode = 0;
  let created = false;
  let browser = null;
  try {
    const { reportsTotal } = await preflight();
    browser = await chromium.launch({ channel: 'chrome', headless: HEADLESS });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    // 全局接受 window.confirm（删除/导出确认），与真实用户点"确定"等价。
    page.on('dialog', (dialog) => dialog.accept().catch(() => {}));

    const listSummary = await phaseList(page);
    await phaseSearch(page);
    await phasePagination(page);
    await phaseCreate(page);
    created = true;
    await phaseExportPackage(page);
    await phaseDelete(page, listSummary.reportsTotal);
    await phaseCleanupCheck(page);
  } catch (exception) {
    exitCode = 1;
    record('F-0', '流程中断', false, exception instanceof Error ? exception.message : String(exception));
    if (browser) {
      const pages = browser.contexts()[0]?.pages();
      if (pages && pages.length > 0) await shot(pages[0], 'F_failure');
    }
  } finally {
    if (created) {
      // 阶段：兜底清理测试模板，避免失败中断在列表里留下脏数据。
      const cleaned = await cleanupTestTemplate();
      console.log(`兜底清理测试模板 ${TEST_CODE}: ${cleaned ? '完成' : '失败（需人工删除）'}`);
    }
    if (browser) await browser.close().catch(() => {});
  }

  result.finished_at = new Date().toISOString();
  fs.writeFileSync(path.join(OUT_DIR, 'e2e_result.json'), JSON.stringify(result, null, 2), 'utf8');
  console.log(`\n===== 模板列表 E2E 完成：${result.pass}/${result.total} 通过 =====`);
  return exitCode;
}

main().then((code) => process.exit(code)).catch((exception) => {
  console.error(exception);
  process.exit(1);
});
