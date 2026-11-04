/**
 * hd-report-center 模板列表 E2E 测试报告生成器（Markdown + Word 图文）。
 *
 * 职责：读取 report_list_e2e.js 产出的 output/e2e_result.json 与 output/shots/*.png，
 * 生成两份报告——
 * 1. docs/validation/<日期>-report-list-e2e-results.md（文本表格，随仓库提交到三端 git）；
 * 2. 仓库根《模板列表E2E测试报告.docx》（封面+检查点表+过程截图的图文报告，本地交付，不入库）。
 *
 * 运行前提：已成功运行 report_list_e2e.js；报告内容以结果 JSON 为唯一事实来源。
 */
const fs = require('fs');
const path = require('path');
const {
  Document, Packer, ImageRun, Paragraph, TextRun, HeadingLevel,
  AlignmentType, Table, TableRow, TableCell, WidthType, ShadingType,
} = require('docx');

/** 结果 JSON 与截图所在目录。 */
const OUT_DIR = path.join(__dirname, 'output');
/** 截图目录。 */
const SHOT_DIR = path.join(OUT_DIR, 'shots');
/** 仓库根目录（脚本位于 scripts/e2e/report-list 下三级）。 */
const REPO_ROOT = path.join(__dirname, '..', '..', '..');
/** Markdown 报告输出目录。 */
const MD_DIR = path.join(REPO_ROOT, 'docs', 'validation');
/** Word 报告输出文件（仓库根，不入库）。 */
const DOCX_PATH = path.join(REPO_ROOT, '模板列表E2E测试报告.docx');

/** 报告配色：通过绿 / 失败红 / 主题蓝 / 辅助灰。 */
const GREEN = '2E7D32';
const RED = 'C62828';
const BLUE = '1565C0';
const GRAY = '666666';

/**
 * 读取并校验 E2E 结果 JSON；文件缺失时直接终止并提示先跑测试。
 * @returns {{started_at: string, finished_at: string, base_url: string, headless: boolean, test_code: string, total: number, pass: number, fail: number, checkpoints: Array<{id: string, title: string, status: string, detail: string}>}} 结果对象。
 */
