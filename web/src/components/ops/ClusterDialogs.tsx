import { CircleAlert, CircleCheck, CircleHelp, Info, LoaderCircle, TriangleAlert, Wrench } from 'lucide-react';
import { useCallback, useState } from 'react';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { ClusterIssue, ClusterNodeEnrollment, ClusterSummary, MaintenanceCheck } from '@/api/cluster-types';
import type { Accepted } from '@/api/ops-types';
import type { EnrollmentInfo, Host } from '@/api/types';
import { AgentSetupGuide } from '@/components/AgentSetupGuide';
import { ErrorAlert, Loading, Notice } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Textarea } from '@/components/ui/textarea';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { GuideDialog } from './HostDialogs';

const BYTE_ARGS = new Set(['needed', 'available', 'free', 'size', 'reserve', 'remaining']);

/** Text of a cluster problem in the user's language (the server's English message when there is no translation). */
export function useIssueText() {
  const { t } = useI18n();
  return useCallback((issue: ClusterIssue) => {
    const key = ('cl.issue.' + issue.code) as TKey;
    const vars: Record<string, string> = { subject: issue.subject ?? '' };
    for (const [k, v] of Object.entries(issue.args ?? {})) vars[k] = BYTE_ARGS.has(k) && v !== '' && !Number.isNaN(Number(v)) ? formatBytes(Number(v)) : v;
    const text = t(key, vars);
    // No translation, or a value the server did not send: its own (English) message is complete.
    return text === key || /\{[a-z]+\}/i.test(text) ? issue.message : text;
  }, [t]);
}

const ICONS = { Critical: CircleAlert, Warning: TriangleAlert, Unknown: CircleHelp, Info, Ok: CircleCheck } as const;
const TONES = { Critical: 'text-destructive', Warning: 'text-warning', Unknown: 'text-muted-foreground', Info: 'text-info', Ok: 'text-success' } as const;

/** Problems first (critical, warning, unknown), then notes. */
export function IssueList({ issues, className }: { issues: ClusterIssue[]; className?: string }) {
  const text = useIssueText();
  const rank = { Critical: 0, Warning: 1, Unknown: 2, Info: 3, Ok: 4 } as const;
  const sorted = [...issues].sort((a, b) => (rank[a.level] ?? 5) - (rank[b.level] ?? 5));
  return (
    <ul className={cn('space-y-1.5', className)}>
      {sorted.map((i, n) => {
        const Icon = ICONS[i.level] ?? CircleHelp;
        return (
          <li key={`${i.code}-${i.subject ?? ''}-${n}`} className="flex items-start gap-2 text-sm">
            <Icon className={cn('mt-0.5 size-4 shrink-0', TONES[i.level])} />
            <span className="[overflow-wrap:anywhere]">{text(i)}</span>
          </li>
        );
      })}
    </ul>
  );
}

// ---------------------------------------------------------------- add host: failover cluster

export interface ClusterFieldsValue { target: string; name: string; nodesText: string; hostIds: string[] }

export const newNodeNames = (text: string) => text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length > 0);

