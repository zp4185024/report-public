import { useMemo } from 'react';
import CodeMirror, { EditorView } from '@uiw/react-codemirror';
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
}

const DIALECTS: Record<DataSourceDbType, SQLDialect> = {
  SqlServer: MSSQL,
  Oracle: PLSQL,
  MySql: MySQL,
  PostgreSQL,
};

export default function SqlEditor({ value, dbType, onChange }: SqlEditorProps) {
  const extensions = useMemo(() => [
    sql({ dialect: DIALECTS[dbType] ?? StandardSQL, upperCaseKeywords: true }),
    EditorView.lineWrapping,
    EditorView.contentAttributes.of({ 'aria-label': '参数化只读 SQL' }),
  ], [dbType]);

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
