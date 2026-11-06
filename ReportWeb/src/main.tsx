import { createRoot } from 'react-dom/client';
import App from './App';
import AppFrame from './AppFrame';
import AiSettingsPage from './AiSettingsPage';
import DataAssetPage from './DataAssetPage';
import DataSyncPackageImportPage from './DataSyncPackageImportPage';
import DataSyncTaskPage from './DataSyncTaskPage';
import DatasetPreviewPage from './DatasetPreviewPage';
import ReportListPage from './ReportListPage';
import ExternalParameterSourcePage from './ExternalParameterSourcePage';
import JnpfMigrationPage from './JnpfMigrationPage';
import { aiApi, reportCenterFeaturesApi } from './api';
import type { ReportCenterFeatures } from './types';
import './app.css';

// 入口只负责挂载 React；Univer/Worker 的重量级初始化延迟到模板数据加载完成后的宿主组件中。
const path = window.location.pathname.replace(/\/$/, '') || '/';

/**
 * 根据 main 当前状态渲染 route 界面内容，并绑定所需交互。
 * @param features - 服务端功能开关。
 * @param aiEnabled - AI 助手开关（来自 GET /ai/settings），控制 AI 设置菜单与浮钮渲染。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function route(features: ReportCenterFeatures, aiEnabled: boolean) {
  if (path.startsWith('/share/reports/')) {
    const reportCode = decodeURIComponent(path.slice('/share/reports/'.length));
    return <App reportCode={reportCode} shared />;
  }
  if (path === '/data-assets/preview') {
    return <AppFrame active="datasets" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><DatasetPreviewPage /></AppFrame>;
  }
  if (path === '/data-sources' || path === '/data-assets') {
    return <AppFrame active="data-sources" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><DataAssetPage initialKind="data-sources" /></AppFrame>;
  }
  if (path === '/datasets') {
    return <AppFrame active="datasets" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><DataAssetPage initialKind="datasets" /></AppFrame>;
  }
  if (path === '/external-parameters') {
    return <AppFrame active="external-parameters" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><ExternalParameterSourcePage /></AppFrame>;
  }
  if (path === '/data-sync-tasks') {
    return <AppFrame active="data-sync-tasks" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><DataSyncTaskPage aiAssistantEnabled={aiEnabled} /></AppFrame>;
  }
  if (path === '/data-sync-package-import') {
    return <AppFrame active="data-sync-package-import" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><DataSyncPackageImportPage /></AppFrame>;
  }
  if (path === '/jnpf-migration') {
    if (!features.jnpfMigrationMenuEnabled) {
      return <AppFrame active="reports" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><ReportListPage aiAssistantEnabled={aiEnabled} /></AppFrame>;
    }
    return <AppFrame active="jnpf-migration" jnpfMigrationMenuEnabled><JnpfMigrationPage /></AppFrame>;
  }
  if (path === '/ai-settings') {
    // AI 关闭时按零感知原则回退到模板列表，菜单入口同样由开关控制不渲染。
    if (!aiEnabled) {
      return <AppFrame active="reports" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled}><ReportListPage /></AppFrame>;
    }
    return <AppFrame active="ai-settings" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled><AiSettingsPage /></AppFrame>;
  }
  // 其余路径按工作台主路由匹配：AI 开关只影响菜单显隐与浮钮渲染，不改变页面行为。
  if (path.startsWith('/reports/')) {
    const reportCode = decodeURIComponent(path.slice('/reports/'.length));
    return <AppFrame active="reports" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><App reportCode={reportCode} /></AppFrame>;
  }
  return <AppFrame active="reports" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled} aiSettingsEnabled={aiEnabled}><ReportListPage aiAssistantEnabled={aiEnabled} /></AppFrame>;
}

/** 启动报表工作台并读取服务端功能开关与 AI 开关，确保菜单、浮钮与后端配置保持一致。 */
async function start() {
  let features: ReportCenterFeatures = { jnpfMigrationMenuEnabled: false };
  let aiEnabled = false;
  try {
    features = await reportCenterFeaturesApi.get();
  } catch {
    // 功能配置读取失败时保持最小权限：隐藏可选迁移菜单。
  }
  try {
    // AI 开关独立于功能配置：读取失败或 AI 关闭时按关闭处理，其余功能零感知。
    const settings = await aiApi.getSettings();
    aiEnabled = settings.isEnabled;
  } catch {
    // AI 配置读取失败按关闭处理（表未建/AI_DISABLED 均落入此处）。
  }
  createRoot(document.getElementById('root')!).render(route(features, aiEnabled));
}

void start();
