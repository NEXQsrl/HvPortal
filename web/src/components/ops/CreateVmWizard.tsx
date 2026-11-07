import { Check, Cpu, Disc3, FolderOpen, FolderSearch, Info, ListChecks, Network, RotateCcw, Server, Settings2, ShieldCheck, HardDrive } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { Accepted, GroupPolicyView, HostDefaultsResponse, NewVmSpec, VmTemplate } from '@/api/ops-types';
import { ErrorAlert, GroupIcon, KeyValue, Notice } from '@/components/common';
import { Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from '@/components/ui/input-group';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { parseVlan } from '@/components/infra/PortalNetworks';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, VM_NAME } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { joinPath, normalizeFolder, safeName, validateLocation } from '@/lib/hostPaths';
import { cn } from '@/lib/utils';
import { GroupPickerList } from './GroupDialogs';
import { HostFolderPicker } from './HostFolderPicker';
import { IsoPicker } from './IsoPicker';
import { PresetInput } from './PresetInput';

const STEPS = [
  { key: 'name', label: 'wiz.step.name', icon: Server },
  { key: 'compute', label: 'wiz.step.compute', icon: Cpu },
  { key: 'storage', label: 'wiz.step.storage', icon: HardDrive },
  { key: 'network', label: 'wiz.step.network', icon: Network },
  { key: 'media', label: 'wiz.step.media', icon: ShieldCheck },
  { key: 'options', label: 'wiz.step.options', icon: Settings2 },
  { key: 'summary', label: 'wiz.step.summary', icon: ListChecks },
] as const;

const MEMORY_PRESETS = [512, 1024, 2048, 4096, 8192, 12288, 16384, 32768, 65536];

/** What a template proposes for a new VM: the hardware of the model (everything but the generation can be changed). */
function fromTemplate(t: VmTemplate): Partial<NewVmSpec> {
  return {
    templateId: t.id, generation: t.generation === 1 ? 1 : 2, processorCount: t.processorCount, memoryStartupMb: t.memoryStartupMb, dynamicMemory: t.dynamicMemory,
    memoryMinimumMb: Math.min(t.memoryMinimumMb ?? 512, t.memoryStartupMb), memoryMaximumMb: Math.max(t.memoryStartupMb, Math.min(t.memoryMaximumMb ?? 8192, 1048576)),
    diskSizeGb: t.bootDiskGb, diskFixed: false, secureBootTemplate: t.secureBootTemplate ?? 'MicrosoftWindows', enableTpm: t.tpmEnabled,
  };
}
const DISK_PRESETS = [40, 60, 80, 127, 256, 512, 1024];

/**
 * New VM wizard. With <paramref name="template"/> the VM is created from a template: its host, generation and disks
 * are the template's (the disks are copied); name, resources, network, location and options are chosen here.
 */
