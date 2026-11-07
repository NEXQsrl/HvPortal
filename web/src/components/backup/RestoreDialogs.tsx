import { FolderOpen } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { newIdempotencyKey, post } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import type { Host } from '@/api/types';
import { Notice } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { HostFolderPicker } from '@/components/ops/HostFolderPicker';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { useI18n, type TKey } from '@/i18n';
import { VM_NAME } from '@/lib/format';
import { normalizeFolder } from '@/lib/hostPaths';
import type { BackupPoint, Repository, RestoreMode, RestoreNetwork } from './backup-api';
import { isInsideStorageFolders, isValidHostFolder } from './backup-rules';

type Need = 'create' | 'files' | 'mount';

/**
 * Hosts that may work on a restore point: connected, agent 0.7.0+, and allowed by the repository (a local folder is
 * reachable only from its own host; a share with credentials needs the host's backup staging folder).
 */
export function hostChoices(hosts: Host[], repo: Repository | undefined, need: Need): { host: Host; reason: TKey | null }[] {
  return hosts.filter((h) => h.status === 'Active').map((h) => {
    let reason: TKey | null = null;
    if (!h.connected) reason = 'bk.host.offline';
    else if (!h.agent7) reason = 'bk.host.agentOld';
    else if (need === 'create' && !h.canCreateVm) reason = 'bk.host.noCreate';
    else if (need === 'mount' && h.connectionMode === 'Remote') reason = 'bk.host.remote';
    else if (repo?.hostId && repo.hostId !== h.id) reason = 'bk.host.otherRepoHost';
    else if (repo?.hasCredentials && !h.backupStagingPath) reason = 'bk.host.noStaging';
    return { host: h, reason };
  });
}

export function HostSelect({ id, value, onChange, choices }: { id: string; value: string; onChange: (v: string) => void; choices: { host: Host; reason: TKey | null }[] }) {
  const { t } = useI18n();
  return (
    <NativeSelect id={id} value={value} onChange={(e) => onChange(e.target.value)}>
      {!choices.some((c) => c.host.id === value && !c.reason) && <option value="">{t('bk.host.choose')}</option>}
      {choices.map(({ host, reason }) => (
        <option key={host.id} value={host.id} disabled={!!reason}>{host.displayName}{reason ? ` — ${t(reason)}` : ''}</option>
      ))}
    </NativeSelect>
  );
}

/** Folder of a host: typed or chosen with the folder browser; checked like the server (local, not a system folder, inside the allowed storage folders). */
export function HostFolderField({ id, label, hint, host, value, onChange, placeholder, optional }: {
  id: string; label: ReactNode; hint?: ReactNode; host: Host | undefined; value: string; onChange: (v: string) => void; placeholder?: string; optional?: boolean;
}) {
  const { t } = useI18n();
  const [picking, setPicking] = useState(false);
  const error = folderError(value, host, optional);
  return (
    <>
      <Field label={label} htmlFor={id} hint={hint} error={value && error ? t(error) : undefined}>
        <div className="flex gap-2">
          <Input id={id} className="font-mono" value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} aria-invalid={!!value && !!error} />
          <Button type="button" variant="outline" disabled={!host?.canBrowseFolders} onClick={() => setPicking(true)}><FolderOpen />{t('picker.browse')}</Button>
        </div>
      </Field>
      {host?.storageFolders && host.storageFolders.length > 0 && <p className="text-xs text-muted-foreground">{t('move.allowedFolders', { list: host.storageFolders.join(', ') })}</p>}
      {picking && host && (
        <HostFolderPicker hostId={host.id} mode="folder" initialPath={value || null} onClose={() => setPicking(false)}
          onPickFolder={(f) => { onChange(f.path); setPicking(false); }} />
      )}
    </>
  );
}

export function folderError(value: string, host: Host | undefined, optional?: boolean): TKey | null {
  if (!value.trim()) return optional ? null : 'bk.folder.required';
  if (!isValidHostFolder(value)) return 'bk.folder.invalid';
  if (!isInsideStorageFolders(value, host?.storageFolders)) return 'bk.folder.notAllowed';
  return null;
}

function ModeOption({ value, title, help, disabled }: { value: string; title: string; help: ReactNode; disabled?: boolean }) {
  return (
    <label className={`flex items-start gap-2 rounded-md border p-2 text-sm has-[:checked]:border-primary/50 has-[:checked]:bg-primary/5 ${disabled ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'}`}>
      <RadioGroupItem value={value} className="mt-0.5" disabled={disabled} />
      <span><span className="font-medium">{title}</span><span className="block text-xs text-muted-foreground">{help}</span></span>
    </label>
  );
}

