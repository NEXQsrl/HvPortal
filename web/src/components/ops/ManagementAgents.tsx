import { KeyRound, Network, Plus, RefreshCw, ShieldOff, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { get, post } from '@/api/client';
import type { EnrollmentInfo, ManagementAgent } from '@/api/types';
import { KebabMenu, type Action } from '@/components/actions';
import { Empty, ErrorAlert, Loading, Section, StatusBadge, errorText } from '@/components/common';
import { ActionDialog, ConfirmTypedDialog, Field, HelpTip } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Textarea } from '@/components/ui/textarea';
import { useI18n } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { ManagementEnrollmentDialog } from './HostDialogs';
import { useOps } from './OperationsProvider';

export function NewManagementAgentDialog({ onCreated, onClose }: { onCreated: (info: EnrollmentInfo, id: string, name: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  return (
    <ActionDialog title={t('magent.new')} description={t('magent.newHelp')} confirmLabel={t('host.create')} disabled={!name.trim()} onClose={onClose}
      onConfirm={async () => {
        const r = await post<{ managementAgentId: string; enrollment: EnrollmentInfo }>('/api/v1/management-agents', {
          displayName: name.trim(), description: description.trim() || null,
        });
        onCreated(r.enrollment, r.managementAgentId, name.trim());
      }}>
      <Field label={t('magent.name')} htmlFor="ma-name">
        <Input id="ma-name" value={name} maxLength={64} placeholder="mgmt-01" onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label={t('common.description')} htmlFor="ma-desc">
        <Textarea id="ma-desc" rows={2} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
    </ActionDialog>
  );
}

/** Enrollment guide that follows the agent's connection state (polled while open). */
export function LiveManagementEnrollment({ info, agentId, name, onClose }: { info: EnrollmentInfo; agentId: string; name: string; onClose: () => void }) {
  const [connected, setConnected] = useState(false);
  useEffect(() => {
    let stop = false;
    const tick = async () => {
      try {
        const list = await get<ManagementAgent[]>('/api/v1/management-agents');
        if (!stop) setConnected(list.some((a) => a.id === agentId && a.connected));
      } catch { /* shown as "waiting" */ }
    };
    void tick();
    const id = window.setInterval(() => void tick(), 5000);
    return () => { stop = true; window.clearInterval(id); };
  }, [agentId]);
  return <ManagementEnrollmentDialog info={info} agent={name} connected={connected} onClose={onClose} />;
}

/** Inventory > Management agents: servers that reach remote Hyper-V hosts with stored credentials. */
export function ManagementAgentsView() {
  const { t, fmtDate } = useI18n();
  const ops = useOps();
  const list = useLoad(() => get<ManagementAgent[]>('/api/v1/management-agents'));
  const [revoke, setRevoke] = useState<ManagementAgent | null>(null);
  const [remove, setRemove] = useState<ManagementAgent | null>(null);
  const reload = list.reload;
  useEffect(() => {
    const id = window.setInterval(reload, 15000);
    return () => window.clearInterval(id);
  }, [reload]);
  useEffect(() => ops.onManagementAgentsChanged(reload), [ops, reload]);

  const actions = (a: ManagementAgent): Action[] => [
    {
      key: 'token', label: t('host.newToken'), icon: KeyRound,
      onSelect: async () => {
        try { ops.openManagementEnrollment(await post<EnrollmentInfo>(`/api/v1/management-agents/${a.id}/enrollment-token`), a.id, a.displayName); }
        catch (e) { toast.error(t('common.error'), errorText(e)); }
      },
    },
    { key: 'revoke', label: t('host.revoke'), icon: ShieldOff, danger: true, separatorBefore: true, disabled: a.status === 'Revoked', onSelect: () => setRevoke(a) },
    { key: 'delete', label: t('common.delete'), icon: Trash2, danger: true, disabled: a.hostCount > 0, onSelect: () => setRemove(a) },
  ];

  return (
    <div className="space-y-3 p-4">
      <Section title={<span className="flex items-center gap-1.5"><Network className="size-4" />{t('magent.title')}<HelpTip>{t('magent.help')}</HelpTip></span>}
        actions={<>
          <Button size="sm" variant="ghost" onClick={reload} aria-label={t('common.refresh')} title={t('common.refresh')}><RefreshCw /></Button>
          <Button size="sm" variant="outline" onClick={() => ops.openNewManagementAgent()}><Plus />{t('magent.new')}</Button>
        </>}>
        <ErrorAlert error={list.error} className="mx-3" />
        {list.loading && !list.data ? <Loading /> : (list.data ?? []).length === 0 ? <Empty>{t('magent.empty')}</Empty> : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('magent.name')}</TableHead>
                <TableHead>{t('host.status')}</TableHead>
                <TableHead className="hidden md:table-cell">{t('magent.machine')}</TableHead>
                <TableHead className="hidden md:table-cell">{t('magent.version')}</TableHead>
                <TableHead className="text-right">{t('magent.hosts')}</TableHead>
                <TableHead className="hidden lg:table-cell">{t('magent.lastSeen')}</TableHead>
                <TableHead className="w-10"><span className="sr-only">{t('vm.actions')}</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(list.data ?? []).map((a) => (
                <TableRow key={a.id}>
                  <TableCell className="max-w-72"><div className="truncate font-medium">{a.displayName}</div>
                    {a.description && <div className="truncate text-xs text-muted-foreground">{a.description}</div>}</TableCell>
                  <TableCell className="whitespace-nowrap">
                    <div className="flex items-center gap-1">
                      <StatusBadge tone={a.connected ? 'ok' : 'danger'}>{a.connected ? t('host.online') : t('host.offline')}</StatusBadge>
                      {a.status !== 'Active' && <StatusBadge tone={a.status === 'Revoked' ? 'danger' : 'warn'}>{a.status}</StatusBadge>}
                    </div>
                  </TableCell>
                  <TableCell className="hidden md:table-cell">{a.machineName ?? '—'}</TableCell>
                  <TableCell className="hidden text-xs md:table-cell">{a.agentVersion ?? '—'}</TableCell>
                  <TableCell className="text-right tabular">{a.hostCount}</TableCell>
                  <TableCell className="hidden text-xs whitespace-nowrap tabular lg:table-cell">{fmtDate(a.lastSeenAt)}</TableCell>
                  <TableCell className="text-right"><KebabMenu items={actions(a)} label={t('menu.more')} /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Section>
      {revoke && (
        <ActionDialog title={t('magent.revokeTitle', { name: revoke.displayName })} confirmLabel={t('host.revoke')} danger onClose={() => { setRevoke(null); reload(); }}
          onConfirm={async () => { await post(`/api/v1/management-agents/${revoke.id}/revoke`); }}>
          <p className="text-sm text-muted-foreground">{t('magent.revokeImpact', { n: revoke.hostCount })}</p>
        </ActionDialog>
      )}
      {remove && (
        <ConfirmTypedDialog title={t('magent.deleteTitle', { name: remove.displayName })} name={remove.displayName} confirmLabel={t('common.delete')}
          impact={t('magent.deleteImpact')} onClose={() => { setRemove(null); reload(); }}
          onConfirm={async (typed) => { await post(`/api/v1/management-agents/${remove.id}/delete`, { confirmName: typed }); }} />
      )}
    </div>
  );
}
