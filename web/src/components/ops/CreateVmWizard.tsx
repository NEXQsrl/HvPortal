import { Check, Cpu, Disc3, HardDrive, Info, ListChecks, Network, Server, Settings2, ShieldCheck } from 'lucide-react';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { Accepted, HostDefaultsResponse, IsosResponse, NewVmSpec } from '@/api/ops-types';
import { ErrorAlert, KeyValue, Notice } from '@/components/common';
import { Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, VM_NAME } from '@/lib/format';
import { cn } from '@/lib/utils';
import { GroupPickerList } from './GroupDialogs';

const STEPS = [
  { key: 'name', label: 'wiz.step.name', icon: Server },
  { key: 'compute', label: 'wiz.step.compute', icon: Cpu },
  { key: 'storage', label: 'wiz.step.storage', icon: HardDrive },
  { key: 'network', label: 'wiz.step.network', icon: Network },
  { key: 'media', label: 'wiz.step.media', icon: ShieldCheck },
  { key: 'options', label: 'wiz.step.options', icon: Settings2 },
  { key: 'summary', label: 'wiz.step.summary', icon: ListChecks },
] as const;

const MEMORY_PRESETS = [1024, 2048, 4096, 8192, 16384, 32768];

export function CreateVmWizard({ hostId: initialHost, groupId: initialGroup, onQueued, onClose }: {
  hostId?: string; groupId?: string; onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t } = useI18n();
  const inv = useInventory();
  const hosts = inv.hosts.filter((h) => h.canCreateVm);
  const [step, setStep] = useState(0);
  const [hostId, setHostId] = useState(initialHost && hosts.some((h) => h.id === initialHost) ? initialHost : hosts[0]?.id ?? '');
  const [groupId, setGroupId] = useState<string | null>(initialGroup ?? null);
  const [spec, setSpec] = useState<NewVmSpec>({
    name: '', generation: 2, processorCount: 2, memoryStartupMb: 4096, dynamicMemory: false, memoryMinimumMb: 512, memoryMaximumMb: 8192,
    diskSizeGb: 60, diskFixed: false, switchName: '', isoPath: '', secureBootTemplate: 'MicrosoftWindows', enableTpm: false,
    checkpointType: 'Production', automaticStartAction: 'StartIfRunning', startAfterCreate: false, notes: '',
  });
  const [defaults, setDefaults] = useState<HostDefaultsResponse | null>(null);
  const [isos, setIsos] = useState<IsosResponse | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [key] = useState(newIdempotencyKey);
  const set = (p: Partial<NewVmSpec>) => setSpec((s) => ({ ...s, ...p }));

  useEffect(() => {
    if (!hostId) return;
    let cancelled = false;
    setDefaults(null);
    setIsos(null);
    setLoadError(null);
    get<HostDefaultsResponse>(`/api/v1/hosts/${hostId}/defaults`).then((d) => {
      if (cancelled) return;
      setDefaults(d);
      setSpec((s) => ({ ...s, switchName: d.switches.includes(s.switchName ?? '') ? s.switchName : d.switches[0] ?? '' }));
    }).catch((e) => { if (!cancelled) setLoadError(e); });
    get<IsosResponse>(`/api/v1/hosts/${hostId}/isos`).then((r) => { if (!cancelled) setIsos(r); }).catch(() => { if (!cancelled) setIsos({ roots: [], files: [] }); });
    return () => { cancelled = true; };
  }, [hostId]);

  const name = spec.name.trim();
  const nameError = !name ? null
    : !VM_NAME.test(name) ? t('wiz.nameInvalid')
    : defaults?.existingNames.some((n) => n.toLowerCase() === name.toLowerCase()) ? t('wiz.nameTaken') : null;
  const memOk = (v: number | undefined) => v !== undefined && Number.isInteger(v) && v >= 32 && v % 2 === 0;
  const memoryError = !memOk(spec.memoryStartupMb) || (spec.dynamicMemory && (!memOk(spec.memoryMinimumMb) || !memOk(spec.memoryMaximumMb)
    || spec.memoryMinimumMb! > spec.memoryStartupMb || spec.memoryMaximumMb! < spec.memoryStartupMb)) ? t('wiz.memoryInvalid') : null;
  const stepValid = [
    !!hostId && !!name && !nameError && !!defaults,
    spec.processorCount >= 1 && spec.processorCount <= 1024 && !memoryError,
    spec.diskSizeGb >= 0 && spec.diskSizeGb <= 65536,
    true, true, true, true,
  ];
  const canFinish = stepValid.every(Boolean);
  const host = inv.hostById.get(hostId);

  const body = useMemo(() => ({
    ...spec,
    name,
    switchName: spec.switchName || undefined,
    isoPath: spec.isoPath || undefined,
    notes: spec.notes || undefined,
    secureBootTemplate: spec.generation === 2 ? spec.secureBootTemplate : undefined,
    enableTpm: spec.generation === 2 && spec.enableTpm,
    memoryMinimumMb: spec.dynamicMemory ? spec.memoryMinimumMb : undefined,
    memoryMaximumMb: spec.dynamicMemory ? spec.memoryMaximumMb : undefined,
  }), [spec, name]);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<Accepted>(`/api/v1/hosts/${hostId}/vms`, { vm: body, groupId: groupId ?? undefined, idempotencyKey: key });
      onQueued(r, `${t('jobop.vm.create.New')} — ${name}`);
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
        <Field label={t('wiz.host')} htmlFor="wiz-host">
          <NativeSelect id="wiz-host" value={hostId} onChange={(e) => setHostId(e.target.value)}>
            {hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
          </NativeSelect>
        </Field>
        {loadError ? <ErrorAlert error={loadError} /> : defaults && (
          <p className="text-xs text-muted-foreground">{t('wiz.hostCapacity', { lp: defaults.defaults.logicalProcessorCount, mem: formatBytes(defaults.defaults.memoryCapacityBytes) })}</p>
        )}
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
        <Field label={t('wiz.generation')} hint={t('wiz.genFixed')}>
          <RadioGroup value={String(spec.generation)} onValueChange={(v) => set({ generation: v === '1' ? 1 : 2 })} className="gap-2">
            {(['2', '1'] as const).map((g) => (
              <label key={g} className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm has-[[data-state=checked]]:border-primary/50 has-[[data-state=checked]]:bg-primary/5">
                <RadioGroupItem value={g} />{t(g === '2' ? 'wiz.gen2' : 'wiz.gen1')}
              </label>
            ))}
          </RadioGroup>
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t('cfg.vcpu')} htmlFor="wiz-cpu" hint={defaults ? `max ${defaults.defaults.logicalProcessorCount}` : undefined}>
            <Input id="wiz-cpu" type="number" min={1} max={defaults?.defaults.logicalProcessorCount ?? 1024} value={spec.processorCount}
              onChange={(e) => set({ processorCount: Number(e.target.value) })} />
          </Field>
          <Field label={t('cfg.startupMemory')} htmlFor="wiz-mem" error={memoryError}>
            <Input id="wiz-mem" type="number" min={32} step={2} value={spec.memoryStartupMb} onChange={(e) => set({ memoryStartupMb: Number(e.target.value) })} />
            <div className="flex flex-wrap gap-1">
              {MEMORY_PRESETS.map((m) => (
                <Button key={m} type="button" size="xs" variant={spec.memoryStartupMb === m ? 'secondary' : 'ghost'} onClick={() => set({ memoryStartupMb: m })}>{formatBytes(m * 1048576)}</Button>
              ))}
            </div>
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
      <div className="space-y-4">
        <label className="flex items-center gap-2 text-sm">
          <Checkbox checked={spec.diskSizeGb === 0} onCheckedChange={(c) => set({ diskSizeGb: c ? 0 : 60 })} />{t('wiz.noDisk')}
        </label>
        {spec.diskSizeGb > 0 && (
          <>
            <Field label={t('cfg.sizeGb')} htmlFor="wiz-disk">
              <Input id="wiz-disk" type="number" min={1} max={65536} value={spec.diskSizeGb} onChange={(e) => set({ diskSizeGb: Number(e.target.value) })} className="max-w-40" />
            </Field>
            <label className="flex items-center gap-2 text-sm"><Checkbox checked={spec.diskFixed} onCheckedChange={(c) => set({ diskFixed: !!c })} />{t('cfg.fixed')}</label>
            {defaults?.defaults.virtualHardDiskPath && <Notice kind="info">{t('wiz.diskWhere', { path: defaults.defaults.virtualHardDiskPath })}</Notice>}
          </>
        )}
      </div>
    ),
    network: (
      <Field label={t('wiz.switch')} htmlFor="wiz-switch">
        <NativeSelect id="wiz-switch" value={spec.switchName} onChange={(e) => set({ switchName: e.target.value })}>
          <option value="">{t('cfg.notConnected')}</option>
          {(defaults?.switches ?? []).map((s) => <option key={s} value={s}>{s}</option>)}
        </NativeSelect>
      </Field>
    ),
    media: (
      <div className="space-y-4">
        <Field label={t('wiz.iso')} htmlFor="wiz-iso" hint={isos && isos.roots.length === 0 ? t('cfg.noLibrary') : undefined}>
          <NativeSelect id="wiz-iso" value={spec.isoPath} onChange={(e) => set({ isoPath: e.target.value })} disabled={!isos || isos.files.length === 0}>
            <option value="">{t('wiz.noIso')}</option>
            {(isos?.files ?? []).map((f) => <option key={f.path} value={f.path}>{f.path} ({formatBytes(f.sizeBytes)})</option>)}
          </NativeSelect>
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
        [t('wiz.generation'), spec.generation === 2 ? 'Gen 2 (UEFI)' : 'Gen 1 (BIOS)'],
        [t('cfg.vcpu'), spec.processorCount],
        [t('cfg.memory'), `${formatBytes(spec.memoryStartupMb * 1048576)}${spec.dynamicMemory ? ` (${t('cfg.dynamic')}: ${spec.memoryMinimumMb}–${spec.memoryMaximumMb} MB)` : ''}`],
        [t('wiz.disk'), spec.diskSizeGb > 0 ? `${spec.diskSizeGb} GB ${spec.diskFixed ? 'fixed' : 'dynamic'}` : t('wiz.noDisk')],
        [t('wiz.switch'), spec.switchName || t('cfg.notConnected')],
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
      <DialogContent className="max-h-[92dvh] gap-0 overflow-hidden p-0 sm:max-w-4xl" onInteractOutside={(e) => e.preventDefault()}>
        <DialogHeader className="border-b px-5 py-3">
          <DialogTitle>{t('wiz.title')}</DialogTitle>
          <DialogDescription className="sr-only">{t(current.label as TKey)}</DialogDescription>
        </DialogHeader>
        {hosts.length === 0 ? <div className="p-5"><Notice kind="warn">{t('wiz.noHosts')}</Notice></div> : (
          <div className="grid min-h-0 md:grid-cols-[13rem_1fr]">
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
            <div className="max-h-[calc(92dvh-8.5rem)] min-h-80 overflow-auto p-5">
              <h3 className="mb-4 flex items-center gap-2 text-sm font-semibold"><current.icon className="size-4 text-muted-foreground" />{t(current.label as TKey)}
                <span className="ml-auto text-xs font-normal text-muted-foreground md:hidden">{step + 1}/{STEPS.length}</span></h3>
              {pages[current.key]}
              <ErrorAlert error={error} />
            </div>
          </div>
        )}
        <DialogFooter className="border-t px-5 py-3">
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
