import { Cpu, Disc3, HardDrive, MemoryStick, Network, Plus, Save, Settings2, ShieldCheck, Undo2 } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, get } from '@/api/client';
import type { Accepted, ConfigChange, ConfigResponse, VmConfig } from '@/api/ops-types';
import { realtime } from '@/api/realtime';
import type { Vm } from '@/api/types';
import { ErrorAlert, Loading, Notice, StatusBadge } from '@/components/common';
import { Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { IsoPicker } from '@/components/ops/IsoPicker';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, VM_NAME } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';

interface Draft {
  name: string; notes: string; autoStart: string; autoStartDelay: number; autoStop: string; checkpointType: string;
  cpu: number; reserve: number; limit: number; weight: number;
  startupMb: number; dynamic: boolean; minMb: number; maxMb: number; buffer: number; priority: number;
  secureBoot: boolean; template: string;
  iso: string;
  adapters: Record<string, { switchName: string; vlanId: number }>;
  addAdapter: boolean; addAdapterSwitch: string;
  newDisk: boolean; newDiskGb: number; newDiskFixed: boolean;
  expandPath: string; expandGb: number;
}

const MB = 1048576;

function fromConfig(c: VmConfig): Draft {
  return {
    name: c.name, notes: c.notes ?? '', autoStart: c.automaticStartAction ?? 'Nothing', autoStartDelay: c.automaticStartDelay ?? 0,
    autoStop: c.automaticStopAction ?? 'Save', checkpointType: c.checkpointType ?? 'Production',
    cpu: c.processorCount, reserve: c.processorReserve ?? 0, limit: c.processorLimit ?? 100, weight: c.processorWeight ?? 100,
    startupMb: Math.round(c.memoryStartupBytes / MB), dynamic: c.dynamicMemoryEnabled,
    minMb: Math.round((c.memoryMinimumBytes ?? 512 * MB) / MB), maxMb: Math.round((c.memoryMaximumBytes ?? c.memoryStartupBytes) / MB),
    buffer: c.memoryBuffer ?? 20, priority: c.memoryPriority ?? 50,
    secureBoot: !!c.secureBootEnabled, template: c.secureBootTemplate ?? 'MicrosoftWindows',
    iso: c.dvdDrives[0]?.path ?? '',
    adapters: Object.fromEntries(c.networkAdapters.map((n) => [n.id, { switchName: n.switchName ?? '', vlanId: n.vlanId ?? 0 }])),
    addAdapter: false, addAdapterSwitch: '',
    newDisk: false, newDiskGb: 40, newDiskFixed: false,
    expandPath: '', expandGb: 0,
  };
}

function diff(c: VmConfig, d: Draft): ConfigChange {
  const o = fromConfig(c);
  const ch: ConfigChange = {};
  if (d.name.trim() !== o.name) ch.newName = d.name.trim();
  if (d.notes !== o.notes) ch.notes = d.notes;
  if (d.autoStart !== o.autoStart) ch.automaticStartAction = d.autoStart;
  if (d.autoStartDelay !== o.autoStartDelay) ch.automaticStartDelaySeconds = d.autoStartDelay;
  if (d.autoStop !== o.autoStop) ch.automaticStopAction = d.autoStop;
  if (d.checkpointType !== o.checkpointType) ch.checkpointType = d.checkpointType;
  if (d.cpu !== o.cpu) ch.processorCount = d.cpu;
  if (d.reserve !== o.reserve) ch.processorReserve = d.reserve;
  if (d.limit !== o.limit) ch.processorLimit = d.limit;
  if (d.weight !== o.weight) ch.processorWeight = d.weight;
  if (d.dynamic !== o.dynamic) ch.dynamicMemoryEnabled = d.dynamic;
  if (d.startupMb !== o.startupMb) ch.memoryStartupMb = d.startupMb;
  if (d.dynamic) {
    if (d.minMb !== o.minMb) ch.memoryMinimumMb = d.minMb;
    if (d.maxMb !== o.maxMb) ch.memoryMaximumMb = d.maxMb;
    if (d.buffer !== o.buffer) ch.memoryBuffer = d.buffer;
  }
  if (d.priority !== o.priority) ch.memoryPriority = d.priority;
  if (c.generation === 2) {
    if (d.secureBoot !== o.secureBoot) ch.secureBootEnabled = d.secureBoot;
    if (d.secureBoot && d.template !== o.template) ch.secureBootTemplate = d.template;
  }
  if (d.iso !== o.iso) ch.dvdIsoPath = d.iso;
  const nics = c.networkAdapters.flatMap((n) => {
    const a = d.adapters[n.id]!;
    const b = o.adapters[n.id]!;
    if (a.switchName === b.switchName && a.vlanId === b.vlanId) return [];
    return [{ adapterId: n.id, switchName: a.switchName !== b.switchName ? a.switchName : undefined, vlanId: a.vlanId !== b.vlanId ? a.vlanId : undefined }];
  });
  if (nics.length > 0) ch.networkAdapterChanges = nics;
  if (d.addAdapter) ch.addNetworkAdapterSwitch = d.addAdapterSwitch;
  if (d.newDisk) ch.newDisk = { sizeGb: d.newDiskGb, fixed: d.newDiskFixed };
  if (d.expandPath && d.expandGb > 0) ch.expandDisk = { path: d.expandPath, newSizeGb: d.expandGb };
  return ch;
}

