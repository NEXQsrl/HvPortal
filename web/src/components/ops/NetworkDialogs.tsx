import { useState } from 'react';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { Accepted, NetAdapterEntry } from '@/api/ops-types';
import { ErrorAlert, Notice } from '@/components/common';
import { ActionDialog, ConfirmTypedDialog, Field } from '@/components/dialogs';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Switch } from '@/components/ui/switch';
import { useI18n, type TKey } from '@/i18n';
import { useLoad } from '@/lib/hooks';

const SWITCH_NAME = /^[\p{L}\p{N}][\p{L}\p{N} ._()-]{0,63}$/u;
const TYPES = ['External', 'Internal', 'Private'] as const;
type SwitchType = (typeof TYPES)[number];

export interface NetworkHostOption { id: string; displayName: string }

/**
 * New virtual switch (job on the host). Host mode: any type (external needs a physical adapter). Folder mode
 * (<paramref name="groupId"/>): a private network of the folder, within its network quota.
 */
export function NewSwitchDialog({ hosts, initialHostId, groupId, groupName, onQueued, onClose }: {
  hosts: NetworkHostOption[]; initialHostId?: string; groupId?: string; groupName?: string;
  onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t } = useI18n();
  const [hostId, setHostId] = useState(initialHostId && hosts.some((h) => h.id === initialHostId) ? initialHostId : hosts[0]?.id ?? '');
  const [name, setName] = useState('');
  const [type, setType] = useState<SwitchType>(groupId ? 'Private' : 'External');
  const [adapter, setAdapter] = useState('');
  const [shareWithHost, setShareWithHost] = useState(true);
  const [notes, setNotes] = useState('');
  const [key] = useState(newIdempotencyKey);
  const external = type === 'External';
  const adapters = useLoad(() => (external && hostId ? get<NetAdapterEntry[]>(`/api/v1/hosts/${hostId}/net-adapters`) : Promise.resolve([] as NetAdapterEntry[])), [hostId, external]);
  const nameOk = SWITCH_NAME.test(name) && name.trim() === name;
  const valid = !!hostId && nameOk && (!external || !!adapter);
  const free = (adapters.data ?? []).filter((a) => !a.switchName);
  return (
    <ActionDialog title={groupId ? t('net.newForFolder', { name: groupName ?? '' }) : t('net.new')} confirmLabel={t('common.create')} disabled={!valid} onClose={onClose} wide
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/hosts/${hostId}/switches`, {
          name, type, netAdapterName: external ? adapter : undefined, allowManagementOs: external && shareWithHost,
          notes: notes.trim() || undefined, groupId, idempotencyKey: key,
        });
        onQueued(r, `${t('net.new')} — ${name}`);
      }}>
      <Field label={t('vm.host')} htmlFor="sw-host" hint={groupId ? t('net.hostHintFolder') : t('net.hostHint')}>
        <NativeSelect id="sw-host" value={hostId} onChange={(e) => { setHostId(e.target.value); setAdapter(''); }}>
          {hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
        </NativeSelect>
      </Field>
      <Field label={t('common.name')} htmlFor="sw-name" hint={t('net.nameHint')} error={name && !nameOk ? t('net.nameInvalid') : undefined}>
        <Input id="sw-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      {groupId ? <Notice kind="info">{t('net.privateOnly')}</Notice> : (
        <Field label={t('host.switchType')} hint={t('net.typeHint')}>
          <RadioGroup value={type} onValueChange={(v) => setType(v as SwitchType)} className="gap-2">
            {TYPES.map((x) => (
              <label key={x} className="flex cursor-pointer items-start gap-2 rounded-md border p-2 text-sm has-[:checked]:border-primary/50 has-[:checked]:bg-primary/5">
                <RadioGroupItem value={x} className="mt-0.5" />
                <span><span className="font-medium">{t(('net.type.' + x) as TKey)}</span><span className="block text-xs text-muted-foreground">{t(('net.type.' + x + '.help') as TKey)}</span></span>
              </label>
            ))}
          </RadioGroup>
        </Field>
      )}
      {external && (
        <>
          <ErrorAlert error={adapters.error} />
          <Field label={t('net.adapter')} htmlFor="sw-adapter" hint={t('net.adapterHint')}
            error={!adapters.loading && adapters.data && free.length === 0 ? t('net.noFreeAdapter') : undefined}>
            <NativeSelect id="sw-adapter" value={adapter} disabled={adapters.loading} onChange={(e) => setAdapter(e.target.value)}>
              <option value="">{adapters.loading ? t('common.loading') : t('net.chooseAdapter')}</option>
              {(adapters.data ?? []).map((a) => (
                <option key={a.name} value={a.name} disabled={!!a.switchName}>
                  {a.name} — {a.description ?? ''} · {a.linkSpeed ?? ''} · {a.status ?? ''}{a.switchName ? ` · ${t('net.usedBy', { name: a.switchName })}` : ''}
                </option>
              ))}
            </NativeSelect>
          </Field>
          <Field label={t('net.shareWithHost')} hint={t('net.shareWithHostHint')}>
            <div className="flex h-8 items-center"><Switch checked={shareWithHost} onCheckedChange={setShareWithHost} aria-label={t('net.shareWithHost')} /></div>
          </Field>
          <Notice kind="warn">{shareWithHost ? t('net.externalWarning') : t('net.dedicatedWarning')}</Notice>
        </>
      )}
      <Field label={t('net.notes')} htmlFor="sw-notes" hint={t('net.notesHint')}>
        <Input id="sw-notes" value={notes} maxLength={200} onChange={(e) => setNotes(e.target.value)} />
      </Field>
    </ActionDialog>
  );
}

export function DeleteSwitchDialog({ hostId, hostName, switchId, name, connectedVms, onQueued, onClose }: {
  hostId: string; hostName: string; switchId: string; name: string; connectedVms?: number;
  onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t } = useI18n();
  return (
    <ConfirmTypedDialog title={t('net.deleteTitle', { name, host: hostName })} name={name} confirmLabel={t('common.delete')} onClose={onClose}
      impact={connectedVms ? t('net.deleteInUse', { n: connectedVms }) : t('net.deleteImpact')}
      onConfirm={async (typed) => {
        const r = await post<Accepted>(`/api/v1/hosts/${hostId}/switches/delete`, { switchId, name, confirmName: typed, idempotencyKey: newIdempotencyKey() });
        onQueued(r, `${t('net.delete')} — ${name}`);
      }} />
  );
}
