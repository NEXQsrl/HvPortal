import { FolderSearch, Gauge, Network, Plus, RefreshCw, Trash2, X } from 'lucide-react';
import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import { get, put } from '@/api/client';
import type { Accepted, Group, GroupNetwork, GroupPolicy, GroupPolicyLevel, GroupPolicyView } from '@/api/ops-types';
import { ErrorAlert, HostIcon, Loading, Meter, Notice, Section, StatusBadge } from '@/components/common';
import { ActionDialog, Field, HelpTip } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { NativeSelect } from '@/components/ui/native-select';
import { Switch } from '@/components/ui/switch';
import { HostFileExplorer } from '@/components/vm/HostFileExplorer';
import { useI18n, type TKey } from '@/i18n';
import { useLive, useLoad } from '@/lib/hooks';
import { normalizeFolder, validateLocation } from '@/lib/hostPaths';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';
import { HostFolderPicker } from './HostFolderPicker';
import { DeleteSwitchDialog, NewSwitchDialog } from './NetworkDialogs';

/** Editable form of the limits: numbers as text (empty = no limit), lists as plain arrays. */
export interface LimitsDraft {
  maxVms: string; maxProcessors: string; maxMemoryGb: string; maxDiskGb: string; maxNetworks: string;
  hosts: string[];
  restrictNetworks: boolean; networks: { hostId: string; name: string }[];
  restrictStorage: boolean; storage: { hostId: string; path: string }[];
}

const text = (n: number | null | undefined) => (n == null ? '' : String(n));

export function draftFromPolicy(p: GroupPolicy | null | undefined): LimitsDraft {
  return {
    maxVms: text(p?.maxVms), maxProcessors: text(p?.maxProcessors), maxMemoryGb: p?.maxMemoryMb == null ? '' : String(p.maxMemoryMb / 1024),
    maxDiskGb: text(p?.maxDiskGb), maxNetworks: text(p?.maxNetworks),
    hosts: p?.hosts ?? [],
    restrictNetworks: p?.networks != null, networks: p?.networks ?? [],
    restrictStorage: (p?.storage?.length ?? 0) > 0, storage: p?.storage ?? [],
  };
}

const whole = (s: string) => s.trim() === '' || /^\d{1,9}$/.test(s.trim());
const decimal = (s: string) => s.trim() === '' || /^\d{1,7}([.,]\d{1,3})?$/.test(s.trim());

export function limitsValid(d: LimitsDraft): boolean {
  return whole(d.maxVms) && whole(d.maxProcessors) && decimal(d.maxMemoryGb) && whole(d.maxDiskGb) && whole(d.maxNetworks)
    && (!d.restrictStorage || (d.storage.length > 0 && d.storage.every((s) => validateLocation(s.path) === null)));
}

export function policyFromDraft(d: LimitsDraft): GroupPolicy {
  const int = (s: string) => (s.trim() === '' ? null : Number(s.trim()));
  return {
    maxVms: int(d.maxVms), maxProcessors: int(d.maxProcessors),
    maxMemoryMb: d.maxMemoryGb.trim() === '' ? null : Math.round(Number(d.maxMemoryGb.trim().replace(',', '.')) * 1024),
    maxDiskGb: int(d.maxDiskGb), maxNetworks: int(d.maxNetworks),
    hosts: d.hosts.length > 0 ? d.hosts : null,
    networks: d.restrictNetworks ? d.networks : null,
    storage: d.restrictStorage ? d.storage.map((s) => ({ hostId: s.hostId, path: normalizeFolder(s.path) })) : null,
  };
}

export function limitsEmpty(d: LimitsDraft): boolean {
  const p = policyFromDraft(d);
  return p.maxVms == null && p.maxProcessors == null && p.maxMemoryMb == null && p.maxDiskGb == null && p.maxNetworks == null && !p.hosts && !p.networks && !p.storage;
}

