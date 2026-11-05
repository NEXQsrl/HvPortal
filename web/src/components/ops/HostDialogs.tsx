import { useState } from 'react';
import { patch, post } from '@/api/client';
import type { EnrollmentInfo, Host } from '@/api/types';
import { AgentSetupGuide } from '@/components/AgentSetupGuide';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { useI18n } from '@/i18n';

export function AddHostDialog({ onCreated, onClose }: { onCreated: (info: EnrollmentInfo, hostId: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  return (
    <ActionDialog title={t('host.add')} confirmLabel={t('host.create')} disabled={!name.trim()} onClose={onClose}
      onConfirm={async () => {
        const r = await post<{ hostId: string; enrollment: EnrollmentInfo }>('/api/v1/hosts', { displayName: name.trim() });
        if (description.trim()) await patch(`/api/v1/hosts/${r.hostId}`, { description });
        onCreated(r.enrollment, r.hostId);
      }}>
      <Field label={t('host.name')} htmlFor="host-name">
        <Input id="host-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label={t('common.description')} htmlFor="host-desc">
        <Textarea id="host-desc" rows={3} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
    </ActionDialog>
  );
}

/** Enrollment guide with the one-time token; the host status updates live while the dialog is open. */
export function EnrollmentDialog({ info, hostId, onClose }: { info: EnrollmentInfo; hostId: string; onClose: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const host = inv.hostById.get(hostId) ?? null;
  const connection = {
    gatewayNames: info.gatewayNames,
    preferredName: new URL(info.gatewayAgentUrl).hostname,
    agentPort: info.agentPort,
    gatewayAgentUrl: info.gatewayAgentUrl,
    caFingerprintSha256: info.caFingerprintSha256,
    listensOnNetwork: info.listensOnNetwork,
    bindAddress: info.bindAddress,
  };
  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="max-h-[90dvh] overflow-hidden sm:max-w-3xl" onInteractOutside={(e) => e.preventDefault()}>
        <DialogHeader>
          <DialogTitle>{t('host.enrollTitle') + (host ? ` — ${host.displayName}` : '')}</DialogTitle>
        </DialogHeader>
        <div className="-mx-4 max-h-[calc(90dvh-9rem)] overflow-auto px-4 py-1">
          <AgentSetupGuide connection={connection} token={info.token} expiresAt={info.expiresAt} host={host} />
        </div>
        <DialogFooter>
          <Button type="button" onClick={onClose}>{t('common.close')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function RevokeHostDialog({ host, onClose }: { host: Host; onClose: () => void }) {
  const { t } = useI18n();
  return (
    <ActionDialog title={t('host.revokeTitle', { name: host.displayName })} confirmLabel={t('host.revoke')} danger onClose={onClose}
      onConfirm={async () => { await post(`/api/v1/hosts/${host.id}/revoke`); }}>
      <p className="text-sm text-muted-foreground">{t('host.revokeImpact')}</p>
    </ActionDialog>
  );
}