/**
 * Restore a VM from a restore point (POST /backup/points/{id}/restore): as a new VM, over the original VM (destructive,
 * typed confirmation; the original disk files are kept renamed) or as an isolated restore test.
 */
export function RestoreVmDialog({ point, repo, onQueued, onClose }: {
  point: BackupPoint; repo?: Repository; onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const original = inv.vmById.get(point.vmId);
  const replaceHost = original ? inv.hostById.get(original.hostId) : undefined;
  const replaceable = point.vmExists && !!original && !original.isMissing && original.hyperVId.toLowerCase() === point.vmHyperVId.toLowerCase()
    && original.canRestore !== false && !!replaceHost?.agent7 && !(repo?.hostId && repo.hostId !== original.hostId) && !(repo?.hasCredentials && !replaceHost?.backupStagingPath);
  const choices = hostChoices(inv.hosts, repo, 'create');
  const usable = choices.filter((c) => !c.reason);
  const defaultName = `${point.vmName}-restored`.slice(0, 100);
  const [mode, setModeState] = useState<RestoreMode>('NewVm');
  const [hostId, setHostId] = useState(usable.find((c) => c.host.id === point.hostId)?.host.id ?? usable[0]?.host.id ?? '');
  const [name, setName] = useState(defaultName);
  // A restore test gets its own temporary name (hvp-test-…) unless one is typed; a new VM gets "<name>-restored".
  const setMode = (m: RestoreMode) => {
    if (m === 'Test' && name === defaultName) setName('');
    if (m === 'NewVm' && name === '') setName(defaultName);
    setModeState(m);
  };
  const [location, setLocation] = useState('');
  const [network, setNetwork] = useState<RestoreNetwork>('Same');
  const [switchName, setSwitchName] = useState('');
  const [startAfter, setStartAfter] = useState(false);
  const [confirm, setConfirm] = useState('');
  const [key] = useState(newIdempotencyKey);

  const host = mode === 'Replace' ? replaceHost : inv.hostById.get(hostId);
  const switches = host?.virtualSwitches ?? [];
  const testName = `hvp-test-${point.id.replace(/-/g, '').slice(0, 8)}`;
  const effectiveName = mode === 'Test' ? (name.trim() || testName) : name.trim();
  const nameTaken = mode !== 'Replace' && inv.vms.some((v) => v.hostId === hostId && !v.isMissing && v.name === effectiveName);
  const nameError: TKey | null = mode === 'Replace' ? null : mode === 'NewVm' && !name.trim() ? 'bk.restore.nameRequired'
    : !VM_NAME.test(effectiveName) ? 'wiz.nameInvalid' : nameTaken ? 'bk.restore.nameTaken' : null;
  const locError = mode === 'Replace' ? null : folderError(location, host, true);
  const netError = (mode !== 'Test' && network === 'Switch' && !switches.includes(switchName));
  const valid = (mode === 'Replace' ? replaceable && confirm === original?.name : !!hostId && !nameError && !locError) && !netError;
  const realNetwork: RestoreNetwork = mode === 'Test' ? 'Disconnected' : network;

  const submit = async () => {
    const body = mode === 'Replace'
      ? { mode, network: realNetwork, switchName: realNetwork === 'Switch' ? switchName : undefined, startAfter, confirmName: confirm, idempotencyKey: key }
      : {
        mode, hostId, name: mode === 'Test' && !name.trim() ? undefined : name.trim(), location: location.trim() ? normalizeFolder(location) : undefined,
        network: realNetwork, switchName: realNetwork === 'Switch' ? switchName : undefined, startAfter: mode === 'NewVm' && startAfter, idempotencyKey: key,
      };
    const r = await post<Accepted>(`/api/v1/backup/points/${point.id}/restore`, body);
    onQueued(r, mode === 'Replace' ? `${t('jobop.backup.restore.Replace')} — ${point.vmName}`
      : mode === 'Test' ? `${t('jobop.backup.restore.Test')} — ${point.vmName}` : `${t('jobop.backup.restore.NewVm')} — ${point.vmName} → ${effectiveName}`);
  };

  return (
    <ActionDialog title={t('bk.restore.title', { name: point.vmName })} description={t('bk.restore.from', { date: fmtDate(point.createdAt), repo: point.repositoryName ?? '—' })}
      confirmLabel={t(mode === 'Replace' ? 'bk.restore.confirmReplace' : mode === 'Test' ? 'bk.restore.confirmTest' : 'bk.restore.confirm')}
      danger={mode === 'Replace'} disabled={!valid} wide onClose={onClose} onConfirm={submit}>
      {point.status === 'Unverified' && <Notice kind="warn" title={t('bk.pointStatus.Unverified')}>{t('bk.restore.unverified')}</Notice>}
      <RadioGroup value={mode} onValueChange={(v) => setMode(v as RestoreMode)} className="gap-2">
        <ModeOption value="NewVm" title={t('bk.restore.mode.NewVm')} help={t('bk.restore.mode.NewVmHelp')} />
        <ModeOption value="Replace" title={t('bk.restore.mode.Replace')} disabled={!replaceable}
          help={replaceable ? t('bk.restore.mode.ReplaceHelp') : t('bk.restore.mode.ReplaceUnavailable')} />
        <ModeOption value="Test" title={t('bk.restore.mode.Test')} help={t('bk.restore.mode.TestHelp')} />
      </RadioGroup>

      {mode !== 'Replace' && (
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('bk.restore.host')} htmlFor="rs-host">
            <HostSelect id="rs-host" value={hostId} onChange={(v) => { setHostId(v); setSwitchName(''); setLocation(''); }} choices={choices} />
          </Field>
          <Field label={t('bk.restore.name')} htmlFor="rs-name" error={nameError && (name || mode === 'NewVm') ? t(nameError) : undefined}>
            <Input id="rs-name" value={name} maxLength={100} placeholder={mode === 'Test' ? testName : undefined} onChange={(e) => setName(e.target.value)} />
          </Field>
        </div>
      )}
      {mode !== 'Replace' && usable.length === 0 && <Notice kind="warn">{t('bk.restore.noHost')}</Notice>}
      {mode !== 'Replace' && (
        <HostFolderField id="rs-location" label={t('bk.restore.location')} hint={t(mode === 'Test' ? 'bk.restore.locationTestHint' : 'bk.restore.locationHint')}
          host={host} value={location} onChange={setLocation} optional
          placeholder={mode === 'Test' && host?.backupStagingPath ? `${host.backupStagingPath}\\restore-tests` : t('bk.restore.locationDefault')} />
      )}

      {mode !== 'Test' ? (
        <Field label={t('bk.restore.network')} htmlFor="rs-network">
          <RadioGroup id="rs-network" value={network} onValueChange={(v) => setNetwork(v as RestoreNetwork)} className="gap-1.5">
            {(['Same', 'Disconnected', 'Switch'] as const).map((n) => (
              <label key={n} className="flex cursor-pointer items-start gap-2 text-sm">
                <RadioGroupItem value={n} className="mt-0.5" />
                <span>{t(('bk.restore.net.' + n) as TKey)}<span className="block text-xs text-muted-foreground">{t(('bk.restore.net.' + n + 'Help') as TKey)}</span></span>
              </label>
            ))}
          </RadioGroup>
          {network === 'Switch' && (
            <NativeSelect aria-label={t('bk.restore.switch')} value={switchName} onChange={(e) => setSwitchName(e.target.value)} className="mt-1.5">
              <option value="">{t('bk.restore.switchChoose')}</option>
              {switches.map((s) => <option key={s} value={s}>{s}</option>)}
            </NativeSelect>
          )}
        </Field>
      ) : <Notice kind="info" title={t('bk.restore.testTitle')}>{t('bk.restore.testExplain')}</Notice>}
      {mode === 'NewVm' && network === 'Same' && original && !original.isMissing && <Notice kind="warn">{t('bk.restore.sameNetworkWarn')}</Notice>}

      {mode !== 'Test' && (
        <label className="flex items-start gap-2 text-sm">
          <Checkbox checked={startAfter} onCheckedChange={(v) => setStartAfter(!!v)} className="mt-0.5" />
          <span>{t('bk.restore.startAfter')}</span>
        </label>
      )}

      {mode === 'Replace' && original && (
        <>
          <Notice kind="danger" title={t('bk.restore.replaceTitle')}>
            {t('bk.restore.replaceImpact', { name: original.name, date: fmtDate(point.createdAt) })}
          </Notice>
          <Field label={t('confirm.typeExact', { name: original.name })} htmlFor="rs-confirm">
            <Input id="rs-confirm" autoComplete="off" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          </Field>
        </>
      )}
      <p className="text-xs text-muted-foreground">{t('bk.restore.serverChecks')}</p>
    </ActionDialog>
  );
}

