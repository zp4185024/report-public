---
name: report-list-test
description: hd-report-center 报表中心"模板列表"页全自动E2E测试(列表加载→搜索→分页→新增校验→编辑器跳转→报表包导出→删除→现场恢复→报表包导入弹窗信封解包, 29检查点)，跑完自动生成 Markdown 入库报告 + 仓库根 Word 图文报告，并可按规范把本次改动提交推送到公司 git + gitee + github 三端。当用户说"模板列表测试"、"报表中心测试"、"列表页跑一遍"、"报表中心回归"，或涉及 ReportWeb 模板列表/报表目录/删除/导出报表包的功能改动验证、新功能开发完成后需要验证时自动触发。
---

# 模板列表页全自动 E2E 测试 + 三端 git 同步

一条龙：**预检环境 → Playwright 真实浏览器跑 29 检查点 → 生成报告 → 提交推送三端 git**。
测试以接口响应为锚点断言（不读陈旧 DOM 文案），测试数据用后即删，不留脏数据。

## 一键执行

```bash
# 前置：bash 下先补 nvm node 到 PATH（本机固定版本 v22.21.1）
export PATH="/c/Users/1/AppData/Local/nvm/v22.21.1:$PATH"
cd D:/zp/git/hd-report-center/scripts/e2e/report-list

# 1) 依赖（首次或 node_modules 缺失时；独立目录，不污染 ReportWeb）
npm install

# 2) 跑 E2E（系统 Chrome 有头模式，全程约 2 分钟；E2E_HEADLESS=1 可无头）
node report_list_e2e.js

# 3) 生成报告（Markdown → docs/validation/，Word 图文 → 仓库根）
node gen_report.js
```

- 退出码 0 = 29/29 全绿；非 0 = 有失败，先看控制台 ❌ 行和 `output/shots/F_failure.png`。
- 报告：`docs/validation/<日期>-report-list-e2e-results.md`（入库）+
  仓库根《模板列表E2E测试报告.docx》（图文，本地交付，**不入库**）。

## 环境依赖（P0 预检会自动核对，不满足直接终止）

- 前端 5175（Vite 代理）、后端 5005（Furion 须从 bin 目录启动；5006 是 `--urls` 备选端口）。
- 数据库可达（P0-2 检查分页 API 返回真实 total>0）；DB 不通时不要硬跑其余环节。
- 测试需要库里至少存在 1 个 `templateRevision>0` 的已保存模板（供导出报表包环节）。

## 测试链路（九环节 29 检查点）

| 环节 | 核心验证 |
|---|---|
| P0 预检 | 前端/网关 200、分页 API 真实数据（DB 连通）、features 接口 |
| P1 列表 | h1=模板列表、7 业务列表头齐全、行数=接口 total 截断、meta 计数一致 |
| P2 搜索 | 无命中空态提示、按编码命中 1 行（匹配口径）、清空恢复全量 |
| P3 分页 | 每页 15→30 切换（接口+汇总同步）、第 1 页按钮状态、真实翻到第 2 页 |
| P4 新增 | 弹窗加载数据集选项、非法编码拦截、名称/工作表必填拦截、合法创建跳转 `/reports/<code>`、编辑器渲染出模板名 |
| P5 导出报表包 | 自动选第一个未禁用导出按钮（templateRevision>0）→ confirm 接受 → 触发下载 .json → **落盘解析断言顶层 packageKind（信封包装回归门禁）** |
| P6 删除 | confirm 接受 → 成功 toast → 行移除 → 接口 total 回落到测试前 |
| P7 现场 | 按测试编码搜索 total=0，无残留 |
| P8 导入弹窗 | 在模板列表工具栏点「导入报表包」打开弹窗（原独立页 `/report-package-import` 已下线）→ 把 P5 纯包重新包一层接口信封做夹具上传 → 本地自动解包进入第 2 步校验 → 断言"已自动解包"提示 → 关闭弹窗；只读验证，绝不点确认导入 |

测试模板编码固定 `e2e_tpl_<时间戳>`（合法编码规则 `^[a-z][a-z0-9_-]{0,63}$`）；
流程中断时兜底调 `DELETE /api/report-center/poc/reports/<code>` 清理。

