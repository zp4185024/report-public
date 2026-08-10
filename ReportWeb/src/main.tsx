import { createRoot } from 'react-dom/client';
import App from './App';
import DataAssetPage from './DataAssetPage';
import './app.css';

// 入口只负责挂载 React；Univer/Worker 的重量级初始化延迟到模板数据加载完成后的宿主组件中。
createRoot(document.getElementById('root')!).render(
  window.location.pathname.replace(/\/$/, '') === '/data-assets' ? <DataAssetPage /> : <App />,
);
