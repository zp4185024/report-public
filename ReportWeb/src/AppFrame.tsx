import type { ReactNode } from 'react';
import { Database, FileSpreadsheet, Server } from 'lucide-react';

export type ManagementSection = 'reports' | 'data-sources' | 'datasets';

interface AppFrameProps {
  active: ManagementSection;
  children: ReactNode;
}

const menuItems = [
  { key: 'reports', href: '/reports', label: '模板列表', icon: FileSpreadsheet },
  { key: 'data-sources', href: '/data-sources', label: '数据源', icon: Server },
  { key: 'datasets', href: '/datasets', label: '数据集', icon: Database },
] as const;

export default function AppFrame({ active, children }: AppFrameProps) {
  return (
    <div className="management-shell">
      <aside className="management-sidebar">
        <a className="management-brand" href="/reports" aria-label="返回模板列表">
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
                href={item.href}
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