/**
 * Restore one virtual disk of a restore point (POST /backup/points/{id}/restore-disk): copied to a folder of a host, and
 * optionally attached to a VM, so that files are copied from inside the guest with the guest's own permissions.
 */
export function RestoreDiskDialog({ point, repo, onQueued, onClose }: {
  point: BackupPoint; repo?: Repository; onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const [disk, setDisk] = useState(point.disks[0] ?? '');
  const [target, setTarget] = useState<'vm' | 'host'>('vm');
  const choices = hostChoices(inv.hosts, repo, 'files');
  const usableHosts = new Set(choices.filter((c) => !c.reason).map((c) => c.host.id));
  const vms = inv.vms.filter((v) => !v.isMissing && usableHosts.has(v.hostId)).sort((a, b) => a.name.localeCompare(b.name));
  const [vmId, setVmId] = useState(vms.find((v) => v.id === point.vmId)?.id ?? '');
  const [hostId, setHostId] = useState(usableHosts.has(point.hostId) ? point.hostId : [...usableHosts][0] ?? '');
  const [folder, setFolder] = useState('');
  const vm = inv.vmById.get(vmId);
  const host = target === 'vm' ? (vm ? inv.hostById.get(vm.hostId) : undefined) : inv.hostById.get(hostId);
  const fErr = folderError(folder, host);
  const valid = !!disk && (target === 'vm' ? !!vm : !!hostId) && !fErr;
  const diskName = (d: string) => d.split('\\').pop() ?? d;
  return (
    <ActionDialog title={t('bk.disk.title', { name: point.vmName })} description={t('bk.restore.from', { date: fmtDate(point.createdAt), repo: point.repositoryName ?? '—' })}
      confirmLabel={t('bk.disk.confirm')} disabled={!valid} wide onClose={onClose}
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/backup/points/${point.id}/restore-disk`, {
          disk, destinationFolder: normalizeFolder(folder), hostId: target === 'host' ? hostId : undefined, attachVmId: target === 'vm' ? vmId : undefined,
        });
        onQueued(r, `${t('jobop.backup.restoredisk.Disk')} — ${diskName(disk)}${target === 'vm' && vm ? ` → ${vm.name}` : ''}`);
      }}>
      {point.status === 'Unverified' && <Notice kind="warn" title={t('bk.pointStatus.Unverified')}>{t('bk.restore.unverified')}</Notice>}
      <Field label={t('bk.disk.disk')} htmlFor="rd-disk">
        <NativeSelect id="rd-disk" value={disk} onChange={(e) => setDisk(e.target.value)}>
          {point.disks.map((d) => <option key={d} value={d}>{d}</option>)}
        </NativeSelect>
      </Field>
      <RadioGroup value={target} onValueChange={(v) => { setTarget(v as 'vm' | 'host'); setFolder(''); }} className="gap-2">
        <ModeOption value="vm" title={t('bk.disk.toVm')} help={t('bk.disk.toVmHelp')} />
        <ModeOption value="host" title={t('bk.disk.toHost')} help={t('bk.disk.toHostHelp')} />
      </RadioGroup>
      {target === 'vm' ? (
        <Field label={t('bk.disk.vm')} htmlFor="rd-vm">
          <NativeSelect id="rd-vm" value={vmId} onChange={(e) => { setVmId(e.target.value); setFolder(''); }}>
            <option value="">{t('bk.disk.vmChoose')}</option>
            {vms.map((v) => <option key={v.id} value={v.id}>{v.name} ({v.hostName})</option>)}
          </NativeSelect>
        </Field>
      ) : (
        <Field label={t('bk.restore.host')} htmlFor="rd-host">
          <HostSelect id="rd-host" value={hostId} onChange={(v) => { setHostId(v); setFolder(''); }} choices={choices} />
        </Field>
      )}
      <HostFolderField id="rd-folder" label={t('bk.disk.folder')} hint={t('bk.disk.folderHint')} host={host} value={folder} onChange={setFolder} placeholder="D:\Restored" />
      {target === 'vm' && vm && <Notice kind="info">{vm.id === point.vmId ? t('bk.disk.sameVmNote') : t('bk.disk.attachNote')}</Notice>}
      <p className="text-xs text-muted-foreground">{t(target === 'vm' ? 'bk.disk.permVm' : 'bk.disk.permHost')}</p>
    </ActionDialog>
  );
}
