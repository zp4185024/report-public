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

function parameterHighlightExtension(
  parameters: readonly SqlParameterPresentation[],
): Extension {
  return ViewPlugin.fromClass(class {
    decorations: DecorationSet;

    constructor(view: EditorView) {
      this.decorations = parameterDecorations(view.state.doc.toString(), parameters);
    }

    update(update: ViewUpdate) {
      if (update.docChanged) {
        this.decorations = parameterDecorations(update.state.doc.toString(), parameters);
      }
    }
  }, { decorations: (plugin) => plugin.decorations });
}

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
