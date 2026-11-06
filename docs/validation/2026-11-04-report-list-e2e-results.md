# 模板列表 E2E 测试结果（2026-11-04）

- **结果**：28/28 通过，全部通过 ✅
- **目标环境**：http://localhost:5175（无头 Chrome）
- **测试模板**：e2e_tpl_1793775729422（测试后已删除，现场无残留）
- **执行方式**：Playwright 驱动系统 Chrome，真实页面 + 真实接口；截图在 `scripts/e2e/report-list/output/shots/`（本地留证，不入库）

| 编号 | 检查点 | 结果 | 说明 |
| --- | --- | --- | --- |
| P0-1 | 前端与后端网关可达 | ✅ | GET /api/report-center/features → 200 |
| P0-2 | 模板分页 API 返回真实数据（数据库连通） | ✅ | GET /poc/reports/page → 200，模板总数=71 |
| P0-3 | 功能开关接口可用 | ✅ | features.code=200 |
| P1-1 | 列表页打开且主标题正确 | ✅ | h1="模板列表" |
| P1-2 | 表头包含全部业务列 | ✅ | 7 个业务列齐全（另有操作列） |
| P1-3 | 列表渲染真实模板数据 | ✅ | 接口 total=71，页面 15 行 |
| P1-4 | 当前页计数与分页汇总一致 | ✅ | meta="当前页 15 个" |
| P2-1 | 无命中关键字显示空态提示 | ✅ | 接口 total=0，空态文案已显示 |
| P2-2 | 有效关键字命中并切换匹配口径 | ✅ | 接口 total=1，命中 1 行 |
| P2-3 | 清空搜索恢复全量列表 | ✅ | 接口 total=71，当前页 15 行 |
| P3-1 | 每页条数切换生效 | ✅ | 接口 pageSize=30，汇总="1-30 / 71 个模板" |
| P3-2 | 第 1 页翻页按钮状态正确 | ✅ | 上一页禁用=true，下一页禁用=false |
| P3-3 | 下一页翻到第 2 页 | ✅ | 接口 page=2，文案="第 2 / 3 页" |
| P4-1 | 新增弹窗加载数据集选项 | ✅ | 可选数据集 64 个 |
| P4-2 | 非法编码被拦截并提示 | ✅ | 提示="模板编码必须以字母开头…" |
| P4-3 | 名称/工作表为空被拦截 | ✅ | 提示="模板名称和工作表名称不能为空" |
| P4-4 | 合法创建成功并跳转编辑器 | ✅ | URL=http://localhost:5175/reports/e2e_tpl_1793775729422 |
| P4-5 | 编辑器正常渲染 | ✅ | 编辑器标题="E2E模板列表测试" |
| P5-1 | 存在可导出的已保存模板 | ✅ | 目标模板="报表资产清单" |
| P5-2 | 导出报表包触发下载 | ✅ | 下载文件="report_asset_inventory.report-package.json" |
| P5-3 | 下载文件为纯报表包 JSON（无信封包装） | ✅ | packageKind=hd-report-center.report-package report=report_asset_inventory version=1 |
| P6-1 | 删除弹出确认且成功提示 | ✅ | 提示="模板「E2E模板列表测试」已删除" |
| P6-2 | 删除后列表行移除 | ✅ | 测试模板行已移除 |
| P6-3 | 模板总数恢复到测试前 | ✅ | 接口 total=71，页面="共 71 个模板" |
| P7-1 | 测试数据无残留 | ✅ | 搜索 e2e_tpl_1793775729422 接口 total=0 |
| P8-1 | 构造信封包装夹具 | ✅ | 夹具="envelope_fixture.json" |
| P8-2 | 信封文件自动解包并进入第2步校验 | ✅ | 报表="报表资产清单" |
| P8-3 | 展示自动解包提示（建议重新导出） | ✅ | 提示="检测到接口信封包装，已自动解包；建议升级导出端重新导出。报表包读取成功，请核对校验结果并选择目标数据源。" |

过程截图（本地路径，仓库不含图片）：

- `../../scripts/e2e/report-list/output/shots/P1_list.png` — 模板列表首屏（表头/数据行/分页汇总）
- `../../scripts/e2e/report-list/output/shots/P2_search_empty.png` — 搜索无命中的空态提示
- `../../scripts/e2e/report-list/output/shots/P2_search_hit.png` — 按编码搜索命中目标模板
- `../../scripts/e2e/report-list/output/shots/P3_pagination.png` — 每页 30 条并翻到第 2 页
- `../../scripts/e2e/report-list/output/shots/P4_dialog.png` — 新增模板弹窗（数据集选项已加载）
- `../../scripts/e2e/report-list/output/shots/P4_editor.png` — 创建成功后进入模板编辑器
- `../../scripts/e2e/report-list/output/shots/P4_invalid_code.png` — 非法编码被前端拦截提示
- `../../scripts/e2e/report-list/output/shots/P5_export.png` — 导出报表包触发浏览器下载
- `../../scripts/e2e/report-list/output/shots/P6_deleted.png` — 删除测试模板成功且总数回落
- `../../scripts/e2e/report-list/output/shots/P8_import_validate.png` — P8_import_validate
- `../../scripts/e2e/report-list/output/shots/user_real_file_import.png` — user_real_file_import

结论：模板列表页加载、搜索、分页、新增校验、编辑器跳转、报表包导出、删除与现场恢复全链路可用。
