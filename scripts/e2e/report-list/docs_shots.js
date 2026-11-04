/**
 * README 界面截图生成脚本（演示数据脱敏出图）。
 *
 * 职责：临时通过 API 创建 3 个通用名演示模板，抓取 模板列表 / 新增模板弹窗 /
 * 模板设计器 三张截图写入 docs/images/ 供 README 首页使用；真实业务模板
 * 通过搜索过滤完全不进入画面，数据集下拉文字经 DOM 置换屏蔽；结束即删演示模板。
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

/** 演示模板定义：编码必须 demo_ 前缀（防泄露断言依赖），名称不带任何业务身份信息。 */
const DEMO_REPORTS = [
  { reportCode: 'demo_visits', name: '示例报表·门诊就诊量统计', sheetName: '门诊量统计' },
  { reportCode: 'demo_fees', name: '示例报表·费用项目汇总', sheetName: '费用汇总' },
  { reportCode: 'demo_stock', name: '示例报表·库存月报', sheetName: '库存月报' },
];

/**
 * 调用后端接口并解析 JSON；非 2xx 或业务失败码时抛错中断。
 * @param {string} urlPath - 以 /api 开头的接口路径。
 * @param {RequestInit} options - fetch 选项（method/body）。
 * @returns {Promise<object>} 接口响应体 JSON。
 */
async function api(urlPath, options = {}) {
  const response = await fetch(`${BASE_URL}${urlPath}`, options);
  const body = await response.json();
  if (!response.ok || body?.code !== 200) {
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
 * 主流程：建演示模板 → 抓三张脱敏截图 → 删演示模板，输出图片路径清单。
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

    // 阶段：新增模板弹窗——把数据集下拉的选中项文字置换为演示名后再出图。
    await page.locator('button.report-create-button').click();
    await page.waitForFunction(() => {
      const select = document.querySelector('.create-report-grid select');
      return select instanceof HTMLSelectElement && select.options.length > 0;
    }, null, { timeout: 10000 });
    await page.evaluate(() => {
      const select = document.querySelector('.create-report-grid select');
      if (select?.selectedOptions[0]) select.selectedOptions[0].text = `演示数据集（${'demo-dataset'}）`;
    });
    await page.screenshot({ path: path.join(IMG_DIR, 'report-create.png') });
    console.log('已生成 docs/images/report-create.png');
    await page.locator('.create-report-dialog button[type="button"]').first().click();

    // 阶段：模板设计器——打开第一个演示模板，标题防泄露校验通过后出图。
    await page.goto(`${BASE_URL}/reports/${DEMO_REPORTS[0].reportCode}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction((name) => {
      const heading = document.querySelector('header[aria-label="模板命令栏"] h1');
      return heading?.textContent?.trim() === name;
    }, DEMO_REPORTS[0].name, { timeout: 15000 });
    const editorTitle = await page.locator('header[aria-label="模板命令栏"] h1').textContent();
    assertSafeTitle(editorTitle?.trim() ?? '');
    await page.screenshot({ path: path.join(IMG_DIR, 'report-editor.png') });
    console.log('已生成 docs/images/report-editor.png');
    console.log(`（演示模板使用数据集编码 ${datasetCode}，仅作创建占位，截图未展示真实数据集名）`);
    return 0;
  } catch (exception) {
    console.error(`截图生成失败：${exception instanceof Error ? exception.message : String(exception)}`);
    return 1;
  } finally {
    if (browser) await browser.close().catch(() => {});
    // 阶段：无论成败都清理演示模板，避免公开画面之外遗留脏数据。
    await deleteDemoReports();
  }
}

main().then((code) => process.exit(code)).catch((exception) => {
  console.error(exception);
  process.exit(1);
});
