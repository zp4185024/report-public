/**
 * README 界面截图生成脚本（演示数据脱敏出图）。
 *
 * 职责：临时通过 API 创建 3 个行业中性的演示模板（销售/考勤主题，不暗示医疗行业），
 * 给其中一个保存带完整示例数据的模板快照，使设计器截图呈现真实填充效果；
 * 抓取 模板列表 / 新增模板弹窗 / 模板设计器 三张截图写入 docs/images/ 供 README 使用；
 * 结束即删演示模板，不遗留任何脏数据。
 *
 * 防泄露断言：每张截图保存前都校验画面内只有 demo_ 前缀模板或已脱敏元素，
 * 任一校验不通过立即中断且不落盘，杜绝真实业务数据出现在公开仓库。
 */
const { chromium } = require('playwright-core');
const fs = require('fs');
const path = require('path');

/** 前端基地址，与 E2E 主脚本保持一致，可用环境变量覆盖。 */
const BASE_URL = process.env.E2E_BASE_URL || 'http://localhost:5175';
/** 截图输出目录（随仓库提交，README 相对引用 docs/images/）。 */
const IMG_DIR = path.join(__dirname, '..', '..', '..', 'docs', 'images');

/** 演示模板定义：编码必须 demo_ 前缀（防泄露断言依赖），名称行业中性与绑定数据集无关。 */
const DEMO_REPORTS = [
  { reportCode: 'demo_sales', name: '示例报表·销售订单统计', sheetName: '销售统计' },
  { reportCode: 'demo_region', name: '示例报表·区域销量月报', sheetName: '销量月报' },
  { reportCode: 'demo_attendance', name: '示例报表·月度考勤汇总', sheetName: '考勤汇总' },
];

/**
 * 在 Univer 设计器画布上用键盘录入销售月报示例数据。
 * 运行中的后端尚未包含模板保存接口（本地未提交的新路由），因此改走纯 UI 键入：
 * 只在首次进入时点一下 A1 获得焦点，之后全部用 输入+Enter/Tab/方向键 导航，
 * 行尾统一 Tab 到种子残留的 F 列 Delete 清空，再 ←×5 回到 A 列、↓ 到下一行，
 * 完全不依赖固定行高坐标（种子模板行高不均，坐标点击曾导致整表错行）。
 * @param {import('playwright-core').Page} page - 设计器页面。
 * @returns {Promise<void>} 录入完成后返回。
 */
async function typeDemoContent(page) {
  /** 在当前单元格输入内容并按指定键移动；内容为空时仅清空该格。 */
  const put = async (value, move) => {
    await page.keyboard.press('Delete');
    if (value) await page.keyboard.type(value, { delay: 15 });
    if (move) await page.keyboard.press(move);
  };
  /** 行尾清理：F 列 Delete 后 ←×5 回 A 列、↓ 到下一行。 */
  const endRow = async () => {
    await page.keyboard.press('Delete');
    for (let index = 0; index < 5; index += 1) await page.keyboard.press('ArrowLeft');
    await page.keyboard.press('ArrowDown');
  };

  await page.mouse.click(500, 320);
  await put('示例销售月报（2026年11月）', 'Enter');
  await put('统计周期：2026-11-01 至 2026-11-30　　编制部门：销售部', 'Enter');
  // 跳过种子的空行 3，落到表头行 4。
  await put('', 'Enter');
  await put('区域', 'Tab');
  await put('城市', 'Tab');
  await put('销量（件）', 'Tab');
  await put('销售额（元）', 'Tab');
  await put('达成率', 'Tab');
  await endRow();
  await put('华东大区', 'Tab');
  await put('上海', 'Tab');
  await put('1286', 'Tab');
  await put('385800', 'Tab');
  await put('102.4%', 'Tab');
  await endRow();
  await put('华北大区', 'Tab');
  await put('北京', 'Tab');
  await put('964', 'Tab');
  await put('289200', 'Tab');
  await put('96.8%', 'Tab');
  await endRow();
  await put('华南大区', 'Tab');
  await put('广州', 'Tab');
  await put('1103', 'Tab');
  await put('330900', 'Tab');
  await put('99.2%', 'Tab');
  await endRow();
  await put('西南大区', 'Tab');
  await put('成都', 'Tab');
  await put('745', 'Tab');
  await put('223500', 'Tab');
  await put('88.6%', 'Tab');
  await endRow();
  await put('合计', 'Tab');
  await put('', 'Tab');
  await put('4098', 'Tab');
  await put('1229400', '');
  // 输入完成后回车一次收起编辑态，让截图呈现普通选中效果。
  await page.keyboard.press('Enter');
}

