import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

/** 统计注释中真正的文字和数字，忽略标点、标签及空白。 */
function meaningfulLength(value) {
  return Array.from(value.match(/[\p{L}\p{N}]/gu) ?? []).length;
}

/** 读取包装脚本传入的阈值和源文件清单位置。 */
function parseArguments(argv) {
  const options = {
    fileList: "",
    longMethodLines: 40,
    minDescriptionLength: 8,
    minStageComments: 2,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    const value = argv[index + 1];

    if (argument === "--file-list") {
      options.fileList = value;
      index += 1;
    } else if (argument === "--long-method-lines") {
      options.longMethodLines = Number(value);
      index += 1;
    } else if (argument === "--min-description-length") {
      options.minDescriptionLength = Number(value);
      index += 1;
    } else if (argument === "--min-stage-comments") {
      options.minStageComments = Number(value);
      index += 1;
    }
  }

  if (!options.fileList) {
    throw new Error("缺少 --file-list 参数。");
  }

  return options;
}

/** 从两个前端工程的本地依赖中定位 TypeScript 编译器。 */
async function loadTypeScript(repoRoot) {
  const candidates = [
    path.join(repoRoot, "ReportWeb", "node_modules", "typescript", "lib", "typescript.js"),
    path.join(repoRoot, "Web", "node_modules", "typescript", "lib", "typescript.js"),
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) {
      return import(pathToFileURL(candidate).href);
    }
  }

  throw new Error("未找到 TypeScript 编译器，请先在 ReportWeb 或 Web 目录安装前端依赖。");
}

/** 保留 Vue 脚本区的行号并把模板、样式等区域替换为空白。 */
function extractVueScripts(source) {
  const output = Array.from(source, (character) => (character === "\n" || character === "\r" ? character : " "));
  const scriptPattern = /<script\b([^>]*)>([\s\S]*?)<\/script\s*>/gi;
  let match;

  while ((match = scriptPattern.exec(source)) !== null) {
    const content = match[2];
    const contentStart = match.index + match[0].indexOf(content);

    for (let index = 0; index < content.length; index += 1) {
      output[contentStart + index] = content[index];
    }
  }

  return output.join("");
}

/** 把不同函数语法统一成可报告的方法名称。 */
function getFunctionName(ts, node, sourceFile) {
  if (ts.isConstructorDeclaration(node)) {
    return "constructor";
  }

  if (node.name) {
    return node.name.getText(sourceFile);
  }

  if ((ts.isArrowFunction(node) || ts.isFunctionExpression(node)) && node.parent) {
    const parent = node.parent;
    if (ts.isVariableDeclaration(parent) || ts.isPropertyDeclaration(parent) || ts.isPropertyAssignment(parent)) {
      return parent.name.getText(sourceFile);
    }
    if (
      ts.isBinaryExpression(parent) &&
      parent.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
      parent.right === node
    ) {
      return parent.left.getText(sourceFile);
    }
  }

  return "";
}

/** 判断语法节点是否属于必须提供方法级注释的具名函数。 */
function isDocumentedFunctionNode(ts, node, sourceFile) {
  if (
    ts.isFunctionDeclaration(node) ||
    ts.isMethodDeclaration(node) ||
    ts.isMethodSignature(node) ||
    ts.isConstructorDeclaration(node) ||
    ts.isGetAccessorDeclaration(node) ||
    ts.isSetAccessorDeclaration(node)
  ) {
    return Boolean(getFunctionName(ts, node, sourceFile));
  }

  if (!ts.isArrowFunction(node) && !ts.isFunctionExpression(node)) {
    return false;
  }

  const parent = node.parent;
  return Boolean(
    parent &&
      (ts.isVariableDeclaration(parent) ||
        ts.isPropertyDeclaration(parent) ||
        ts.isPropertyAssignment(parent) ||
        (ts.isBinaryExpression(parent) &&
          parent.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
          parent.right === node)) &&
      getFunctionName(ts, node, sourceFile),
  );
}

/** 找到真正承载 TSDoc 的声明节点，例如箭头函数外层的变量语句。 */
function getDocumentationTarget(ts, node) {
  if (ts.isArrowFunction(node) || ts.isFunctionExpression(node)) {
    if (ts.isVariableDeclaration(node.parent)) {
      const declarationList = node.parent.parent;
      if (declarationList && ts.isVariableDeclarationList(declarationList) && ts.isVariableStatement(declarationList.parent)) {
        return declarationList.parent;
      }
    }

    if (ts.isPropertyDeclaration(node.parent) || ts.isPropertyAssignment(node.parent)) {
      return node.parent;
    }

    if (
      ts.isBinaryExpression(node.parent) &&
      node.parent.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
      node.parent.right === node
    ) {
      return ts.isExpressionStatement(node.parent.parent) ? node.parent.parent : node.parent;
    }
  }

  return node;
}

/** 提取与声明紧邻的 TSDoc，避免把更早的普通块注释误算为方法注释。 */
function getDocumentation(source, target) {
  const prefix = source.slice(0, target.getStart());
  const match = prefix.match(/\/\*\*([\s\S]*?)\*\/\s*$/u);

  if (!match) {
    return null;
  }

  const raw = match[1]
    .split(/\r?\n/u)
    .map((line) => line.replace(/^\s*\*\s?/u, ""))
    .join("\n")
    .trim();
  const description = raw
    .split(/\r?\n/u)
    .filter((line) => !line.trimStart().startsWith("@"))
    .join(" ")
    .replace(/<[^>]+>/gu, " ")
    .trim();

  return {
    description,
    inherited: /@inheritDoc\b|\{@inheritDoc\}/iu.test(raw),
  };
}