function Block({ icon, title, badge, children, disabled }: { icon: ReactNode; title: string; badge?: ReactNode; children: ReactNode; disabled?: boolean }) {
  return (
    <section className={cn('rounded-lg border bg-card', disabled && 'opacity-80')}>
      <div className="flex items-center gap-2 border-b px-3 py-2 text-sm font-semibold">
        <span className="text-muted-foreground">{icon}</span>{title}<span className="flex-1" />{badge}
      </div>
      <fieldset disabled={disabled} className="grid gap-3 p-3 sm:grid-cols-2 xl:grid-cols-3">{children}</fieldset>
    </section>
  );
}

const num = (v: string) => (v === '' ? 0 : Number(v));

export function VmSettings({ vm }: { vm: Vm }) {
  const { t } = useI18n();
  const ops = useOps();
  const cfg = useLoad(() => get<ConfigResponse>(`/api/v1/vms/${vm.id}/config`), [vm.id]);
  const host = useInventory().hostById.get(vm.hostId);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [pendingJob, setPendingJob] = useState<string | null>(null);

  useEffect(() => { if (cfg.data) setDraft(fromConfig(cfg.data.config)); }, [cfg.data]);
  // Reload the live configuration when the change we queued has finished.
  useEffect(() => realtime.on('jobChanged', (p) => {
    if (pendingJob && p.jobId === pendingJob && p.status && !['Queued', 'Running', 'Reconciling'].includes(p.status)) {
      setPendingJob(null);
      cfg.reload();
    }
  }), [pendingJob, cfg]);

  const change = useMemo(() => (cfg.data && draft ? diff(cfg.data.config, draft) : {}), [cfg.data, draft]);
  const count = Object.keys(change).length;

  if (cfg.loading && !cfg.data) return <Loading />;
  if (!cfg.data || !draft) return <div className="p-4"><Notice kind="danger" title={t('cfg.loadFailed')}>{cfg.error ? String((cfg.error as Error).message) : null}</Notice></div>;
  const { config: c, can, options, switches } = cfg.data;
  const off = vm.powerState === 'Off';
  const set = (p: Partial<Draft>) => setDraft({ ...draft, ...p });
  const offBadge = !off && <StatusBadge tone="warn">{t('cfg.requiresOff')}</StatusBadge>;
  const nameInvalid = draft.name.trim() !== c.name && !VM_NAME.test(draft.name.trim());
  const memInvalid = [draft.startupMb, ...(draft.dynamic ? [draft.minMb, draft.maxMb] : [])].some((m) => m < 32 || m % 2 !== 0)
    || (draft.dynamic && (draft.minMb > draft.startupMb || draft.maxMb < draft.startupMb));

  const apply = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await api<Accepted>('PATCH', `/api/v1/vms/${vm.id}/config`, change);
      setPendingJob(r.jobId);
      ops.queued(r, `${t('jobop.vm.config.Update')} — ${vm.name}`);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        {!can.compute && !can.storage && !can.network && <Notice kind="info">{t('cfg.readOnly')}</Notice>}
        {!off && <Notice kind="warn">{t('cfg.requiresOffHelp')}</Notice>}
        <Block icon={<Settings2 className="size-4" />} title={t('cfg.general')} disabled={!can.compute}>
          <Field label={t('cfg.name')} htmlFor="cfg-name" error={nameInvalid ? t('wiz.nameInvalid') : undefined}>
            <Input id="cfg-name" value={draft.name} maxLength={100} onChange={(e) => set({ name: e.target.value })} />
          </Field>
          <Field label={t('cfg.checkpointType')} htmlFor="cfg-cp">
            <NativeSelect id="cfg-cp" value={draft.checkpointType} onChange={(e) => set({ checkpointType: e.target.value })}>
              {options.checkpointTypes.map((o) => <option key={o} value={o}>{t(('opt.cp.' + o) as TKey)}</option>)}
            </NativeSelect>
          </Field>
          <Field label={t('vm.version')}><Input value={`${c.version} · Gen ${c.generation}`} readOnly disabled /></Field>
          <Field label={t('cfg.autoStart')} htmlFor="cfg-start">
            <NativeSelect id="cfg-start" value={draft.autoStart} onChange={(e) => set({ autoStart: e.target.value })}>
              {options.startActions.map((o) => <option key={o} value={o}>{t(('opt.start.' + o) as TKey)}</option>)}
            </NativeSelect>
          </Field>
          <Field label={t('cfg.autoStartDelay')} htmlFor="cfg-delay">
            <Input id="cfg-delay" type="number" min={0} max={86400} value={draft.autoStartDelay} onChange={(e) => set({ autoStartDelay: num(e.target.value) })} />
          </Field>
          <Field label={t('cfg.autoStop')} htmlFor="cfg-stop">
            <NativeSelect id="cfg-stop" value={draft.autoStop} onChange={(e) => set({ autoStop: e.target.value })}>
              {options.stopActions.map((o) => <option key={o} value={o}>{t(('opt.stop.' + o) as TKey)}</option>)}
            </NativeSelect>
          </Field>
          <Field label={t('cfg.notes')} htmlFor="cfg-notes" className="sm:col-span-2 xl:col-span-3">
            <Textarea id="cfg-notes" rows={3} maxLength={4000} value={draft.notes} onChange={(e) => set({ notes: e.target.value })} />
          </Field>
        </Block>

        <Block icon={<Cpu className="size-4" />} title={t('cfg.processor')} disabled={!can.compute}>
          <Field label={<span className="flex items-center gap-2">{t('cfg.vcpu')}{offBadge}</span>} htmlFor="cfg-cpu">
            <Input id="cfg-cpu" type="number" min={1} max={1024} value={draft.cpu} disabled={!off} onChange={(e) => set({ cpu: num(e.target.value) })} />
          </Field>
          <Field label={t('cfg.reserve')} htmlFor="cfg-res"><Input id="cfg-res" type="number" min={0} max={100} value={draft.reserve} onChange={(e) => set({ reserve: num(e.target.value) })} /></Field>
          <Field label={t('cfg.limit')} htmlFor="cfg-lim"><Input id="cfg-lim" type="number" min={0} max={100} value={draft.limit} onChange={(e) => set({ limit: num(e.target.value) })} /></Field>
          <Field label={t('cfg.weight')} htmlFor="cfg-w"><Input id="cfg-w" type="number" min={1} max={10000} value={draft.weight} onChange={(e) => set({ weight: num(e.target.value) })} /></Field>
        </Block>

        <Block icon={<MemoryStick className="size-4" />} title={t('cfg.memory')} disabled={!can.compute}
          badge={memInvalid ? <StatusBadge tone="danger">{t('wiz.memoryInvalid')}</StatusBadge> : undefined}>
          <Field label={t('cfg.startupMemory')} htmlFor="cfg-mem" aside={formatBytes(draft.startupMb * MB)}>
            <Input id="cfg-mem" type="number" min={32} step={2} value={draft.startupMb} onChange={(e) => set({ startupMb: num(e.target.value) })} />
          </Field>
          <Field label={<span className="flex items-center gap-2">{t('cfg.dynamic')}{offBadge}</span>}>
            <div className="flex h-8 items-center"><Switch checked={draft.dynamic} disabled={!off} onCheckedChange={(v) => set({ dynamic: v })} aria-label={t('cfg.dynamic')} /></div>
          </Field>
          <Field label={t('cfg.priority')} htmlFor="cfg-prio"><Input id="cfg-prio" type="number" min={0} max={100} value={draft.priority} onChange={(e) => set({ priority: num(e.target.value) })} /></Field>
          {draft.dynamic && (
            <>
              <Field label={t('cfg.minMemory')} htmlFor="cfg-min"><Input id="cfg-min" type="number" min={32} step={2} value={draft.minMb} onChange={(e) => set({ minMb: num(e.target.value) })} /></Field>
              <Field label={t('cfg.maxMemory')} htmlFor="cfg-max"><Input id="cfg-max" type="number" min={32} step={2} value={draft.maxMb} onChange={(e) => set({ maxMb: num(e.target.value) })} /></Field>
              <Field label={t('cfg.buffer')} htmlFor="cfg-buf"><Input id="cfg-buf" type="number" min={5} max={2000} value={draft.buffer} onChange={(e) => set({ buffer: num(e.target.value) })} /></Field>
            </>
          )}
        </Block>

        <Block icon={<ShieldCheck className="size-4" />} title={t('cfg.firmware')} disabled={!can.compute || c.generation !== 2} badge={c.generation === 2 ? offBadge : undefined}>
          {c.generation !== 2 ? <p className="text-sm text-muted-foreground sm:col-span-2 xl:col-span-3">{t('cfg.gen1NoFirmware')}</p> : (
            <>
              <Field label={t('cfg.secureBoot')}>
                <div className="flex h-8 items-center"><Switch checked={draft.secureBoot} disabled={!off} onCheckedChange={(v) => set({ secureBoot: v })} aria-label={t('cfg.secureBoot')} /></div>
              </Field>
              <Field label={t('cfg.template')} htmlFor="cfg-tpl">
                <NativeSelect id="cfg-tpl" value={draft.template} disabled={!off || !draft.secureBoot} onChange={(e) => set({ template: e.target.value })}>
                  {options.secureBootTemplates.map((o) => <option key={o} value={o}>{t(('opt.sb.' + o) as TKey)}</option>)}
                </NativeSelect>
              </Field>
              <Field label={t('cfg.tpm')} hint={t('cfg.tpmReadOnly')}>
                <div className="flex h-8 items-center"><StatusBadge tone={c.tpmEnabled ? 'ok' : 'muted'}>{c.tpmEnabled ? t('common.yes') : t('common.no')}</StatusBadge></div>
              </Field>
            </>
          )}
        </Block>

        <Block icon={<Disc3 className="size-4" />} title={t('cfg.dvd')} disabled={!can.storage}>
          <Field label={t('cfg.iso')} htmlFor="cfg-iso" className="sm:col-span-2" hint={c.dvdDrives.length === 0 ? t('cfg.noDvd') : undefined}>
            {host && can.storage
              ? <IsoPicker id="cfg-iso" host={host} value={draft.iso} onChange={(p) => set({ iso: p })} emptyLabel={t('cfg.eject')} />
              : <NativeSelect id="cfg-iso" value={draft.iso} disabled><option value={draft.iso}>{draft.iso || t('cfg.eject')}</option></NativeSelect>}
          </Field>
        </Block>

        <Block icon={<Network className="size-4" />} title={t('cfg.network')} disabled={!can.network}>
          {c.networkAdapters.length === 0 && <p className="text-sm text-muted-foreground">{t('cfg.noAdapters')}</p>}
          {c.networkAdapters.map((n) => {
            const a = draft.adapters[n.id]!;
            return (
              <div key={n.id} className="grid gap-2 rounded-md border p-2 sm:col-span-2 xl:col-span-3 xl:grid-cols-[1fr_2fr_1fr]">
                <div className="min-w-0 text-sm"><div className="font-medium">{n.name}</div><div className="font-mono text-xs text-muted-foreground">{n.macAddress ?? ''}</div></div>
                <Field label={t('cfg.switch')} htmlFor={`nic-${n.id}`}>
                  <NativeSelect id={`nic-${n.id}`} value={a.switchName} onChange={(e) => set({ adapters: { ...draft.adapters, [n.id]: { ...a, switchName: e.target.value } } })}>
                    <option value="">{t('cfg.notConnected')}</option>
                    {[...new Set([...(a.switchName ? [a.switchName] : []), ...switches])].map((s) => <option key={s} value={s}>{s}</option>)}
                  </NativeSelect>
                </Field>
                <Field label={t('cfg.vlan')} htmlFor={`vlan-${n.id}`}>
                  <Input id={`vlan-${n.id}`} type="number" min={0} max={4094} value={a.vlanId} onChange={(e) => set({ adapters: { ...draft.adapters, [n.id]: { ...a, vlanId: num(e.target.value) } } })} />
                </Field>
              </div>
            );
          })}
          <div className="flex flex-wrap items-end gap-3 sm:col-span-2 xl:col-span-3">
            <label className="flex h-8 items-center gap-2 text-sm"><Checkbox checked={draft.addAdapter} onCheckedChange={(v) => set({ addAdapter: !!v })} /><Plus className="size-4" />{t('cfg.addAdapter')}</label>
            {draft.addAdapter && (
              <NativeSelect aria-label={t('cfg.switch')} value={draft.addAdapterSwitch} onChange={(e) => set({ addAdapterSwitch: e.target.value })} className="w-64">
                <option value="">{t('cfg.notConnected')}</option>
                {switches.map((s) => <option key={s} value={s}>{s}</option>)}
              </NativeSelect>
            )}
          </div>
        </Block>

        <Block icon={<HardDrive className="size-4" />} title={t('cfg.disks')} disabled={!can.storage}>
          <div className="space-y-1 sm:col-span-2 xl:col-span-3">
            {c.hardDisks.map((d) => (
              <label key={`${d.controllerType}${d.controllerNumber}${d.controllerLocation}`} className="flex items-center gap-2 rounded-md border px-2 py-1.5 text-sm">
                <input type="radio" name="expand" className="accent-primary" checked={draft.expandPath === d.path} disabled={!d.path}
                  onChange={() => set({ expandPath: d.path ?? '', expandGb: 0 })} aria-label={`${t('cfg.expand')} ${d.path}`} />
                <span className="min-w-0 flex-1 truncate font-mono text-xs" title={d.path ?? ''}>{d.path ?? '—'}</span>
                <span className="text-xs text-muted-foreground">{d.controllerType} {d.controllerNumber}:{d.controllerLocation}</span>
              </label>
            ))}
          </div>
          {draft.expandPath && (
            <div className="flex flex-wrap items-end gap-2 sm:col-span-2 xl:col-span-3">
              <Field label={`${t('cfg.expandTo')} — ${draft.expandPath.split('\\').pop()}`} htmlFor="cfg-expand" hint={t('cfg.noShrink')}>
                <Input id="cfg-expand" type="number" min={1} max={65536} value={draft.expandGb || ''} onChange={(e) => set({ expandGb: num(e.target.value) })} className="w-40" />
              </Field>
              <Button type="button" variant="ghost" size="sm" onClick={() => set({ expandPath: '', expandGb: 0 })}>{t('common.cancel')}</Button>
            </div>
          )}
          <div className="flex flex-wrap items-end gap-3 sm:col-span-2 xl:col-span-3">
            <label className="flex h-8 items-center gap-2 text-sm"><Checkbox checked={draft.newDisk} onCheckedChange={(v) => set({ newDisk: !!v })} /><Plus className="size-4" />{t('cfg.addDisk')}</label>
            {draft.newDisk && (
              <>
                <Input type="number" min={1} max={65536} value={draft.newDiskGb} onChange={(e) => set({ newDiskGb: num(e.target.value) })} className="w-28" aria-label={t('cfg.sizeGb')} />
                <span className="text-sm">GB</span>
                <label className="flex h-8 items-center gap-2 text-sm"><Checkbox checked={draft.newDiskFixed} onCheckedChange={(v) => set({ newDiskFixed: !!v })} />{t('cfg.fixed')}</label>
              </>
            )}
          </div>
          {draft.newDisk && <p className="text-xs text-muted-foreground sm:col-span-2 xl:col-span-3">{t('cfg.newDiskLocation')}</p>}
        </Block>
        <ErrorAlert error={error} />
      </div>
      <div className="flex shrink-0 items-center gap-2 border-t bg-panel px-4 py-2">
        <span className="text-xs text-muted-foreground">{count > 0 ? t('cfg.changes', { n: count }) : ''}{pendingJob ? ` · ${t('jobstatus.Running')}` : ''}</span>
        <span className="flex-1" />
        <Button type="button" variant="outline" size="sm" disabled={count === 0 || busy} onClick={() => setDraft(fromConfig(c))}><Undo2 />{t('cfg.discard')}</Button>
        <Button type="button" size="sm" disabled={count === 0 || busy || nameInvalid || memInvalid || !!pendingJob} onClick={() => void apply()}><Save />{t('cfg.apply')}</Button>
      </div>
    </div>
  );
}
