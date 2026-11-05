import { Archive, BookOpen, HardDrive, LayoutDashboard, Layers, ListChecks, Monitor, Network, ScrollText, Server, Settings, Users, type LucideIcon } from 'lucide-react';

export interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  section: 'main' | 'ops' | 'admin';
  /** Visible when the user holds at least one of these global permissions (UI hint; the server authorizes). */
  permission?: string[];
  planned?: boolean;
  end?: boolean;
}

export const NAV_ITEMS: NavItem[] = [
  { to: '/', label: 'nav.dashboard', icon: LayoutDashboard, section: 'main', end: true },
  { to: '/inventory', label: 'nav.inventory', icon: Layers, section: 'main' },
  { to: '/vms', label: 'nav.vms', icon: Monitor, section: 'main' },
  { to: '/hosts', label: 'nav.hosts', icon: Server, section: 'main' },
  { to: '/networks', label: 'nav.networks', icon: Network, section: 'main' },
  { to: '/storage', label: 'nav.storage', icon: HardDrive, section: 'main' },
  { to: '/jobs', label: 'nav.jobs', icon: ListChecks, section: 'ops' },
  { to: '/guide', label: 'nav.guide', icon: BookOpen, section: 'ops', permission: ['hosts.manage'] },
  { to: '/backup', label: 'nav.backup', icon: Archive, section: 'ops', planned: true },
  { to: '/templates', label: 'nav.templates', icon: Layers, section: 'ops', planned: true },
  { to: '/users', label: 'nav.users', icon: Users, section: 'admin', permission: ['users.manage', 'roles.manage'] },
  { to: '/audit', label: 'nav.audit', icon: ScrollText, section: 'admin', permission: ['audit.read'] },
  { to: '/settings', label: 'nav.settings', icon: Settings, section: 'admin', permission: ['settings.manage'] },
];