/** Form of the limits of a group: quotas, enabled hosts, allowed networks and storage folders (every field has a help tip). */
export function LimitsEditor({ value, onChange }: { value: LimitsDraft; onChange: (d: LimitsDraft) => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [browse, setBrowse] = useState<number | null>(null);
  const set = (p: Partial<LimitsDraft>) => onChange({ ...value, ...p });
  // Networks and storage are listed for the enabled hosts, or for every host when none is enabled.
  const scoped = value.hosts.length > 0 ? inv.hosts.filter((h) => value.hosts.includes(h.id)) : inv.hosts;
  const hasNetwork = (hostId: string, name: string) => value.networks.some((n) => n.hostId === hostId && n.name === name);
  const quota = (id: keyof LimitsDraft, label: TKey, hint: TKey | undefined, ok: boolean) => (
    <Field label={t(label)} htmlFor={`lim-${id}`} hint={hint ? t(hint) : undefined} error={ok ? undefined : ' '}>
      <Input id={`lim-${id}`} inputMode="decimal" value={value[id] as string} placeholder={t('lim.noLimit')} aria-invalid={!ok || undefined}
        onChange={(e) => set({ [id]: e.target.value } as Partial<LimitsDraft>)} />
    </Field>
  );
  return (
    <div className="space-y-4">
      <div>
        <div className="mb-2 flex items-center gap-1.5 text-sm font-medium">{t('lim.quotas')}<HelpTip>{t('lim.quotasHint')}</HelpTip></div>
        <div className="grid gap-3 sm:grid-cols-3">
          {quota('maxVms', 'lim.maxVms', undefined, whole(value.maxVms))}
          {quota('maxProcessors', 'lim.maxCpu', 'lim.maxCpuHint', whole(value.maxProcessors))}
          {quota('maxMemoryGb', 'lim.maxMemory', 'lim.maxMemoryHint', decimal(value.maxMemoryGb))}
          {quota('maxDiskGb', 'lim.maxDisk', 'lim.maxDiskHint', whole(value.maxDiskGb))}
          {quota('maxNetworks', 'lim.maxNetworks', 'lim.maxNetworksHint', whole(value.maxNetworks))}
        </div>
      </div>

      <div>
        <div className="mb-2 flex items-center gap-1.5 text-sm font-medium">{t('lim.hosts')}<HelpTip>{t('lim.hostsHint')}</HelpTip></div>
        <div className="grid gap-1 rounded-md border p-2 sm:grid-cols-2">
          {inv.hosts.length === 0 && <span className="text-sm text-muted-foreground">{t('net.noHosts')}</span>}
          {inv.hosts.map((h) => (
            <label key={h.id} className="flex items-center gap-2 text-sm">
              <Checkbox checked={value.hosts.includes(h.id)} onCheckedChange={(c) => set({ hosts: c ? [...value.hosts, h.id] : value.hosts.filter((x) => x !== h.id) })} />
              <HostIcon connected={h.connected} access={h.access} /><span className="truncate">{h.displayName}</span>
            </label>
          ))}
        </div>
      </div>

      <div>
        <div className="mb-2 flex items-center gap-2">
          <Switch id="lim-restrict-net" checked={value.restrictNetworks} onCheckedChange={(c) => set({ restrictNetworks: c })} />
          <Label htmlFor="lim-restrict-net" className="cursor-pointer text-sm font-medium">{t('lim.restrictNetworks')}</Label>
          <HelpTip>{t('lim.restrictNetworksHint')}</HelpTip>
        </div>
        {value.restrictNetworks && (
          <div className="space-y-2 rounded-md border p-2">
            {scoped.map((h) => (
              <div key={h.id}>
                <div className="text-xs font-medium text-muted-foreground">{h.displayName}</div>
                <div className="flex flex-wrap gap-x-4 gap-y-1 pt-1">
                  {h.virtualSwitches.length === 0 && <span className="text-xs text-muted-foreground">{t('lim.noSwitches')}</span>}
                  {h.virtualSwitches.map((s) => (
                    <label key={s} className="flex items-center gap-1.5 text-sm">
                      <Checkbox checked={hasNetwork(h.id, s)}
                        onCheckedChange={(c) => set({ networks: c ? [...value.networks, { hostId: h.id, name: s }] : value.networks.filter((n) => !(n.hostId === h.id && n.name === s)) })} />
                      <Network className="size-3.5 text-muted-foreground" />{s}
                    </label>
                  ))}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div>
        <div className="mb-2 flex items-center gap-2">
          <Switch id="lim-restrict-sto" checked={value.restrictStorage}
            onCheckedChange={(c) => set({ restrictStorage: c, storage: c && value.storage.length === 0 && scoped[0] ? [{ hostId: scoped[0].id, path: '' }] : value.storage })} />
          <Label htmlFor="lim-restrict-sto" className="cursor-pointer text-sm font-medium">{t('lim.restrictStorage')}</Label>
          <HelpTip>{t('lim.restrictStorageHint')}</HelpTip>
        </div>
        {value.restrictStorage && (
          <div className="space-y-2 rounded-md border p-2">
            {value.storage.map((s, i) => {
              const bad = s.path.trim() !== '' && validateLocation(s.path) !== null;
              const host = inv.hostById.get(s.hostId);
              return (
                <div key={i} className="flex flex-wrap items-center gap-2">
                  <NativeSelect aria-label={t('vm.host')} value={s.hostId} className="w-44"
                    onChange={(e) => set({ storage: value.storage.map((x, j) => (j === i ? { ...x, hostId: e.target.value } : x)) })}>
                    {inv.hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
                  </NativeSelect>
                  <Input aria-label={t('lim.storageList')} value={s.path} placeholder={t('lim.pathPlaceholder')} aria-invalid={bad || undefined}
                    className="min-w-48 flex-1 font-mono text-xs" onChange={(e) => set({ storage: value.storage.map((x, j) => (j === i ? { ...x, path: e.target.value } : x)) })} />
                  {host?.canBrowseFolders && host.canCreateVm && (
                    <Button type="button" size="icon-sm" variant="outline" aria-label={t('picker.browse')} title={t('picker.browse')} onClick={() => setBrowse(i)}><FolderSearch /></Button>
                  )}
                  <Button type="button" size="icon-sm" variant="ghost" aria-label={t('common.delete')} onClick={() => set({ storage: value.storage.filter((_, j) => j !== i) })}><X /></Button>
                  {bad && <div className="w-full text-xs text-destructive" role="alert">{t('lim.pathInvalid')}</div>}
                </div>
              );
            })}
            <Button type="button" size="xs" variant="outline" disabled={inv.hosts.length === 0}
              onClick={() => set({ storage: [...value.storage, { hostId: (scoped[0] ?? inv.hosts[0])!.id, path: '' }] })}><Plus />{t('lim.addPath')}</Button>
          </div>
        )}
      </div>
      {browse !== null && value.storage[browse] && (
        <HostFolderPicker hostId={value.storage[browse]!.hostId} mode="folder" initialPath={value.storage[browse]!.path || null}
          onPickFolder={(f) => { set({ storage: value.storage.map((x, j) => (j === browse ? { ...x, path: f.path } : x)) }); setBrowse(null); }}
          onClose={() => setBrowse(null)} />
      )}
    </div>
  );
}

/** Edit the limits of an existing group (groups.manage + hosts.manage). */
export function GroupLimitsDialog({ group, onSaved, onClose }: { group: Group; onSaved: () => void; onClose: () => void }) {
  const { t } = useI18n();
  const view = useLoad(() => get<GroupPolicyView>(`/api/v1/groups/${group.id}/policy`), [group.id]);
  const [draft, setDraft] = useState<LimitsDraft | null>(null);
  const current = draft ?? (view.data ? draftFromPolicy(view.data.policy) : null);
  return (
    <ActionDialog title={t('lim.dialogTitle', { name: group.name })} description={t('lim.advancedHelp')} confirmLabel={t('common.save')} wide
      disabled={!current || !limitsValid(current) || view.data?.canEdit === false} onClose={onClose}
      footer={current && !limitsEmpty(current) ? <Button type="button" variant="ghost" onClick={() => setDraft(draftFromPolicy(null))}><Trash2 />{t('lim.clear')}</Button> : undefined}
      onConfirm={async () => {
        await put(`/api/v1/groups/${group.id}/policy`, policyFromDraft(current!));
        toast.success(t('lim.saved'), group.name);
        onSaved();
      }}>
      <ErrorAlert error={view.error} />
      {!current ? <Loading /> : <LimitsEditor value={current} onChange={setDraft} />}
    </ActionDialog>
  );
}

function UsageRow({ label, used, max, unit }: { label: string; used: number; max: number | null | undefined; unit?: string }) {
  const { t } = useI18n();
  const u = unit ? ` ${unit}` : '';
  return (
    <div className="grid grid-cols-[minmax(7rem,1fr)_minmax(5rem,8rem)_auto] items-center gap-2 text-sm">
      <span className="truncate text-muted-foreground">{label}</span>
      {max != null ? <Meter value={max > 0 ? (used / max) * 100 : used > 0 ? 100 : 0} label={label} /> : <span />}
      <span className={cn('text-right text-xs tabular', max != null && used > max && 'font-semibold text-destructive')}>
        {used}{max != null ? ` / ${max}${u}` : `${u} · ${t('lim.noLimit')}`}
      </span>
    </div>
  );
}

function LevelUsage({ level, own }: { level: GroupPolicyLevel; own: boolean }) {
  const { t } = useI18n();
  const p = level.policy;
  const gb = (mb: number) => Math.round((mb / 1024) * 10) / 10;
  return (
    <div className="space-y-1.5 rounded-md border p-3">
      <div className="flex items-center gap-2 text-xs font-medium">
        {own ? t('lim.thisGroup') : <Link className="text-primary hover:underline" to={`/groups/${level.groupId}`}>{t('lim.inherited', { name: level.name })}</Link>}
      </div>
      <UsageRow label={t('lim.maxVms')} used={level.usage.vms} max={p.maxVms} />
      <UsageRow label={t('lim.maxCpu')} used={level.usage.processors} max={p.maxProcessors} />
      <UsageRow label={t('lim.maxMemory')} used={gb(level.usage.memoryMb)} max={p.maxMemoryMb == null ? null : gb(p.maxMemoryMb)} />
      <UsageRow label={t('lim.maxDisk')} used={level.usage.diskGb} max={p.maxDiskGb} />
      <UsageRow label={t('lim.maxNetworks')} used={level.usage.networks} max={p.maxNetworks} />
      {level.usage.diskUnknownVms > 0 && <p className="text-xs text-warning">{t('lim.diskUnknown', { n: level.usage.diskUnknownVms })}</p>}
    </div>
  );
}

/**
 * Group page: limits and usage (this group and the groups above it), what the group may use on each enabled host, its
 * private networks (create / delete within the quota) and the files of its storage folders.
 */
export function GroupLimitsPanel({ group, onQueued }: { group: Group; onQueued: (r: Accepted, label: string) => void }) {
  const { t } = useI18n();
  const [live, setLive] = useState(false);
  const view = useLoad(() => get<GroupPolicyView>(`/api/v1/groups/${group.id}/policy${live ? '?live=true' : ''}`), [group.id, live]);
  const reload = useCallback(() => view.reload(), [view.reload]);
  useLive(reload, ['groupsChanged', 'jobChanged'], 1500);
  const [editing, setEditing] = useState(false);
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<GroupNetwork | null>(null);
  const [fileHost, setFileHost] = useState('');
  const v = view.data;
  if (!v) return view.error ? <ErrorAlert error={view.error} /> : null;
  const networkHosts = v.hosts.filter((h) => h.canCreateNetwork);
  const fileHosts = v.hosts.filter((h) => h.canBrowseFiles);
  const selectedFileHost = fileHosts.find((h) => h.id === fileHost) ?? fileHosts[0];
  const nothing = v.levels.length === 0 && v.hosts.length === 0 && v.networks.length === 0;
  if (nothing && !v.canEdit) return null;
  return (
    <>
      <Section title={<span className="flex items-center gap-1.5"><Gauge className="size-4 text-muted-foreground" />{t('lim.usage')}<HelpTip>{t('lim.advancedHelp')}</HelpTip></span>}
        actions={<>
          {v.levels.some((l) => l.policy.maxDiskGb != null) && (
            <Button size="xs" variant="ghost" title={t('lim.refresh')} aria-label={t('lim.refresh')} disabled={view.loading}
              onClick={() => { if (live) view.reload(); else setLive(true); }}><RefreshCw className={cn(view.loading && 'animate-spin')} /></Button>
          )}
          {v.canEdit && <Button size="xs" variant="outline" onClick={() => setEditing(true)}>{t('lim.edit').replace('…', '')}</Button>}
        </>} bodyClassName="space-y-3 p-3">
        {v.levels.length === 0 && <p className="text-sm text-muted-foreground">{t('lim.none')}</p>}
        {v.levels.length > 0 && (
          <div className="grid gap-3 lg:grid-cols-2">
            {v.levels.map((l) => <LevelUsage key={l.groupId} level={l} own={l.groupId === group.id} />)}
          </div>
        )}
        {v.hosts.length > 0 && (
          <div className="space-y-1.5">
            <div className="text-xs font-medium text-muted-foreground">{t('lim.hosts')}</div>
            {v.hosts.map((h) => (
              <div key={h.id} className="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-md border px-3 py-2 text-sm">
                <span className="flex items-center gap-1.5 font-medium"><HostIcon connected={h.connected} access={h.ready ? 'Ok' : null} />{h.displayName}</span>
                <span className="text-xs text-muted-foreground">{t('lim.networksList')}: {h.networksRestricted ? (h.switches.length > 0 ? h.switches.join(', ') : t('lim.ownNetworksOnly')) : t('lim.anyNetwork')}</span>
                <span className="text-xs text-muted-foreground">{t('lim.storageList')}: {h.storageRoots.length > 0 ? <span className="font-mono">{h.storageRoots.join(', ')}</span> : t('lim.anyStorage')}</span>
              </div>
            ))}
          </div>
        )}
      </Section>

      {(v.networks.length > 0 || networkHosts.length > 0) && (
        <Section title={<span className="flex items-center gap-1.5"><Network className="size-4 text-muted-foreground" />{t('lim.networksOwned')}</span>}
          actions={networkHosts.length > 0 && <Button size="xs" onClick={() => setCreating(true)}><Plus />{t('net.new')}</Button>} bodyClassName="p-3">
          {v.networks.length === 0 ? <p className="text-sm text-muted-foreground">{t('lim.noNetworks')}</p> : (
            <ul className="divide-y rounded-md border text-sm">
              {v.networks.map((n) => (
                <li key={n.id} className="flex items-center gap-3 px-3 py-1.5">
                  <Network className="size-4 text-muted-foreground" /><span className="font-medium">{n.name}</span>
                  <span className="text-xs text-muted-foreground">{n.hostName}</span>
                  {n.groupId !== group.id && <StatusBadge tone="muted">{n.groupName}</StatusBadge>}
                  <span className="flex-1" />
                  {n.canDelete && n.switchId && <Button size="icon-sm" variant="ghost" className="text-destructive" aria-label={`${t('net.delete')} ${n.name}`} title={t('net.delete')} onClick={() => setDeleting(n)}><Trash2 /></Button>}
                </li>
              ))}
            </ul>
          )}
        </Section>
      )}

      {selectedFileHost && (
        <Section title={<span className="flex items-center gap-1.5">{t('lim.files')}<HelpTip>{t('lim.filesHelp')}</HelpTip></span>}
          actions={fileHosts.length > 1 && (
            <NativeSelect aria-label={t('vm.host')} value={selectedFileHost.id} onChange={(e) => setFileHost(e.target.value)} className="h-7 w-44">
              {fileHosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
            </NativeSelect>
          )} bodyClassName="p-3">
          <HostFileExplorer key={selectedFileHost.id} host={selectedFileHost} groupId={group.id} readOnly={!selectedFileHost.canUploadFiles} canManage={selectedFileHost.canManageFiles === true} />
        </Section>
      )}
      {v.levels.some((l) => l.policy.maxMemoryMb != null) && v.canCreateVm && <Notice kind="info">{t('lim.memoryMax')}</Notice>}

      {editing && <GroupLimitsDialog group={group} onSaved={() => view.reload()} onClose={() => setEditing(false)} />}
      {creating && <NewSwitchDialog hosts={networkHosts} groupId={group.id} groupName={group.name} onQueued={onQueued} onClose={() => setCreating(false)} />}
      {deleting && deleting.switchId && (
        <DeleteSwitchDialog hostId={deleting.hostId} hostName={deleting.hostName ?? ''} switchId={deleting.switchId} name={deleting.name}
          onQueued={onQueued} onClose={() => setDeleting(null)} />
      )}
    </>
  );
}
