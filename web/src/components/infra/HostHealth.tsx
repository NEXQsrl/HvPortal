import { Activity, CircleAlert, CircleCheck, CircleHelp, HardDrive, Network, RefreshCw, Server, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { get } from '@/api/client';
import type { HealthLevel, HostHealthReport } from '@/api/infra-types';
import type { Host } from '@/api/types';
import { ErrorAlert, KeyValue, Loading, Meter, Notice, Section, StatusBadge } from '@/components/common';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, percent } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';

const TONE: Record<HealthLevel, 'ok' | 'warn' | 'danger' | 'muted'> = { Ok: 'ok', Warning: 'warn', Critical: 'danger', Unknown: 'muted' };

export function HealthBadge({ status, className }: { status: HealthLevel; className?: string }) {
  const { t } = useI18n();
  const Icon = status === 'Ok' ? CircleCheck : status === 'Warning' ? TriangleAlert : status === 'Critical' ? CircleAlert : CircleHelp;
  return <StatusBadge tone={TONE[status] ?? 'muted'} className={className}><Icon />{t(('health.' + status) as TKey)}</StatusBadge>;
}

function Empty({ children }: { children: ReactNode }) {
  return <TableRow><TableCell colSpan={9} className="text-sm text-muted-foreground">{children}</TableCell></TableRow>;
}

/**
 * Health tab of a host: hardware, physical disks (with reliability counters when readable), volumes, Storage Spaces
 * pools, physical network adapters, Hyper-V services and recent critical/error events, read live from the host
 * (GetHostHealth). Sections the agent could not read are listed as such: the overall state is never better than
 * what was actually read.
 */
