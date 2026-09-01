import type { ReactNode } from 'react';
import { ArrowRightLeft, Database, FileSpreadsheet, PlugZap, Server } from 'lucide-react';

export type ManagementSection = 'reports' | 'data-sources' | 'datasets' | 'external-parameters' | 'jnpf-migration';

interface AppFrameProps {
  active: ManagementSection;
  children: ReactNode;
}

const menuItems = [
  { key: 'reports', href: '/reports', label: '模板列表', icon: FileSpreadsheet },
  { key: 'data-sources', href: '/data-sources', label: '数据源', icon: Server },
  { key: 'datasets', href: '/datasets', label: '数据集', icon: Database },
  { key: 'external-parameters', href: '/external-parameters', label: '接口参数', icon: PlugZap },
  { key: 'jnpf-migration', href: '/jnpf-migration', label: 'JNPF迁移', icon: ArrowRightLeft },
] as const;

/**
 * 负责 AppFrame 的 preserve Page Context 处理：读取该步骤所需数据，完成对应状态变更并返回结果。
 * @param href - href 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function preservePageContext(href: string): string {
  return window.location.search.length > 0
    ? `${href}${window.location.search}`
    : href;
}

/**
 * 遍历 AppFrame 数据并执行 App Frame 转换，生成后续流程需要的结果。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
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
