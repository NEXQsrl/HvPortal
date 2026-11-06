import { FolderOpen, FolderSearch } from 'lucide-react';
import { useState } from 'react';
import { newIdempotencyKey, patch, post, put } from '@/api/client';
import type { Accepted, VmTemplate } from '@/api/ops-types';
import type { Host, Vm } from '@/api/types';
import { ErrorAlert, Notice, Section } from '@/components/common';
import { ActionDialog, ConfirmTypedDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { useI18n } from '@/i18n';
import { LIBRARY_PATH } from '@/lib/format';
import { toast } from '@/lib/toast';
import { useSession } from '@/session';
import { HostFolderPicker } from './HostFolderPicker';

const validLibrary = (p: string) => LIBRARY_PATH.test(p) && !p.includes('..') && p.length <= 240;

/** Folder of the host where its templates are kept: text field plus the folder browser of the host. */
function LibraryField({ host, value, onChange }: { host: Host; value: string; onChange: (v: string) => void }) {
  const { t } = useI18n();
  const [browsing, setBrowsing] = useState(false);
  const invalid = !!value.trim() && !validLibrary(value.trim());
  return (
    <>
      <Field label={t('tpl.capture.libraryChoose')} htmlFor="tpl-library" hint={t('tpl.capture.libraryHint')} error={invalid ? t('tpl.library.invalid') : null}>
        <div className="flex gap-2">
          <Input id="tpl-library" value={value} onChange={(e) => onChange(e.target.value)} placeholder="D:\Templates" className="min-w-0 flex-1 font-mono text-xs" aria-invalid={invalid || undefined} />
          <Button type="button" variant="outline" disabled={!host.canBrowseFolders} onClick={() => setBrowsing(true)}><FolderSearch />{t('picker.browse')}</Button>
        </div>
      </Field>
      {browsing && <HostFolderPicker hostId={host.id} mode="folder" initialPath={value.trim() || null} onPickFolder={(f) => { onChange(f.path); setBrowsing(false); }} onClose={() => setBrowsing(false)} />}
    </>
  );
}

/** Create a template from a VM that is off: its disks are copied into the template library of the host. */
export function CaptureTemplateDialog({ vm, onQueued, onClose }: { vm: Vm; onQueued: (r: Accepted, label: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const { can } = useSession();
  const inv = useInventory();
  const host = inv.hostById.get(vm.hostId);
  const [name, setName] = useState(`${vm.name}-template`.slice(0, 100));
  const [description, setDescription] = useState('');
  const [library, setLibrary] = useState('');
  const [key] = useState(newIdempotencyKey);
  const current = host?.templateLibraryPath ?? null;
  const manageHosts = can('hosts.manage');
  const off = vm.powerState === 'Off';
  const needsLibrary = !current;
  const libraryOk = !needsLibrary || (manageHosts && validLibrary(library.trim()));
  const submit = async () => {
    if (needsLibrary && host) {
      await put(`/api/v1/hosts/${host.id}/template-library`, { path: library.trim() });
      inv.reload();
    }
    const r = await post<Accepted>(`/api/v1/vms/${vm.id}/template`, { name: name.trim(), description: description.trim() || undefined, idempotencyKey: key });
    onQueued(r, `${t('jobop.template.capture.Create')} — ${name.trim()}`);
  };
  return (
    <ActionDialog title={t('tpl.capture.title', { vm: vm.name })} confirmLabel={t('tpl.capture.confirm')} onClose={onClose} onConfirm={submit}
      disabled={!off || !name.trim() || !libraryOk || !host}>
      {!off && <Notice kind="warn">{t('tpl.capture.mustBeOff')}</Notice>}
      {off && (vm.checkpointCount ?? 0) > 0 && <Notice kind="warn">{t('tpl.capture.checkpoints')}</Notice>}
      <p className="text-sm text-muted-foreground">{t('tpl.capture.what')}</p>
      <Notice kind="info">{t('tpl.capture.prepare')}</Notice>
      <Field label={t('tpl.capture.name')} htmlFor="tpl-name">
        <Input id="tpl-name" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label={t('tpl.capture.description')} htmlFor="tpl-description">
        <Textarea id="tpl-description" rows={2} maxLength={2000} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      {current ? (
        <div className="text-xs text-muted-foreground">{t('tpl.capture.library')}: <span className="font-mono text-foreground">{current}</span></div>
      ) : manageHosts && host ? (
        <>
          <Notice kind="warn">{t('tpl.capture.libraryMissing')}</Notice>
          <LibraryField host={host} value={library} onChange={setLibrary} />
        </>
      ) : <Notice kind="warn">{t('tpl.capture.libraryMissing')} {t('tpl.capture.libraryAsk')}</Notice>}
    </ActionDialog>
  );
}

export function EditTemplateDialog({ template, onSaved, onClose }: { template: VmTemplate; onSaved: () => void; onClose: () => void }) {
  const { t } = useI18n();
  const [name, setName] = useState(template.name);
  const [description, setDescription] = useState(template.description ?? '');
  return (
    <ActionDialog title={t('tpl.edit.title')} confirmLabel={t('common.save')} onClose={onClose} disabled={!name.trim()}
      onConfirm={async () => { await patch(`/api/v1/templates/${template.id}`, { name: name.trim(), description: description.trim() }); onSaved(); }}>
      <Field label={t('tpl.capture.name')} htmlFor="tpl-edit-name">
        <Input id="tpl-edit-name" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label={t('tpl.capture.description')} htmlFor="tpl-edit-description">
        <Textarea id="tpl-edit-description" rows={3} maxLength={2000} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
    </ActionDialog>
  );
}

export function DeleteTemplateDialog({ template, onQueued, onClose }: { template: VmTemplate; onQueued: (r: Accepted, label: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const [key] = useState(newIdempotencyKey);
  return (
    <ConfirmTypedDialog title={t('tpl.delete.title', { name: template.name })} name={template.name} impact={t('tpl.delete.impact')} confirmLabel={t('tpl.delete')} onClose={onClose}
      onConfirm={async (typed) => {
        const r = await post<Accepted>(`/api/v1/templates/${template.id}/delete`, { confirmName: typed, idempotencyKey: key });
        onQueued(r, `${t('jobop.template.delete.Delete')} — ${template.name}`);
      }} />
  );
}

/** Host settings: the folder where the templates of the host are kept. */
export function TemplateLibrarySection({ host, onChanged }: { host: Host; onChanged: () => void }) {
  const { t } = useI18n();
  const { can } = useSession();
  const manage = can('hosts.manage');
  const [path, setPath] = useState(host.templateLibraryPath ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [browsing, setBrowsing] = useState(false);
  const trimmed = path.trim();
  const dirty = trimmed !== (host.templateLibraryPath ?? '');
  const invalid = !!trimmed && !validLibrary(trimmed);
  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await put(`/api/v1/hosts/${host.id}/template-library`, { path: trimmed || null });
      toast.success(t('settings.saved'));
      onChanged();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <Section title={<span className="flex items-center gap-2"><FolderOpen className="size-4 text-muted-foreground" />{t('tpl.library')}</span>} bodyClassName="space-y-3 p-3">
      <p className="text-xs text-muted-foreground">{t('tpl.library.help')}</p>
      {!host.templates && <Notice kind="info">{t('tpl.library.agent')}</Notice>}
      {manage ? (
        <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); if (dirty && !invalid) void save(); }}>
          <Input value={path} onChange={(e) => setPath(e.target.value)} placeholder="D:\Templates" className="w-72 font-mono" aria-label={t('tpl.library')} aria-invalid={invalid || undefined} />
          <Button type="button" size="sm" variant="outline" disabled={!host.canBrowseFolders} onClick={() => setBrowsing(true)}><FolderSearch />{t('picker.browse')}</Button>
          <span className="flex-1" />
          <Button type="submit" size="sm" disabled={!dirty || invalid || busy}>{t('common.save')}</Button>
        </form>
      ) : <div className="font-mono text-xs">{host.templateLibraryPath ?? <span className="font-sans text-muted-foreground">{t('tpl.library.none')}</span>}</div>}
      {invalid && <div className="text-xs text-destructive">{t('tpl.library.invalid')}</div>}
      {manage && !trimmed && !host.templateLibraryPath && <div className="text-xs text-muted-foreground">{t('tpl.library.none')}</div>}
      <ErrorAlert error={error} />
      {browsing && <HostFolderPicker hostId={host.id} mode="folder" initialPath={trimmed || null} onPickFolder={(f) => { setPath(f.path); setBrowsing(false); }} onClose={() => setBrowsing(false)} />}
    </Section>
  );
}
