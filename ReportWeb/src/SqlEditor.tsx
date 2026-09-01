import { useMemo } from 'react';
import CodeMirror, {
  Decoration,
  EditorView,
  ViewPlugin,
  type DecorationSet,
  type Extension,
  type ViewUpdate,
} from '@uiw/react-codemirror';
import {
  MSSQL,
  MySQL,
  PLSQL,
  PostgreSQL,
  sql,
  StandardSQL,
  type SQLDialect,
} from '@codemirror/lang-sql';
import type { DataSourceDbType } from './types';

interface SqlEditorProps {
  value: string;
  dbType: DataSourceDbType;
  onChange: (value: string) => void;
  parameters?: readonly SqlParameterPresentation[];
}

export interface SqlParameterPresentation {
  code: string;
  category: 'form' | 'system' | 'external';
}

const DIALECTS: Record<DataSourceDbType, SQLDialect> = {
  SqlServer: MSSQL,
  Oracle: PLSQL,
  MySql: MySQL,
  PostgreSQL,
};

/**
 * 遍历 SqlEditor 数据并执行 parameter Decorations 转换，生成后续流程需要的结果。
 * @param text - 需要读取、校验或转换的业务值。
 * @param parameters - parameters 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function parameterDecorations(
  text: string,
  parameters: readonly SqlParameterPresentation[],
): DecorationSet {
  const categoryByCode = new Map(parameters.map((parameter) => [
    parameter.code.toLowerCase(),
    parameter.category,
  ]));
  const ranges = [...text.matchAll(/(?<!@)@([A-Za-z][A-Za-z0-9_]{0,63})/g)]
    .map((match) => {
      const category = categoryByCode.get(match[1].toLowerCase()) ?? 'form';
      const from = match.index ?? 0;
      return Decoration.mark({ class: `cm-sql-parameter cm-sql-parameter-${category}` })
        .range(from, from + match[0].length);
    });
  return Decoration.set(ranges, true);
}

/**
 * 负责 SqlEditor 的 parameter Highlight Extension 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param parameters - parameters 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function parameterHighlightExtension(
  parameters: readonly SqlParameterPresentation[],
): Extension {
  return ViewPlugin.fromClass(class {
    decorations: DecorationSet;

    /**
     * 初始化 SqlEditor 实例，保存依赖并建立组件所需的初始状态。
     * @param view - view 对应的业务输入；具体类型和空值范围以方法签名为准。
     */
    constructor(view: EditorView) {
      this.decorations = parameterDecorations(view.state.doc.toString(), parameters);
    }

    /**
     * 更新 SqlEditor 的 update 状态，并同步相关业务数据。
     * @param update - update 对应的业务输入；具体类型和空值范围以方法签名为准。
     * @returns 返回值与当前方法声明的类型及空值约束保持一致。
     */
    update(update: ViewUpdate) {
      if (update.docChanged) {
        this.decorations = parameterDecorations(update.state.doc.toString(), parameters);
      }
    }
  }, {
    /** 返回当前插件实例维护的 SQL 参数高亮集合。 */
    decorations: (plugin) => plugin.decorations,
  });
}

/**
 * 负责 SqlEditor 的 Sql Editor 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export default function SqlEditor({ value, dbType, onChange, parameters = [] }: SqlEditorProps) {
  const extensions = useMemo(() => [
    sql({ dialect: DIALECTS[dbType] ?? StandardSQL, upperCaseKeywords: true }),
    EditorView.lineWrapping,
    EditorView.contentAttributes.of({ 'aria-label': '参数化只读 SQL' }),
    parameterHighlightExtension(parameters),
  ], [dbType, parameters]);

  return (
    <CodeMirror
      className="asset-sql-editor"
      value={value}
      height="240px"
      theme="light"
      extensions={extensions}
      basicSetup
      indentWithTab
      placeholder="SELECT ..."
      onChange={onChange}
    />
  );
}
