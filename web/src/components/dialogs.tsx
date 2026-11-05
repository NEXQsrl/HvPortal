import { useState, type ReactNode } from 'react';
import { useI18n } from '../i18n';
import { Dialog, ErrorAlert } from './ui';

/** Edit portal-only description (and optionally the display name). */
export function EditDialog({ title, initialName, initialDescription, withName, onSave, onClose }: {
  title: string;
  initialName?: string;
  initialDescription: string | null;
  withName?: boolean;
  onSave: (name: string | undefined, description: string) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [name, setName] = useState(initialName ?? '');
  const [description, setDescription] = useState(initialDescription ?? '');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await onSave(withName ? name : undefined, description);
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open title={title} onClose={onClose}
      actions={<><button type="button" onClick={onClose}>{t('common.cancel')}</button>
        <button type="button" className="primary" disabled={busy || (withName && !name.trim())} onClick={() => void save()}>{t('common.save')}</button></>}>
      <ErrorAlert error={error} />
      {withName && (
        <div className="field">
          <label htmlFor="edit-name">{t('host.name')}</label>
          <input id="edit-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
        </div>
      )}
      <div className="field">
        <label htmlFor="edit-description">{t('common.description')}</label>
        <textarea id="edit-description" rows={5} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />
        <div className="hint">{t('common.descriptionHint')} ({description.length}/1000)</div>
      </div>
    </Dialog>
  );
}

/** Destructive confirmation: shows the exact resource and consequences and requires the name to be typed back. */
export function ConfirmDeleteDialog({ title, name, impact, confirmLabel, onConfirm, onClose }: {
  title: string; name: string; impact: ReactNode; confirmLabel: string; onConfirm: (typed: string) => Promise<void>; onClose: () => void;
}) {
  const { t } = useI18n();
  const [typed, setTyped] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const go = async () => {
    setBusy(true);
    setError(null);
    try {
      await onConfirm(typed);
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open title={title} onClose={onClose}
      actions={<><button type="button" onClick={onClose}>{t('common.cancel')}</button>
        <button type="button" className="danger" disabled={busy || typed !== name} onClick={() => void go()}>{confirmLabel}</button></>}>
      <div className="alert danger">{impact}</div>
      <ErrorAlert error={error} />
      <div className="field">
        <label htmlFor="confirm-delete">{t('confirm.typeExact', { name })}</label>
        <input id="confirm-delete" autoComplete="off" value={typed} onChange={(e) => setTyped(e.target.value)} />
      </div>
    </Dialog>
  );
}
