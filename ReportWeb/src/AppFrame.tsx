import type { ReactNode } from 'react';
import { Database, FileSpreadsheet, PlugZap, Server } from 'lucide-react';

export type ManagementSection = 'reports' | 'data-sources' | 'datasets' | 'external-parameters';

interface AppFrameProps {
  active: ManagementSection;
  children: ReactNode;
}

const menuItems = [
  { key: 'reports', href: '/reports', label: '模板列表', icon: FileSpreadsheet },
  { key: 'data-sources', href: '/data-sources', label: '数据源', icon: Server },
  { key: 'datasets', href: '/datasets', label: '数据集', icon: Database },
  { key: 'external-parameters', href: '/external-parameters', label: '接口参数', icon: PlugZap },
] as const;

function preservePageContext(href: string): string {
  return window.location.search.length > 0
    ? `${href}${window.location.search}`
    : href;
}

export default function AppFrame({ active, children }: AppFrameProps) {
  return (
    <div className="management-shell">
      <aside className="management-sidebar">
        <a className="management-brand" href={preservePageContext('/reports')} aria-label="返回模板列表">
          <span>HD</span>
          <strong>报表中心</strong>
        </a>
        <nav className="management-nav" aria-label="报表中心菜单">
          {menuItems.map((item) => {
            const Icon = item.icon;
            return (
              <a
                key={item.key}
                className={active === item.key ? 'active' : ''}
                href={preservePageContext(item.href)}
                aria-current={active === item.key ? 'page' : undefined}
              >
                <Icon size={18} />
                <span>{item.label}</span>
              </a>
            );
          })}
        </nav>
      </aside>
      <div className="management-content">{children}</div>
    </div>
  );
}