/** "Add host" in cluster mode: a new cluster (or an existing one), new nodes to enroll and hosts already in the portal. */
export function ClusterHostFields({ value, onChange }: { value: ClusterFieldsValue; onChange: (v: ClusterFieldsValue) => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const set = (p: Partial<ClusterFieldsValue>) => onChange({ ...value, ...p });
  const standalone = inv.hosts.filter((h) => !h.clusterId && h.connectionMode !== 'Remote' && h.status !== 'Revoked');
  const names = newNodeNames(value.nodesText);
  const duplicate = new Set(names.map((n) => n.toLowerCase())).size !== names.length;
  return (
    <>
      <Field label={t('cl.add.target')} htmlFor="cl-target">
        <NativeSelect id="cl-target" value={value.target} onChange={(e) => set({ target: e.target.value })}>
          <option value="new">{t('cl.add.new')}</option>
          {inv.clusters.filter((c) => c.can.manage).map((c) => <option key={c.id} value={c.id}>{t('cl.add.existing', { name: c.name })}</option>)}
        </NativeSelect>
      </Field>
      <Field label={t('cl.add.nodes')} htmlFor="cl-nodes" hint={t('cl.add.nodesHint')} error={duplicate ? t('cl.add.nodesHint') : undefined}>
        <Textarea id="cl-nodes" rows={3} value={value.nodesText} placeholder={'HV01\nHV02'} className="font-mono text-sm" onChange={(e) => set({ nodesText: e.target.value })} />
      </Field>
      {standalone.length > 0 && (
        <Field label={t('cl.add.existingHosts')} hint={t('cl.add.existingHint')}>
          <div className="max-h-36 space-y-1 overflow-auto rounded-md border p-2">
            {standalone.map((h) => (
              <label key={h.id} className="flex items-center gap-2 text-sm">
                <Checkbox checked={value.hostIds.includes(h.id)}
                  onCheckedChange={(c) => set({ hostIds: c ? [...value.hostIds, h.id] : value.hostIds.filter((x) => x !== h.id) })} />
                <span>{h.displayName}</span>
                {h.clusterFacts?.name && <span className="text-xs text-muted-foreground">{t('cl.add.reportsCluster', { name: h.clusterFacts.name })}</span>}
              </label>
            ))}
          </div>
        </Field>
      )}
      <Notice kind="info" title={t('cl.add.prereqTitle')}>
        <ul className="list-disc space-y-0.5 pl-4 text-xs">
          <li>{t('cl.add.prereq1')}</li>
          <li>{t('cl.add.prereq2')}</li>
          <li>{t('cl.add.prereq3')}</li>
          <li>{t('cl.add.prereq4')}</li>
        </ul>
      </Notice>
    </>
  );
}

/** Creates the cluster (or adds to an existing one); returns the cluster id, its name and the enrollments of the new nodes. */
export async function createClusterNodes(value: ClusterFieldsValue, displayName: string, description: string, clusters: ClusterSummary[]) {
  const names = newNodeNames(value.nodesText);
  if (value.target === 'new') {
    const r = await post<{ clusterId: string; nodes: ClusterNodeEnrollment[] }>('/api/v1/clusters', {
      name: displayName, description: description || undefined, newNodes: names, hostIds: value.hostIds,
    });
    return { clusterId: r.clusterId, name: displayName, nodes: r.nodes };
  }
  const nodes: ClusterNodeEnrollment[] = [];
  for (const hostId of value.hostIds) await post(`/api/v1/clusters/${value.target}/nodes`, { hostId });
  for (const n of names) {
    const r = await post<{ hostId: string; enrollment: EnrollmentInfo }>(`/api/v1/clusters/${value.target}/nodes`, { displayName: n });
    nodes.push({ hostId: r.hostId, displayName: n, enrollment: r.enrollment });
  }
  return { clusterId: value.target, name: clusters.find((c) => c.id === value.target)?.name ?? '', nodes };
}

/** Enrollment of every new node of a cluster: each one gets its own agent; the state follows the inventory live. */
export function ClusterEnrollmentDialog({ clusterName, nodes, onClose }: { clusterName: string; nodes: ClusterNodeEnrollment[]; onClose: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [guide, setGuide] = useState<ClusterNodeEnrollment | null>(null);
  if (guide) {
    return (
      <GuideDialog title={`${t('host.enrollTitle')} — ${guide.displayName}`} info={guide.enrollment} onClose={() => setGuide(null)}>
        {(connection) => <AgentSetupGuide connection={connection} token={guide.enrollment.token} expiresAt={guide.enrollment.expiresAt}
          host={inv.hostById.get(guide.hostId) ?? null} clusterNode />}
      </GuideDialog>
    );
  }
  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="sm:max-w-xl" onInteractOutside={(e) => e.preventDefault()}>
        <DialogHeader>
          <DialogTitle>{t('cl.enroll.title', { name: clusterName })}</DialogTitle>
          <DialogDescription>{t('cl.enroll.help')}</DialogDescription>
        </DialogHeader>
        <ul className="divide-y rounded-lg border">
          {nodes.map((n) => {
            const h = inv.hostById.get(n.hostId);
            const connected = !!h?.connected;
            return (
              <li key={n.hostId} className="flex items-center gap-3 px-3 py-2 text-sm">
                {connected ? <CircleCheck className="size-4 text-success" /> : <LoaderCircle className="size-4 animate-spin text-info" />}
                <span className="min-w-0 flex-1">
                  <span className="font-medium">{n.displayName}</span>
                  <span className="block text-xs text-muted-foreground">{connected ? t('cl.enroll.done') : t('cl.enroll.waiting')}</span>
                </span>
                <Button size="sm" variant="outline" onClick={() => setGuide(n)}>{t('cl.enroll.guide')}</Button>
              </li>
            );
          })}
        </ul>
        <DialogFooter><Button type="button" onClick={onClose}>{t('common.close')}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Adds a host already in the portal, or a new one to enroll, as a node of the cluster. */
export function AddClusterNodeDialog({ cluster, onEnroll, onClose }: {
  cluster: { id: string; name: string }; onEnroll: (node: ClusterNodeEnrollment) => void; onClose: () => void;
}) {
  const { t } = useI18n();
  const inv = useInventory();
  const standalone = inv.hosts.filter((h) => !h.clusterId && h.connectionMode !== 'Remote' && h.status !== 'Revoked');
  const [mode, setMode] = useState<'existing' | 'new'>(standalone.length > 0 ? 'existing' : 'new');
  const [hostId, setHostId] = useState(standalone[0]?.id ?? '');
  const [name, setName] = useState('');
  return (
    <ActionDialog title={t('cl.addNode.title', { name: cluster.name })} confirmLabel={t('common.add')} wide
      disabled={mode === 'existing' ? !hostId : !name.trim()} onClose={onClose}
      onConfirm={async () => {
        if (mode === 'existing') {
          await post(`/api/v1/clusters/${cluster.id}/nodes`, { hostId });
          inv.reload();
          return;
        }
        const r = await post<{ hostId: string; enrollment: EnrollmentInfo }>(`/api/v1/clusters/${cluster.id}/nodes`, { displayName: name.trim() });
        inv.reload();
        onEnroll({ hostId: r.hostId, displayName: name.trim(), enrollment: r.enrollment });
      }}>
      <RadioGroup value={mode} onValueChange={(v) => setMode(v as 'existing' | 'new')} className="gap-2">
        <label className="flex items-center gap-2 text-sm"><RadioGroupItem value="existing" disabled={standalone.length === 0} />{t('cl.addNode.existing')}</label>
        {mode === 'existing' && (standalone.length === 0 ? <p className="pl-6 text-xs text-muted-foreground">{t('cl.addNode.none')}</p> : (
          <NativeSelect className="ml-6 w-auto" value={hostId} onChange={(e) => setHostId(e.target.value)} aria-label={t('cl.addNode.existing')}>
            {standalone.map((h) => <option key={h.id} value={h.id}>{h.displayName}{h.clusterFacts?.name ? ` — ${t('cl.add.reportsCluster', { name: h.clusterFacts.name })}` : ''}</option>)}
          </NativeSelect>
        ))}
        <label className="flex items-center gap-2 text-sm"><RadioGroupItem value="new" />{t('cl.addNode.new')}</label>
        {mode === 'new' && <Input className="ml-6 w-auto" value={name} maxLength={64} placeholder="HV03" onChange={(e) => setName(e.target.value)} aria-label={t('host.name')} />}
      </RadioGroup>
    </ActionDialog>
  );
}

// ---------------------------------------------------------------- maintenance

/**
 * Enter or exit maintenance of a cluster node. The cluster is read first (through any node): what blocks is shown and
 * cannot be overridden here; warnings must be acknowledged. The job re-checks everything when it runs.
 */
export function MaintenanceDialog({ host, action, onQueued, onClose }: {
  host: Host; action: 'Enter' | 'Exit'; onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t } = useI18n();
  const check = useLoad(() => get<MaintenanceCheck>(`/api/v1/hosts/${host.id}/maintenance?action=${action}`), [host.id, action]);
  const [ack, setAck] = useState(false);
  const [storage, setStorage] = useState(true);
  const [failback, setFailback] = useState(false);
  const [key] = useState(newIdempotencyKey);
  const c = check.data;
  const warnings = c?.warnings.filter((w) => w.level !== 'Info') ?? [];
  const notes = c?.warnings.filter((w) => w.level === 'Info') ?? [];
  const enter = action === 'Enter';
  return (
    <ActionDialog title={t(enter ? 'cl.maint.titleEnter' : 'cl.maint.titleExit', { name: host.displayName })}
      confirmLabel={t(enter ? 'cl.maint.confirmEnter' : 'cl.maint.confirmExit')} wide
      disabled={!c || !c.allowed || (warnings.length > 0 && !ack)} onClose={onClose}
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/hosts/${host.id}/maintenance`, {
          action, storageMaintenance: storage, failback: !enter && failback, acknowledgeWarnings: ack, idempotencyKey: key,
        });
        onQueued(r, `${t(enter ? 'jobop.host.maintenance.Enter' : 'jobop.host.maintenance.Exit')} — ${host.displayName}`);
      }}>
      <div className="flex items-start gap-2 rounded-lg bg-muted/50 p-3 text-sm">
        <Wrench className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
        <p>{t(enter ? 'cl.maint.explainEnter' : 'cl.maint.explainExit')}</p>
      </div>
      {check.loading && !c && <div className="flex items-center gap-2 text-sm text-muted-foreground"><Loading className="p-0" />{t('cl.maint.checking')}</div>}
      <ErrorAlert error={check.error} />
      {c && (
        <>
          {c.blocking.length > 0 && (
            <Notice kind="danger" title={t('cl.maint.blocked')}>
              <IssueList issues={c.blocking} />
              <p className="mt-2 text-xs">{t('cl.maint.blockedNote')}</p>
            </Notice>
          )}
          {c.allowed && warnings.length === 0 && <Notice kind="ok">{t('cl.maint.ok')}</Notice>}
          {warnings.length > 0 && (
            <Notice kind="warn" title={t('cl.maint.warnings')}>
              <IssueList issues={warnings} />
            </Notice>
          )}
          {notes.length > 0 && <Notice kind="info" title={t('cl.maint.notes')}><IssueList issues={notes} /></Notice>}
          {c.s2d && (
            <label className="flex items-start gap-2 text-sm">
              <Checkbox checked={storage} onCheckedChange={(v) => setStorage(!!v)} className="mt-0.5" />
              <span>{t(enter ? 'cl.maint.storage' : 'cl.maint.storageExit')}</span>
            </label>
          )}
          {!enter && (
            <label className="flex items-start gap-2 text-sm">
              <Checkbox checked={failback} onCheckedChange={(v) => setFailback(!!v)} className="mt-0.5" />
              <span>{t('cl.maint.failback')}</span>
            </label>
          )}
          {c.allowed && warnings.length > 0 && (
            <label className="flex items-start gap-2 rounded-md border border-warning/40 bg-warning/5 p-2 text-sm">
              <Checkbox checked={ack} onCheckedChange={(v) => setAck(!!v)} className="mt-0.5" />
              <span className="font-medium">{t('cl.maint.ack')}</span>
            </label>
          )}
        </>
      )}
    </ActionDialog>
  );
}
