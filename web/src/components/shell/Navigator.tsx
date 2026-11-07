import { Activity, Archive, Bell, KeySquare, CircleCheck, CircleX, Clock, Database, FolderOpen, FolderSearch, HardDrive, History, KeyRound, LayoutTemplate, ListChecks, Network, ScrollText, ShieldCheck, ShieldX, TriangleAlert, Users } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { HostIcon } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { InventoryTree } from '@/components/inventory/InventoryTree';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { useI18n, type TKey } from '@/i18n';
import { cn } from '@/lib/utils';
import { guideTree } from '@/pages/guide/GuidePages';
import { useSession } from '@/session';
import { NAV_ITEMS, settingsHome, type NavItem } from './nav';

/** Icon-only main navigation (the labels are tooltips), above the tree of the current page. */
function NavIcons() {
  const { t } = useI18n();
  const { pathname } = useLocation();
  const { can } = useSession();
  // Planned sections have no page yet: they stay out of the icon row (the command palette still lists them).
  // Users and Audit live in the tree of Settings: the icon row keeps only the main areas.
  const visible = (n: NavItem) => !n.planned && !n.inSettings && (!n.permission || n.permission.some(can));
  return (
    <nav className="nav flex flex-wrap items-center gap-x-1.5 gap-y-1 border-b px-2 py-1.5" aria-label="Main">
      {NAV_ITEMS.filter(visible).map((n) => {
        // The class is a plain string: the tooltip trigger merges props into the link, and a className function
        // (NavLink's own way to mark the current section) would be turned into text, marking every icon as current.
        const current = n.match ? n.match.test(pathname) : n.end ? pathname === n.to : pathname === n.to || pathname.startsWith(n.to + '/');
        const to = n.to === '/settings' ? settingsHome(can) : n.to;
        return (
          <Tooltip key={n.to}>
            <TooltipTrigger asChild>
              <Link to={to} aria-label={t(n.label as TKey)} aria-current={current ? 'page' : undefined}
                className={cn('relative flex h-9 w-7 items-center justify-center rounded-md text-primary outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring/60 dark:text-white',
                  current && 'bg-primary/15 hover:bg-primary/20 dark:bg-white/15 dark:hover:bg-white/20')}>
                <n.icon className="size-[18px]" />
              </Link>
            </TooltipTrigger>
            <TooltipContent side="bottom">{t(n.label as TKey)}</TooltipContent>
          </Tooltip>
        );
      })}
    </nav>
  );
}

interface PageNode { key: string; label: string; icon: ReactNode; to: string; depth: number; badge?: ReactNode }

/** Canonical "path?query" so a tree node can be compared with the current location whatever the parameter order. */
function canonical(path: string): string {
  const [pathname, query = ''] = path.split('?');
  const params = [...new URLSearchParams(query).entries()].sort(([a], [b]) => a.localeCompare(b));
  return pathname + (params.length ? '?' + new URLSearchParams(params).toString() : '');
}

export function pageLink(pathname: string, params: Record<string, string | undefined>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v) p.set(k, v);
  const q = p.toString();
  return q ? `${pathname}?${q}` : pathname;
}

/** Simple navigation tree of a page (sections, hosts and their resources, filters): every node is a link. */
function PageTree({ title, nodes, onNavigate }: { title: string; nodes: PageNode[]; onNavigate?: () => void }) {
  const location = useLocation();
  const current = canonical(location.pathname + location.search);
  return (
    <div className="flex h-full min-h-0 flex-col bg-panel">
      <div className="flex h-10 items-center border-b px-3 text-xs font-semibold">{title}</div>
      <div role="tree" aria-label={title} className="min-h-0 flex-1 overflow-auto py-1 text-sm select-none">
        {nodes.map((n) => {
          const selected = canonical(n.to) === current;
          return (
            <Link key={n.key} to={n.to} role="treeitem" aria-level={n.depth + 1} aria-selected={selected} onClick={onNavigate}
              className={cn('tree-node mx-1 flex h-7 items-center gap-1.5 rounded-md pr-2 outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring/60',
                n.depth === 0 && 'font-medium', selected && 'bg-primary/10 font-medium text-primary hover:bg-primary/15')}
              style={{ paddingLeft: 8 + n.depth * 14 }}>
              {n.icon}
              <span className="min-w-0 flex-1 truncate">{n.label}</span>
              {n.badge}
            </Link>
          );
        })}
      </div>
    </div>
  );
}

const JOB_STATUS_ICONS: Record<string, ReactNode> = {
  Queued: <Clock className="size-4 text-muted-foreground" />,
  Running: <Activity className="size-4 text-info" />,
  Reconciling: <Activity className="size-4 text-warning" />,
  Succeeded: <CircleCheck className="size-4 text-success" />,
  Unverified: <CircleCheck className="size-4 text-warning" />,
  Failed: <CircleX className="size-4 text-destructive" />,
  Cancelled: <CircleX className="size-4 text-muted-foreground" />,
  Unknown: <CircleX className="size-4 text-warning" />,
};

