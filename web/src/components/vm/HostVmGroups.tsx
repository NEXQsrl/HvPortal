import { Download, FolderTree, Monitor, RefreshCw } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { get, post } from '@/api/client';
import type { Host } from '@/api/types';
import { Empty, ErrorAlert, GroupIcon, Loading, Notice, Section, StatusBadge } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { HelpTip } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { useLive, useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { useSession } from '@/session';

interface NativeGroupRow {
  id: string;
  name: string;
  groupType: 'VMCollectionType' | 'ManagementCollectionType' | string;
  managed: boolean;
  portalGroupId: string | null;
  vms: { id: string | null; name: string }[];
  groups: string[];
}
interface VmGroupsResponse { supported: boolean; connected: boolean; status: { syncedAt: string | null; error: string | null }; groups: NativeGroupRow[] }
interface ImportResponse { result: { groupsCreated: number; groupsReused: number; vmsAdded: number; linked: number; warnings: string[] }; sync: { status: string; errors: string[] } }

/**
 * Native Hyper-V VM groups of a host (Get-VMGroup). Portal groups are mirrored here automatically (agent 0.3.2+);
 * groups created directly in Hyper-V can be imported once, after which the portal manages them.
 */
export function HostVmGroups({ host }: { host: Host }) {
  const { t, fmtDate } = useI18n();
  const { can } = useSession();
  const inv = useInventory();
  const data = useLoad(() => get<VmGroupsResponse>(`/api/v1/hosts/${host.id}/vm-groups`), [host.id]);
  useLive(data.reload, ['groupsChanged'], 4000);
  const [busy, setBusy] = useState<'sync' | 'import' | null>(null);
  const [error, setError] = useState<unknown>(null);

  const run = async (kind: 'sync' | 'import') => {
    setBusy(kind);
    setError(null);
    try {
      if (kind === 'sync') {
        const r = await post<{ status: string; errors: string[] }>(`/api/v1/hosts/${host.id}/vm-groups/sync`);
        if (r.errors.length) toast.error(t('vmg.syncPartial'), r.errors.slice(0, 3).join(' · '));
        else toast.success(t('vmg.synced'));
      } else {
        const r = await post<ImportResponse>(`/api/v1/hosts/${host.id}/vm-groups/import`);
        toast.success(t('vmg.imported', { created: r.result.groupsCreated, reused: r.result.groupsReused, vms: r.result.vmsAdded }),
          r.result.warnings.length ? r.result.warnings.join(' · ') : undefined);
        inv.reload();
      }
      data.reload();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  };

  if (data.loading && !data.data) return <Loading />;
  const d = data.data;
  if (!d) return <ErrorAlert error={data.error} />;
  const unmanaged = d.groups.filter((g) => !g.managed).length;
  const canSync = inv.canManageGroups;
  const canImport = can('hosts.manage') && inv.canManageGroups;

  return (
    <div className="space-y-3">
      {!d.supported ? (
        <Notice kind="warn">{d.connected ? t('vmg.agent') : t('vmg.offline')}</Notice>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <span>{t('vmg.lastSync')}: {d.status.syncedAt ? fmtDate(d.status.syncedAt) : '—'}</span>
            {d.status.error ? <StatusBadge tone="danger" title={d.status.error}>{t('vmg.errors')}</StatusBadge> : d.status.syncedAt && <StatusBadge tone="ok">OK</StatusBadge>}
            <span className="flex-1" />
            {canImport && unmanaged > 0 && (
              <Button size="sm" variant="outline" disabled={!!busy} onClick={() => void run('import')}><Download />{t('vmg.import', { n: unmanaged })}</Button>
            )}
            {canSync && <Button size="sm" variant="outline" disabled={!!busy} onClick={() => void run('sync')}><RefreshCw className={busy === 'sync' ? 'animate-spin' : ''} />{t('vmg.syncNow')}</Button>}
          </div>
          {d.status.error && <Notice kind="danger">{d.status.error}</Notice>}
          <ErrorAlert error={error} />
          <Section title={<span className="flex items-center gap-2"><FolderTree className="size-4 text-muted-foreground" />Get-VMGroup<HelpTip>{t('vmg.help')}</HelpTip></span>}>
            {d.groups.length === 0 ? <Empty>{t('vmg.none')}</Empty> : (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t('vmg.name')}</TableHead>
                    <TableHead>{t('vmg.type')}</TableHead>
                    <TableHead>{t('vmg.members')}</TableHead>
                    <TableHead>{t('vmg.portal')}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {d.groups.map((g) => (
                    <TableRow key={g.id}>
                      <TableCell className="font-medium">{g.name}<div className="font-mono text-[10px] text-muted-foreground">{g.id}</div></TableCell>
                      <TableCell className="text-xs whitespace-nowrap">{g.groupType === 'ManagementCollectionType' ? t('vmg.mgmt') : t('vmg.vms')}</TableCell>
                      <TableCell className="text-xs">
                        <div className="flex flex-wrap gap-1">
                          {g.vms.map((v) => v.id
                            ? <Link key={v.name} to={`/vms/${v.id}`}><StatusBadge tone="muted"><Monitor className="size-3" />{v.name}</StatusBadge></Link>
                            : <StatusBadge key={v.name} tone="muted">{v.name}</StatusBadge>)}
                          {g.groups.map((n, i) => <StatusBadge key={n + i} tone="muted"><GroupIcon className="size-3" />{n}</StatusBadge>)}
                          {g.vms.length + g.groups.length === 0 && <span className="text-muted-foreground">—</span>}
                        </div>
                      </TableCell>
                      <TableCell className="text-xs">
                        {g.managed && g.portalGroupId && inv.groupById.get(g.portalGroupId)
                          ? <Link className="text-primary hover:underline" to={`/groups/${g.portalGroupId}`}>{inv.groupPath(g.portalGroupId).map((x) => x.name).join(' / ')}</Link>
                          : g.managed ? <StatusBadge tone="ok">{t('vmg.managed')}</StatusBadge> : <StatusBadge tone="warn">{t('vmg.notImported')}</StatusBadge>}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Section>
        </>
      )}
    </div>
  );
}
