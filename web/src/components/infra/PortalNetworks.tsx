import { Pencil, Plus, RefreshCw, Tags, Trash2 } from 'lucide-react';
import { useCallback, useState } from 'react';
import { del, get, post, put } from '@/api/client';
import type { NetworkUpdateResult, PortalNetwork } from '@/api/infra-types';
import type { SwitchInfo } from '@/api/ops-types';
import type { Host } from '@/api/types';
import { ErrorAlert, Loading, Notice, Section, StatusBadge } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Textarea } from '@/components/ui/textarea';
import { useI18n } from '@/i18n';
import { GROUP_NAME } from '@/lib/format';
import { useLive, useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';

/** "" = untagged; otherwise 1-4094. Returns undefined when the text is not a valid VLAN. */
export function parseVlan(text: string): number | null | undefined {
  const s = text.trim();
  if (s === '') return null;
  if (!/^\d{1,4}$/.test(s)) return undefined;
  const n = Number(s);
  return n >= 1 && n <= 4094 ? n : undefined;
}

export function VlanBadge({ vlan }: { vlan: number | null | undefined }) {
  const { t } = useI18n();
  return vlan ? <StatusBadge tone="info"><Tags />VLAN {vlan}</StatusBadge> : <StatusBadge tone="muted">{t('pnet.untagged')}</StatusBadge>;
}

/** Networks of the portal on a host (switch + access VLAN), read from the gateway. */
export function usePortalNetworks(hostId: string | null | undefined) {
  return useLoad(() => (hostId ? get<PortalNetwork[]>(`/api/v1/hosts/${hostId}/networks`) : Promise.resolve([] as PortalNetwork[])), [hostId ?? '']);
}

/**
 * Networks of the portal of one host (ADR-030): a name for "virtual switch + VLAN". A VM adapter put on a network gets
 * that switch and that VLAN; changing the VLAN can also re-tag the adapters on it now (one job per VM).
 */
export function PortalNetworksSection({ host }: { host: Host }) {
  const { t } = useI18n();
  const data = usePortalNetworks(host.id);
  const reload = useCallback(() => data.reload(), [data.reload]);
  useLive(reload, ['inventoryChanged'], 2000);
  const [editing, setEditing] = useState<PortalNetwork | 'new' | null>(null);
  const [deleting, setDeleting] = useState<PortalNetwork | null>(null);
  const rows = data.data ?? [];
  const manage = host.canManageNetworks;
  return (
    <Section title={<span className="flex items-center gap-2"><Tags className="size-4 text-muted-foreground" />{t('pnet.title')}</span>} actions={<>
      {manage && host.connected && <Button size="xs" onClick={() => setEditing('new')}><Plus />{t('pnet.new')}</Button>}
      <Button size="xs" variant="ghost" onClick={data.reload} disabled={data.loading} aria-label={t('common.refresh')}><RefreshCw className={cn(data.loading && 'animate-spin')} /></Button>
    </>}>
      <p className="px-3 pt-2 text-xs text-muted-foreground">{t('pnet.help')}</p>
      <ErrorAlert error={data.error} className="mx-3" />
      {data.loading && !data.data ? <Loading /> : (
        <Table>
          <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('pnet.switch')}</TableHead><TableHead>VLAN</TableHead><TableHead>{t('common.description')}</TableHead>{manage && <TableHead className="w-20"><span className="sr-only">{t('vm.actions')}</span></TableHead>}</TableRow></TableHeader>
          <TableBody>
            {rows.length === 0 ? <TableRow><TableCell colSpan={5} className="text-sm text-muted-foreground">{t('pnet.none')}</TableCell></TableRow> : rows.map((n) => (
              <TableRow key={n.id}>
                <TableCell className="font-medium">{n.name}</TableCell>
                <TableCell className="text-xs">{n.switchName}</TableCell>
                <TableCell><VlanBadge vlan={n.vlanId} /></TableCell>
                <TableCell className="max-w-80 text-xs text-muted-foreground [overflow-wrap:anywhere]">{n.description ?? '—'}</TableCell>
                {manage && (
                  <TableCell className="text-right whitespace-nowrap">
                    {n.canManage && <>
                      <Button size="icon-sm" variant="ghost" aria-label={`${t('common.edit')} ${n.name}`} title={t('common.edit')} onClick={() => setEditing(n)}><Pencil /></Button>
                      <Button size="icon-sm" variant="ghost" className="text-destructive" aria-label={`${t('common.delete')} ${n.name}`} title={t('common.delete')} onClick={() => setDeleting(n)}><Trash2 /></Button>
                    </>}
                  </TableCell>
                )}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {editing && <PortalNetworkDialog host={host} network={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); data.reload(); }} />}
      {deleting && (
        <ActionDialog title={t('pnet.deleteTitle', { name: deleting.name })} confirmLabel={t('common.delete')} danger onClose={() => setDeleting(null)}
          onConfirm={async () => { await del(`/api/v1/networks/${deleting.id}`); toast.success(t('pnet.deleted')); setDeleting(null); data.reload(); }}>
          <p className="text-sm text-muted-foreground">{t('pnet.deleteHelp')}</p>
        </ActionDialog>
      )}
    </Section>
  );
}

function PortalNetworkDialog({ host, network, onClose, onSaved }: { host: Host; network: PortalNetwork | null; onClose: () => void; onSaved: () => void }) {
  const { t } = useI18n();
  const switches = useLoad(() => (network ? Promise.resolve([] as SwitchInfo[]) : get<SwitchInfo[]>(`/api/v1/hosts/${host.id}/switches`)), [host.id, network?.id ?? '']);
  const [name, setName] = useState(network?.name ?? '');
  const [switchName, setSwitchName] = useState(network?.switchName ?? '');
  const [vlanText, setVlanText] = useState(network?.vlanId ? String(network.vlanId) : '');
  const [description, setDescription] = useState(network?.description ?? '');
  const [updateAdapters, setUpdateAdapters] = useState(true);
  const vlan = parseVlan(vlanText);
  const sw = switchName || switches.data?.[0]?.name || '';
  const nameOk = GROUP_NAME.test(name.trim());
  const vlanChanged = !!network && vlan !== undefined && (vlan ?? null) !== (network.vlanId ?? null);
  const valid = nameOk && vlan !== undefined && (!!network || !!sw) && description.length <= 500;
  return (
    <ActionDialog title={network ? t('pnet.editTitle', { name: network.name }) : t('pnet.newTitle', { host: host.displayName })} confirmLabel={t('common.save')} disabled={!valid} onClose={onClose}
      onConfirm={async () => {
        const body = { name: name.trim(), switchName: network ? network.switchName : sw, vlanId: vlan ?? null, description: description.trim() || null, updateAdapters: vlanChanged && updateAdapters };
        if (network) {
          const r = await put<NetworkUpdateResult>(`/api/v1/networks/${network.id}`, body);
          toast.success(r.jobs.length > 0 ? t('pnet.savedRetag', { n: r.jobs.length }) : t('settings.saved'));
          if (r.skipped.length > 0) toast.info(t('pnet.skipped', { list: r.skipped.join(', ') }));
        } else {
          await post<PortalNetwork>(`/api/v1/hosts/${host.id}/networks`, body);
          toast.success(t('pnet.created'));
        }
        onSaved();
      }}>
      <Field label={t('common.name')} htmlFor="pnet-name" error={name && !nameOk ? t('pnet.nameInvalid') : undefined}>
        <Input id="pnet-name" value={name} maxLength={64} placeholder="Production VLAN 20" onChange={(e) => setName(e.target.value)} aria-invalid={!!name && !nameOk} autoFocus />
      </Field>
      <Field label={t('pnet.switch')} htmlFor="pnet-switch" hint={network ? t('pnet.switchFixed') : undefined}>
        {network ? <Input id="pnet-switch" value={network.switchName} disabled /> : switches.loading && !switches.data ? <Loading /> : (
          <NativeSelect id="pnet-switch" value={sw} onChange={(e) => setSwitchName(e.target.value)}>
            {(switches.data ?? []).map((s) => <option key={s.id} value={s.name}>{s.name} ({s.type})</option>)}
          </NativeSelect>
        )}
      </Field>
      {!network && switches.data && switches.data.length === 0 && <Notice kind="warn">{t('pnet.noSwitch')}</Notice>}
      <ErrorAlert error={switches.error} />
      <Field label={t('pnet.vlan')} htmlFor="pnet-vlan" hint={t('pnet.vlanHint')} error={vlan === undefined ? t('pnet.vlanInvalid') : undefined}>
        <Input id="pnet-vlan" inputMode="numeric" className="w-32 tabular" value={vlanText} placeholder={t('pnet.untagged')} onChange={(e) => setVlanText(e.target.value)} aria-invalid={vlan === undefined} />
      </Field>
      <Field label={t('common.description')} htmlFor="pnet-desc">
        <Textarea id="pnet-desc" value={description} maxLength={500} rows={2} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      {vlanChanged && (
        <label className="flex items-start gap-2 rounded-md border p-2 text-sm">
          <Checkbox checked={updateAdapters} onCheckedChange={(v) => setUpdateAdapters(!!v)} className="mt-0.5" />
          <span>{t('pnet.updateAdapters', { from: network.vlanId ?? t('pnet.untagged'), to: vlan ?? t('pnet.untagged') })}
            <span className="block text-xs text-muted-foreground">{t('pnet.updateAdaptersHelp')}</span></span>
        </label>
      )}
      <p className="text-xs text-muted-foreground">{t('pnet.trunkNote')}</p>
    </ActionDialog>
  );
}
