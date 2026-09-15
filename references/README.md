# 参考源码说明

本目录只放参考项目源码，用于分析功能、交互和实现路径，不作为最终产品直接对外发布。

## Admin.NET

Admin.NET 已作为当前项目底座复制到根目录，原始 README 保存在 `../README.AdminNET.md`。

## luck-report

路径：`references/luck-report`

定位：报表中心主要功能组件（设计器、数据集、预览、导出）的参考实现，仅用于研究，不进入交付代码。

组成部分：

- `luck-report-server/`：Java 后端（Spring Boot + Maven 多模块，含 core 表达式与渲染内核、font、pub、web 模块）。
- `luck-report-ui/`：Vue 网页报表设计器前端。
- 根目录含 `README.md`、`CHANGELOG.md`、`LICENSE`（Apache-2.0）与 `LICENSE-UREPORT`（上游 UReport2，同为 Apache-2.0）。

用途：

- 参考报表设计器交互。
- 参考 SQL 数据集、查询参数和预览流程。
- 参考单元格公式、统计函数、HTML 预览、Excel 导出实现。

注意：

- `luck-report` 使用 Apache-2.0 协议，仍需要保留许可证信息并避免直接复制成闭源商业产品的一部分。
- 其 PDF 导出链路涉及 iText 5，存在商业授权风险；第一版不直接采用该 PDF 实现。
