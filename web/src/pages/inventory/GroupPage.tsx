import { ChevronDown, FolderPlus, Layers, Plus, Server } from 'lucide-react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ActionsButton, KebabMenu, rowLink, WithContextMenu } from '@/components/actions';
import { Empty, GroupIcon, Loading, Notice, PageHeader, Section, StatCard } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { Breadcrumb, BreadcrumbItem, BreadcrumbLink, BreadcrumbList, BreadcrumbPage, BreadcrumbSeparator } from '@/components/ui/breadcrumb';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableRow } from '@/components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { VmTable } from '@/components/vm/VmTable';
import { useI18n, type TKey } from '@/i18n';
import { useSession } from '@/session';
import { HostTable } from './HostListPage';
import { VmListView } from './VmListPage';

export function GroupPage() {
  const { id } = useParams();
  const { t } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const navigate = useNavigate();
  const group = id ? inv.groupById.get(id) : undefined;
  if (!group) return inv.loading ? <Loading /> : <Empty>{t('cmd.empty')}</Empty>;

  const path = inv.groupPath(group.id);
  const children = inv.childrenOf(group.id);
  const vms = group.vmIds.map((v) => inv.vmById.get(v)).filter((v): v is NonNullable<typeof v> => !!v).sort((a, b) => a.name.localeCompare(b.name));
  // All VMs in the subtree (assignments on this group apply to them).
  const subtree = new Set<string>();
  const collect = (gid: string) => { for (const v of inv.groupById.get(gid)?.vmIds ?? []) subtree.add(v); for (const c of inv.childrenOf(gid)) collect(c.id); };
  collect(group.id);
  const running = [...subtree].filter((v) => inv.vmById.get(v)?.powerState === 'Running').length;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<GroupIcon className="size-5" />} title={group.name}
        subtitle={
          <Breadcrumb>
            <BreadcrumbList className="text-xs">
              <BreadcrumbItem><BreadcrumbLink asChild><Link to="/inventory">{t('tree.root')}</Link></BreadcrumbLink></BreadcrumbItem>
              {path.map((g, i) => (
                <span key={g.id} className="contents">
                  <BreadcrumbSeparator />
                  <BreadcrumbItem>{i === path.length - 1 ? <BreadcrumbPage>{g.name}</BreadcrumbPage> : <BreadcrumbLink asChild><Link to={`/groups/${g.id}`}>{g.name}</Link></BreadcrumbLink>}</BreadcrumbItem>
                </span>
              ))}
            </BreadcrumbList>
          </Breadcrumb>
        }
        actions={<>
          {inv.canManageGroups && <Button size="sm" variant="outline" onClick={() => ops.openNewGroup(group.id)}><FolderPlus />{t('group.newSub').replace('…', '')}</Button>}
          <ActionsButton items={ops.groupActions(group).filter((a) => a.key !== 'open')} label={group.name}>{t('common.actions')}<ChevronDown /></ActionsButton>
        </>} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        {group.description && <p className="text-sm text-muted-foreground">{group.description}</p>}
        <div className="grid gap-3 sm:grid-cols-3">
          <StatCard label={t('group.members')} value={vms.length} />
          <StatCard label={t('group.subgroups')} value={children.length} />
          <StatCard label={`${t('dash.vms')} ${t('dash.running')}`} value={`${running} / ${subtree.size}`} sub={t('group.inheritHelp')} />
        </div>
        {children.length > 0 && (
          <Section title={t('group.subgroups')}>
            <Table>
              <TableBody>
                {children.map((c) => {
                  const items = ops.groupActions(c);
                  return (
                    <WithContextMenu key={c.id} items={items} label={c.name}>
                      <TableRow {...rowLink(() => navigate(`/groups/${c.id}`))}>
                        <TableCell><div className="flex items-center gap-2"><GroupIcon /><span className="font-medium">{c.name}</span></div></TableCell>
                        <TableCell className="text-xs text-muted-foreground">{c.description}</TableCell>
                        <TableCell className="text-right text-xs text-muted-foreground tabular">{c.vmIds.length} VM</TableCell>
                        <TableCell className="w-10 text-right"><KebabMenu items={items} label={t('menu.more')} /></TableCell>
                      </TableRow>
                    </WithContextMenu>
                  );
                })}
              </TableBody>
            </Table>
          </Section>
        )}
        <Section title={t('group.members')} actions={inv.canManageGroups && <Button size="xs" variant="outline" onClick={() => ops.groupActions(group).find((a) => a.key === 'add-vms')?.onSelect()}><Plus />{t('group.addVms')}</Button>}>
          <VmTable vms={vms} groupId={group.id} emptyText={t('group.empty')} className="max-h-[60vh]" />
        </Section>
      </div>
    </div>
  );
}

