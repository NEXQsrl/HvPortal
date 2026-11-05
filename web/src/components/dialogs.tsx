import { useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { useI18n } from '@/i18n';
import { cn } from '@/lib/utils';
import { ErrorAlert, Notice } from './common';

/** Dialog with a primary action that may fail: shows the error inline and keeps the dialog open. */
export function ActionDialog({ title, description, children, confirmLabel, onConfirm, onClose, danger, disabled, wide, footer }: {
  title: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  confirmLabel: string;
  onConfirm: () => Promise<void>;
  onClose: () => void;
  danger?: boolean;
  disabled?: boolean;
  wide?: boolean;
  footer?: ReactNode;
}) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const go = async () => {
    setBusy(true);
    setError(null);
    try {
      await onConfirm();
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog open onOpenChange={(o) => { if (!o && !busy) onClose(); }}>
      <DialogContent className={cn(wide ? 'sm:max-w-3xl' : 'sm:max-w-lg')}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          {description && <DialogDescription>{description}</DialogDescription>}
        </DialogHeader>
        <form id="action-dialog-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); if (!disabled && !busy) void go(); }}>
          {children}
          <ErrorAlert error={error} />
        </form>
        <DialogFooter>
          {footer}
          <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('common.cancel')}</Button>
          <Button type="submit" form="action-dialog-form" variant={danger ? 'destructive' : 'default'} disabled={disabled || busy}>{confirmLabel}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function Field({ label, htmlFor, hint, children, className, error }: { label: ReactNode; htmlFor?: string; hint?: ReactNode; children: ReactNode; className?: string; error?: ReactNode }) {
  return (
    <div className={cn('field grid gap-1.5', className)}>
      <Label htmlFor={htmlFor} className="text-xs font-medium text-muted-foreground">{label}</Label>
      {children}
      {error ? <div className="text-xs text-destructive" role="alert">{error}</div> : hint && <div className="hint text-xs text-muted-foreground">{hint}</div>}
    </div>
  );
}

/** Edit portal-only description (and optionally the display name). */
export function EditDialog({ title, initialName, initialDescription, withName, nameLabel, onSave, onClose }: {
  title: string;
  initialName?: string;
  initialDescription: string | null;
  withName?: boolean;
  nameLabel?: string;
  onSave: (name: string | undefined, description: string) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [name, setName] = useState(initialName ?? '');
  const [description, setDescription] = useState(initialDescription ?? '');
  return (
    <ActionDialog title={title} confirmLabel={t('common.save')} onClose={onClose} disabled={withName && !name.trim()}
      onConfirm={() => onSave(withName ? name.trim() : undefined, description)}>
      {withName && (
        <Field label={nameLabel ?? t('host.name')} htmlFor="edit-name">
          <Input id="edit-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} autoFocus />
        </Field>
      )}
      <Field label={t('common.description')} htmlFor="edit-description" hint={`${t('common.descriptionHint')} (${description.length}/1000)`}>
        <Textarea id="edit-description" rows={5} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
    </ActionDialog>
  );
}

/** Destructive confirmation: shows the exact resource and consequences and requires the name to be typed back. */
export function ConfirmTypedDialog({ title, name, impact, confirmLabel, onConfirm, onClose, children }: {
  title: string; name: string; impact: ReactNode; confirmLabel: string; onConfirm: (typed: string) => Promise<void>; onClose: () => void; children?: ReactNode;
}) {
  const { t } = useI18n();
  const [typed, setTyped] = useState('');
  return (
    <ActionDialog title={title} confirmLabel={confirmLabel} danger disabled={typed !== name} onClose={onClose} onConfirm={() => onConfirm(typed)}>
      <Notice kind="danger">{impact}</Notice>
      {children}
      <Field label={t('confirm.typeExact', { name })} htmlFor="confirm-delete">
        <Input id="confirm-delete" autoComplete="off" value={typed} onChange={(e) => setTyped(e.target.value)} autoFocus />
      </Field>
    </ActionDialog>
  );
}