export function CreateVmWizard({ hostId: initialHost, groupId: initialGroup, template, onQueued, onClose }: {
  hostId?: string; groupId?: string; template?: VmTemplate; onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t } = useI18n();
  const inv = useInventory();
  const [groupId, setGroupId] = useState<string | null>(initialGroup ?? null);
  // Inside a group: its limits (quotas in use, enabled hosts). Users of the group create only on those hosts.
  const folder = useLoad(() => (groupId ? get<GroupPolicyView>(`/api/v1/groups/${groupId}/policy`).catch(() => null) : Promise.resolve(null)), [groupId]);
  const hosts = useMemo(() => {
    const own = inv.hosts.filter((h) => h.canCreateVm).map((h) => ({ id: h.id, displayName: h.displayName }));
    const viaGroup = (folder.data?.hosts ?? []).filter((h) => h.canCreateVm && !own.some((o) => o.id === h.id)).map((h) => ({ id: h.id, displayName: h.displayName }));
    // A template is used on its own host (the server checks that the user may create VMs there).
    if (template) return [{ id: template.hostId, displayName: template.hostName }];
    return [...own, ...viaGroup];
  }, [inv.hosts, folder.data, template]);
  const [step, setStep] = useState(0);
  const [hostId, setHostId] = useState(initialHost && hosts.some((h) => h.id === initialHost) ? initialHost : hosts[0]?.id ?? '');
  useEffect(() => {
    if (hosts.length > 0 && !hosts.some((h) => h.id === hostId)) setHostId(hosts[0]!.id);
  }, [hosts, hostId]);
  // The template in use: given by the caller (Templates page) or chosen in the first step among those of the host.
  const [tpl, setTpl] = useState<VmTemplate | null>(template ?? null);
  const [spec, setSpec] = useState<NewVmSpec>(() => ({
    name: '', generation: 2, processorCount: 2, memoryStartupMb: 4096, dynamicMemory: false, memoryMinimumMb: 512, memoryMaximumMb: 8192,
    diskSizeGb: 60, diskFixed: false, switchName: '', isoPath: '', secureBootTemplate: 'MicrosoftWindows', enableTpm: false,
    checkpointType: 'Production', automaticStartAction: 'StartIfRunning', startAfterCreate: false, notes: '', location: '',
    ...(template ? fromTemplate(template) : {}),
  }));
  const available = useLoad(() => (hostId && !template
    ? get<{ items: VmTemplate[] }>(`/api/v1/templates?hostId=${hostId}${groupId ? `&groupId=${groupId}` : ''}`).then((r) => r.items.filter((i) => i.can.deploy)).catch(() => [])
    : Promise.resolve([] as VmTemplate[])), [hostId, groupId, template]);
  const chooseTemplate = (next: VmTemplate | null) => {
    setTpl(next);
    setSpec((s) => (next ? { ...s, ...fromTemplate(next) } : { ...s, templateId: undefined, diskSizeGb: 60 }));
  };
  // A template belongs to its host: changing host goes back to an empty VM.
  useEffect(() => {
    if (!template && tpl && tpl.hostId !== hostId) { setTpl(null); setSpec((s) => ({ ...s, templateId: undefined, diskSizeGb: 60 })); }
  }, [hostId, tpl, template]);
  const [browsing, setBrowsing] = useState(false);
  const [locationFree, setLocationFree] = useState<number | null>(null);
  const [defaults, setDefaults] = useState<HostDefaultsResponse | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [key] = useState(newIdempotencyKey);
  const set = (p: Partial<NewVmSpec>) => setSpec((s) => ({ ...s, ...p }));
  const [vlanText, setVlanText] = useState('');

  useEffect(() => {
    if (!hostId) return;
    let cancelled = false;
    setDefaults(null);
    setLoadError(null);
    get<HostDefaultsResponse>(`/api/v1/hosts/${hostId}/defaults${groupId ? `?groupId=${groupId}` : ''}`).then((d) => {
      if (cancelled) return;
      setDefaults(d);
      // A group that restricts storage: the VM goes into one of its folders (the first one by default).
      setSpec((s) => ({ ...s, networkId: undefined, switchName: d.switches.includes(s.switchName ?? '') ? s.switchName : d.switches[0] ?? '', location: d.storageRoots?.[0] ?? '' }));
    }).catch((e) => { if (!cancelled) setLoadError(e); });
    setSpec((s) => ({ ...s, location: '', isoPath: '' }));
    setLocationFree(null);
    return () => { cancelled = true; };
  }, [hostId, groupId]);

  const name = spec.name.trim();
  const nameError = !name ? null
    : !VM_NAME.test(name) ? t('wiz.nameInvalid')
    : defaults?.existingNames.some((n) => n.toLowerCase() === name.toLowerCase()) ? t('wiz.nameTaken') : null;
  const memOk = (v: number | undefined) => v !== undefined && Number.isInteger(v) && v >= 32 && v % 2 === 0;
  const memoryError = !memOk(spec.memoryStartupMb) || (spec.dynamicMemory && (!memOk(spec.memoryMinimumMb) || !memOk(spec.memoryMaximumMb)
    || spec.memoryMinimumMb! > spec.memoryStartupMb || spec.memoryMaximumMb! < spec.memoryStartupMb)) ? t('wiz.memoryInvalid') : null;
  const location = spec.location?.trim() ? normalizeFolder(spec.location) : '';
  const locationCode = location ? validateLocation(location) : null;
  const locationError = locationCode ? t(('wiz.' + locationCode) as TKey) : null;
  const diskError = tpl
    ? (!Number.isInteger(spec.diskSizeGb) || spec.diskSizeGb < tpl.bootDiskGb || spec.diskSizeGb > 65536 ? t('tpl.wiz.diskMin', { gb: tpl.bootDiskGb }) : null)
    : spec.diskSizeGb !== 0 && (!Number.isInteger(spec.diskSizeGb) || spec.diskSizeGb < 1 || spec.diskSizeGb > 65536) ? t('wiz.diskInvalid') : null;
  const networks = defaults?.networks ?? [];
  const selectedNetwork = spec.networkId ? networks.find((x) => x.id === spec.networkId) : undefined;
  const vlan = parseVlan(vlanText);
  const vlanError = !spec.networkId && spec.switchName && vlan === undefined ? t('pnet.vlanInvalid') : null;
  // One entry per STEPS item (name, compute, storage, network, media, options, summary).
  const stepValid = [
    !!hostId && !!name && !nameError && !!defaults,
    spec.processorCount >= 1 && spec.processorCount <= 1024 && !memoryError,
    !diskError && !locationError,
    !vlanError,
    true, true, true,
  ];
  const canFinish = stepValid.every(Boolean);
  const host = inv.hostById.get(hostId);
  const canBrowse = !!host?.canBrowseFolders;
  const storageRoots = defaults?.storageRoots ?? [];
  const group = groupId ? inv.groupById.get(groupId) : undefined;
  const level = folder.data?.levels[0];
  const usageText = level ? [
    level.policy.maxVms != null && `VM ${level.usage.vms}/${level.policy.maxVms}`,
    level.policy.maxProcessors != null && `vCPU ${level.usage.processors}/${level.policy.maxProcessors}`,
    level.policy.maxMemoryMb != null && `RAM ${Math.round(level.usage.memoryMb / 102.4) / 10}/${Math.round(level.policy.maxMemoryMb / 102.4) / 10} GB`,
    level.policy.maxDiskGb != null && `${t('wiz.disk')} ${level.usage.diskGb}/${level.policy.maxDiskGb} GB`,
  ].filter(Boolean).join(' · ') : '';
  // Where Hyper-V puts the files: New-VM -Path <location> creates <location>\<name>\Virtual Machines.
  const vmName = name || t('wiz.vmNamePlaceholder');
  const configWhere = location ? joinPath(joinPath(location, vmName), 'Virtual Machines') : defaults?.defaults.virtualMachinePath ?? '—';
  const diskWhere = location ? joinPath(joinPath(location, vmName), 'Virtual Hard Disks') : defaults?.defaults.virtualHardDiskPath ?? '—';
  const diskFile = joinPath(diskWhere, `${safeName(vmName)}.vhdx`);

  const body = useMemo(() => ({
    ...spec,
    name,
    switchName: spec.switchName || undefined,
    // A network of the portal decides switch and VLAN on the server; otherwise the VLAN typed here (agent 0.7.0+).
    networkId: spec.networkId || undefined,
    vlanId: !spec.networkId && spec.switchName && defaults?.vlanSupported && vlan ? vlan : undefined,
    isoPath: spec.isoPath || undefined,
    notes: spec.notes || undefined,
    location: location || undefined,
    secureBootTemplate: spec.generation === 2 ? spec.secureBootTemplate : undefined,
    enableTpm: spec.generation === 2 && spec.enableTpm,
    memoryMinimumMb: spec.dynamicMemory ? spec.memoryMinimumMb : undefined,
    memoryMaximumMb: spec.dynamicMemory ? spec.memoryMaximumMb : undefined,
  }), [spec, name, location, vlan, defaults?.vlanSupported]);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<Accepted>(`/api/v1/hosts/${hostId}/vms`, { vm: body, groupId: groupId ?? undefined, idempotencyKey: key });
      onQueued(r, `${t(tpl ? 'jobop.vm.create.FromTemplate' : 'jobop.vm.create.New')} — ${name}`);
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const pages: Record<(typeof STEPS)[number]['key'], ReactNode> = {
    name: (
      <div className="space-y-4">
        <Field label={t('cfg.name')} htmlFor="wiz-name" error={nameError}>
          <Input id="wiz-name" value={spec.name} maxLength={100} onChange={(e) => set({ name: e.target.value })} autoFocus />
        </Field>
        {tpl && <Notice kind="info">{t('tpl.wiz.notice')}</Notice>}
        <Field label={t('wiz.host')} htmlFor="wiz-host">
          <NativeSelect id="wiz-host" value={hostId} disabled={!!template} onChange={(e) => setHostId(e.target.value)}>
            {hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
          </NativeSelect>
        </Field>
        {!template && (available.data?.length ?? 0) > 0 && (
          <Field label={t('tpl.wiz.source')} htmlFor="wiz-source" hint={t('tpl.help')}>
            <NativeSelect id="wiz-source" value={tpl?.id ?? ''} onChange={(e) => chooseTemplate(available.data?.find((x) => x.id === e.target.value) ?? null)}>
              <option value="">{t('tpl.wiz.sourceEmpty')}</option>
              {(available.data ?? []).map((x) => <option key={x.id} value={x.id}>{t('tpl.wiz.sourceTemplate', { name: x.name })}</option>)}
            </NativeSelect>
          </Field>
        )}
        {hosts.length === 0 && !folder.loading && <Notice kind="warn">{t('wiz.noHostForGroup')}</Notice>}
        {loadError ? <ErrorAlert error={loadError} /> : defaults && (
          <p className="text-xs text-muted-foreground">{t('wiz.hostCapacity', { lp: defaults.defaults.logicalProcessorCount, mem: formatBytes(defaults.defaults.memoryCapacityBytes) })}</p>
        )}
        {!inv.canManageGroups && group && (
          <Field label={t('wiz.group')}>
            <div className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm"><GroupIcon />{group.name}</div>
            <p className="text-xs text-muted-foreground">{t('wiz.groupFixed')}</p>
          </Field>
        )}
        {usageText && <Notice kind="info">{t('wiz.groupUsage', { text: usageText })}</Notice>}
        {inv.canManageGroups && (
          <Field label={t('wiz.group')}>
            <div className="flex items-center gap-2 text-xs">
              <Checkbox id="wiz-nogroup" checked={groupId === null} onCheckedChange={(c) => setGroupId(c ? null : inv.groups[0]?.id ?? null)} />
              <label htmlFor="wiz-nogroup">{t('wiz.noGroup')}</label>
            </div>
            {groupId !== null && <GroupPickerList value={groupId} onChange={(g) => setGroupId(g)} />}
          </Field>
        )}
      </div>
    ),
    compute: (
      <div className="space-y-4">
        {tpl && <div className="rounded-md border px-3 py-2 text-sm">{t('tpl.wiz.generation', { gen: tpl.generation })}</div>}
        <Field label={t('wiz.generation')} hint={t('wiz.genFixed')} className={tpl ? 'hidden' : undefined}>
          <RadioGroup value={String(spec.generation)} onValueChange={(v) => set({ generation: v === '1' ? 1 : 2 })} className="gap-2">
            {(['2', '1'] as const).map((g) => (
              <label key={g} className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm has-[[data-state=checked]]:border-primary/50 has-[[data-state=checked]]:bg-primary/5">
                <RadioGroupItem value={g} />{t(g === '2' ? 'wiz.gen2' : 'wiz.gen1')}
              </label>
            ))}
          </RadioGroup>
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('cfg.vcpu')} htmlFor="wiz-cpu" aside={defaults ? `max ${defaults.defaults.logicalProcessorCount}` : undefined}>
            <Input id="wiz-cpu" type="number" min={1} max={defaults?.defaults.logicalProcessorCount ?? 1024} value={spec.processorCount}
              onChange={(e) => set({ processorCount: Number(e.target.value) })} />
          </Field>
          <Field label={t('cfg.startupMemory')} htmlFor="wiz-mem" error={memoryError}
            aside={memOk(spec.memoryStartupMb) ? `= ${formatBytes(spec.memoryStartupMb * 1048576)}` : undefined}>
            <PresetInput id="wiz-mem" value={spec.memoryStartupMb} onChange={(v) => set({ memoryStartupMb: v })} unit="MB" min={32} step={2} invalid={!!memoryError}
              menuLabel={t('wiz.presets')}
              presets={MEMORY_PRESETS.filter((m) => !defaults || m * 1048576 <= defaults.defaults.memoryCapacityBytes).map((m) => ({ value: m, label: formatBytes(m * 1048576) }))} />
          </Field>
        </div>
        <label className="flex items-center gap-2 text-sm"><Switch checked={spec.dynamicMemory} onCheckedChange={(c) => set({ dynamicMemory: c })} />{t('cfg.dynamic')}</label>
        {spec.dynamicMemory && (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t('cfg.minMemory')} htmlFor="wiz-min"><Input id="wiz-min" type="number" min={32} step={2} value={spec.memoryMinimumMb} onChange={(e) => set({ memoryMinimumMb: Number(e.target.value) })} /></Field>
            <Field label={t('cfg.maxMemory')} htmlFor="wiz-max"><Input id="wiz-max" type="number" min={32} step={2} value={spec.memoryMaximumMb} onChange={(e) => set({ memoryMaximumMb: Number(e.target.value) })} /></Field>
          </div>
        )}
      </div>
    ),
    storage: (
      <div className="space-y-5">
        {storageRoots.length > 0 && (
          <Field label={t('wiz.locationFolder')} htmlFor="wiz-loc-folder" hint={t('wiz.locationFolderHint')}>
            <NativeSelect id="wiz-loc-folder" value={spec.location ?? ''} onChange={(e) => { set({ location: e.target.value }); setLocationFree(null); }} className="font-mono text-xs">
              {storageRoots.map((r) => <option key={r} value={r}>{r}</option>)}
            </NativeSelect>
          </Field>
        )}
        {storageRoots.length === 0 && <Field label={t('wiz.location')} htmlFor="wiz-loc" error={locationError}
          hint={canBrowse ? t('wiz.locationHint') : t('wiz.locationAgent')}>
          <div className="flex gap-2">
            <InputGroup className="min-w-0 flex-1">
              <InputGroupAddon><FolderOpen /></InputGroupAddon>
              <InputGroupInput id="wiz-loc" value={spec.location ?? ''} disabled={!canBrowse} aria-invalid={!!locationError || undefined}
                placeholder={t('wiz.locationDefault', { path: defaults?.defaults.virtualMachinePath ?? '…' })}
                onChange={(e) => { set({ location: e.target.value }); setLocationFree(null); }} className="font-mono text-xs" />
              {location && (
                <InputGroupAddon align="inline-end">
                  <InputGroupButton size="icon-xs" aria-label={t('wiz.locationReset')} title={t('wiz.locationReset')}
                    onClick={() => { set({ location: '' }); setLocationFree(null); }}><RotateCcw /></InputGroupButton>
                </InputGroupAddon>
              )}
            </InputGroup>
            <Button type="button" variant="outline" onClick={() => setBrowsing(true)} disabled={!canBrowse}><FolderSearch />{t('picker.browse')}</Button>
          </div>
        </Field>}

        {tpl && (
          <div className="space-y-3 rounded-lg border p-3">
            <Field label={t('tpl.wiz.disk')} htmlFor="wiz-tpl-disk" error={diskError} hint={t('tpl.wiz.diskHint', { gb: tpl.bootDiskGb })}>
              <PresetInput id="wiz-tpl-disk" value={spec.diskSizeGb} onChange={(v) => set({ diskSizeGb: v })} unit="GB" min={tpl.bootDiskGb} max={65536} invalid={!!diskError}
                menuLabel={t('wiz.presets')}
                presets={[tpl.bootDiskGb, ...DISK_PRESETS.filter((g) => g > tpl.bootDiskGb)].map((g) => ({ value: g, label: g >= 1024 ? `${g / 1024} TB` : `${g} GB` }))} />
            </Field>
            {tpl.disks.length > 1 && (
              <p className="text-xs text-muted-foreground">{t('tpl.wiz.extraDisks', { text: tpl.disks.slice(1).map((d) => formatBytes(d.sizeBytes)).join(' + ') })}</p>
            )}
          </div>
        )}
        <div className={cn('space-y-3 rounded-lg border p-3', tpl && 'hidden')}>
          <label className="flex items-center gap-2 text-sm">
            <Checkbox checked={spec.diskSizeGb === 0} onCheckedChange={(c) => set({ diskSizeGb: c ? 0 : 60 })} />{t('wiz.noDisk')}
          </label>
          {spec.diskSizeGb !== 0 && (
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t('cfg.sizeGb')} htmlFor="wiz-disk" error={diskError}>
                <PresetInput id="wiz-disk" value={spec.diskSizeGb} onChange={(v) => set({ diskSizeGb: v })} unit="GB" min={1} max={65536} invalid={!!diskError}
                  menuLabel={t('wiz.presets')} presets={DISK_PRESETS.map((g) => ({ value: g, label: g >= 1024 ? `${g / 1024} TB` : `${g} GB` }))} />
              </Field>
              <Field label={t('wiz.diskType')}>
                <RadioGroup value={spec.diskFixed ? 'fixed' : 'dynamic'} onValueChange={(v) => set({ diskFixed: v === 'fixed' })} className="flex gap-4 pt-1.5">
                  <label className="flex items-center gap-2 text-sm"><RadioGroupItem value="dynamic" />{t('wiz.diskDynamic')}</label>
                  <label className="flex items-center gap-2 text-sm"><RadioGroupItem value="fixed" />{t('cfg.fixed')}</label>
                </RadioGroup>
              </Field>
            </div>
          )}
        </div>

        <div className="rounded-lg bg-muted/50 p-3 text-xs">
          <div className="mb-1.5 flex items-center gap-1.5 font-medium text-muted-foreground"><HardDrive className="size-3.5" />{t('wiz.filesWhere')}</div>
          <KeyValue items={[
            [t('wiz.configFiles'), <span key="c" className="font-mono [overflow-wrap:anywhere]">{configWhere}</span>],
            [t('wiz.disk'), spec.diskSizeGb !== 0 ? <span key="d" className="font-mono [overflow-wrap:anywhere]">{diskFile}</span> : t('wiz.noDisk')],
            ...(locationFree != null ? [[t('wiz.freeSpace'), formatBytes(locationFree)] as [string, string]] : []),
          ]} />
          {spec.diskFixed && spec.diskSizeGb > 0 && locationFree != null && spec.diskSizeGb * 1073741824 > locationFree && (
            <div className="mt-2"><Notice kind="warn">{t('wiz.notEnoughSpace')}</Notice></div>
          )}
        </div>
        {browsing && host && (
          <HostFolderPicker hostId={host.id} mode="folder" initialPath={location || null}
            onPickFolder={(f) => { set({ location: f.path }); setLocationFree(f.freeBytes); setBrowsing(false); }} onClose={() => setBrowsing(false)} />
        )}
      </div>
    ),
    network: (
      <div className="space-y-4">
        {networks.length > 0 && (
          <Field label={t('pnet.network')} htmlFor="wiz-net" hint={t('pnet.wizHint')}>
            <NativeSelect id="wiz-net" value={spec.networkId ?? ''} onChange={(e) => {
              const net = networks.find((x) => x.id === e.target.value);
              set(net ? { networkId: net.id, switchName: net.switchName } : { networkId: undefined });
              if (net) setVlanText('');
            }}>
              <option value="">{t('pnet.custom')}</option>
              {networks.map((x) => <option key={x.id} value={x.id} disabled={!!x.vlanId && !defaults?.vlanSupported}>{x.name} — {x.switchName}{x.vlanId ? ` · VLAN ${x.vlanId}` : ''}</option>)}
            </NativeSelect>
          </Field>
        )}
        {!spec.networkId && (
          <div className="grid gap-4 sm:grid-cols-[2fr_1fr]">
            <Field label={t('wiz.switch')} htmlFor="wiz-switch">
              <NativeSelect id="wiz-switch" value={spec.switchName} onChange={(e) => set({ switchName: e.target.value })}>
                <option value="">{t('cfg.notConnected')}</option>
                {(defaults?.switches ?? []).map((s) => <option key={s} value={s}>{s}</option>)}
              </NativeSelect>
            </Field>
            {defaults?.vlanSupported && spec.switchName && (
              <Field label={t('pnet.vlan')} htmlFor="wiz-vlan" hint={t('pnet.vlanHint')} error={vlanError ?? undefined}>
                <Input id="wiz-vlan" inputMode="numeric" className="tabular" value={vlanText} placeholder={t('pnet.untagged')} onChange={(e) => setVlanText(e.target.value)} aria-invalid={!!vlanError} />
              </Field>
            )}
          </div>
        )}
        {defaults && !defaults.vlanSupported && <p className="text-xs text-muted-foreground">{t('pnet.vlanOldAgent')}</p>}
      </div>
    ),
    media: (
      <div className="space-y-4">
        <Field label={t('wiz.iso')} htmlFor="wiz-iso">
          {hostId && (
            <IsoPicker key={hostId} id="wiz-iso" groupId={groupId ?? undefined} value={spec.isoPath ?? ''} onChange={(p) => set({ isoPath: p })} emptyLabel={t('wiz.noIso')}
              host={host ?? { id: hostId, isoLibrary: defaults?.isoLibrary ?? [], canBrowseFolders: false, connected: true }} />
          )}
        </Field>
        {spec.generation === 2 ? (
          <>
            <Field label={t('wiz.secureBoot')} htmlFor="wiz-sb">
              <NativeSelect id="wiz-sb" value={spec.secureBootTemplate} onChange={(e) => set({ secureBootTemplate: e.target.value })}>
                {['MicrosoftWindows', 'MicrosoftUEFICertificateAuthority', 'OpenSourceShieldedVM', 'Off'].map((s) => <option key={s} value={s}>{t(('opt.sb.' + s) as TKey)}</option>)}
              </NativeSelect>
            </Field>
            <label className="flex items-start gap-2 text-sm"><Checkbox className="mt-0.5" checked={spec.enableTpm} onCheckedChange={(c) => set({ enableTpm: !!c })} />{t('wiz.tpm')}</label>
          </>
        ) : <Notice kind="info">{t('cfg.gen1NoFirmware')}</Notice>}
      </div>
    ),
    options: (
      <div className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('cfg.checkpointType')} htmlFor="wiz-cp">
            <NativeSelect id="wiz-cp" value={spec.checkpointType} onChange={(e) => set({ checkpointType: e.target.value })}>
              {(defaults?.options.checkpointTypes ?? ['Production']).map((c) => <option key={c} value={c}>{t(('opt.cp.' + c) as TKey)}</option>)}
            </NativeSelect>
          </Field>
          <Field label={t('cfg.autoStart')} htmlFor="wiz-start">
            <NativeSelect id="wiz-start" value={spec.automaticStartAction} onChange={(e) => set({ automaticStartAction: e.target.value })}>
              {(defaults?.options.startActions ?? ['Nothing']).map((c) => <option key={c} value={c}>{t(('opt.start.' + c) as TKey)}</option>)}
            </NativeSelect>
          </Field>
        </div>
        <label className="flex items-center gap-2 text-sm"><Checkbox checked={spec.startAfterCreate} onCheckedChange={(c) => set({ startAfterCreate: !!c })} />{t('wiz.startAfter')}</label>
        <Field label={t('cfg.notes')} htmlFor="wiz-notes">
          <Textarea id="wiz-notes" rows={3} maxLength={4000} value={spec.notes} onChange={(e) => set({ notes: e.target.value })} />
        </Field>
      </div>
    ),
    summary: (
      <KeyValue items={[
        [t('cfg.name'), name || '—'],
        [t('wiz.host'), host?.displayName],
        [t('wiz.group'), groupId ? inv.groupPath(groupId).map((g) => g.name).join(' / ') : t('wiz.noGroup')],
        ...(tpl ? [[t('tpl.wiz.template'), tpl.name] as [string, string]] : []),
        [t('wiz.generation'), spec.generation === 2 ? 'Gen 2 (UEFI)' : 'Gen 1 (BIOS)'],
        [t('cfg.vcpu'), spec.processorCount],
        [t('cfg.memory'), `${formatBytes(spec.memoryStartupMb * 1048576)}${spec.dynamicMemory ? ` (${t('cfg.dynamic')}: ${spec.memoryMinimumMb}–${spec.memoryMaximumMb} MB)` : ''}`],
        [t('wiz.location'), <span key="l" className="font-mono text-xs [overflow-wrap:anywhere]">{location || t('wiz.locationDefault', { path: defaults?.defaults.virtualMachinePath ?? '—' })}</span>],
        [t('wiz.disk'), spec.diskSizeGb > 0 ? `${spec.diskSizeGb} GB ${spec.diskFixed ? t('cfg.fixed') : t('wiz.diskDynamic')}` : t('wiz.noDisk')],
        [t('wiz.switch'), selectedNetwork
          ? `${selectedNetwork.name} (${selectedNetwork.switchName}${selectedNetwork.vlanId ? ` · VLAN ${selectedNetwork.vlanId}` : ''})`
          : `${spec.switchName || t('cfg.notConnected')}${spec.switchName && vlan ? ` · VLAN ${vlan}` : ''}`],
        [t('wiz.iso'), spec.isoPath || t('wiz.noIso')],
        [t('cfg.firmware'), spec.generation === 2 ? `${t(('opt.sb.' + spec.secureBootTemplate) as TKey)}${spec.enableTpm ? ' · TPM' : ''}` : 'BIOS'],
        [t('cfg.checkpointType'), t(('opt.cp.' + spec.checkpointType) as TKey)],
        [t('cfg.autoStart'), t(('opt.start.' + spec.automaticStartAction) as TKey)],
        [t('wiz.startAfter'), spec.startAfterCreate ? t('common.yes') : t('common.no')],
      ]} />
    ),
  };

  const current = STEPS[step]!;
  return (
    <Dialog open onOpenChange={(o) => { if (!o && !busy) onClose(); }}>
      <DialogContent className="flex max-h-[92dvh] flex-col gap-0 overflow-hidden p-0 sm:max-w-4xl" onInteractOutside={(e) => e.preventDefault()}>
        <DialogHeader className="border-b px-5 py-3">
          <DialogTitle>{tpl ? t('tpl.wiz.title', { name: tpl.name }) : t('wiz.title')}</DialogTitle>
          <DialogDescription className="sr-only">{t(current.label as TKey)}</DialogDescription>
        </DialogHeader>
        {hosts.length === 0 ? <div className="p-5"><Notice kind="warn">{t('wiz.noHosts')}</Notice></div> : (
          <div className="grid min-h-0 flex-1 md:grid-cols-[13rem_1fr]">
            <nav className="hidden border-r bg-muted/40 p-2 md:block" aria-label={t('wiz.title')}>
              <ol className="space-y-0.5">
                {STEPS.map((s, i) => (
                  <li key={s.key}>
                    <button type="button" disabled={i > step && !stepValid.slice(0, i).every(Boolean)} onClick={() => setStep(i)}
                      className={cn('flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm disabled:opacity-50',
                        i === step ? 'bg-card font-medium shadow-sm ring-1 ring-border' : 'text-muted-foreground hover:bg-accent')}>
                      <span className={cn('flex size-5 items-center justify-center rounded-full border text-[10px]', i < step && 'border-primary bg-primary text-primary-foreground')}>
                        {i < step ? <Check className="size-3" /> : i + 1}
                      </span>
                      {t(s.label as TKey)}
                    </button>
                  </li>
                ))}
              </ol>
            </nav>
            <div className="h-[min(34rem,calc(92dvh-8rem))] min-h-0 overflow-auto p-5">
              <h3 className="mb-4 flex items-center gap-2 text-sm font-semibold"><current.icon className="size-4 text-muted-foreground" />{t(current.label as TKey)}
                <span className="ml-auto text-xs font-normal text-muted-foreground md:hidden">{step + 1}/{STEPS.length}</span></h3>
              {pages[current.key]}
              <ErrorAlert error={error} />
            </div>
          </div>
        )}
        {/* m-0: shadcn's footer uses negative margins meant for a padded dialog; this one has p-0 (they clipped the buttons). */}
        <DialogFooter className="m-0 shrink-0 flex-row flex-wrap items-center justify-end rounded-none rounded-b-xl border-t px-5 py-3">
          <span className="mr-auto hidden items-center gap-1.5 text-xs text-muted-foreground sm:flex"><Info className="size-3.5" />{host?.displayName}</span>
          <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('common.cancel')}</Button>
          <Button type="button" variant="outline" onClick={() => setStep(step - 1)} disabled={step === 0 || busy}>{t('wiz.back')}</Button>
          {step < STEPS.length - 1
            ? <Button type="button" onClick={() => setStep(step + 1)} disabled={!stepValid[step] || hosts.length === 0}>{t('wiz.next')}</Button>
            : <Button type="button" onClick={() => void create()} disabled={!canFinish || busy}><Disc3 className={cn(busy && 'animate-spin')} />{t('wiz.create')}</Button>}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