/**
 * 调用后端接口并解析 JSON；非 2xx 或业务失败码时抛错中断。
 * @param {string} urlPath - 以 /api 开头的接口路径。
 * @param {RequestInit} options - fetch 选项（method/body）。
 * @returns {Promise<object>} 接口响应体 JSON。
 */
async function api(urlPath, options = {}) {
  const response = await fetch(`${BASE_URL}${urlPath}`, options);
  const rawText = await response.text();
  // 部分接口可能返回空响应体，空串按 null 处理而不是解析炸掉。
  const body = rawText ? JSON.parse(rawText) : null;
  if (!response.ok || (body !== null && body?.code !== 200)) {
    throw new Error(`接口失败 ${options.method ?? 'GET'} ${urlPath} → ${response.status} ${body?.message ?? ''}`);
  }
  return body;
}

/**
 * 创建全部演示模板并返回可用的数据集编码。
 * @returns {Promise<string>} 从 create-options 取的第一个数据集编码。
 */
async function createDemoReports() {
  const options = await api('/api/report-center/poc/reports/create-options');
  const datasetCode = options?.result?.datasets?.[0]?.code;
  if (!datasetCode) throw new Error('没有可用数据集，无法创建演示模板');
  for (const demo of DEMO_REPORTS) {
    await api('/api/report-center/poc/reports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...demo, datasetCode }),
    });
  }
  return datasetCode;
}

/**
 * 删除全部演示模板；单个失败仅告警不中断，便于人工补删。
 * @returns {Promise<void>} 清理完成后返回。
 */
async function deleteDemoReports() {
  for (const demo of DEMO_REPORTS) {
    try {
      await api(`/api/report-center/poc/reports/${demo.reportCode}`, { method: 'DELETE' });
    } catch (exception) {
      console.warn(`演示模板 ${demo.reportCode} 删除失败，请人工处理：${exception.message}`);
    }
  }
}

/**
 * 屏蔽列表画面中的真实数据集编码：把每行"数据集"列置换为 demo-dataset 展示名。
 * 真实数据集编码虽已随样例文件入库公开，但截图统一脱敏，画面不携带任何真实资源标识。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 置换完成后返回。
 */
async function maskDatasetColumn(page) {
  await page.evaluate(() => {
    document.querySelectorAll('.report-table tbody tr td:nth-child(3)').forEach((cell) => {
      cell.textContent = 'demo-dataset';
    });
  });
}

/**
 * 校验列表画面只包含演示内容：行数=3、meta 为"匹配 3 个模板"、
 * 模板编码都是 demo_ 前缀、数据集列已置换为 demo-dataset。
 * 这是截图前的防泄露闸门，不满足直接抛错终止，不产出任何图片。
 * @param {import('playwright-core').Page} page - 当前页面。
 * @returns {Promise<void>} 校验通过返回，否则抛错。
 */
async function assertDemoOnlyList(page) {
  const rowCount = await page.locator('.report-table tbody tr').count();
  if (rowCount !== DEMO_REPORTS.length) throw new Error(`列表行数 ${rowCount} ≠ 演示模板数，拒绝出图`);
  const metaText = await page.locator('.report-table-meta span').first().textContent();
  if (!metaText?.includes(`匹配 ${DEMO_REPORTS.length} 个模板`)) throw new Error(`meta 口径异常："${metaText}"，拒绝出图`);
  const codes = await page.locator('.report-table tbody tr td:nth-child(2) code').allTextContents();
  const leaking = codes.filter((code) => !code.startsWith('demo_'));
  if (leaking.length > 0) throw new Error(`画面中出现非演示模板编码：${leaking.join('、')}，拒绝出图`);
  const datasetCells = await page.locator('.report-table tbody tr td:nth-child(3)').allTextContents();
  const leakedDatasets = datasetCells.filter((text) => text.trim() !== 'demo-dataset');
  if (leakedDatasets.length > 0) throw new Error(`数据集列未完成脱敏：${leakedDatasets.join('、')}，拒绝出图`);
}

/**
 * 校验页面标题不携带真实业务信息：必须以"示例报表"开头。
 * @param {string} title - 待校验的标题文本。
 * @returns {void} 不通过时抛错。
 */
function assertSafeTitle(title) {
  if (!title.startsWith('示例报表')) throw new Error(`页面标题"${title}"不是演示名称，拒绝出图`);
}

