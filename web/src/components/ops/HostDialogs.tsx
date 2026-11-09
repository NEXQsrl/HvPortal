import { Boxes, Network, Server } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { get, patch, post, put } from '@/api/client';
import type { ClusterNodeEnrollment } from '@/api/cluster-types';
import type { EnrollmentInfo, GatewayConnection, Host, ManagementAgent } from '@/api/types';
import { AgentSetupGuide } from '@/components/AgentSetupGuide';
import { Loading, Notice } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Textarea } from '@/components/ui/textarea';
import { useI18n } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { ClusterHostFields, createClusterNodes, newNodeNames, type ClusterFieldsValue } from './ClusterDialogs';

type Mode = 'agent' | 'remote' | 'cluster';

/** Same rules as RemoteHosts on the server (the server re-validates). */
const COMPUTER = /^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/;
const isIpv4 = (s: string) => /^[0-9.]+$/.test(s);
const qualified = (u: string) => u.includes('\\') || u.includes('@');

interface RemoteFields { agentId: string; computer: string; user: string; password: string; https: boolean }

function remoteProblem(f: RemoteFields, passwordRequired: boolean): 'computer' | 'user' | 'userHost' | 'password' | null {
  const c = f.computer.trim();
  if (!COMPUTER.test(c) || c.toLowerCase() === 'localhost' || (isIpv4(c) && c.split('.').length !== 4)) return 'computer';
  if (!f.user.trim()) return 'user';
  if (isIpv4(c) && !qualified(f.user.trim())) return 'userHost';
  if (passwordRequired && !f.password) return 'password';
  return null;
}

/** Fields shared by "add remote host" and "connection and credentials". */
function RemoteHostFields({ value, onChange, agents, passwordRequired }: {
  value: RemoteFields; onChange: (v: RemoteFields) => void; agents: ManagementAgent[]; passwordRequired: boolean;
}) {
  const { t } = useI18n();
  const set = (p: Partial<RemoteFields>) => onChange({ ...value, ...p });
  const problem = remoteProblem(value, passwordRequired);
  const touchedComputer = value.computer.trim() !== '';
  return (
    <>
      <Field label={t('remote.agent')} htmlFor="rh-agent" hint={t('remote.agentHint')}>
        <NativeSelect id="rh-agent" value={value.agentId} onChange={(e) => set({ agentId: e.target.value })}>
          {agents.filter((a) => a.status !== 'Revoked').map((a) => (
            <option key={a.id} value={a.id}>{a.displayName}{a.machineName ? ` (${a.machineName})` : ''}{a.connected ? '' : ` — ${t('host.offline')}`}</option>
          ))}
        </NativeSelect>
      </Field>
      <Field label={t('remote.computer')} htmlFor="rh-computer" hint={t('remote.computerHint')}
        error={touchedComputer && problem === 'computer' ? t('remote.computerInvalid') : undefined}>
        <Input id="rh-computer" value={value.computer} maxLength={253} autoComplete="off" placeholder="hv01.lab.local" onChange={(e) => set({ computer: e.target.value })} />
      </Field>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('remote.user')} htmlFor="rh-user" hint={t('remote.userHint')}
          error={problem === 'userHost' ? t('remote.userNeedsHost') : undefined}>
          <Input id="rh-user" value={value.user} maxLength={256} autoComplete="off" placeholder="HV01\Administrator" onChange={(e) => set({ user: e.target.value })} />
        </Field>
        <Field label={t('remote.password')} htmlFor="rh-pass" hint={passwordRequired ? undefined : t('remote.passwordKeep')}>
          <Input id="rh-pass" type="password" value={value.password} maxLength={256} autoComplete="new-password"
            placeholder={passwordRequired ? '' : '••••••••'} onChange={(e) => set({ password: e.target.value })} />
        </Field>
      </div>
      <label className="flex items-start gap-2 text-sm">
        <Checkbox checked={value.https} onCheckedChange={(v) => set({ https: !!v })} className="mt-0.5" />
        <span>{t('remote.https')}<span className="block text-xs text-muted-foreground">{t('remote.httpsHelp')}</span></span>
      </label>
      <Notice kind="info" title={t('remote.prereqTitle')}>
        <ul className="list-disc space-y-0.5 pl-4 text-xs">
          <li>{t('remote.prereqAccount')}</li>
          <li>{t('remote.prereqPorts')}</li>
          <li>{t('remote.prereqTrusted')}</li>
          <li>{t('remote.prereqStorage')}</li>
        </ul>
      </Notice>
    </>
  );
}

