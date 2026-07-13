# 参考源码说明

本目录只放参考项目源码，用于分析功能、交互和实现路径，不作为最终产品直接对外发布。

## Admin.NET

Admin.NET 已作为当前项目底座复制到根目录，原始 README 保存在 `../README.AdminNET.md`。

## luck-report

路径：`references/luck-report`

用途：

- 参考报表设计器交互。
- 参考 SQL 数据集、查询参数和预览流程。
- 参考单元格公式、统计函数、HTML 预览、Excel 导出实现。

注意：

- `luck-report` 使用 Apache-2.0 协议，仍需要保留许可证信息并避免直接复制成闭源商业产品的一部分。
- 其 PDF 导出链路涉及 iText 5，存在商业授权风险；第一版不直接采用该 PDF 实现。
