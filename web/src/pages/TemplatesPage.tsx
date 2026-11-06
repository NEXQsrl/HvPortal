import { CopyPlus, LayoutTemplate, Pencil, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { get } from '@/api/client';
import type { VmTemplate } from '@/api/ops-types';
import { Empty, ErrorAlert, Loading, Notice, PageHeader, StatusBadge } from '@/components/common';
import { HelpTip } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { DeleteTemplateDialog, EditTemplateDialog } from '@/components/ops/TemplateDialogs';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLive, useLoad } from '@/lib/hooks';

const TONE: Record<VmTemplate['status'], 'ok' | 'warn' | 'danger' | 'info'> = { Ready: 'ok', Creating: 'info', Deleting: 'info', Failed: 'danger', Unverified: 'warn' };

/** Templates of the hosts where the user creates VMs: what each one contains and what can be done with it. */
export function TemplatesPage() {
  const { t, fmtDate } = useI18n();
  const ops = useOps();
  const inv = useInventory();
  const [params] = useSearchParams();
  const hostId = params.get('host') ?? '';
  const data = useLoad(() => get<{ items: VmTemplate[] }>(`/api/v1/templates${hostId ? `?hostId=${hostId}` : ''}`), [hostId]);
  // A template changes when its task ends (copy finished, deleted): follow the tasks.
  useLive(data.reload, ['jobChanged', 'inventoryChanged'], 800);
  const [editing, setEditing] = useState<VmTemplate | null>(null);
  const [deleting, setDeleting] = useState<VmTemplate | null>(null);
  const items = data.data?.items ?? [];
  const host = hostId ? inv.hostById.get(hostId) : undefined;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<LayoutTemplate className="size-5" />} title={<span className="flex items-center gap-2">{t('tpl.title')}{host && <span className="text-muted-foreground">· {host.displayName}</span>}<HelpTip>{t('tpl.help')}</HelpTip></span>}
        subtitle={t('tpl.subtitle')} />
      <div className="min-h-0 flex-1 overflow-auto">
        <div className="px-4 empty:hidden"><ErrorAlert error={data.error} /></div>
        {data.loading && !data.data ? <Loading /> : items.length === 0 ? <Empty>{t(hostId ? 'tpl.emptyHost' : 'tpl.empty')}</Empty> : (
          <Table>
            <TableHeader><TableRow>
              <TableHead>{t('tpl.name')}</TableHead><TableHead>{t('tpl.host')}</TableHead><TableHead>{t('tpl.status')}</TableHead>
              <TableHead>{t('tpl.hardware')}</TableHead><TableHead>{t('tpl.disks')}</TableHead><TableHead>{t('tpl.created')}</TableHead><TableHead />
            </TableRow></TableHeader>
            <TableBody>
              {items.map((tpl) => (
                <TableRow key={tpl.id} className="template-row align-top">
                  <TableCell className="max-w-xs whitespace-normal">
                    <div className="font-medium">{tpl.name}</div>
                    {tpl.description && <div className="text-xs text-muted-foreground">{tpl.description}</div>}
                    {tpl.folderPath && <div className="font-mono text-[11px] break-all text-muted-foreground">{tpl.folderPath}</div>}
                  </TableCell>
                  <TableCell><Link className="text-primary hover:underline" to={`/hosts/${tpl.hostId}`}>{tpl.hostName}</Link></TableCell>
                  <TableCell className="max-w-xs whitespace-normal">
                    <StatusBadge tone={TONE[tpl.status]}>{t(('tpl.status.' + tpl.status) as TKey)}</StatusBadge>
                    {(tpl.status === 'Failed' || tpl.status === 'Unverified') && (
                      <div className="mt-1 text-xs text-muted-foreground">
                        {t(tpl.status === 'Failed' ? 'tpl.statusHelp.Failed' : 'tpl.statusHelp.Unverified')}
                        {tpl.error && <div className="mt-0.5 break-words text-destructive">{tpl.error}</div>}
                      </div>
                    )}
                    {tpl.jobId && <div className="mt-1 text-xs"><Link className="text-primary hover:underline" to={`/jobs/${tpl.jobId}`}>{t('tpl.task')}</Link></div>}
                  </TableCell>
                  <TableCell className="text-xs whitespace-nowrap">
                    {tpl.disks.length > 0 ? (
                      <>
                        <div>{tpl.processorCount} vCPU · {formatBytes(tpl.memoryStartupMb * 1048576)}</div>
                        <div className="text-muted-foreground">Gen {tpl.generation}{tpl.secureBootTemplate && tpl.secureBootTemplate !== 'Off' ? ' · Secure Boot' : ''}{tpl.tpmEnabled ? ' · TPM' : ''}</div>
                      </>
                    ) : '—'}
                  </TableCell>
                  <TableCell className="text-xs whitespace-nowrap">
                    {tpl.disks.length > 0 ? (
                      <>
                        <div>{tpl.disks.map((d) => formatBytes(d.sizeBytes)).join(' + ')}</div>
                        <div className="text-muted-foreground">{tpl.disks.length === 1 ? t('tpl.diskOne') : t('tpl.diskCount', { n: tpl.disks.length })} · {t('tpl.onDisk', { size: formatBytes(tpl.sizeBytes) })}</div>
                      </>
                    ) : '—'}
                  </TableCell>
                  <TableCell className="text-xs whitespace-nowrap">
                    <div>{fmtDate(tpl.createdAt)}</div>
                    {tpl.sourceVmName && <div className="text-muted-foreground">{t('tpl.from', { vm: tpl.sourceVmName })}</div>}
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-wrap justify-end gap-1.5">
                      {tpl.can.deploy && <Button size="sm" onClick={() => ops.openCreateVm({ template: tpl })}><CopyPlus />{t('tpl.newVm')}</Button>}
                      {tpl.can.manage && <Button size="sm" variant="outline" onClick={() => setEditing(tpl)}><Pencil />{t('tpl.edit')}</Button>}
                      {tpl.can.delete && <Button size="sm" variant="outline" onClick={() => setDeleting(tpl)}><Trash2 />{t('tpl.delete')}</Button>}
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        <div className="p-4"><Notice kind="info">{t('tpl.help')}</Notice></div>
      </div>
      {editing && <EditTemplateDialog template={editing} onSaved={data.reload} onClose={() => setEditing(null)} />}
      {deleting && <DeleteTemplateDialog template={deleting} onQueued={(r, label) => { ops.queued(r, label); data.reload(); }} onClose={() => setDeleting(null)} />}
    </div>
  );
}