function readResult() {
  const file = path.join(OUT_DIR, 'e2e_result.json');
  if (!fs.existsSync(file)) {
    console.error('未找到 output/e2e_result.json，请先运行 node report_list_e2e.js');
    process.exit(1);
  }
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

/**
 * 列出截图目录中的 PNG 文件（按文件名排序），作为 Word 报告的图文素材。
 * @returns {string[]} 截图文件名列表（不含扩展名也含扩展名，按原文件名）。
 */
function listShots() {
  if (!fs.existsSync(SHOT_DIR)) return [];
  return fs.readdirSync(SHOT_DIR).filter((name) => name.endsWith('.png')).sort();
}

/**
 * 把截图文件名翻译成中文说明（如 P4_dialog → 新增模板弹窗）。
 * @param {string} shotName - 截图文件名（含扩展名）。
 * @returns {string} 中文说明，未知阶段回退为原始文件名。
 */
function shotCaption(shotName) {
  const captions = {
    P1_list: '模板列表首屏（表头/数据行/分页汇总）',
    P2_search_empty: '搜索无命中的空态提示',
    P2_search_hit: '按编码搜索命中目标模板',
    P3_pagination: '每页 30 条并翻到第 2 页',
    P4_dialog: '新增模板弹窗（数据集选项已加载）',
    P4_invalid_code: '非法编码被前端拦截提示',
    P4_editor: '创建成功后进入模板编辑器',
    P5_export: '导出报表包触发浏览器下载',
    P6_deleted: '删除测试模板成功且总数回落',
    F_failure: '流程中断时的现场截图',
  };
  const key = path.basename(shotName, '.png');
  return captions[key] ?? key;
}

/**
 * 生成 Markdown 报告全文（表格口径，随仓库提交）。
 * @param {object} data - E2E 结果对象。
 * @returns {string} Markdown 全文。
 */
function buildMarkdown(data) {
  const date = data.started_at.slice(0, 10);
  const lines = [];
  // 阶段：标题与概要，交代环境与执行方式。
  lines.push(`# 模板列表 E2E 测试结果（${date}）`);
  lines.push('');
  lines.push(`- **结果**：${data.pass}/${data.total} 通过${data.fail === 0 ? '，全部通过 ✅' : `，${data.fail} 个失败 ❌`}`);
  lines.push(`- **目标环境**：${data.base_url}（${data.headless ? '无头' : '有头'} Chrome）`);
  lines.push(`- **测试模板**：${data.test_code}（测试后已删除，现场无残留）`);
  lines.push(`- **执行方式**：Playwright 驱动系统 Chrome，真实页面 + 真实接口；截图在 \`scripts/e2e/report-list/output/shots/\`（本地留证，不入库）`);
  lines.push('');
  // 阶段：检查点明细表。
  lines.push('| 编号 | 检查点 | 结果 | 说明 |');
  lines.push('| --- | --- | --- | --- |');
  for (const item of data.checkpoints) {
    const mark = item.status === 'PASS' ? '✅' : '❌';
    lines.push(`| ${item.id} | ${item.title} | ${mark} | ${item.detail.replace(/\|/g, '/')} |`);
  }
  lines.push('');
  // 阶段：截图索引与结论。
  const shots = listShots();
  if (shots.length > 0) {
    lines.push('过程截图（本地路径，仓库不含图片）：');
    lines.push('');
    for (const name of shots) {
      lines.push(`- \`${path.relative(MD_DIR, path.join(SHOT_DIR, name)).replace(/\\/g, '/')}\` — ${shotCaption(name)}`);
    }
    lines.push('');
  }
  lines.push(data.fail === 0
    ? '结论：模板列表页加载、搜索、分页、新增校验、编辑器跳转、报表包导出、删除与现场恢复全链路可用。'
    : `结论：存在 ${data.fail} 个失败检查点，见上表 ❌ 行，需定位修复后重跑。`);
  return `${lines.join('\n')}\n`;
}

/**
 * 构造 Word 表格单元格。
 * @param {string} text - 单元格文本。
 * @param {{bold?: boolean, color?: string, fill?: string}} options - 样式选项。
 * @returns {TableCell} docx 单元格。
 */
function cell(text, options = {}) {
  return new TableCell({
    children: [new Paragraph({ children: [new TextRun({ text, size: 18, bold: options.bold, color: options.color })] })],
    shading: options.fill ? { fill: options.fill, type: ShadingType.CLEAR } : undefined,
  });
}

/**
 * 构造一级/二级标题段落。
 * @param {string} text - 标题文本。
 * @param {number} level - 标题级别（1 或 2）。
 * @returns {Paragraph} docx 标题段落。
 */
function heading(text, level) {
  return new Paragraph({
    text,
    heading: level === 1 ? HeadingLevel.HEADING_1 : HeadingLevel.HEADING_2,
    spacing: { before: 300, after: 120 },
  });
}

/**
 * 构造正文段落。
 * @param {string} text - 正文文本。
 * @param {{color?: string, bold?: boolean}} options - 样式选项。
 * @returns {Paragraph} docx 正文段落。
 */
function body(text, options = {}) {
  return new Paragraph({
    children: [new TextRun({ text, size: 21, color: options.color, bold: options.bold })],
    spacing: { after: 80 },
  });
}

/**
 * 构造截图段落（等比缩放到 580px 宽）；截图缺失时输出占位文字。
 * @param {string} shotName - 截图文件名（含扩展名）。
 * @returns {Paragraph} docx 图片段落。
 */
function imageParagraph(shotName) {
  const file = path.join(SHOT_DIR, shotName);
  if (!fs.existsSync(file)) {
    return new Paragraph({ children: [new TextRun({ text: `[缺截图: ${shotName}]`, color: GRAY, size: 18 })] });
  }
  // 阶段：读 PNG 头取宽高，等比缩放到固定宽度后嵌入。
  const buffer = fs.readFileSync(file);
  const width = buffer.readUInt32BE(16);
  const height = buffer.readUInt32BE(20);
  const target = 580;
  return new Paragraph({
    children: [new ImageRun({ data: buffer, transformation: { width: target, height: Math.round(target * height / width) } })],
    spacing: { after: 80 },
  });
}

/**
 * 构造截图下方的灰色斜体说明文字。
 * @param {string} text - 说明文本。
 * @returns {Paragraph} docx 说明段落。
 */
function captionParagraph(text) {
  return new Paragraph({
    children: [new TextRun({ text, size: 18, color: GRAY, italics: true })],
    spacing: { after: 160 },
  });
}

/**
 * 生成 Word 图文报告并写入仓库根。
 * @param {object} data - E2E 结果对象。
 * @returns {Promise<void>} 写盘完成后返回。
 */
async function buildDocx(data) {
  const children = [];
  // 阶段：封面——标题、链路副标题、日期与通过率。
  children.push(new Paragraph({ text: '', spacing: { before: 2200 } }));
  children.push(new Paragraph({
    children: [new TextRun({ text: 'hd-report-center 模板列表 E2E 自动化测试报告', bold: true, size: 48, color: BLUE })],
    alignment: AlignmentType.CENTER,
  }));
  children.push(new Paragraph({
    children: [new TextRun({ text: '列表加载 → 搜索 → 分页 → 新增校验 → 编辑器 → 报表包导出 → 删除 → 现场恢复', size: 24, color: GRAY })],
    alignment: AlignmentType.CENTER,
    spacing: { before: 200 },
  }));
  children.push(new Paragraph({
    children: [new TextRun({ text: `测试日期 ${data.started_at.slice(0, 19).replace('T', ' ')} · Playwright 真实浏览器 · 真实接口`, size: 22, color: GRAY })],
    alignment: AlignmentType.CENTER,
    spacing: { before: 120 },
  }));
  children.push(new Paragraph({
    children: [new TextRun({ text: `检查点 ${data.pass} 通过 / ${data.fail} 失败`, bold: true, size: 26, color: data.fail === 0 ? GREEN : RED })],
    alignment: AlignmentType.CENTER,
    spacing: { before: 280 },
  }));

  // 阶段：测试概要。
  children.push(heading('一、测试概要', 1));
  children.push(body(`本次测试使用 Playwright 驱动系统 Chrome（${data.headless ? '无头' : '有头'}模式），以真实用户视角访问 ${data.base_url} 的模板列表页，完整走通八个环节共 ${data.total} 个检查点。所有列表断言以接口响应为锚点：先按参数捕获分页接口响应拿到真实 total，再等 DOM 渲染一致，避免读到陈旧文案。测试模板 ${data.test_code} 创建后即删除，不遗留脏数据。`));

  // 阶段：检查点总表（表头蓝底白字，结果列按状态着色）。
  children.push(heading('二、检查点明细', 1));
  const headerCells = ['编号', '检查点', '结果', '说明'].map((text) => cell(text, { bold: true, color: 'FFFFFF', fill: BLUE }));
  const rows = [new TableRow({ children: headerCells })];
  for (const item of data.checkpoints) {
    const passed = item.status === 'PASS';
    rows.push(new TableRow({
      children: [
        cell(item.id),
        cell(item.title),
        cell(passed ? '✅ 通过' : '❌ 失败', { color: passed ? GREEN : RED, bold: true }),
        cell(item.detail),
      ],
    }));
  }
  children.push(new Table({ rows, width: { size: 100, type: WidthType.PERCENTAGE } }));
  children.push(new Paragraph({ text: '' }));

  // 阶段：过程截图逐张嵌入。
  children.push(heading('三、过程截图', 1));
  const shots = listShots();
  if (shots.length === 0) children.push(body('本次运行没有截图。', { color: GRAY }));
  for (const name of shots) {
    children.push(imageParagraph(name));
    children.push(captionParagraph(shotCaption(name)));
  }

  // 阶段：结论。
  children.push(heading('四、结论', 1));
  children.push(body(
    data.fail === 0
      ? '模板列表页全链路（加载、搜索、分页、新增校验、编辑器跳转、报表包导出、删除与现场恢复）验证可用，可以继续后续开发或发布流程。'
      : `存在 ${data.fail} 个失败检查点，需按明细表定位修复后重跑；失败现场见截图章节。`,
    { color: data.fail === 0 ? GREEN : RED, bold: true },
  ));

  const document = new Document({ sections: [{ children }] });
  const buffer = await Packer.toBuffer(document);
  fs.writeFileSync(DOCX_PATH, buffer);
}

/**
 * 主流程：读结果 → 写 Markdown → 写 Word 报告，输出两份报告路径。
 * @returns {Promise<void>} 完成后返回。
 */
async function main() {
  const data = readResult();
  const date = data.started_at.slice(0, 10);
  const markdownPath = path.join(MD_DIR, `${date}-report-list-e2e-results.md`);
  fs.mkdirSync(MD_DIR, { recursive: true });
  fs.writeFileSync(markdownPath, buildMarkdown(data), 'utf8');
  await buildDocx(data);
  console.log(`Markdown 报告: ${markdownPath}`);
  console.log(`Word 图文报告: ${DOCX_PATH}`);
}

main().catch((exception) => {
  console.error(exception);
  process.exit(1);
});