## 关键坑（实测沉淀，改页面/改脚本前必读）

1. **等待策略是灵魂**：列表文案（meta/汇总/空态）存在陈旧窗口——必须"先挂接口响应等待、
   再触发操作"，拿到响应里的真实 total 后再等 DOM 渲染同一数值。
   直接等 `meta.includes('匹配')` 会立即被上一轮遗留文案满足 → 假失败/假通过。
   封装见 `waitForReportsResponse`（按 query/page/pageSize 参数匹配响应）与 `fillSearchAndWait`。
2. **空关键字时前端整体省略 `query` 参数**（api.ts 里 `if (query.trim()) params.set(...)`），
   URL 里没有 `query=`，匹配响应时缺失必须视同空串，否则清空搜索永远等不到响应。
3. **编辑器标题异步**：刚跳转时 h1 是占位"模板编辑器"，要等它变成模板名再断言。
4. **P3 结尾必须把分页恢复到第 1 页+每页 15**，再进 P4；带着"第 2 页/每页 30"进新增流程，
   或对禁用态的"第一页"按钮 click，都会 30s 超时中断。
5. **`window.confirm` 全局自动接受**：`page.on('dialog', d => d.accept())`，覆盖删除与导出确认。
6. **导出按钮 disabled 判定**：`templateRevision===0`（从未保存模板）的行不可导出，
   遍历跳过禁用行，取第一个可导出目标。
7. 每页 15/30/60 选项来自 PaginationBar；总数小于每页条数时只有一页，翻页按钮全禁用是正常态。
8. 改了 ReportWeb 列表页/弹窗/分页条的选择器（aria-label、class）后必须同步改本脚本再回归。

## 三端 git 同步（测试通过后的收尾步骤）

远端约定（已配置；缺失时按此补）：

| remote | 地址 | 说明 |
|---|---|---|
| origin | http://60.23.29.213:3060/hd/hd-report-center.git | 公司 git（主） |
| gitee | https://gitee.com/zp4185024/report-public.git | 个人镜像（2026-11-04 已强推覆盖旧快照，与公司 git 一致） |
| github | https://github.com/zp4185024/report-public.git | 个人镜像（空仓库首推） |

提交流程（顺序执行）：

```bash
cd D:/zp/git/hd-report-center
# 1) 注释质量门（AGENTS.md 强制；新增 js/md 都要过）
pwsh -NoProfile -File ./scripts/quality/Test-CodeComments.ps1

# 2) 只暂存本次相关文件——逐个列出，绝不 git add -A / git add .
git add .zcode/skills/report-list-test/SKILL.md \
        scripts/e2e/report-list/package.json \
        scripts/e2e/report-list/.gitignore \
        scripts/e2e/report-list/report_list_e2e.js \
        scripts/e2e/report-list/gen_report.js \
        docs/validation/<日期>-report-list-e2e-results.md
git commit -m "test(report): add template list e2e skill and scripts"

# 3) 依次推送三端；单端失败记录原因继续推下一端，最后汇总
git push origin master
git push gitee master
git push github master
```

硬规则：

- **绝不 `git add -A`/`git add .`**：工作区常有未验收模块（如数据同步）、`docs/ops`、
  `scripts/remote`、日志与 docx 报告，误提交即事故。逐文件 add，提交前 `git status` 复核暂存区。
- `scripts/e2e/report-list/output/`、`node_modules/`、仓库根 `*.docx` 报告不入库
  （目录内 .gitignore 已覆盖前两者；docx 靠逐文件 add 保证不进暂存区）。
- 提交信息用 conventional commits（`test(report): ...` / `fix(report): ...`）。
- gitee 首次历史分叉时需 `git push -f gitee master`（2026-11-04 已获用户确认执行过一次），
  此后一律普通 push；**对 origin（公司 git）任何时候都禁止 -f**。
- 推送认证走 Windows 凭据管理器；凭据失效时提示用户重新登录，绝不把账号密码写进 URL 或脚本。
