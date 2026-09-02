import { createRoot } from 'react-dom/client';
import App from './App';
import AppFrame from './AppFrame';
import DataAssetPage from './DataAssetPage';
import DatasetPreviewPage from './DatasetPreviewPage';
import ReportListPage from './ReportListPage';
import ExternalParameterSourcePage from './ExternalParameterSourcePage';
import JnpfMigrationPage from './JnpfMigrationPage';
import { reportCenterFeaturesApi } from './api';
import type { ReportCenterFeatures } from './types';
import './app.css';

// 入口只负责挂载 React；Univer/Worker 的重量级初始化延迟到模板数据加载完成后的宿主组件中。
const path = window.location.pathname.replace(/\/$/, '') || '/';

/**
 * 根据 main 当前状态渲染 route 界面内容，并绑定所需交互。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function route(features: ReportCenterFeatures) {
  if (path.startsWith('/share/reports/')) {
    const reportCode = decodeURIComponent(path.slice('/share/reports/'.length));
    return <App reportCode={reportCode} shared />;
  }
  if (path === '/data-assets/preview') {
    return <AppFrame active="datasets" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled}><DatasetPreviewPage /></AppFrame>;
  }
  if (path === '/data-sources' || path === '/data-assets') {
    return <AppFrame active="data-sources" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled}><DataAssetPage initialKind="data-sources" /></AppFrame>;
  }
  if (path === '/datasets') {
    return <AppFrame active="datasets" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled}><DataAssetPage initialKind="datasets" /></AppFrame>;
  }
  if (path === '/external-parameters') {
    return <AppFrame active="external-parameters" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled}><ExternalParameterSourcePage /></AppFrame>;
  }
  if (path === '/jnpf-migration') {
    if (!features.jnpfMigrationMenuEnabled) {
      return <AppFrame active="reports"><ReportListPage /></AppFrame>;
    }
    return <AppFrame active="jnpf-migration" jnpfMigrationMenuEnabled><JnpfMigrationPage /></AppFrame>;
  }
  if (path.startsWith('/reports/')) {
    const reportCode = decodeURIComponent(path.slice('/reports/'.length));
    return <AppFrame active="reports" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled}><App reportCode={reportCode} /></AppFrame>;
  }
  return <AppFrame active="reports" jnpfMigrationMenuEnabled={features.jnpfMigrationMenuEnabled}><ReportListPage /></AppFrame>;
}

/** 启动报表工作台并读取服务端功能开关，确保菜单与后端配置保持一致。 */
async function start() {
  let features: ReportCenterFeatures = { jnpfMigrationMenuEnabled: false };
  try {
    features = await reportCenterFeaturesApi.get();
  } catch {
    // 功能配置读取失败时保持最小权限：隐藏可选迁移菜单。
  }
  createRoot(document.getElementById('root')!).render(route(features));
}

void start();
