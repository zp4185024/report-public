import { createRoot } from 'react-dom/client';
import App from './App';
import AppFrame from './AppFrame';
import DataAssetPage from './DataAssetPage';
import DatasetPreviewPage from './DatasetPreviewPage';
import ReportListPage from './ReportListPage';
import './app.css';

// 入口只负责挂载 React；Univer/Worker 的重量级初始化延迟到模板数据加载完成后的宿主组件中。
const path = window.location.pathname.replace(/\/$/, '') || '/';

function route() {
  if (path === '/data-assets/preview') {
    return <AppFrame active="datasets"><DatasetPreviewPage /></AppFrame>;
  }
  if (path === '/data-sources' || path === '/data-assets') {
    return <AppFrame active="data-sources"><DataAssetPage initialKind="data-sources" /></AppFrame>;
  }
  if (path === '/datasets') {
    return <AppFrame active="datasets"><DataAssetPage initialKind="datasets" /></AppFrame>;
  }
  if (path.startsWith('/reports/')) {
    const reportCode = decodeURIComponent(path.slice('/reports/'.length));
    return <AppFrame active="reports"><App reportCode={reportCode} /></AppFrame>;
  }
  return <AppFrame active="reports"><ReportListPage /></AppFrame>;
}

createRoot(document.getElementById('root')!).render(route());