/**
 * Left panel of every page (vCenter-like): the main navigation as icons and, below, a tree that follows the page —
 * hosts, folders and VMs for the inventory and the dashboard; hosts and their switches for Networks; hosts and their
 * disks/files for Storage; filters and sections for the other pages.
 */
export function Navigator({ onNavigate }: { onNavigate?: () => void }) {
  const { t } = useI18n();
  const { pathname } = useLocation();
  const inv = useInventory();
  const { state, can } = useSession();
  const count = (n: number) => <span className="text-[10px] text-muted-foreground tabular">{n}</span>;
  const muted = 'size-4 text-muted-foreground';

  let tree: ReactNode;
  if (pathname.startsWith('/networks')) {
    const nodes: PageNode[] = [{ key: 'all', label: t('net.all'), icon: <Network className={muted} />, to: '/networks', depth: 0 }];
    for (const h of inv.hosts) {
      nodes.push({ key: h.id, label: h.displayName, icon: <HostIcon connected={h.connected} access={h.access} />, to: pageLink('/networks', { host: h.id }), depth: 0, badge: count(h.virtualSwitches.length) });
      for (const s of h.virtualSwitches) nodes.push({ key: `${h.id}/${s}`, label: s, icon: <Network className={muted} />, to: pageLink('/networks', { host: h.id, switch: s }), depth: 1 });
    }
    tree = <PageTree title={t('nav.networks')} nodes={nodes} onNavigate={onNavigate} />;
  } else if (pathname.startsWith('/storage')) {
    const nodes: PageNode[] = [{ key: 'all', label: t('sto.tab.overview'), icon: <HardDrive className={muted} />, to: '/storage', depth: 0 }];
    for (const h of inv.hosts) {
      nodes.push({ key: h.id, label: h.displayName, icon: <HostIcon connected={h.connected} access={h.access} />, to: pageLink('/storage', { host: h.id }), depth: 0, badge: count(h.volumes.length) });
      nodes.push({ key: `${h.id}/disks`, label: t('host.virtualDisks'), icon: <HardDrive className={muted} />, to: pageLink('/storage', { host: h.id, view: 'disks' }), depth: 1 });
      if (h.canBrowseFiles) nodes.push({ key: `${h.id}/files`, label: t('sto.tab.files'), icon: <FolderOpen className={muted} />, to: pageLink('/storage', { tab: 'files', host: h.id }), depth: 1 });
    }
    tree = <PageTree title={t('nav.storage')} nodes={nodes} onNavigate={onNavigate} />;
  } else if (pathname.startsWith('/templates')) {
    const nodes: PageNode[] = [{ key: 'all', label: t('tpl.all'), icon: <LayoutTemplate className={muted} />, to: '/templates', depth: 0 }];
    for (const h of inv.hosts) nodes.push({ key: h.id, label: h.displayName, icon: <HostIcon connected={h.connected} access={h.access} />, to: pageLink('/templates', { host: h.id }), depth: 1 });
    tree = <PageTree title={t('nav.templates')} nodes={nodes} onNavigate={onNavigate} />;
  } else if (pathname.startsWith('/backup')) {
    // Backup area: its tabs, the runs and the restore points by status (the page reads ?tab= and ?status=).
    const tab = (x: string, extra: Record<string, string> = {}) => pageLink('/backup', { tab: x, ...extra });
    const nodes: PageNode[] = [
      { key: 'overview', label: t('bk.tab.overview'), icon: <Archive className={muted} />, to: '/backup', depth: 0 },
      { key: 'jobs', label: t('bk.tab.jobs'), icon: <ListChecks className={muted} />, to: tab('jobs'), depth: 0 },
      { key: 'repositories', label: t('bk.tab.repositories'), icon: <HardDrive className={muted} />, to: tab('repositories'), depth: 0 },
      { key: 'runs', label: t('bk.tab.runs'), icon: <History className={muted} />, to: tab('runs'), depth: 0 },
      { key: 'runs/Running', label: t('bk.runStatus.Running'), icon: <Activity className="size-4 text-info" />, to: tab('runs', { status: 'Running' }), depth: 1 },
      { key: 'runs/Success', label: t('bk.runStatus.Success'), icon: <CircleCheck className="size-4 text-success" />, to: tab('runs', { status: 'Success' }), depth: 1 },
      { key: 'runs/Warning', label: t('bk.runStatus.Warning'), icon: <TriangleAlert className="size-4 text-warning" />, to: tab('runs', { status: 'Warning' }), depth: 1 },
      { key: 'runs/Failed', label: t('bk.runStatus.Failed'), icon: <CircleX className="size-4 text-destructive" />, to: tab('runs', { status: 'Failed' }), depth: 1 },
      { key: 'points', label: t('bk.tab.points'), icon: <Database className={muted} />, to: tab('points'), depth: 0 },
      { key: 'points/Ok', label: t('bk.pointStatus.Ok'), icon: <CircleCheck className="size-4 text-success" />, to: tab('points', { status: 'Ok' }), depth: 1 },
      { key: 'points/Unverified', label: t('bk.pointStatus.Unverified'), icon: <TriangleAlert className="size-4 text-warning" />, to: tab('points', { status: 'Unverified' }), depth: 1 },
      { key: 'points/Failed', label: t('bk.pointStatus.Failed'), icon: <CircleX className="size-4 text-destructive" />, to: tab('points', { status: 'Failed' }), depth: 1 },
      { key: 'files', label: t('bk.tab.files'), icon: <FolderSearch className={muted} />, to: tab('files'), depth: 0 },
    ];
    tree = <PageTree title={t('nav.backup')} nodes={nodes} onNavigate={onNavigate} />;
  } else if (pathname.startsWith('/jobs')) {
    const nodes: PageNode[] = [{ key: 'all', label: t('common.all'), icon: <ListChecks className={muted} />, to: '/jobs', depth: 0 }];
    for (const s of Object.keys(JOB_STATUS_ICONS)) nodes.push({ key: s, label: t(('jobstatus.' + s) as TKey), icon: JOB_STATUS_ICONS[s], to: pageLink('/jobs', { status: s }), depth: 1 });
    tree = <PageTree title={t('nav.jobs')} nodes={nodes} onNavigate={onNavigate} />;
  } else if (/^\/(settings|users|audit)(\/|$)/.test(pathname)) {
    // Settings area: the portal settings, then users and roles, then the audit log with its outcome filters.
    const icons: Record<string, ReactNode> = {
      security: <ShieldCheck className={muted} />, entra: <KeyRound className={muted} />, backup: <HardDrive className={muted} />, system: <Activity className={muted} />,
      notifications: <Bell className={muted} />, apiTokens: <KeySquare className={muted} />,
    };
    const nodes: PageNode[] = [];
    if (can('settings.manage')) {
      for (const x of ['security', 'entra', 'backup', 'notifications', 'apiTokens', 'system']) {
        nodes.push({ key: x, label: t(('settings.tab.' + x) as TKey), icon: icons[x], to: x === 'security' ? '/settings' : pageLink('/settings', { tab: x }), depth: 0 });
      }
    }
    if (can('users.manage') || can('roles.manage')) nodes.push({ key: 'users', label: t('nav.users'), icon: <Users className={muted} />, to: '/users', depth: 0 });
    if (can('audit.read')) {
      nodes.push(
        { key: 'audit', label: t('nav.audit'), icon: <ScrollText className={muted} />, to: '/audit', depth: 0 },
        { key: 'audit/Success', label: t('audit.outcome.Success'), icon: <CircleCheck className="size-4 text-success" />, to: pageLink('/audit', { outcome: 'Success' }), depth: 1 },
        { key: 'audit/Failure', label: t('audit.outcome.Failure'), icon: <CircleX className="size-4 text-destructive" />, to: pageLink('/audit', { outcome: 'Failure' }), depth: 1 },
        { key: 'audit/Denied', label: t('audit.outcome.Denied'), icon: <ShieldX className="size-4 text-warning" />, to: pageLink('/audit', { outcome: 'Denied' }), depth: 1 },
      );
    }
    tree = <PageTree title={t('nav.settings')} onNavigate={onNavigate} nodes={nodes} />;
  } else if (pathname.startsWith('/guide')) {
    // Guide: the home, the user guides (everyone) and the administration pages (hosts.manage), as sections.
    tree = <PageTree title={t('nav.guide')} onNavigate={onNavigate} nodes={guideTree(can).map((n) => (
      { key: n.key, label: t(n.title), icon: <n.icon className={muted} />, to: n.to, depth: n.depth }))} />;
  } else {
    tree = <InventoryTree onNavigate={onNavigate} />;
  }

  return (
    <div className="navigator flex h-full min-h-0 flex-col bg-panel">
      <NavIcons />
      <div className="min-h-0 flex-1">{tree}</div>
      <footer className="sidebar-footer flex flex-wrap items-center gap-x-2 border-t px-3 py-1.5 text-[11px] text-muted-foreground">
        <span className="font-medium text-foreground/80">{t('app.name')} {state?.version ? `v${state.version}` : ''}</span>
        <span>© NEXQ S.r.l.</span>
        <a className="text-primary hover:underline" href="https://www.nexq.it" target="_blank" rel="noopener noreferrer">www.nexq.it</a>
      </footer>
    </div>
  );
}