const VIEWS = ['hosts', 'groups', 'vms'] as const;
type View = (typeof VIEWS)[number];

/**
 * Inventory: the single entry point for hosts, groups and VMs (vCenter-like). Views: hosts, groups (logical folders of
 * the portal) and all VMs; from here hosts are added, groups created and VMs created.
 */
export function InventoryHome() {
  const { t } = useI18n();
  const { can } = useSession();
  const inv = useInventory();
  const ops = useOps();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const view: View = (VIEWS as readonly string[]).includes(params.get('view') ?? '') ? (params.get('view') as View) : 'hosts';
  const roots = inv.childrenOf(null);
  const ungrouped = inv.vms.filter((v) => inv.groupsOfVm(v.id).length === 0);
  const counts: Record<View, number> = { hosts: inv.hosts.length, groups: inv.groups.length, vms: inv.vms.length };
  return (
    <Tabs value={view} onValueChange={(v) => setParams({ view: v }, { replace: true })} className="flex h-full min-h-0 flex-col gap-0">
      <PageHeader icon={<Layers className="size-5" />} title={t('nav.inventory')}
        subtitle={`${inv.hosts.filter((h) => h.connected).length}/${inv.hosts.length} host ${t('dash.connected')} · ${inv.vms.length} VM · ${inv.groups.length} ${t('tree.groups').toLowerCase()}`}
        actions={<>
          {can('hosts.manage') && <Button size="sm" variant="outline" onClick={() => ops.openAddHost()}><Server />{t('host.add')}</Button>}
          {inv.canManageGroups && <Button size="sm" variant="outline" onClick={() => ops.openNewGroup(null)}><FolderPlus />{t('group.new')}</Button>}
          {inv.hosts.some((h) => h.canCreateVm) && <Button size="sm" onClick={() => ops.openCreateVm()}><Plus />{t('wiz.title')}</Button>}
        </>}>
        <TabsList variant="line" className="-mb-px h-9 w-full justify-start overflow-x-auto rounded-none bg-transparent p-0">
          {VIEWS.map((v) => (
            <TabsTrigger key={v} value={v} className="flex-none gap-1.5 px-3">
              {t(('inv.view.' + v) as TKey)}<span className="rounded bg-muted px-1 text-[10px] text-muted-foreground tabular">{counts[v]}</span>
            </TabsTrigger>
          ))}
        </TabsList>
      </PageHeader>
      <TabsContent value="hosts" className="min-h-0">{view === 'hosts' && <HostTable />}</TabsContent>
      <TabsContent value="vms" className="min-h-0">{view === 'vms' && <VmListView />}</TabsContent>
      <TabsContent value="groups" className="min-h-0 overflow-auto">
        {view === 'groups' && (
          <div className="space-y-3 p-4">
            <Notice kind="info">{t('group.logicalNote')}</Notice>
            <Section title={t('tree.groups')}>
              {roots.length === 0 ? <Empty>{t('tree.empty')}</Empty> : (
                <Table>
                  <TableBody>
                    {roots.map((g) => (
                      <WithContextMenu key={g.id} items={ops.groupActions(g)} label={g.name}>
                        <TableRow {...rowLink(() => navigate(`/groups/${g.id}`))}>
                          <TableCell><div className="flex items-center gap-2"><GroupIcon /><span className="font-medium">{g.name}</span></div></TableCell>
                          <TableCell className="text-xs text-muted-foreground">{g.description}</TableCell>
                          <TableCell className="text-right text-xs text-muted-foreground tabular">{inv.childrenOf(g.id).length} · {g.vmIds.length} VM</TableCell>
                          <TableCell className="w-10 text-right"><KebabMenu items={ops.groupActions(g)} label={t('menu.more')} /></TableCell>
                        </TableRow>
                      </WithContextMenu>
                    ))}
                  </TableBody>
                </Table>
              )}
            </Section>
            {ungrouped.length > 0 && (
              <Section title={`${t('tree.ungrouped')} (${ungrouped.length})`}>
                <VmTable vms={ungrouped} className="max-h-[50vh]" />
              </Section>
            )}
          </div>
        )}
      </TabsContent>
    </Tabs>
  );
}
