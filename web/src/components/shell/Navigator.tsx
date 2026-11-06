import { Activity, CircleCheck, CircleX, Clock, FileSearch, FolderOpen, HardDrive, KeyRound, ListChecks, Network, ShieldCheck, ShieldX } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link, NavLink, useLocation } from 'react-router-dom';
import { HostIcon } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { InventoryTree } from '@/components/inventory/InventoryTree';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { useI18n, type TKey } from '@/i18n';
import { cn } from '@/lib/utils';
import { GUIDE_PAGES } from '@/pages/guide/GuidePages';
import { useSession } from '@/session';
import { NAV_ITEMS, type NavItem } from './nav';

/** Icon-only main navigation (the labels are tooltips), above the tree of the current page. */
function NavIcons() {
  const { t } = useI18n();
  const { pathname } = useLocation();
  const { can } = useSession();
  // Planned sections have no page yet: they stay out of the icon row (the command palette still lists them).
  const visible = (n: NavItem) => !n.planned && (!n.permission || n.permission.some(can));
  return (
    <nav className="nav flex flex-wrap items-center gap-0.5 border-b px-1.5 py-1" aria-label="Main">
      {NAV_ITEMS.filter(visible).map((n) => (
        <Tooltip key={n.to}>
          <TooltipTrigger asChild>
            <NavLink to={n.to} end={n.end} aria-label={t(n.label as TKey)}
              className={({ isActive }) => cn('relative flex size-7 items-center justify-center rounded-md text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/60',
                (isActive || n.match?.test(pathname)) && 'bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary')}>
              <n.icon className="size-4" />
            </NavLink>
          </TooltipTrigger>
          <TooltipContent side="bottom">{t(n.label as TKey)}</TooltipContent>
        </Tooltip>
      ))}
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
  } else if (pathname.startsWith('/jobs')) {
    const nodes: PageNode[] = [{ key: 'all', label: t('common.all'), icon: <ListChecks className={muted} />, to: '/jobs', depth: 0 }];
    for (const s of Object.keys(JOB_STATUS_ICONS)) nodes.push({ key: s, label: t(('jobstatus.' + s) as TKey), icon: JOB_STATUS_ICONS[s], to: pageLink('/jobs', { status: s }), depth: 1 });
    tree = <PageTree title={t('nav.jobs')} nodes={nodes} onNavigate={onNavigate} />;
  } else if (pathname.startsWith('/audit')) {
    tree = <PageTree title={t('nav.audit')} onNavigate={onNavigate} nodes={[
      { key: 'all', label: t('audit.allOutcomes'), icon: <FileSearch className={muted} />, to: '/audit', depth: 0 },
      { key: 'Success', label: 'Success', icon: <CircleCheck className="size-4 text-success" />, to: pageLink('/audit', { outcome: 'Success' }), depth: 1 },
      { key: 'Failure', label: 'Failure', icon: <CircleX className="size-4 text-destructive" />, to: pageLink('/audit', { outcome: 'Failure' }), depth: 1 },
      { key: 'Denied', label: 'Denied', icon: <ShieldX className="size-4 text-warning" />, to: pageLink('/audit', { outcome: 'Denied' }), depth: 1 },
    ]} />;
  } else if (pathname.startsWith('/settings')) {
    const icons: Record<string, ReactNode> = {
      security: <ShieldCheck className={muted} />, entra: <KeyRound className={muted} />, backup: <HardDrive className={muted} />, system: <Activity className={muted} />,
    };
    tree = <PageTree title={t('nav.settings')} onNavigate={onNavigate} nodes={['security', 'entra', 'backup', 'system'].map((x) => (
      { key: x, label: t(('settings.tab.' + x) as TKey), icon: icons[x], to: x === 'security' ? '/settings' : pageLink('/settings', { tab: x }), depth: 0 }))} />;
  } else if (pathname.startsWith('/guide') && can('hosts.manage')) {
    tree = <PageTree title={t('nav.guide')} onNavigate={onNavigate} nodes={GUIDE_PAGES.map((p) => (
      { key: p.to, label: t(p.title), icon: <p.icon className={muted} />, to: p.to, depth: p.end ? 0 : 1 }))} />;
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
