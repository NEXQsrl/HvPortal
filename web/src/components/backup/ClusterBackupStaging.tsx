import { Archive, FolderCog } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { put } from '@/api/client';
import type { ClusterDetail } from '@/api/cluster-types';
import { Notice, Section, StatusBadge } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { useI18n } from '@/i18n';
import { stagingFolderOk, suggestStagingPath } from '@/lib/staging';
import { toast } from '@/lib/toast';

/** Nodes of the cluster that have no backup staging folder (only nodes that are hosts of the portal). */
export function nodesWithoutStaging(data: ClusterDetail) {
  return data.nodes.filter((n) => n.hostId && !n.backupStagingPath);
}

/**
 * Backup staging folder of the nodes (ADR-031, ADR-041): a share with credentials needs one on every node, because the
 * VMs move between nodes. Lists the folder of each node and sets the same folder on all of them at once.
 */
export function ClusterBackupStagingSection({ data, onChanged }: { data: ClusterDetail; onChanged: () => void }) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const missing = nodesWithoutStaging(data);
  const needed = data.backup.credentialRepositories > 0;
  return (
    <Section title={<span className="flex items-center gap-2"><Archive className="size-4 text-muted-foreground" />{t('staging.title')}</span>} bodyClassName="space-y-3 p-3"
      actions={data.can.setBackupStaging && <Button size="sm" variant="outline" onClick={() => setOpen(true)}><FolderCog />{t('clst.setAll')}</Button>}>
      <p className="text-xs text-muted-foreground">{t('clst.help')}</p>
      {needed && missing.length > 0 && (
        <Notice kind="warn" title={t('clst.missingTitle', { n: missing.length })}>{t('clst.missing', { nodes: missing.map((n) => n.hostName ?? n.name).join(', ') })}</Notice>
      )}
      <ul className="divide-y rounded-md border text-sm">
        {data.nodes.filter((n) => n.hostId).map((n) => (
          <li key={n.hostId} className="flex flex-wrap items-center justify-between gap-2 px-3 py-1.5">
            <Link to={`/hosts/${n.hostId}?tab=settings`} className="font-medium hover:underline">{n.hostName ?? n.name}</Link>
            {n.backupStagingPath ? <span className="font-mono text-xs [overflow-wrap:anywhere]">{n.backupStagingPath}</span>
              : <StatusBadge tone={needed ? 'warn' : 'muted'}>{t('clst.none')}</StatusBadge>}
          </li>
        ))}
      </ul>
      {open && <ClusterBackupStagingDialog data={data} onClose={() => setOpen(false)} onDone={onChanged} />}
    </Section>
  );
}

function ClusterBackupStagingDialog({ data, onClose, onDone }: { data: ClusterDetail; onClose: () => void; onDone: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const nodeHosts = data.nodes.map((n) => (n.hostId ? inv.hostById.get(n.hostId) : undefined)).filter((h) => !!h);
  // The same suggestion on every node when possible (a drive all nodes have), else the one of the first node.
  const suggestions = nodeHosts.map((h) => suggestStagingPath(h!)).filter((s): s is string => !!s);
  const common = suggestions.find((s) => nodeHosts.every((h) => h!.volumes.length === 0 || h!.volumes.some((v) => v.name.toUpperCase().startsWith(s.slice(0, 2).toUpperCase()))));
  const existing = data.nodes.filter((n) => n.hostId && n.backupStagingPath);
  // A folder some nodes already use comes first, so that every node ends up with the same one.
  const [path, setPath] = useState(existing[0]?.backupStagingPath ?? common ?? suggestions[0] ?? '');
  const [replace, setReplace] = useState(false);
  const value = path.trim();
  const valid = stagingFolderOk(value);
  const differing = existing.filter((n) => n.backupStagingPath!.toLowerCase() !== value.toLowerCase());
  return (
    <ActionDialog title={t('clst.dialogTitle', { name: data.name })} confirmLabel={t('clst.apply')} onClose={onClose} disabled={!valid}
      onConfirm={async () => {
        const r = await put<{ updated: string[]; kept: { name: string; path: string }[] }>(`/api/v1/clusters/${data.id}/backup-staging`, { path: value, replaceExisting: replace });
        toast.success(t('clst.done', { n: r.updated.length }), r.kept.length > 0 ? t('clst.kept', { nodes: r.kept.map((k) => k.name).join(', ') }) : undefined);
        inv.reload();
        onDone();
      }}>
      <div className="space-y-3 text-sm">
        <p className="text-muted-foreground">{t('clst.dialogHelp')}</p>
        <Field label={t('staging.title')}>
          <Input value={path} onChange={(e) => setPath(e.target.value)} className="font-mono" placeholder="D:\HvPortal-staging" aria-invalid={!valid} />
        </Field>
        {!valid && value !== '' && <div className="text-xs text-destructive">{t('staging.invalid')}</div>}
        {differing.length > 0 && (
          <label className="flex items-start gap-2">
            <Checkbox checked={replace} onCheckedChange={(v) => setReplace(!!v)} className="mt-0.5" />
            <span>{t('clst.replace')}<span className="block text-xs text-muted-foreground">
              {differing.map((n) => `${n.hostName ?? n.name}: ${n.backupStagingPath}`).join(' · ')}</span></span>
          </label>
        )}
        <Notice kind="info">{t('clst.space')}</Notice>
      </div>
    </ActionDialog>
  );
}