/** 收集方法体内可作为功能阶段说明的独立注释。 */
function collectStageComments(ts, source, sourceFile, body, minLength) {
  const commentRanges = new Map();

  // 第一阶段沿语法节点收集并按起始位置去重所有候选注释。
  /** 从解析器已经确认的语法间隙提取注释，正确跨过模板字符串和正则字面量。 */
  function collectFromNode(node) {
    const ranges = [
      ...(ts.getLeadingCommentRanges(source, node.getFullStart()) ?? []),
      ...(ts.getTrailingCommentRanges(source, node.end) ?? []),
    ];

    for (const range of ranges) {
      if (range.pos > body.getStart(sourceFile) && range.end < body.end) {
        commentRanges.set(range.pos, range);
      }
    }

    for (const child of node.getChildren(sourceFile)) {
      collectFromNode(child);
    }
  }

  collectFromNode(body);
  // 第二阶段过滤文档注释、行尾注释和内容过短的非阶段说明。
  const comments = [];
  for (const range of commentRanges.values()) {
    const tokenStart = range.pos;
    const rawComment = source.slice(range.pos, range.end);
    const lineStart = Math.max(source.lastIndexOf("\n", tokenStart - 1) + 1, 0);
    const prefix = source.slice(lineStart, tokenStart);
    const commentText = rawComment
      .replace(/^\/\//u, "")
      .replace(/^\/\*/u, "")
      .replace(/\*\/$/u, "")
      .replace(/^\s*\*\s?/gmu, "")
      .trim();

    if (!rawComment.startsWith("/**") && /^\s*$/u.test(prefix) && meaningfulLength(commentText) >= minLength) {
      comments.push(tokenStart);
    }
  }

  return comments;
}

/** 创建包含统一字段的脚本语言诊断结果。 */
function createDiagnostic(relativePath, line, method, rule, message) {
  return {
    path: relativePath,
    line,
    language: "TypeScript/JavaScript/Vue",
    method,
    rule,
    message,
  };
}

/** 使用 TypeScript 语法树检查单个脚本文件的方法注释和长方法阶段注释。 */
function inspectScriptFile(ts, absolutePath, relativePath, options) {
  const originalSource = fs.readFileSync(absolutePath, "utf8");
  const source = path.extname(absolutePath).toLowerCase() === ".vue" ? extractVueScripts(originalSource) : originalSource;
  const extension = path.extname(absolutePath).toLowerCase();
  const scriptKind =
    extension === ".tsx"
      ? ts.ScriptKind.TSX
      : extension === ".jsx"
        ? ts.ScriptKind.JSX
        : extension === ".js" || extension === ".mjs" || extension === ".cjs"
          ? ts.ScriptKind.JS
          : ts.ScriptKind.TS;
  const sourceFile = ts.createSourceFile(absolutePath, source, ts.ScriptTarget.Latest, true, scriptKind);
  const diagnostics = [];

  // 语法错误会让后续覆盖率结果不可信，因此先单独报告解析失败。
  for (const diagnostic of sourceFile.parseDiagnostics ?? []) {
    const position = diagnostic.start ?? 0;
    const line = sourceFile.getLineAndCharacterOfPosition(position).line + 1;
    diagnostics.push(
      createDiagnostic(relativePath, line, "<parse>", "CQ004", ts.flattenDiagnosticMessageText(diagnostic.messageText, " ")),
    );
  }

  /** 递归访问声明，同时避免把匿名回调误当成必须写方法文档的具名方法。 */
  function visit(node) {
    // 第一阶段检查当前具名函数的方法文档和长方法阶段说明。
    if (isDocumentedFunctionNode(ts, node, sourceFile)) {
      const method = getFunctionName(ts, node, sourceFile);
      const line = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1;
      const documentation = getDocumentation(source, getDocumentationTarget(ts, node));

      if (!documentation) {
        diagnostics.push(createDiagnostic(relativePath, line, method, "CQ001", "缺少紧邻声明的 TSDoc 方法注释。"));
      } else if (!documentation.inherited && meaningfulLength(documentation.description) < options.minDescriptionLength) {
        diagnostics.push(
          createDiagnostic(
            relativePath,
            line,
            method,
            "CQ002",
            `方法注释有效文字不足 ${options.minDescriptionLength} 个，必须说明职责和业务意图。`,
          ),
        );
      }

      const body = node.body;
      if (body && ts.isBlock(body)) {
        const startLine = sourceFile.getLineAndCharacterOfPosition(body.getStart(sourceFile)).line;
        const endLine = sourceFile.getLineAndCharacterOfPosition(body.end).line;
        const bodyLines = endLine - startLine + 1;

        if (bodyLines > options.longMethodLines) {
          const stageComments = collectStageComments(ts, source, sourceFile, body, 6);
          const required = Math.max(options.minStageComments, Math.ceil(bodyLines / options.longMethodLines));

          if (stageComments.length < required) {
            diagnostics.push(
              createDiagnostic(
                relativePath,
                line,
                method,
                "CQ003",
                `方法体 ${bodyLines} 行，至少需要 ${required} 段功能阶段注释，当前只有 ${stageComments.length} 段。`,
              ),
            );
          }
        }
      }
    }

    // 第二阶段递归处理子节点，匿名回调只参与其内部具名函数的发现。
    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return diagnostics;
}

/** 运行全部脚本语言检查并以 JSON 返回给 PowerShell 包装器。 */
async function main() {
  const options = parseArguments(process.argv.slice(2));
  const payload = JSON.parse(fs.readFileSync(options.fileList, "utf8"));
  const ts = await loadTypeScript(payload.repoRoot);
  const diagnostics = [];

  for (const file of payload.files) {
    diagnostics.push(...inspectScriptFile(ts, file.absolutePath, file.relativePath, options));
  }

  process.stdout.write(JSON.stringify(diagnostics));
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 2;
});
