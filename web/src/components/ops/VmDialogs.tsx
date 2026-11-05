import { useState } from 'react';
import { newIdempotencyKey, post } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import type { PowerOperation, Vm } from '@/api/types';
import { KeyValue, Notice, PowerBadge } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { Input } from '@/components/ui/input';
import { useI18n, type TKey } from '@/i18n';
import { CHECKPOINT_NAME } from '@/lib/format';

export function PowerDialog({ vm, op, onQueued, onClose }: { vm: Vm; op: PowerOperation; onQueued: (r: Accepted, label: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const destructive = vm.destructiveOperations.includes(op);
  const [typed, setTyped] = useState('');
  const [key] = useState(newIdempotencyKey);
  const label = t(('op.' + op) as TKey);
  return (
    <ActionDialog title={t('confirm.title', { op: label, name: vm.name })} confirmLabel={label} danger={destructive}
      disabled={destructive && typed !== vm.name} onClose={onClose}
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/vms/${vm.id}/power`, { operation: op, confirmName: destructive ? typed : undefined, idempotencyKey: key });
        onQueued(r, `${label} — ${vm.name}`);
      }}>
      <Notice kind={destructive ? 'danger' : 'info'}>{t(('impact.' + op) as TKey)}</Notice>
      <KeyValue items={[['VM', vm.name], [t('vm.host'), vm.hostName], [t('vm.state'), <PowerBadge key="s" state={vm.powerState} critical={vm.isCritical} />]]} />
      {destructive && (
        <Field label={t('confirm.typeName', { name: vm.name })} htmlFor="confirm-name">
          <Input id="confirm-name" autoComplete="off" value={typed} onChange={(e) => setTyped(e.target.value)} autoFocus />
        </Field>
      )}
    </ActionDialog>
  );
}

export function CreateCheckpointDialog({ vm, onQueued, onClose }: { vm: Vm; onQueued: (r: Accepted, label: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const [key] = useState(newIdempotencyKey);
  const valid = name.trim() === '' || CHECKPOINT_NAME.test(name.trim());
  return (
    <ActionDialog title={`${t('cp.create')} — ${vm.name}`} confirmLabel={t('cp.create')} disabled={!valid} onClose={onClose}
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/vms/${vm.id}/checkpoints`, { name: name.trim() || undefined, idempotencyKey: key });
        onQueued(r, `${t('cp.create')} — ${vm.name}`);
      }}>
      <Field label={t('cp.name')} htmlFor="cp-name" error={valid ? undefined : t('wiz.nameInvalid')}>
        <Input id="cp-name" value={name} maxLength={100} placeholder={t('cp.namePlaceholder')} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Notice kind="warn">{t('cp.notBackup')}</Notice>
    </ActionDialog>
  );
}
