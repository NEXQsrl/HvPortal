import { ArrowUpCircle, CircleCheck, Download, ExternalLink, RefreshCw } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { get, post, put } from '@/api/client';
import { ErrorAlert, KeyValue, Loading, Notice, Section, StatusBadge } from '@/components/common';
import { ActionDialog } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Switch } from '@/components/ui/switch';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';

export interface PortalUpdateStatus {
  current: string;
  autoCheck: boolean;
  lastCheckAt: string | null;
  lastError: string | null;
  latest: {
    version: string; tag: string; publishedAt: string | null; htmlUrl: string | null; notes: string | null;
    gatewaySetup: { name: string; size: number; hasChecksum: boolean } | null;
    agentSetup: { name: string; size: number; hasChecksum: boolean } | null;
  } | null;
  updateAvailable: boolean;
  /** container | not_a_service | updater_missing: the portal cannot install by itself here. */
  installBlocker: string | null;
  installRequestedAt: string | null;
  installRequestedVersion: string | null;
  lastResult: { timeUtc: string | null; status: string | null; version: string | null; message: string | null } | null;
}

/**
 * Settings > System > Updates (ADR-042): latest release on GitHub, checked only when asked (or automatically when turned
 * on); install it from here when the gateway runs as a Windows service with the updater task. The updater installs only
 * setups signed by the same publisher as the installed gateway.
 */
export function PortalUpdatesSection() {
  const { t, fmtDate } = useI18n();
  const data = useLoad(() => get<PortalUpdateStatus>('/api/v1/system/updates'));
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [confirm, setConfirm] = useState(false);
  const s = data.data;

  const check = async () => {
    setChecking(true);
    setError(null);
    try { await post('/api/v1/system/updates/check'); data.reload(); } catch (e) { setError(e); } finally { setChecking(false); }
  };

  return (
    <Section title={<span className="flex items-center gap-2"><ArrowUpCircle className="size-4 text-muted-foreground" />{t('pupd.title')}</span>} bodyClassName="space-y-3 p-4"
      actions={<Button size="sm" variant="outline" onClick={() => void check()} disabled={checking}><RefreshCw className={cn(checking && 'animate-spin')} />{t('pupd.check')}</Button>}>
      <ErrorAlert error={data.error ?? error} />
      {!s ? <Loading /> : (
        <>
          <KeyValue items={[
            [t('pupd.current'), <span key="c" className="font-medium">{s.current}</span>],
            [t('pupd.latest'), s.latest ? (
              <span key="l" className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{s.latest.version}</span>
                {s.latest.publishedAt && <span className="text-xs text-muted-foreground">{fmtDate(s.latest.publishedAt)}</span>}
                {s.updateAvailable ? <StatusBadge tone="warn">{t('pupd.available')}</StatusBadge> : <StatusBadge tone="ok"><CircleCheck />{t('pupd.upToDate')}</StatusBadge>}
                {s.latest.htmlUrl && <a className="inline-flex items-center gap-1 text-xs text-primary hover:underline" href={s.latest.htmlUrl} target="_blank" rel="noopener noreferrer">{t('pupd.notes')}<ExternalLink className="size-3" /></a>}
              </span>
            ) : '—'],
            [t('pupd.lastCheck'), s.lastCheckAt ? fmtDate(s.lastCheckAt) : t('pupd.never')],
          ]} />
          {s.lastError && <Notice kind="warn" title={t('pupd.checkFailed')}><span className="text-xs [overflow-wrap:anywhere]">{s.lastError}</span></Notice>}
          {s.lastResult && (
            <Notice kind={s.lastResult.status === 'ok' ? 'ok' : 'danger'} title={t(s.lastResult.status === 'ok' ? 'pupd.lastOk' : 'pupd.lastFailed', { at: fmtDate(s.lastResult.timeUtc) })}>
              <span className="text-xs [overflow-wrap:anywhere]">{s.lastResult.message}</span>
            </Notice>
          )}
          {s.installRequestedVersion && s.installRequestedVersion !== s.current && (!s.lastResult?.timeUtc || (s.installRequestedAt && s.lastResult.timeUtc < s.installRequestedAt)) && (
            <Notice kind="info">{t('pupd.inProgress', { version: s.installRequestedVersion, at: fmtDate(s.installRequestedAt) })}</Notice>
          )}
          {s.updateAvailable && s.latest && (
            s.installBlocker ? <Notice kind="info" title={t('pupd.manualTitle')}>{t(('pupd.blocker.' + s.installBlocker) as TKey)}</Notice> : (
              <div className="flex flex-wrap items-center gap-3 rounded-lg border border-primary/30 bg-primary/5 p-3">
                <div className="min-w-0 flex-1 text-sm">
                  <div className="font-medium">{t('pupd.ready', { version: s.latest.version })}</div>
                  <div className="text-xs text-muted-foreground">{t('pupd.readyHelp')}</div>
                </div>
                <Button onClick={() => setConfirm(true)} disabled={!s.latest.gatewaySetup?.hasChecksum}><Download />{t('pupd.install')}</Button>
              </div>
            )
          )}
          <label className="flex items-start gap-2 border-t pt-3 text-sm">
            <Switch checked={s.autoCheck} className="mt-0.5" onCheckedChange={async (v) => {
              try { await put('/api/v1/system/updates/settings', { autoCheck: v }); data.reload(); } catch (e) { setError(e); }
            }} />
            <span>{t('pupd.auto')}<span className="block text-xs text-muted-foreground">{t('pupd.autoHelp')}</span></span>
          </label>
        </>
      )}
      {confirm && s?.latest && (
        <ActionDialog title={t('pupd.confirmTitle', { version: s.latest.version })} confirmLabel={t('pupd.install')} onClose={() => setConfirm(false)}
          onConfirm={async () => {
            await post('/api/v1/system/updates/install', { version: s.latest!.version });
            toast.success(t('pupd.started'), t('pupd.startedHelp'));
            data.reload();
          }}>
          <ul className="list-disc space-y-1 pl-4 text-sm">
            <li>{t('pupd.step1', { size: formatBytes(s.latest.gatewaySetup?.size) })}</li>
            <li>{t('pupd.step2')}</li>
            <li>{t('pupd.step3')}</li>
            <li>{t('pupd.step4')}</li>
          </ul>
        </ActionDialog>
      )}
    </Section>
  );
}

/** Dashboard (settings administrators): a newer release found by the last check (the dashboard never contacts GitHub). */
export function PortalUpdateNotice() {
  const { t } = useI18n();
  const data = useLoad(() => get<PortalUpdateStatus>('/api/v1/system/updates').catch(() => null));
  const s = data.data;
  if (!s?.updateAvailable || !s.latest) return null;
  return (
    <Notice kind="info" className="mb-3">
      <span className="flex flex-wrap items-center gap-2"><ArrowUpCircle className="size-4" />{t('pupd.banner', { version: s.latest.version })}
        <Link className="text-primary hover:underline" to="/settings?tab=system">{t('pupd.bannerLink')}</Link></span>
    </Notice>
  );
}
