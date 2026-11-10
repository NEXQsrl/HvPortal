import { Boxes, ChevronRight, CircleAlert, TriangleAlert, ChevronsDownUp, FolderPlus, Monitor, Plus, Search, Server, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type DragEvent, type KeyboardEvent, type ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import type { Group } from '@/api/ops-types';
import type { Vm } from '@/api/types';
import type { Action } from '@/components/actions';
import { ContextActions } from '@/components/actions';
import { GroupIcon, HostIcon, VmIcon } from '@/components/common';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { ContextMenu, ContextMenuContent, ContextMenuTrigger } from '@/components/ui/context-menu';
import { Input } from '@/components/ui/input';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { useI18n } from '@/i18n';
import { useLocalStorage } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';
import { useInventory } from './InventoryProvider';
import type { ClusterIssue } from '@/api/cluster-types';
import { useIssueText } from '@/components/ops/ClusterDialogs';

/** Red (critical) or amber (warning, unknown) icon when a cluster or a node has problems; nothing otherwise. */
function problemIcon(issues: ClusterIssue[]) {
  if (issues.length === 0) return null;
  return issues.some((i) => i.level === 'Critical')
    ? <CircleAlert className="size-3.5 text-destructive" aria-label="!" />
    : <TriangleAlert className="size-3.5 text-warning" aria-label="!" />;
}

interface Node {
  key: string;
  /** hostsRoot / groupsRoot / allVms are the three top-level sections. */
  kind: 'hostsRoot' | 'groupsRoot' | 'allVms' | 'cluster' | 'host' | 'group' | 'vm' | 'ungrouped';
  label: string;
  depth: number;
  hasChildren: boolean;
  path: string | null;
  vm?: Vm;
  group?: Group;
  /** Group the VM node sits in (groups view). */
  parentGroupId?: string;
  badge?: ReactNode;
  icon: ReactNode;
  sub?: string;
  /** Tooltip of the row (problems of a cluster or of a node). */
  title?: string;
  section?: boolean;
}

const DND_VM = 'application/x-hvp-vm';
const DND_GROUP = 'application/x-hvp-group';

/**
 * Inventory navigator (vCenter-like), one tree with three sections: Hosts (host → VMs), Groups (logical folders →
 * sub-folders/VMs, plus "Ungrouped") and All VMs. Keyboard: ↑/↓ move, →/← expand/collapse, Enter opens.
 * Right click opens the same actions as everywhere else (add host on Hosts, new group on Groups, new VM in a group...).
 * VMs (from any section) and groups can be dragged onto groups: move inside Groups, add from Hosts; Ctrl = add.
 */
export function InventoryTree({ onNavigate }: { onNavigate?: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const { can } = useSession();
  const navigate = useNavigate();
  const location = useLocation();
  const [expandedList, setExpandedList] = useLocalStorage<string[]>('hvp.tree.open', ['hosts', 'groups']);
  const expanded = useMemo(() => new Set(expandedList), [expandedList]);
  const [filter, setFilter] = useState('');
  const [focusKey, setFocusKey] = useState<string | null>(null);
  const [menuNode, setMenuNode] = useState<Node | null>(null);
  const [dropKey, setDropKey] = useState<string | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const issueText = useIssueText();

  const toggle = (key: string, open?: boolean) => {
    const next = new Set(expanded);
    const shouldOpen = open ?? !next.has(key);
    if (shouldOpen) next.add(key); else next.delete(key);
    setExpandedList([...next]);
  };

  const q = filter.trim().toLowerCase();
  const matches = (s: string | null | undefined) => !q || (s ?? '').toLowerCase().includes(q);

  // ---------------------------------------------------------------- build visible nodes
  const nodes = useMemo(() => {
    const out: Node[] = [];
    const isOpen = (k: string) => !!q || expanded.has(k);
    const vmNode = (vm: Vm, depth: number, parentGroupId?: string): Node => ({
      key: parentGroupId ? `vm:${vm.id}@${parentGroupId}` : `vm:${vm.id}`, kind: 'vm', label: vm.name, depth, hasChildren: false,
      path: `/vms/${vm.id}`, vm, parentGroupId, icon: <VmIcon state={vm.powerState} critical={vm.isCritical} />,
      sub: vm.isMissing ? '⚠' : undefined,
    });
    const count = (n: number) => <span className="text-[10px] text-muted-foreground tabular">{n}</span>;

    // ---- Hosts
    out.push({ key: 'hosts', kind: 'hostsRoot', label: t('tree.hosts'), depth: 0, hasChildren: inv.hosts.length > 0, path: '/inventory?view=hosts',
      icon: <Server className="size-4 text-muted-foreground" />, badge: count(inv.hosts.length), section: true });
    if (isOpen('hosts')) {
      // In a cluster (inCluster) the nodes have no VM children: the VMs move between nodes, so they are listed under the
      // cluster, after its nodes. Standalone hosts keep their VMs below them.
      const hostNodes = (hosts: typeof inv.hosts, depth: number, inCluster = false, problems?: Map<string, ClusterIssue[]>) => {
        for (const h of hosts) {
          const vms = inv.vms.filter((v) => v.hostId === h.id).sort((a, b) => a.name.localeCompare(b.name));
          const visibleVms = vms.filter((v) => matches(v.name) || matches(v.notes));
          if (q && !matches(h.displayName) && (inCluster || visibleVms.length === 0)) continue;
          const key = `host:${h.id}`;
          const paused = h.clusterFacts?.nodeState === 'Paused';
          const issues = problems?.get(h.id) ?? [];
          out.push({
            key, kind: 'host', label: h.displayName, depth, hasChildren: !inCluster && vms.length > 0, path: `/hosts/${h.id}`,
            icon: <HostIcon connected={h.connected} access={h.access} />, sub: paused ? '⏸' : undefined,
            badge: <span className="flex items-center gap-1">{problemIcon(issues)}{count(vms.length)}</span>,
            title: issues.length > 0 ? issues.map(issueText).join('\n') : undefined,
          });
          if (!inCluster && isOpen(key)) for (const v of (q && !matches(h.displayName) ? visibleVms : vms)) out.push(vmNode(v, depth + 1));
        }
      };
      // Failover clusters (ADR-041): cluster -> nodes -> VMs; standalone hosts as before.
      for (const c of inv.clusters) {
        const nodes = inv.hosts.filter((h) => h.clusterId === c.id);
        const clusterVms = inv.vms.filter((v) => nodes.some((h) => h.id === v.hostId));
        if (q && !matches(c.name) && !nodes.some((h) => matches(h.displayName)) && !clusterVms.some((v) => matches(v.name) || matches(v.notes))) continue;
        const key = `cluster:${c.id}`;
        const tone = c.status === 'Critical' ? 'text-destructive' : c.status === 'Warning' ? 'text-warning' : c.status === 'Ok' ? 'text-success' : 'text-muted-foreground';
        const problems = c.problems ?? [];
        out.push({
          key, kind: 'cluster', label: c.name, depth: 1, hasChildren: nodes.length + clusterVms.length > 0, path: c.can.readStatus ? `/clusters/${c.id}` : null,
          icon: <Boxes className={cn('size-4', tone)} />,
          badge: <span className="flex items-center gap-1">{problemIcon(problems)}{count(clusterVms.length)}</span>,
          title: problems.length > 0 ? problems.map(issueText).join('\n') : undefined,
        });
        if (isOpen(key)) {
          hostNodes(nodes, 2, true, new Map((c.nodeProblems ?? []).map((n) => [n.hostId, n.issues])));
          const visible = clusterVms.filter((v) => !q || matches(c.name) || matches(v.name) || matches(v.notes)).sort((a, b) => a.name.localeCompare(b.name));
          for (const v of visible) out.push(vmNode(v, 2));
        }
      }
      hostNodes(inv.hosts.filter((h) => !h.clusterId || !inv.clusterById.has(h.clusterId)), 1);
    }

    // ---- Groups
    const subtreeMatches = (g: Group): boolean =>
      matches(g.name) || g.vmIds.some((id) => matches(inv.vmById.get(id)?.name)) || inv.childrenOf(g.id).some(subtreeMatches);
    const walk = (parent: string | null, depth: number) => {
      for (const g of inv.childrenOf(parent)) {
        if (q && !subtreeMatches(g)) continue;
        const key = `group:${g.id}`;
        const children = inv.childrenOf(g.id);
        const vms = g.vmIds.map((id) => inv.vmById.get(id)).filter((v): v is Vm => !!v).sort((a, b) => a.name.localeCompare(b.name));
        out.push({
          key, kind: 'group', label: g.name, depth, hasChildren: children.length + vms.length > 0, path: `/groups/${g.id}`, group: g,
          icon: <GroupIcon open={isOpen(key)} />, badge: count(vms.length),
        });
        if (isOpen(key)) {
          walk(g.id, depth + 1);
          for (const v of vms) if (!q || matches(v.name) || matches(g.name)) out.push(vmNode(v, depth + 1, g.id));
        }
      }
    };
    const ungrouped = inv.vms.filter((v) => inv.groupsOfVm(v.id).length === 0 && matches(v.name)).sort((a, b) => a.name.localeCompare(b.name));
    out.push({ key: 'groups', kind: 'groupsRoot', label: t('tree.groups'), depth: 0, hasChildren: inv.groups.length + ungrouped.length > 0,
      path: '/inventory?view=groups', icon: <GroupIcon className="text-muted-foreground" />, badge: count(inv.groups.length), section: true });
    if (isOpen('groups')) {
      walk(null, 1);
      if (ungrouped.length > 0) {
        out.push({ key: 'ungrouped', kind: 'ungrouped', label: t('tree.ungrouped'), depth: 1, hasChildren: true, path: null, icon: <GroupIcon className="text-muted-foreground" />, badge: count(ungrouped.length) });
        if (isOpen('ungrouped')) for (const v of ungrouped) out.push({ ...vmNode(v, 2), key: `vm:${v.id}@ungrouped` });
      }
    }

    // ---- All VMs (flat list page)
    out.push({ key: 'allvms', kind: 'allVms', label: t('inv.view.vms'), depth: 0, hasChildren: false, path: '/inventory?view=vms',
      icon: <Monitor className="size-4 text-muted-foreground" />, badge: count(inv.vms.length), section: true });
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [inv, expanded, q, t, issueText]);

  // Selection follows the route; reveal the selected object in the tree (a VM under its host, a group in its folders).
  const selectedPath = location.pathname + location.search;
  useEffect(() => {
    const m = /^\/(vms|hosts|groups|clusters)\/([0-9a-f-]{36})/.exec(location.pathname);
    if (!m) return;
    const [, kind, id] = m;
    const add: string[] = [];
    const clusterOf = (hostId: string) => inv.hostById.get(hostId)?.clusterId;
    if (kind === 'vms') {
      const vm = inv.vmById.get(id!);
      // A VM of a cluster is listed under the cluster, a VM of a standalone host under its host.
      if (vm) add.push('hosts', ...(clusterOf(vm.hostId) ? [`cluster:${clusterOf(vm.hostId)}`] : [`host:${vm.hostId}`]));
    }
    if (kind === 'hosts') add.push('hosts', ...(clusterOf(id!) ? [`cluster:${clusterOf(id!)}`] : []));
    if (kind === 'clusters') add.push('hosts');
    if (kind === 'groups') add.push('groups', ...inv.groupPath(id!).slice(0, -1).map((g) => `group:${g.id}`));
    if (add.some((k) => !expanded.has(k))) setExpandedList([...new Set([...expandedList, ...add])]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.pathname, inv.vms.length, inv.groups.length]);

  const open = (n: Node) => {
    if (n.path) { navigate(n.path); onNavigate?.(); } else if (n.hasChildren) toggle(n.key);
  };

  const canCreateVm = inv.hosts.some((h) => h.canCreateVm);
  const actionsFor = (n: Node): Action[] =>
    n.kind === 'vm' && n.vm ? ops.vmActions(n.vm, { groupId: n.parentGroupId })
      : n.kind === 'host' ? (inv.hostById.get(n.key.slice(5)) ? ops.hostActions(inv.hostById.get(n.key.slice(5))!) : [])
      : n.kind === 'cluster' ? (inv.clusterById.get(n.key.slice(8)) ? ops.clusterActions(inv.clusterById.get(n.key.slice(8))!) : [])
      : n.kind === 'group' && n.group ? ops.groupActions(n.group)
      : n.kind === 'groupsRoot' ? ops.groupActions(null)
      : n.kind === 'hostsRoot' && can('hosts.manage') ? [{ key: 'add-host', label: t('host.add'), icon: Plus, onSelect: () => ops.openAddHost() }]
      : n.kind === 'allVms' && canCreateVm ? [{ key: 'new-vm', label: t('wiz.title'), icon: Plus, onSelect: () => ops.openCreateVm() }]
      : [];

  // ---------------------------------------------------------------- keyboard
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const i = nodes.findIndex((n) => n.key === focusKey);
    const cur = nodes[i];
    const focus = (n?: Node) => {
      if (!n) return;
      setFocusKey(n.key);
      listRef.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(n.key)}"]`)?.focus();
    };
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); focus(nodes[Math.min(nodes.length - 1, i + 1)]); break;
      case 'ArrowUp': e.preventDefault(); focus(nodes[Math.max(0, i - 1)]); break;
      case 'Home': e.preventDefault(); focus(nodes[0]); break;
      case 'End': e.preventDefault(); focus(nodes[nodes.length - 1]); break;
      case 'ArrowRight':
        if (cur?.hasChildren) { e.preventDefault(); if (!expanded.has(cur.key)) toggle(cur.key, true); else focus(nodes[i + 1]); }
        break;
      case 'ArrowLeft':
        if (!cur) break;
        e.preventDefault();
        if (cur.hasChildren && expanded.has(cur.key)) toggle(cur.key, false);
        else { for (let j = i - 1; j >= 0; j--) if (nodes[j]!.depth < cur.depth) { focus(nodes[j]); break; } }
        break;
      case 'Enter':
        if (cur) { e.preventDefault(); open(cur); }
        break;
    }
  };

  // ---------------------------------------------------------------- drag and drop (onto groups)
  const canDnd = inv.canManageGroups;
  const onDragStart = (e: DragEvent, n: Node) => {
    if (!canDnd) return;
    if (n.kind === 'vm' && n.vm) e.dataTransfer.setData(DND_VM, JSON.stringify({ vmId: n.vm.id, from: n.parentGroupId ?? null }));
    else if (n.kind === 'group' && n.group) e.dataTransfer.setData(DND_GROUP, n.group.id);
    else return;
    e.dataTransfer.effectAllowed = 'copyMove';
  };
  const dropTarget = (n: Node) => canDnd && (n.kind === 'group' || n.kind === 'groupsRoot');
  const onDragOver = (e: DragEvent, n: Node) => {
    if (!dropTarget(n)) return;
    const types = e.dataTransfer.types;
    if (!types.includes(DND_VM) && !types.includes(DND_GROUP)) return;
    if (n.kind === 'groupsRoot' && !types.includes(DND_GROUP)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = e.ctrlKey ? 'copy' : 'move';
    setDropKey(n.key);
  };
  const onDrop = (e: DragEvent, n: Node) => {
    setDropKey(null);
    if (!dropTarget(n)) return;
    e.preventDefault();
    const vmData = e.dataTransfer.getData(DND_VM);
    const groupData = e.dataTransfer.getData(DND_GROUP);
    if (vmData && n.group) {
      const { vmId, from } = JSON.parse(vmData) as { vmId: string; from: string | null };
      if (from === n.group.id) return;
      void ops.placeVms([vmId], n.group.id, e.ctrlKey ? undefined : from ?? undefined);
    } else if (groupData) {
      const target = n.kind === 'groupsRoot' ? null : n.group!.id;
      if (groupData === target) return;
      // Refuse moving a group below itself (the server checks too).
      if (target && inv.groupPath(target).some((g) => g.id === groupData)) return;
      void ops.moveGroup(groupData, target);
    }
  };

  const allCollapsed = expandedList.length === 0;
  return (
    <div className="flex h-full min-h-0 flex-col bg-panel [--tree-bg:var(--panel)]" aria-label={t('shell.inventoryPanel')}>
      <div className="flex items-center gap-1 border-b px-2 py-1.5">
        <span className="px-1 text-xs font-semibold">{t('nav.inventory')}</span>
        <span className="flex-1" />
        {can('hosts.manage') && (
          <Tooltip><TooltipTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={t('host.add')} onClick={() => ops.openAddHost()}><Server /></Button>
          </TooltipTrigger><TooltipContent>{t('host.add')}</TooltipContent></Tooltip>
        )}
        {inv.canManageGroups && (
          <Tooltip><TooltipTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={t('group.new')} onClick={() => ops.openNewGroup(null)}><FolderPlus /></Button>
          </TooltipTrigger><TooltipContent>{t('group.new')}</TooltipContent></Tooltip>
        )}
        {canCreateVm && (
          <Tooltip><TooltipTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={t('wiz.title')} onClick={() => ops.openCreateVm()}><Plus /></Button>
          </TooltipTrigger><TooltipContent>{t('wiz.title')}</TooltipContent></Tooltip>
        )}
        <Tooltip><TooltipTrigger asChild>
          <Button variant="ghost" size="icon-sm" aria-label={t('tree.collapseAll')} disabled={allCollapsed} onClick={() => setExpandedList([])}><ChevronsDownUp /></Button>
        </TooltipTrigger><TooltipContent>{t('tree.collapseAll')}</TooltipContent></Tooltip>
      </div>
      <div className="relative border-b px-2 py-1.5">
        <Search className="pointer-events-none absolute top-1/2 left-4 size-3.5 -translate-y-1/2 text-muted-foreground" />
        <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder={t('tree.filter')} aria-label={t('tree.filter')} className="h-7 pr-7 pl-7 text-xs" />
        {filter && <button type="button" className="absolute top-1/2 right-3.5 -translate-y-1/2 text-muted-foreground hover:text-foreground" onClick={() => setFilter('')} aria-label={t('common.cancel')}><X className="size-3.5" /></button>}
      </div>
      <ContextMenu modal={false} onOpenChange={(o) => { if (!o) setMenuNode(null); }}>
        <ContextMenuTrigger asChild>
          <div ref={listRef} role="tree" aria-label={t('nav.inventory')}
            className="min-h-0 flex-1 overflow-auto py-1 text-sm select-none" onKeyDown={onKeyDown}
            onContextMenu={(e) => { if (!(e.target as HTMLElement).closest('[role=treeitem]')) setMenuNode(null); }}>
            {q && nodes.every((n) => n.depth === 0) && <div className="px-3 py-4 text-xs text-muted-foreground">{t('tree.noMatch')}</div>}
            {nodes.map((n) => {
              const selected = !!n.path && (selectedPath === n.path || (n.kind === 'vm' && location.pathname.startsWith(n.path + '/')));
              const isExpanded = n.hasChildren && (!!q || expanded.has(n.key));
              return (
                <div key={n.key} data-key={n.key} role="treeitem" aria-level={n.depth + 1} aria-selected={selected}
                  aria-expanded={n.hasChildren ? isExpanded : undefined}
                  tabIndex={(focusKey ?? nodes[0]?.key) === n.key ? 0 : -1}
                  draggable={canDnd && (n.kind === 'vm' || n.kind === 'group')}
                  onDragStart={(e) => onDragStart(e, n)} onDragOver={(e) => onDragOver(e, n)} onDragLeave={() => setDropKey(null)} onDrop={(e) => onDrop(e, n)}
                  onFocus={() => setFocusKey(n.key)}
                  onClick={() => { setFocusKey(n.key); open(n); }}
                  onContextMenu={() => { setFocusKey(n.key); setMenuNode(n); }}
                  className={cn('tree-node group/node mx-1 flex h-7 cursor-pointer items-center gap-1.5 rounded-md pr-2 outline-none', n.section && 'mt-1 font-medium',
                    'hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring/60',
                    selected && 'bg-primary/10 font-medium text-primary hover:bg-primary/15',
                    dropKey === n.key && 'bg-primary/15 ring-2 ring-primary/50')}
                  title={n.title}
                  style={{ paddingLeft: 4 + n.depth * 14 }}>
                  <span className="flex size-4 shrink-0 items-center justify-center">
                    {n.hasChildren && (
                      <button type="button" tabIndex={-1} aria-hidden="true" className="rounded text-muted-foreground hover:text-foreground"
                        onClick={(e) => { e.stopPropagation(); toggle(n.key); }}>
                        <ChevronRight className={cn('size-3.5 transition-transform', isExpanded && 'rotate-90')} />
                      </button>
                    )}
                  </span>
                  {n.icon}
                  <span className={cn('min-w-0 flex-1 truncate', n.vm?.isMissing && 'text-muted-foreground line-through')}>{n.label}</span>
                  {n.badge}
                </div>
              );
            })}
          </div>
        </ContextMenuTrigger>
        {menuNode && (
          <ContextMenuContent className="min-w-56">
            <ContextActions items={actionsFor(menuNode)} label={menuNode.label} />
          </ContextMenuContent>
        )}
      </ContextMenu>
      {canDnd && inv.groups.length > 0 && <p className="border-t px-3 py-1.5 text-[11px] leading-snug text-muted-foreground">{t('tree.dropHint')}</p>}
    </div>
  );
}
