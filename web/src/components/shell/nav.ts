import { Archive, BookOpen, HardDrive, LayoutDashboard, LayoutTemplate, Layers, ListChecks, Network, ScrollText, Settings, Users, type LucideIcon } from 'lucide-react';

export interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  section: 'main' | 'ops' | 'admin';
  /** Visible when the user holds at least one of these global permissions (UI hint; the server authorizes). */
  permission?: string[];
  planned?: boolean;
  end?: boolean;
  /** Also active on these routes (e.g. Inventory for hosts, VMs and groups). */
  match?: RegExp;
  /** Reached from the tree of Settings, not from the icon row (still listed in the command palette). */
  inSettings?: boolean;
}

/** Global permissions that open some page of the Settings area (settings, users and roles, audit). */
export const SETTINGS_AREA_PERMISSIONS = ['settings.manage', 'users.manage', 'roles.manage', 'audit.read'];

export const NAV_ITEMS: NavItem[] = [
  { to: '/', label: 'nav.dashboard', icon: LayoutDashboard, section: 'main', end: true },
  // Hosts, groups and VMs all live in the inventory (tree + views), as in vCenter.
  { to: '/inventory', label: 'nav.inventory', icon: Layers, section: 'main', match: /^\/(inventory|vms|hosts|groups)(\/|$)/ },
  { to: '/networks', label: 'nav.networks', icon: Network, section: 'main' },
  { to: '/storage', label: 'nav.storage', icon: HardDrive, section: 'main' },
  { to: '/templates', label: 'nav.templates', icon: LayoutTemplate, section: 'main' },
  { to: '/backup', label: 'nav.backup', icon: Archive, section: 'ops', match: /^\/backup(\/|$)/ },
  { to: '/jobs', label: 'nav.jobs', icon: ListChecks, section: 'ops' },
  // Open to everyone: topic guides for all users; the administration pages inside check hosts.manage.
  { to: '/guide', label: 'nav.guide', icon: BookOpen, section: 'ops' },
  { to: '/users', label: 'nav.users', icon: Users, section: 'admin', permission: ['users.manage', 'roles.manage'], inSettings: true },
  { to: '/audit', label: 'nav.audit', icon: ScrollText, section: 'admin', permission: ['audit.read'], inSettings: true },
  { to: '/settings', label: 'nav.settings', icon: Settings, section: 'admin', permission: SETTINGS_AREA_PERMISSIONS, match: /^\/(settings|users|audit)(\/|$)/ },
];

/** First page of the Settings area the user can open (Settings itself needs settings.manage). */
export function settingsHome(can: (p: string) => boolean): string {
  if (can('settings.manage')) return '/settings';
  if (can('users.manage') || can('roles.manage')) return '/users';
  return '/audit';
}
