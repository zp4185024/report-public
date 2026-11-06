import { useEffect, useMemo, useState } from 'react';
import { CheckCircle2, ClipboardList, LoaderCircle, PlugZap, Plus, Save, Trash2, X, Zap } from 'lucide-react';
import { dataAssetApi } from './api';
import { confirmDeleteMessage, deleteErrorMessage, pickResourceAfterDeletion } from './assetDeletion';
import { StatusToasts } from './StatusToasts';
import type {
  ExternalParameterFieldDefinition,
  ExternalParameterSourceDocument,
  ExternalRequestParameterDefinition,
} from './types';
import {
  currentUrlParameterValues,
  externalParameterDestinations,
  externalParameterTemplateKeys,
} from './externalParameterContext';

/**
 * 按 next Code 规则筛选 ExternalParameterSourcePage 数据，并返回满足条件的结果。
 * @param sources - sources 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function nextCode(sources: readonly ExternalParameterSourceDocument[]): string {
  let index = sources.length + 1;
  while (sources.some((source) => source.code === `external_${index}`)) index += 1;
  return `external_${index}`;
}

/**
 * 构建 ExternalParameterSourcePage 所需的 create Source 内容，并保证生成结果符合调用约定。
 * @param sources - sources 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function createSource(sources: readonly ExternalParameterSourceDocument[]): ExternalParameterSourceDocument {
  return {
    code: nextCode(sources),
    name: '新建接口参数源',
    baseUrl: 'http://127.0.0.1:48080/api/',
    endpoint: '',
    httpMethod: 'POST',
    requestBodyTemplate: '{}',
    headersTemplate: '{}',
    requestParameters: [],
    timeoutSeconds: 10,
    enabled: true,
    revision: 0,
    fields: [{ code: 'value', displayName: '返回值', dataType: 'string', responsePath: 'content.value' }],
  };
}

/**
 * 按照 ExternalParameterSourcePage 的业务约束完成 clone Source 数据合并或复制。
 * @param source - 本次转换或写入使用的数据对象。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function cloneSource(source: ExternalParameterSourceDocument): ExternalParameterSourceDocument {
  return {
    ...source,
    requestParameters: (source.requestParameters ?? []).map((parameter) => ({ ...parameter })),
    fields: source.fields.map((field) => ({ ...field })),
  };
}

/**
 * 负责 ExternalParameterSourcePage 的 context Keys 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param source - 本次转换或写入使用的数据对象。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function contextKeys(source: ExternalParameterSourceDocument | null): string[] {
  return source === null ? [] : externalParameterTemplateKeys(source);
}

/**
 * 按 External Parameter Source Page 规则筛选 ExternalParameterSourcePage 数据，并返回满足条件的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export default function ExternalParameterSourcePage() {
  const [sources, setSources] = useState<ExternalParameterSourceDocument[]>([]);
  const [draft, setDraft] = useState<ExternalParameterSourceDocument | null>(null);
  const [testContext, setTestContext] = useState<Record<string, string>>({});
  const [testValues, setTestValues] = useState<Record<string, string> | null>(null);
  const [testResultOpen, setTestResultOpen] = useState(false);
  const [testDialogOpen, setTestDialogOpen] = useState(false);
  const [loading, setLoading] = useState(true);
  // 第一阶段：构造 ExternalParameterSourcePage 当前阶段需要的中间数据和执行参数。
  const [saving, setSaving] = useState(false);
  // 第一阶段：构造 ExternalParameterSourcePage 当前阶段需要的中间数据和执行参数。
  const [testing, setTesting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  // 删除请求进行中标志；与保存、测试互斥，防止删除后仍提交旧草稿。
  const [deleting, setDeleting] = useState(false);

  useEffect(() => {
    let active = true;
    void dataAssetApi.externalParameterSources()
      .then((items) => {
        if (!active) return;
        setSources(items);
        setDraft(items[0] ? cloneSource(items[0]) : createSource([]));
      })
      .catch((exception) => {
        if (active) setError(exception instanceof Error ? exception.message : '接口参数源加载失败。');
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, []);

  const requiredContextKeys = useMemo(() => contextKeys(draft), [draft]);
  const templateContextKeys = useMemo(() => draft === null
    ? new Set<string>()
    : new Set(externalParameterTemplateKeys({
      ...draft,
      requestParameters: [],
    })), [draft]);
  const requiredKeys = useMemo(() => new Set([
    ...templateContextKeys,
    ...(draft?.requestParameters ?? [])
      .filter((parameter) => parameter.required)
      .map((parameter) => parameter.urlKey),
  ]), [draft, templateContextKeys]);
  // 第2阶段：构造 ExternalParameterSourcePage 当前阶段需要的中间数据和执行参数。
  // 第2阶段：构造 ExternalParameterSourcePage 当前阶段需要的中间数据和执行参数。
  const urlContext = useMemo(
    () => currentUrlParameterValues(requiredContextKeys),
    [requiredContextKeys],
  );

  /**
   * 更新 ExternalParameterSourcePage 的 update Field 状态，并同步相关业务数据。
   * @param index - 控制数据范围或位置的数值。
   * @param patch - patch 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const updateField = (index: number, patch: Partial<ExternalParameterFieldDefinition>) => {
    setDraft((current) => current === null ? current : {
      ...current,
      fields: current.fields.map((field, fieldIndex) => (
        fieldIndex === index ? { ...field, ...patch } : field
      )),
    });
    setTestValues(null);
  };

  /**
   * 更新 ExternalParameterSourcePage 的 update Request Parameter 状态，并同步相关业务数据。
   * @param index - 控制数据范围或位置的数值。
   * @param patch - patch 对应的业务输入；具体类型和空值范围以方法签名为准。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const updateRequestParameter = (
    index: number,
    patch: Partial<ExternalRequestParameterDefinition>,
  ) => {
    // 第3阶段：遍历 ExternalParameterSourcePage 当前数据集合，逐项转换并维护处理结果。
    setDraft((current) => current === null ? current : {
      ...current,
      requestParameters: current.requestParameters.map((parameter, parameterIndex) => (
        parameterIndex === index ? { ...parameter, ...patch } : parameter
      )),
    });
    setTestValues(null);
  };

  /**
   * 更新 ExternalParameterSourcePage 的 save 状态，并同步相关业务数据。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const save = async () => {
    if (draft === null) return;
    setSaving(true);
    setError(null);
    setStatus(null);
    try {
      const saved = await dataAssetApi.saveExternalParameterSource(draft);
      // 第3阶段：构造 ExternalParameterSourcePage 当前阶段需要的中间数据和执行参数。
      const next = [...sources.filter((source) => source.code !== saved.code), saved]
        .sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
      setSources(next);
      setDraft(cloneSource(saved));
      setStatus(`接口参数源已保存，当前修订号 ${saved.revision}`);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : '接口参数源保存失败。');
    } finally {
      setSaving(false);
    }
  };

  /**
   * 确认后删除当前选中的接口参数源；报表参数或数据集变量仍绑定时后端回传 409 清单并直接展示。
   */
  const deleteSource = async () => {
    if (draft === null || deleting) return;
    if (draft.revision === 0) {
      setError('尚未保存的接口参数源无需删除，直接切换资源即可。');
      return;
    }
    if (!window.confirm(confirmDeleteMessage('接口参数源', draft))) return;
    setDeleting(true);
    setError(null);
    setStatus(null);
    try {
      const deletedCode = draft.code;
      const deletedName = draft.name;
      await dataAssetApi.deleteExternalParameterSource(deletedCode);
      // 从本地列表移除并选中相邻资源，删空时进入新增草稿。
      const next = sources.filter((item) => item.code !== deletedCode);
      setSources(next);
      const replacement = pickResourceAfterDeletion(sources, deletedCode);
      setDraft(replacement ? cloneSource(replacement) : createSource(next));
      setTestValues(null);
      setTestResultOpen(false);
      setStatus(`接口参数源「${deletedName}」已删除`);
    } catch (exception) {
      setError(deleteErrorMessage(exception, '接口参数源'));
    } finally {
      setDeleting(false);
    }
  };

  /**
   * 遍历 ExternalParameterSourcePage 数据并执行 test 转换，生成后续流程需要的结果。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const test = async () => {
    // 第4阶段：检查 ExternalParameterSourcePage 当前条件，并按匹配分支执行对应处理。
    if (draft === null) return;
    if (draft.revision === 0) {
      setError('请先保存接口参数源，再进行接口测试。');
      return;
    }
    setTesting(true);
    setError(null);
    setStatus(null);
    setTestValues(null);
    try {
      const result = await dataAssetApi.testExternalParameterSource(draft.code, {
        context: Object.fromEntries(requiredContextKeys.map((key) => [
          key,
          testContext[key] || urlContext[key] || null,
        ])),
      });
      setTestValues(result.values);
      setStatus(`接口调用成功，已解析 ${Object.keys(result.values).length} 个字段`);
      setTestDialogOpen(false);
      setTestResultOpen(true);
      setTestContext({});
    } catch (exception) {
      // 第4阶段：根据 ExternalParameterSourcePage 当前分支转换输入数据，并保存该阶段产生的中间结果。
      setError(exception instanceof Error ? exception.message : '接口测试失败。');
    } finally {
      setTesting(false);
    }
  };

  /**
   * 结束 ExternalParameterSourcePage 的 close Test Dialog 交互并清理临时状态。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const closeTestDialog = () => {
    // 第5阶段：检查 ExternalParameterSourcePage 当前条件，并按匹配分支执行对应处理。
    if (testing) return;
    setTestDialogOpen(false);
    setTestContext({});
  };

  /**
   * 启动 ExternalParameterSourcePage 的 start Test 流程，并建立运行期间所需状态。
   * @returns 返回值与当前方法声明的类型及空值约束保持一致。
   */
  const startTest = () => {
    if (requiredContextKeys.length === 0) {
      void test();
      return;
    }
    setError(null);
    setStatus(null);
    setTestDialogOpen(true);
  };

  // 第5阶段：遍历 ExternalParameterSourcePage 当前数据集合，逐项转换并维护处理结果。
  // 第6阶段：遍历 ExternalParameterSourcePage 当前数据集合，逐项转换并维护处理结果。
  return (
    <main className="asset-page external-source-page">
      <header className="asset-header">
        <div className="asset-title-row"><div><span className="app-kicker">PARAMETER SOURCE</span><h1>接口参数管理</h1></div></div>
        <div className="runtime-status"><span className="status-dot" />后端受信任调用</div>
      </header>
      <section className="asset-workspace">
        <aside className="asset-list">
          <div className="asset-list-header"><strong>接口参数源</strong><button type="button" className="icon-button" title="新增接口参数源" aria-label="新增接口参数源" onClick={() => { setDraft(createSource(sources)); setTestValues(null); setError(null); setStatus(null); }}><Plus size={17} /></button></div>
          <div className="asset-list-scroll">{loading ? <div className="asset-empty"><LoaderCircle className="spin" size={20} />正在读取</div> : sources.map((source) => <button key={source.code} type="button" className={`asset-list-item ${draft?.code === source.code && draft.revision > 0 ? 'active' : ''}`} onClick={() => { setDraft(cloneSource(source)); setTestValues(null); setTestResultOpen(false); setError(null); setStatus(null); }}><PlugZap size={17} /><span><strong>{source.name}</strong><small>{source.code} · {source.fields.length} 个字段</small></span><i className={source.enabled ? 'enabled' : ''}>{source.enabled ? '启用' : '停用'}</i></button>)}</div>
        </aside>
        <div className="asset-editor">
          {draft === null ? <div className="asset-empty">请选择接口参数源</div> : <>
            <div className="asset-editor-header"><div><h2>{draft.revision === 0 ? '新增接口参数源' : draft.name}</h2><p>修订号 {draft.revision}</p></div><div><button type="button" className="button" disabled={testing || saving || deleting || draft.revision === 0} onClick={startTest}>{testing ? <LoaderCircle className="spin" size={16} /> : <Zap size={16} />}测试接口</button><button type="button" className="button button-primary" disabled={saving || testing || deleting} onClick={() => void save()}>{saving ? <LoaderCircle className="spin" size={16} /> : <Save size={16} />}保存</button><button type="button" className="button danger" disabled={saving || testing || deleting || draft.revision === 0} onClick={() => void deleteSource()}>{deleting ? <LoaderCircle className="spin" size={16} /> : <Trash2 size={16} />}删除</button></div></div>
            <div className="asset-form-grid external-source-form">
              <label><span>接口源编码</span><input disabled={draft.revision > 0} value={draft.code} onChange={(event) => setDraft({ ...draft, code: event.target.value })} /></label>
              <label><span>接口源名称</span><input value={draft.name} onChange={(event) => setDraft({ ...draft, name: event.target.value })} /></label>
              <label><span>请求方法</span><select value={draft.httpMethod} onChange={(event) => setDraft({ ...draft, httpMethod: event.target.value as 'GET' | 'POST' })}><option value="POST">POST</option><option value="GET">GET</option></select></label>
              <label className="toggle-field"><span>状态</span><input type="checkbox" checked={draft.enabled} onChange={(event) => setDraft({ ...draft, enabled: event.target.checked })} /></label>
              <label className="wide-field"><span>基础地址</span><input value={draft.baseUrl} onChange={(event) => setDraft({ ...draft, baseUrl: event.target.value })} /></label>
              <label className="wide-field"><span>接口路径</span><input value={draft.endpoint} onChange={(event) => setDraft({ ...draft, endpoint: event.target.value })} /></label>
              <label><span>超时（秒）</span><input type="number" min="1" max="120" value={draft.timeoutSeconds} onChange={(event) => setDraft({ ...draft, timeoutSeconds: Number(event.target.value) })} /></label>
            </div>
            <div className="external-template-grid">
              <label><span>请求体 JSON 模板</span><textarea disabled={draft.httpMethod === 'GET'} value={draft.requestBodyTemplate} onChange={(event) => setDraft({ ...draft, requestBodyTemplate: event.target.value })} /></label>
              <label><span>请求头 JSON 模板</span><textarea value={draft.headersTemplate} onChange={(event) => setDraft({ ...draft, headersTemplate: event.target.value })} /></label>
            </div>
            <div className="dataset-columns-header"><div><strong>URL 动态请求参数</strong><span>{draft.requestParameters.length} 个参数</span></div><button type="button" className="button" onClick={() => setDraft({ ...draft, requestParameters: [...draft.requestParameters, { name: `param${draft.requestParameters.length + 1}`, urlKey: `param${draft.requestParameters.length + 1}`, location: 'Query', required: false }] })}><Plus size={16} />新增参数</button></div>
            <div className="configuration-table-scroll external-request-parameter-scroll"><table className="configuration-table external-request-parameter-table"><thead><tr><th>页面 URL 参数名</th><th>发送位置</th><th>目标名称 / JSON 路径</th><th>必填</th><th>当前 URL 值</th><th>操作</th></tr></thead><tbody>{draft.requestParameters.length === 0 ? <tr><td colSpan={6} className="external-parameter-empty">未配置动态参数</td></tr> : draft.requestParameters.map((parameter, index) => <tr key={`${index}-${parameter.name}`}><td><input value={parameter.urlKey} onChange={(event) => updateRequestParameter(index, { urlKey: event.target.value })} /></td><td><select value={parameter.location} onChange={(event) => updateRequestParameter(index, { location: event.target.value as ExternalRequestParameterDefinition['location'] })}><option value="Query">查询参数</option><option value="Body">请求体</option><option value="Header">请求头</option></select></td><td><input value={parameter.name} placeholder={parameter.location === 'Body' ? '如 content.token' : '如 token'} onChange={(event) => updateRequestParameter(index, { name: event.target.value })} /></td><td><input type="checkbox" checked={parameter.required} onChange={(event) => updateRequestParameter(index, { required: event.target.checked })} /></td><td><code>{urlContext[parameter.urlKey] ? '已获取' : '未传入'}</code></td><td><button type="button" className="table-icon danger" title="删除请求参数" aria-label={`删除请求参数 ${parameter.name}`} onClick={() => setDraft({ ...draft, requestParameters: draft.requestParameters.filter((_, parameterIndex) => parameterIndex !== index) })}><Trash2 size={15} /></button></td></tr>)}</tbody></table></div>
            <div className="dataset-columns-header"><div><strong>返回字段映射</strong><span>{draft.fields.length} 个字段</span></div><button type="button" className="button" onClick={() => setDraft({ ...draft, fields: [...draft.fields, { code: `field${draft.fields.length + 1}`, displayName: '新字段', dataType: 'string', responsePath: 'content.value' }] })}><Plus size={16} />新增字段</button></div>
            <div className="configuration-table-scroll external-field-scroll"><table className="configuration-table external-field-table"><thead><tr><th>字段编码</th><th>显示名称</th><th>数据类型</th><th>响应 JSON 路径</th><th>测试值</th><th>操作</th></tr></thead><tbody>{draft.fields.map((field, index) => <tr key={`${index}-${field.code}`}><td><input value={field.code} onChange={(event) => updateField(index, { code: event.target.value })} /></td><td><input value={field.displayName} onChange={(event) => updateField(index, { displayName: event.target.value })} /></td><td><select value={field.dataType} onChange={(event) => updateField(index, { dataType: event.target.value })}><option value="string">文本</option><option value="int">整数</option><option value="long">长整数</option><option value="decimal">小数</option><option value="date">日期</option><option value="datetime">日期时间</option><option value="bool">布尔值</option></select></td><td><input value={field.responsePath} onChange={(event) => updateField(index, { responsePath: event.target.value })} /></td><td title={testValues?.[field.code] ?? ''}>{testValues === null ? '-' : testValues[field.code] || '空'}</td><td><button type="button" className="table-icon danger" title="删除字段" aria-label={`删除字段 ${field.displayName}`} disabled={draft.fields.length <= 1} onClick={() => setDraft({ ...draft, fields: draft.fields.filter((_, fieldIndex) => fieldIndex !== index) })}><Trash2 size={15} /></button></td></tr>)}</tbody></table></div>
          </>}
        </div>
      </section>
      {testDialogOpen && draft !== null && <div className="external-test-dialog-backdrop" role="presentation"><form className="external-test-dialog" role="dialog" aria-modal="true" aria-labelledby="external-test-dialog-title" onSubmit={(event) => { event.preventDefault(); void test(); }}><header><div><h2 id="external-test-dialog-title"><Zap size={18} />测试接口</h2><p>{draft.name} · {draft.httpMethod} · {requiredContextKeys.length} 个动态参数</p></div><button type="button" className="icon-button" title="关闭" aria-label="关闭接口测试" disabled={testing} onClick={closeTestDialog}><X size={18} /></button></header><div className="external-test-dialog-body">{requiredContextKeys.map((key) => <label key={key}><span className="external-test-parameter-heading"><strong>{key}</strong><i>{requiredKeys.has(key) ? '必填' : '可选'}</i></span><small>{externalParameterDestinations(draft, key).join('；') || 'JSON 模板变量'}</small><input autoFocus={key === requiredContextKeys[0]} aria-label={key} type="text" autoComplete="off" placeholder={urlContext[key] ? `当前 URL 已传入：${urlContext[key]}` : '输入本次测试值'} value={testContext[key] ?? ''} onChange={(event) => setTestContext({ ...testContext, [key]: event.target.value })} /></label>)}</div><footer><button type="button" className="button" disabled={testing} onClick={closeTestDialog}>取消</button><button type="submit" className="button button-primary" disabled={testing}>{testing ? <LoaderCircle className="spin" size={16} /> : <Zap size={16} />}调用接口</button></footer></form></div>}
      {testResultOpen && draft !== null && testValues !== null && <div className="external-test-dialog-backdrop" role="presentation"><section className="external-test-result-dialog" role="dialog" aria-modal="true" aria-labelledby="external-test-result-title"><header><div><h2 id="external-test-result-title"><CheckCircle2 size={19} />接口测试结果</h2><p>{draft.name} · 调用成功 · {Object.keys(testValues).length} 个字段</p></div><button type="button" className="icon-button" title="关闭" aria-label="关闭接口测试结果" onClick={() => setTestResultOpen(false)}><X size={18} /></button></header><div className="external-test-result-summary"><CheckCircle2 size={17} /><span>接口已正常返回，以下是本次解析出的实际值。</span></div><div className="external-test-result-body"><table className="external-test-result-table"><thead><tr><th>字段编码</th><th>显示名称</th><th>响应 JSON 路径</th><th>返回值</th></tr></thead><tbody>{draft.fields.map((field) => <tr key={field.code}><td><code>{field.code}</code></td><td>{field.displayName}</td><td><code>{field.responsePath}</code></td><td className="external-test-result-value">{testValues[field.code] || <span className="external-test-result-empty">空</span>}</td></tr>)}</tbody></table></div><footer><button type="button" className="button" onClick={() => setTestResultOpen(false)}><ClipboardList size={16} />返回参数配置</button></footer></section></div>}
      <StatusToasts error={error} success={status} onErrorClose={() => setError(null)} onSuccessClose={() => setStatus(null)} />
    </main>
  );
}
