# 仓库协作规则

## 代码注释质量（强制）

- 新增或修改的 C#、TypeScript、JavaScript、Vue 和 PowerShell 代码中，每一个具名方法、构造函数或具名函数都必须有紧邻声明的方法级注释。C# 使用 XML 文档注释，TypeScript/JavaScript/Vue 使用 TSDoc，PowerShell 使用声明前的注释。
- 方法级注释必须说明职责和业务意图，不能只复述方法名，也不能用“处理数据”“执行方法”、`TODO` 等占位文字应付检查。输入、输出、副作用、失败策略或特殊边界不直观时，必须一并写清楚。
- 方法级注释至少包含 8 个有效文字或数字字符。`<inheritdoc />` 可用于语义没有变化的继承实现；如果实现增加了业务约束、失败策略或副作用，仍须写独立说明。
- 方法体超过 40 行视为长方法。长方法除方法级注释外，还必须按功能阶段写独立的块内注释，例如“校验输入”“构造查询”“持久化结果”“失败回滚”。阶段注释不能逐行复述代码；每约 40 行至少一段，且长方法至少两段，每段至少包含 6 个有效文字或数字字符。
- 自动生成文件、第三方源码、构建产物和声明文件不执行本规则。测试代码不豁免；测试方法也要说明所验证的行为或边界。
- 自动检查只能验证覆盖率、注释长度和长方法阶段注释数量，不能判断文字是否准确。代码审查时必须核对注释与实际行为一致，修改逻辑时同步更新注释。

提交前检查当前工作区改动：

```powershell
pwsh -NoProfile -File ./scripts/quality/Test-CodeComments.ps1
```

全量审计仓库内受支持的源文件：

```powershell
pwsh -NoProfile -File ./scripts/quality/Test-CodeComments.ps1 -All
```

持续集成检查相对目标分支新增或修改的文件：

```powershell
pwsh -NoProfile -File ./scripts/quality/Test-CodeComments.ps1 -BaseRef origin/master
```