export function AddHostDialog({ onCreated, onRemoteCreated, onClusterCreated, onNewManagementAgent, onClose }: {
  onCreated: (info: EnrollmentInfo, hostId: string) => void;
  onRemoteCreated: (hostId: string) => void;
  /** Failover cluster (ADR-041): the cluster and the enrollments of its new nodes (each one gets its own agent). */
  onClusterCreated: (clusterId: string, name: string, nodes: ClusterNodeEnrollment[]) => void;
  onNewManagementAgent: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [mode, setMode] = useState<Mode>('agent');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const agents = useLoad(() => get<ManagementAgent[]>('/api/v1/management-agents'));
  const usable = (agents.data ?? []).filter((a) => a.status !== 'Revoked');
  const [remote, setRemote] = useState<RemoteFields>({ agentId: '', computer: '', user: '', password: '', https: false });
  const fields = { ...remote, agentId: remote.agentId || usable[0]?.id || '' };
  const remoteInvalid = mode === 'remote' && (!fields.agentId || remoteProblem(fields, true) !== null);
  const inv = useInventory();
  const [cluster, setCluster] = useState<ClusterFieldsValue>({ target: 'new', name: '', nodesText: '', hostIds: [] });
  const clusterNodes = newNodeNames(cluster.nodesText);
  const clusterInvalid = mode === 'cluster' && ((clusterNodes.length === 0 && cluster.hostIds.length === 0)
    || new Set(clusterNodes.map((n) => n.toLowerCase())).size !== clusterNodes.length);
  const needsName = mode !== 'cluster' || cluster.target === 'new';
  return (
    <ActionDialog title={t('host.add')} confirmLabel={t('host.create')} wide disabled={(needsName && !name.trim()) || remoteInvalid || clusterInvalid} onClose={onClose}
      onConfirm={async () => {
        if (mode === 'cluster') {
          const r = await createClusterNodes(cluster, name.trim(), description.trim(), inv.clusters);
          onClusterCreated(r.clusterId, r.name, r.nodes);
          return;
        }
        if (mode === 'agent') {
          const r = await post<{ hostId: string; enrollment: EnrollmentInfo }>('/api/v1/hosts', { displayName: name.trim() });
          if (description.trim()) await patch(`/api/v1/hosts/${r.hostId}`, { description });
          onCreated(r.enrollment, r.hostId);
          return;
        }
        const r = await post<{ hostId: string }>('/api/v1/hosts/remote', {
          displayName: name.trim(), managementAgentId: fields.agentId, computerName: fields.computer.trim(),
          userName: fields.user.trim(), password: fields.password, useHttps: fields.https,
        });
        if (description.trim()) await patch(`/api/v1/hosts/${r.hostId}`, { description });
        onRemoteCreated(r.hostId);
      }}>
      <Field label={t('host.connectionMode')}>
        <RadioGroup value={mode} onValueChange={(v) => setMode(v as Mode)} className="grid gap-2 sm:grid-cols-3">
          {(['agent', 'cluster', 'remote'] as const).map((m) => (
            <label key={m} className={cn('flex cursor-pointer items-start gap-2 rounded-lg border p-3 text-sm has-[[data-state=checked]]:border-primary/50 has-[[data-state=checked]]:bg-primary/5')}>
              <RadioGroupItem value={m} className="mt-0.5" />
              <span className="space-y-0.5">
                <span className="flex items-center gap-1.5 font-medium">{m === 'agent' ? <Server className="size-3.5" /> : m === 'cluster' ? <Boxes className="size-3.5" /> : <Network className="size-3.5" />}
                  {t(m === 'agent' ? 'host.modeAgent' : m === 'cluster' ? 'host.modeCluster' : 'host.modeRemote')}</span>
                <span className="block text-xs text-muted-foreground">{t(m === 'agent' ? 'host.modeAgentHelp' : m === 'cluster' ? 'host.modeClusterHelp' : 'host.modeRemoteHelp')}</span>
              </span>
            </label>
          ))}
        </RadioGroup>
      </Field>
      {needsName && (
        <>
          <Field label={t(mode === 'cluster' ? 'cl.add.name' : 'host.name')} htmlFor="host-name">
            <Input id="host-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} autoFocus />
          </Field>
          <Field label={t('common.description')} htmlFor="host-desc">
            <Textarea id="host-desc" rows={2} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />
          </Field>
        </>
      )}
      {mode === 'cluster' && <ClusterHostFields value={cluster} onChange={setCluster} />}
      {mode === 'remote' && (agents.loading && !agents.data ? <Loading /> : usable.length === 0 ? (
        <Notice kind="warn" title={t('remote.noAgents')}>
          <p className="text-xs">{t('remote.noAgentsHelp')}</p>
          <Button type="button" size="sm" variant="outline" className="mt-2" onClick={onNewManagementAgent}>{t('magent.new')}</Button>
        </Notice>
      ) : <RemoteHostFields value={fields} onChange={setRemote} agents={agents.data ?? []} passwordRequired />)}
    </ActionDialog>
  );
}

/** Connection and credentials of a remote host. The stored password is never shown; leave it empty to keep it. */
export function RemoteHostDialog({ host, onClose }: { host: Host; onClose: () => void }) {
  const { t } = useI18n();
  const agents = useLoad(() => get<ManagementAgent[]>('/api/v1/management-agents'));
  const [value, setValue] = useState<RemoteFields>({
    agentId: host.managementAgentId ?? '', computer: host.remote?.computerName ?? '', user: host.remote?.userName ?? '', password: '', https: host.remote?.useHttps ?? false,
  });
  const invalid = !value.agentId || remoteProblem(value, false) !== null;
  return (
    <ActionDialog title={t('remote.editTitle', { name: host.displayName })} confirmLabel={t('common.save')} wide disabled={invalid} onClose={onClose}
      onConfirm={async () => {
        await put(`/api/v1/hosts/${host.id}/remote`, {
          managementAgentId: value.agentId, computerName: value.computer.trim(), userName: value.user.trim(),
          password: value.password === '' ? null : value.password, useHttps: value.https,
        });
      }}>
      {agents.loading && !agents.data ? <Loading /> : <RemoteHostFields value={value} onChange={setValue} agents={agents.data ?? []} passwordRequired={false} />}
    </ActionDialog>
  );
}

/** Enrollment guide with the one-time token; the host status updates live while the dialog is open. */
export function EnrollmentDialog({ info, hostId, onClose }: { info: EnrollmentInfo; hostId: string; onClose: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const host = inv.hostById.get(hostId) ?? null;
  return (
    <GuideDialog title={t('host.enrollTitle') + (host ? ` — ${host.displayName}` : '')} info={info} onClose={onClose}>
      {(connection) => <AgentSetupGuide connection={connection} token={info.token} expiresAt={info.expiresAt} host={host} clusterNode={!!info.clusterNode || !!host?.clusterId} />}
    </GuideDialog>
  );
}

/** Same guide for a management agent (installed with -Management on a server without Hyper-V). */
export function ManagementEnrollmentDialog({ info, agent, connected, onClose }: { info: EnrollmentInfo; agent: string; connected: boolean; onClose: () => void }) {
  const { t } = useI18n();
  return (
    <GuideDialog title={`${t('magent.enrollTitle')} — ${agent}`} info={info} onClose={onClose}>
      {(connection) => <AgentSetupGuide connection={connection} token={info.token} expiresAt={info.expiresAt} management managementConnected={connected} />}
    </GuideDialog>
  );
}

export function GuideDialog({ title, info, onClose, children }: {
  title: string; info: EnrollmentInfo; onClose: () => void;
  children: (connection: GatewayConnection) => ReactNode;
}) {
  const { t } = useI18n();
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
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        <div className="-mx-4 max-h-[calc(90dvh-9rem)] overflow-auto px-4 py-1">{children(connection)}</div>
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
      <p className="text-sm text-muted-foreground">{t(host.connectionMode === 'Remote' ? 'remote.revokeImpact' : 'host.revokeImpact')}</p>
    </ActionDialog>
  );
}