export function HostHealthTab({ host }: { host: Host }) {
  const { t, fmtDate } = useI18n();
  const data = useLoad(() => get<HostHealthReport>(`/api/v1/hosts/${host.id}/health`), [host.id]);
  const r = data.data;
  if (!host.connected) return <Notice kind="warn">{t('health.offline')}</Notice>;
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-3 rounded-lg border bg-card p-3">
        <Activity className="size-5 text-muted-foreground" />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2 text-sm font-semibold">{t('health.overall')} {r ? <HealthBadge status={r.status} /> : data.loading ? null : <HealthBadge status="Unknown" />}</div>
          <div className="text-xs text-muted-foreground">{r ? t('health.collected', { at: fmtDate(r.collectedUtc) }) : t('health.help')}</div>
        </div>
        <Button size="sm" variant="outline" onClick={data.reload} disabled={data.loading}><RefreshCw className={cn(data.loading && 'animate-spin')} />{t('common.refresh')}</Button>
      </div>
      <ErrorAlert error={data.error} />
      {data.loading && !r && <Loading />}
      {r && r.errors.length > 0 && (
        <Notice kind="warn" title={t('health.partial')}>
          <ul className="list-disc pl-4">{r.errors.map((e) => <li key={e} className="font-mono text-xs [overflow-wrap:anywhere]">{e}</li>)}</ul>
        </Notice>
      )}
      {r && (
        <>
          <div className="grid gap-3 xl:grid-cols-2">
            <Section title={<span className="flex items-center gap-2"><Server className="size-4 text-muted-foreground" />{t('health.hardware')}</span>} bodyClassName="p-3">
              {r.hardware ? (
                <KeyValue items={[
                  [t('health.model'), [r.hardware.manufacturer, r.hardware.model].filter(Boolean).join(' ') || '—'],
                  [t('health.serial'), r.hardware.serialNumber ?? '—'],
                  [t('health.bios'), [r.hardware.biosVersion, r.hardware.biosDate ? fmtDate(r.hardware.biosDate) : null].filter(Boolean).join(' · ') || '—'],
                  [t('health.cpu'), r.hardware.processors.join(', ') || '—'],
                  [t('health.memory'), r.hardware.memoryInstalledBytes != null ? `${formatBytes(r.hardware.memoryInstalledBytes)}${r.hardware.memoryModules != null ? ` · ${t('health.modules', { n: r.hardware.memoryModules })}` : ''}` : '—'],
                  [t('health.hwStatus'), r.hardware.status ? <StatusBadge key="s" tone={r.hardware.status.toUpperCase() === 'OK' ? 'ok' : 'warn'}>{r.hardware.status}</StatusBadge> : '—'],
                ]} />
              ) : <div className="text-sm text-muted-foreground">{t('health.notRead')}</div>}
            </Section>
            <Section title={t('health.services')}>
              <Table>
                <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('health.state')}</TableHead><TableHead>{t('health.startMode')}</TableHead></TableRow></TableHeader>
                <TableBody>
                  {r.services.length === 0 ? <Empty>{t('health.notRead')}</Empty> : r.services.map((s) => (
                    <TableRow key={s.name}>
                      <TableCell><span className="font-medium">{s.displayName ?? s.name}</span> <span className="font-mono text-xs text-muted-foreground">{s.name}</span></TableCell>
                      <TableCell><StatusBadge tone={s.state === 'Running' ? 'ok' : s.name.toLowerCase() === 'vmms' ? 'danger' : 'warn'}>{s.state ?? '—'}</StatusBadge></TableCell>
                      <TableCell className="text-xs">{s.startMode ?? '—'}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Section>
          </div>

          <Section title={<span className="flex items-center gap-2"><HardDrive className="size-4 text-muted-foreground" />{t('health.disks')}</span>}>
            <Table>
              <TableHeader><TableRow>
                <TableHead>{t('common.name')}</TableHead><TableHead>{t('health.type')}</TableHead><TableHead className="text-right">{t('host.size')}</TableHead>
                <TableHead>{t('health.reported')}</TableHead><TableHead className="text-right">{t('health.temp')}</TableHead><TableHead className="text-right">{t('health.wear')}</TableHead>
                <TableHead className="text-right">{t('health.errors')}</TableHead><TableHead className="text-right">{t('health.hours')}</TableHead><TableHead>{t('common.status')}</TableHead>
              </TableRow></TableHeader>
              <TableBody>
                {r.physicalDisks.length === 0 ? <Empty>{t('health.noDisks')}</Empty> : r.physicalDisks.map((d) => (
                  <TableRow key={d.name + (d.serialNumber ?? '')} className={cn(d.status === 'Critical' && 'bg-destructive/5')}>
                    <TableCell><div className="font-medium">{d.name}</div>{d.serialNumber && <div className="font-mono text-xs text-muted-foreground">{d.serialNumber}</div>}</TableCell>
                    <TableCell className="text-xs">{[d.mediaType, d.busType].filter(Boolean).join(' · ') || '—'}</TableCell>
                    <TableCell className="text-right whitespace-nowrap tabular">{formatBytes(d.sizeBytes)}</TableCell>
                    <TableCell className="text-xs">{d.healthStatus ?? '—'}{d.operationalStatus.length > 0 && <div className="text-muted-foreground">{d.operationalStatus.join(', ')}</div>}</TableCell>
                    <TableCell className="text-right tabular">{d.temperatureCelsius != null ? `${d.temperatureCelsius} °C` : '—'}</TableCell>
                    <TableCell className="text-right tabular">{d.wearPercent != null ? `${d.wearPercent}%` : '—'}</TableCell>
                    <TableCell className="text-right tabular">{d.readErrorsUncorrected != null || d.writeErrorsUncorrected != null ? `${d.readErrorsUncorrected ?? '?'} / ${d.writeErrorsUncorrected ?? '?'}` : '—'}</TableCell>
                    <TableCell className="text-right tabular">{d.powerOnHours ?? '—'}</TableCell>
                    <TableCell><HealthBadge status={d.status} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <p className="border-t px-3 py-2 text-xs text-muted-foreground">{t('health.disksHelp')}</p>
          </Section>

          <div className="grid gap-3 xl:grid-cols-2">
            <Section title={t('health.volumes')}>
              <Table>
                <TableHeader><TableRow><TableHead>{t('host.volume')}</TableHead><TableHead>{t('host.fileSystem')}</TableHead><TableHead className="w-36">{t('host.usage')}</TableHead><TableHead>{t('health.reported')}</TableHead><TableHead>{t('common.status')}</TableHead></TableRow></TableHeader>
                <TableBody>
                  {r.volumes.length === 0 ? <Empty>{t('health.notRead')}</Empty> : r.volumes.map((v) => {
                    const used = percent(v.sizeBytes != null && v.freeBytes != null ? v.sizeBytes - v.freeBytes : null, v.sizeBytes);
                    return (
                      <TableRow key={v.name}>
                        <TableCell><span className="font-medium">{v.name}</span> <span className="text-xs text-muted-foreground">{v.label}</span></TableCell>
                        <TableCell className="text-xs">{v.fileSystem ?? '—'}</TableCell>
                        <TableCell><div className="flex items-center gap-2"><Meter value={used} className="w-16" /><span className="text-xs whitespace-nowrap tabular">{formatBytes(v.freeBytes)} {t('health.free')}</span></div></TableCell>
                        <TableCell className="text-xs">{[v.healthStatus, v.operationalStatus].filter(Boolean).join(' · ') || '—'}</TableCell>
                        <TableCell><HealthBadge status={v.status} /></TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            </Section>
            <Section title={t('health.pools')}>
              <Table>
                <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('health.allocated')}</TableHead><TableHead>{t('health.reported')}</TableHead><TableHead>{t('common.status')}</TableHead></TableRow></TableHeader>
                <TableBody>
                  {r.storagePools.length === 0 ? <Empty>{t('health.noPools')}</Empty> : r.storagePools.map((p) => (
                    <TableRow key={p.name}>
                      <TableCell><div className="font-medium">{p.name}</div>{p.virtualDisks.length > 0 && <div className="text-xs text-muted-foreground">{p.virtualDisks.join(', ')}</div>}</TableCell>
                      <TableCell className="text-xs whitespace-nowrap tabular">{formatBytes(p.allocatedBytes)} / {formatBytes(p.sizeBytes)}</TableCell>
                      <TableCell className="text-xs">{[p.healthStatus, p.operationalStatus].filter(Boolean).join(' · ') || '—'}</TableCell>
                      <TableCell><HealthBadge status={p.status} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Section>
          </div>

          <Section title={<span className="flex items-center gap-2"><Network className="size-4 text-muted-foreground" />{t('health.nics')}</span>}>
            <Table>
              <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('health.state')}</TableHead><TableHead>{t('health.speed')}</TableHead><TableHead>{t('common.status')}</TableHead></TableRow></TableHeader>
              <TableBody>
                {r.networkAdapters.length === 0 ? <Empty>{t('health.notRead')}</Empty> : r.networkAdapters.map((a) => (
                  <TableRow key={a.name}>
                    <TableCell><div className="font-medium">{a.name}</div>{a.description && <div className="text-xs text-muted-foreground">{a.description}</div>}</TableCell>
                    <TableCell className="text-xs">{a.status ?? '—'}</TableCell>
                    <TableCell className="text-xs">{a.linkSpeed ?? '—'}</TableCell>
                    <TableCell><HealthBadge status={a.health} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Section>

          <Section title={t('health.events')}>
            <Table containerClassName="max-h-96">
              <TableHeader><TableRow><TableHead className="w-44">{t('health.time')}</TableHead><TableHead>{t('health.level')}</TableHead><TableHead>{t('health.source')}</TableHead><TableHead>{t('health.message')}</TableHead></TableRow></TableHeader>
              <TableBody>
                {r.events.length === 0 ? <Empty>{t('health.noEvents')}</Empty> : r.events.map((e, i) => (
                  <TableRow key={i}>
                    <TableCell className="text-xs whitespace-nowrap">{fmtDate(e.timeUtc)}</TableCell>
                    <TableCell><StatusBadge tone={e.level === 'Critical' ? 'danger' : 'warn'}>{e.level}</StatusBadge></TableCell>
                    <TableCell className="text-xs">{e.provider} <span className="text-muted-foreground">#{e.id}</span></TableCell>
                    <TableCell className="max-w-[40rem] text-xs [overflow-wrap:anywhere]">{e.message ?? '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <p className="border-t px-3 py-2 text-xs text-muted-foreground">{t('health.eventsHelp')}</p>
          </Section>
        </>
      )}
    </div>
  );
}