/**
 * 主流程：建演示模板 → 保存示例数据快照 → 抓三张脱敏截图 → 删演示模板。
 * @returns {Promise<number>} 进程退出码：成功 0，失败 1。
 */
async function main() {
  fs.mkdirSync(IMG_DIR, { recursive: true });
  let browser = null;
  try {
    const datasetCode = await createDemoReports();
    browser = await chromium.launch({ channel: 'chrome', headless: process.env.E2E_HEADLESS === '1' });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

    // 阶段：模板列表——搜索"示例"只显示演示模板，防泄露校验通过后出图。
    const searchPromise = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('等待搜索响应超时')), 15000);
      page.on('response', async (response) => {
        if (!response.url().includes('/api/report-center/poc/reports/page')) return;
        if (new URL(response.url()).searchParams.get('query') !== '示例') return;
        clearTimeout(timer);
        resolve(await response.json());
      });
    });
    await page.goto(`${BASE_URL}/reports`, { waitUntil: 'domcontentloaded' });
    await page.locator('input[aria-label="搜索模板"]').fill('示例');
    const searchResult = await searchPromise;
    if ((searchResult?.result?.total ?? 0) !== DEMO_REPORTS.length) throw new Error(`搜索接口 total=${searchResult?.result?.total}，拒绝出图`);
    await page.waitForFunction(() => {
      const rows = document.querySelectorAll('.report-table tbody tr');
      return rows.length === 3 && !rows[0].querySelector('.report-table-empty');
    }, null, { timeout: 10000 });
    await maskDatasetColumn(page);
    await assertDemoOnlyList(page);
    // 截图裁掉大片空白区域，只保留头部到分页条，README 里展示更紧凑。
    await page.screenshot({ path: path.join(IMG_DIR, 'report-list.png'), clip: { x: 0, y: 0, width: 1440, height: 470 } });
    console.log('已生成 docs/images/report-list.png');

    // 阶段：新增模板弹窗——预填演示输入、置换数据集下拉文字后再出图。
    await page.locator('button.report-create-button').click();
    await page.waitForFunction(() => {
      const select = document.querySelector('.create-report-grid select');
      return select instanceof HTMLSelectElement && select.options.length > 0;
    }, null, { timeout: 10000 });
    await page.locator('.create-report-grid label:has-text("模板编码") input').fill('demo_order');
    await page.locator('.create-report-grid label:has-text("模板名称") input').fill('示例报表·销售订单统计');
    await page.locator('.create-report-grid label:has-text("工作表名称") input').fill('销售统计');
    await page.evaluate(() => {
      const select = document.querySelector('.create-report-grid select');
      if (select?.selectedOptions[0]) select.selectedOptions[0].text = `演示数据集（${'demo-dataset'}）`;
    });
    await page.screenshot({ path: path.join(IMG_DIR, 'report-create.png') });
    console.log('已生成 docs/images/report-create.png');
    await page.locator('.create-report-dialog button[type="button"]').first().click();

    // 阶段：模板设计器——打开演示模板，用键盘逐格录入销售示例数据后出图。
    await page.goto(`${BASE_URL}/reports/${DEMO_REPORTS[0].reportCode}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction((name) => {
      const heading = document.querySelector('header[aria-label="模板命令栏"] h1');
      return heading?.textContent?.trim() === name;
    }, DEMO_REPORTS[0].name, { timeout: 15000 });
    // 等待 Univer 画布渲染完成再开始键入，避免焦点未落到网格。
    await page.waitForFunction(() => document.querySelector('canvas') !== null, null, { timeout: 15000 });
    await page.waitForTimeout(1500);
    await typeDemoContent(page);
    await page.waitForTimeout(500);
    const editorTitle = await page.locator('header[aria-label="模板命令栏"] h1').textContent();
    assertSafeTitle(editorTitle?.trim() ?? '');
    await page.screenshot({ path: path.join(IMG_DIR, 'report-editor.png') });
    console.log('已生成 docs/images/report-editor.png');
    console.log(`（演示模板使用数据集编码 ${datasetCode} 仅作创建占位；设计器数据为静态示例，不展示真实数据集）`);
    return 0;
  } catch (exception) {
    console.error(`截图生成失败：${exception instanceof Error ? exception.message : String(exception)}`);
    return 1;
  } finally {
    if (browser) await browser.close().catch(() => {});
    // 阶段：无论成败都清理演示模板（含其模板快照），避免公开画面之外遗留脏数据。
    await deleteDemoReports();
  }
}

main().then((code) => process.exit(code)).catch((exception) => {
  console.error(exception);
  process.exit(1);
});
