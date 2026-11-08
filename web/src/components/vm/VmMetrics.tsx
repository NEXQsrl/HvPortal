import { Activity, Cpu, Gauge, HardDrive, MemoryStick, Network, Power, PowerOff, RefreshCw } from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Area, AreaChart, CartesianGrid, Line, LineChart, ReferenceLine, Tooltip, XAxis, YAxis } from 'recharts';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import { realtime } from '@/api/realtime';
import type { Vm } from '@/api/types';
import { ErrorAlert, Loading, Notice, Section, StatCard, StatusBadge } from '@/components/common';
import { ActionDialog } from '@/components/dialogs';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { ChartContainer, ChartTooltipContent, type ChartConfig } from '@/components/ui/chart';
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';

interface MetricPoint {
  at: string;
  cpuAvg: number | null; cpuMax: number | null;
  memAssigned: number | null; memAssignedMax: number | null; memDemand: number | null; memMaximum: number | null; memStartup: number | null; memPressure: number | null;
  iops: number | null; readMBps: number | null; writeMBps: number | null; netInMbps: number | null; netOutMbps: number | null;
  gapBefore: boolean;
}

interface MetricSummary {
  samples: number; from: string | null; to: string | null;
  cpuAvg: number | null; cpuMax: number | null; cpuMaxAt: string | null; cpuP95: number | null; cpuHoursAbove80: number;
  memAssignedMax: number | null; memDemandMax: number | null; memDemandMaxAt: string | null; memDemandAvg: number | null; memPressureMax: number | null; memMaximum: number | null;
  dynamicMemory: boolean | null; memStatusWarnings: number;
  iopsAvg: number | null; iopsMax: number | null; readMb: number | null; writeMb: number | null; readMaxMBps: number | null; writeMaxMBps: number | null;
  netInMb: number | null; netOutMb: number | null; netInMaxMbps: number | null; netOutMaxMbps: number | null;
  latencyAvgMs: number | null; totalDiskMb: number | null; maximumMemorySinceResetMb: number | null;
}

interface MetricsResponse {
  enabled: boolean | null; known: boolean; everEnabled: boolean; hostConnected: boolean; agentOutdated: boolean; vmMissing: boolean;
  liveError: string | null; canManage: boolean;
  enabledAt: string | null; lastResetAt: string | null; nextResetAt: string | null; lastSampleAt: string | null;
  lastError: string | null; lastErrorAt: string | null;
  intervalSeconds: number; retentionDays: number; resetDays: number; hours: number;
  live: { cpuUsagePercent: number | null; memoryAssignedMb: number | null; memoryDemandMb: number | null; memoryStatus: string | null } | null;
  summary: MetricSummary;
  points: MetricPoint[];
}

const RANGES = [6, 24, 72, 240] as const;
const MB = 1024 * 1024;

const fmtMb = (mb: number | null | undefined) => (mb == null ? '—' : formatBytes(mb * MB));
/** Numbers in the language of the portal (not of the browser), like the rest of the text. */
const numberFormatter = (lang: string) => (v: number | null | undefined, digits = 0) =>
  (v == null ? '—' : v.toLocaleString(lang === 'it' ? 'it-IT' : 'en-GB', { maximumFractionDigits: digits }));

/**
 * Resource use history of a VM (ADR-039): Hyper-V resource metering turned on from here (once: after it is turned off
 * by someone it stays off until turned on again), samples every 5 minutes kept for 10 days, charts explained for the
 * administrator. Nothing is estimated: periods without samples are gaps, values the host did not report are "—".
 */
export function VmMetrics({ vm }: { vm: Vm }) {
  const { t, lang, fmtDate } = useI18n();
  const fmtNum = numberFormatter(lang);
  const ops = useOps();
  const [hours, setHours] = useState<number>(24);
  const data = useLoad(() => get<MetricsResponse>(`/api/v1/vms/${vm.id}/metrics?hours=${hours}`), [vm.id, hours]);
  const [job, setJob] = useState<string | null>(null);
  const [autoStarted, setAutoStarted] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [confirmOff, setConfirmOff] = useState(false);
  const tried = useRef(false);
  const d = data.data;

  const requestMetering = async (enabled: boolean) => {
    const r = await post<Accepted>(`/api/v1/vms/${vm.id}/metering`, { enabled, idempotencyKey: newIdempotencyKey() });
    setJob(r.jobId);
    ops.queued(r, `${t(enabled ? 'jobop.vm.metering.Enable' : 'jobop.vm.metering.Disable')} — ${vm.name}`);
  };
  const setMetering = async (enabled: boolean, auto = false) => {
    setError(null);
    try {
      await requestMetering(enabled);
      if (auto) setAutoStarted(true);
    } catch (e) {
      setError(e);
    }
  };

  // First visit of a VM never metered: turn metering on (the user asked for the tab). Never again after that, so that
  // "Turn off" stays off.
  useEffect(() => {
    if (!d || tried.current) return;
    if (d.known && d.enabled === false && !d.everEnabled && d.canManage && !d.agentOutdated && !d.vmMissing) {
      tried.current = true;
      void setMetering(true, true);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d]);

  useEffect(() => realtime.on('jobChanged', (p) => {
    if (job && p.jobId === job && p.status && !['Queued', 'Running', 'Reconciling'].includes(p.status)) {
      setJob(null);
      data.reload();
    }
  }), [job, data]);

  // New samples every interval while the tab is open.
  useEffect(() => {
    if (!d?.enabled) return;
    const id = window.setInterval(data.reload, Math.max(60, d.intervalSeconds) * 1000);
    return () => window.clearInterval(id);
  }, [d?.enabled, d?.intervalSeconds, data.reload]);

  const fmtTick = useMemo(() => {
    const short = new Intl.DateTimeFormat(lang === 'it' ? 'it-IT' : 'en-GB', hours <= 24 ? { hour: '2-digit', minute: '2-digit' } : { day: '2-digit', month: '2-digit', hour: '2-digit' });
    return (v: string | number) => short.format(new Date(v));
  }, [lang, hours]);
  const fmtLabel = (v: string | number) => fmtDate(String(v));

  // A gap (host or gateway offline) breaks the lines instead of joining the two sides.
  const series = useMemo(() => {
    const out: (MetricPoint | { at: string })[] = [];
    for (const p of d?.points ?? []) {
      if (p.gapBefore) out.push({ at: new Date(new Date(p.at).getTime() - 1000).toISOString() });
      out.push(p);
    }
    return out;
  }, [d?.points]);

  if (data.loading && !d) return <div className="p-4"><Loading /></div>;
  if (!d) return <div className="p-4"><ErrorAlert error={data.error} /></div>;
  const s = d.summary;
  const hasData = d.points.length > 0;

  const status = d.enabled === true
    ? <StatusBadge tone="ok"><Activity className="size-3" />{t('met.on')}</StatusBadge>
    : d.enabled === false ? <StatusBadge tone="muted">{t('met.off')}</StatusBadge>
      : <StatusBadge tone="warn">{t('met.unknown')}</StatusBadge>;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2">
        {status}
        <ToggleGroup type="single" size="sm" variant="outline" value={String(hours)} onValueChange={(v) => v && setHours(Number(v))} aria-label={t('met.range')}>
          {RANGES.map((h) => <ToggleGroupItem key={h} value={String(h)} className="px-2.5">{t(`met.range.${h}` as TKey)}</ToggleGroupItem>)}
        </ToggleGroup>
        <Button size="sm" variant="outline" onClick={data.reload} disabled={data.loading}><RefreshCw className={cn(data.loading && 'animate-spin')} />{t('common.refresh')}</Button>
        <div className="ml-auto flex items-center gap-2">
          {d.canManage && d.enabled === true && <Button size="sm" variant="outline" disabled={!!job} onClick={() => setConfirmOff(true)}><PowerOff />{t('met.turnOff')}</Button>}
          {d.canManage && d.enabled === false && d.everEnabled && !d.agentOutdated && <Button size="sm" disabled={!!job || vm.isMissing} onClick={() => void setMetering(true)}><Power />{t('met.turnOn')}</Button>}
        </div>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-auto p-4">
        <ErrorAlert error={error} />
        {d.agentOutdated && <Notice kind="warn">{t('met.agentOutdated')}</Notice>}
        {!d.hostConnected && <Notice kind="warn">{t('met.hostOffline')}</Notice>}
        {d.liveError && d.hostConnected && <Notice kind="warn">{t('met.liveError')}</Notice>}
        {job && <Notice kind="info">{t('met.jobRunning')}</Notice>}
        {autoStarted && <Notice kind="info">{t('met.autoOn', { min: Math.round(d.intervalSeconds / 60) })}</Notice>}
        {d.enabled === false && !d.everEnabled && !d.canManage && <Notice kind="warn">{t('met.offNoPermission')}</Notice>}
        {d.enabled === false && d.everEnabled && <Notice kind="info">{t('met.offHistory', { days: d.retentionDays })}</Notice>}
        {d.lastError && <Notice kind="warn" title={t('met.lastError', { at: fmtDate(d.lastErrorAt) })}>{d.lastError}</Notice>}
        {d.enabled === true && !hasData && <Notice kind="info">{t('met.waiting', { min: Math.round(d.intervalSeconds / 60) })}</Notice>}

        {hasData && (
          <>
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-6">
              <StatCard icon={<Cpu className="size-3.5" />} label={t('met.card.cpuMax')} value={s.cpuMax == null ? '—' : `${fmtNum(s.cpuMax)} %`} meter={s.cpuMax}
                sub={<><span className="block">{t('met.card.cpuAvg', { avg: fmtNum(s.cpuAvg, 1), p95: fmtNum(s.cpuP95) })}</span>
                  {s.cpuMaxAt && <span className="block">{t('met.card.at', { at: fmtDate(s.cpuMaxAt) })}</span>}
                  <span className={cn('block', s.cpuHoursAbove80 > 0 && 'text-warning')}>{t('met.card.above80', { h: fmtNum(s.cpuHoursAbove80, 1) })}</span></>} />
              <StatCard icon={<MemoryStick className="size-3.5" />} label={t('met.card.memMax')} value={fmtMb(s.memAssignedMax)}
                sub={<><span className="block">{t('met.card.demandMax', { v: fmtMb(s.memDemandMax) })}</span>
                  <span className="block">{t('met.card.demandAvg', { v: fmtMb(s.memDemandAvg) })}</span>
                  {s.memMaximum != null && <span className="block">{t('met.card.memLimit', { v: fmtMb(s.memMaximum) })}</span>}</>} />
              <StatCard icon={<Gauge className="size-3.5" />} label={t('met.card.pressure')} value={s.memPressureMax == null ? '—' : `${fmtNum(s.memPressureMax)} %`}
                meter={s.memPressureMax == null ? null : Math.min(100, s.memPressureMax)}
                sub={<><span className="block">{t('met.card.pressureHelp')}</span>
                  {s.memStatusWarnings > 0 && <span className="block text-warning">{t('met.card.memStatus', { n: s.memStatusWarnings })}</span>}</>} />
              <StatCard icon={<HardDrive className="size-3.5" />} label={t('met.card.iops')} value={fmtNum(s.iopsMax)}
                sub={<><span className="block">{t('met.card.iopsAvg', { v: fmtNum(s.iopsAvg) })}</span>
                  <span className="block">{t('met.card.latency', { v: fmtNum(s.latencyAvgMs) })}</span></>} />
              <StatCard icon={<HardDrive className="size-3.5" />} label={t('met.card.diskData')} value={fmtMb(s.readMb == null ? null : s.readMb + (s.writeMb ?? 0))}
                sub={<><span className="block">{t('met.card.readWrite', { r: fmtMb(s.readMb), w: fmtMb(s.writeMb) })}</span>
                  <span className="block">{t('met.card.diskPeak', { r: fmtNum(s.readMaxMBps, 1), w: fmtNum(s.writeMaxMBps, 1) })}</span>
                  {s.totalDiskMb != null && <span className="block">{t('met.card.allocated', { v: fmtMb(s.totalDiskMb) })}</span>}</>} />
              <StatCard icon={<Network className="size-3.5" />} label={t('met.card.netPeak')} value={s.netInMaxMbps == null ? '—' : `${fmtNum(Math.max(s.netInMaxMbps, s.netOutMaxMbps ?? 0), 1)} Mbit/s`}
                sub={<><span className="block">{t('met.card.netPeakInOut', { i: fmtNum(s.netInMaxMbps, 1), o: fmtNum(s.netOutMaxMbps, 1) })}</span>
                  <span className="block">{t('met.card.netTotal', { i: fmtMb(s.netInMb), o: fmtMb(s.netOutMb) })}</span></>} />
            </div>
            <p className="text-xs text-muted-foreground">
              {t('met.period', { from: fmtDate(s.from), to: fmtDate(s.to), n: s.samples, min: Math.round(d.intervalSeconds / 60) })}
            </p>

            <div className="grid gap-4 2xl:grid-cols-2">
              <ChartSection title={t('met.chart.cpu')} what={t('met.chart.cpuWhat')} look={t('met.chart.cpuLook')}>
                <ChartContainer config={cpuConfig(t)} legend={['cpuAvg', 'cpuMax']}>
                  <AreaChart data={series} margin={{ left: 4, right: 12, top: 8 }}>
                    <CartesianGrid vertical={false} />
                    <XAxis dataKey="at" tickFormatter={fmtTick} minTickGap={40} tickLine={false} axisLine={false} />
                    <YAxis domain={[0, 100]} unit="%" width={44} tickLine={false} axisLine={false} />
                    <ReferenceLine y={80} strokeDasharray="4 4" />
                    <Tooltip content={<ChartTooltipContent labelFormatter={fmtLabel} />} />
                    <Area dataKey="cpuAvg" type="monotone" stroke="var(--color-cpuAvg)" fill="var(--color-cpuAvg)" fillOpacity={0.25} isAnimationActive={false} />
                    <Line dataKey="cpuMax" type="monotone" stroke="var(--color-cpuMax)" dot={false} strokeWidth={1} isAnimationActive={false} />
                  </AreaChart>
                </ChartContainer>
              </ChartSection>

              <ChartSection title={t('met.chart.mem')} what={t(s.dynamicMemory ? 'met.chart.memWhatDynamic' : 'met.chart.memWhatStatic')} look={t('met.chart.memLook')}>
                <ChartContainer config={memConfig(t)} legend={s.dynamicMemory ? ['memAssigned', 'memDemand', 'memMaximum'] : ['memAssigned', 'memDemand']}>
                  <LineChart data={series} margin={{ left: 4, right: 12, top: 8 }}>
                    <CartesianGrid vertical={false} />
                    <XAxis dataKey="at" tickFormatter={fmtTick} minTickGap={40} tickLine={false} axisLine={false} />
                    <YAxis tickFormatter={(v: number) => fmtMb(v)} width={64} tickLine={false} axisLine={false} />
                    <Tooltip content={<ChartTooltipContent labelFormatter={fmtLabel} valueFormatter={(v) => fmtMb(v)} />} />
                    <Line dataKey="memAssigned" type="stepAfter" stroke="var(--color-memAssigned)" dot={false} strokeWidth={2} isAnimationActive={false} />
                    <Line dataKey="memDemand" type="monotone" stroke="var(--color-memDemand)" dot={false} strokeWidth={2} isAnimationActive={false} />
                    {s.dynamicMemory && <Line dataKey="memMaximum" type="stepAfter" stroke="var(--color-memMaximum)" strokeDasharray="5 4" dot={false} isAnimationActive={false} />}
                  </LineChart>
                </ChartContainer>
              </ChartSection>

              <ChartSection title={t('met.chart.pressure')} what={t('met.chart.pressureWhat')} look={t('met.chart.pressureLook')}>
                <ChartContainer config={pressureConfig(t)} height={180}>
                  <AreaChart data={series} margin={{ left: 4, right: 12, top: 8 }}>
                    <CartesianGrid vertical={false} />
                    <XAxis dataKey="at" tickFormatter={fmtTick} minTickGap={40} tickLine={false} axisLine={false} />
                    <YAxis domain={[0, (max: number) => Math.max(120, Math.ceil(max / 10) * 10)]} unit="%" width={44} tickLine={false} axisLine={false} />
                    <ReferenceLine y={100} strokeDasharray="4 4" />
                    <Tooltip content={<ChartTooltipContent labelFormatter={fmtLabel} />} />
                    <Area dataKey="memPressure" type="monotone" stroke="var(--color-memPressure)" fill="var(--color-memPressure)" fillOpacity={0.2} isAnimationActive={false} />
                  </AreaChart>
                </ChartContainer>
              </ChartSection>

              <ChartSection title={t('met.chart.iops')} what={t('met.chart.iopsWhat')} look={t('met.chart.iopsLook')}>
                <ChartContainer config={iopsConfig(t)} height={180}>
                  <AreaChart data={series} margin={{ left: 4, right: 12, top: 8 }}>
                    <CartesianGrid vertical={false} />
                    <XAxis dataKey="at" tickFormatter={fmtTick} minTickGap={40} tickLine={false} axisLine={false} />
                    <YAxis width={56} tickLine={false} axisLine={false} />
                    <Tooltip content={<ChartTooltipContent labelFormatter={fmtLabel} valueFormatter={(v) => fmtNum(v)} />} />
                    <Area dataKey="iops" type="monotone" stroke="var(--color-iops)" fill="var(--color-iops)" fillOpacity={0.25} isAnimationActive={false} />
                  </AreaChart>
                </ChartContainer>
              </ChartSection>

              <ChartSection title={t('met.chart.disk')} what={t('met.chart.diskWhat')} look={t('met.chart.diskLook')}>
                <ChartContainer config={diskConfig(t)} legend={['readMBps', 'writeMBps']}>
                  <LineChart data={series} margin={{ left: 4, right: 12, top: 8 }}>
                    <CartesianGrid vertical={false} />
                    <XAxis dataKey="at" tickFormatter={fmtTick} minTickGap={40} tickLine={false} axisLine={false} />
                    <YAxis unit=" MB/s" width={72} tickLine={false} axisLine={false} />
                    <Tooltip content={<ChartTooltipContent labelFormatter={fmtLabel} valueFormatter={(v) => `${fmtNum(v, 2)} MB/s`} />} />
                    <Line dataKey="readMBps" type="monotone" stroke="var(--color-readMBps)" dot={false} strokeWidth={2} isAnimationActive={false} />
                    <Line dataKey="writeMBps" type="monotone" stroke="var(--color-writeMBps)" dot={false} strokeWidth={2} isAnimationActive={false} />
                  </LineChart>
                </ChartContainer>
              </ChartSection>

              <ChartSection title={t('met.chart.net')} what={t('met.chart.netWhat')} look={t('met.chart.netLook')}>
                <ChartContainer config={netConfig(t)} legend={['netInMbps', 'netOutMbps']}>
                  <LineChart data={series} margin={{ left: 4, right: 12, top: 8 }}>
                    <CartesianGrid vertical={false} />
                    <XAxis dataKey="at" tickFormatter={fmtTick} minTickGap={40} tickLine={false} axisLine={false} />
                    <YAxis unit=" Mbit/s" width={80} tickLine={false} axisLine={false} />
                    <Tooltip content={<ChartTooltipContent labelFormatter={fmtLabel} valueFormatter={(v) => `${fmtNum(v, 2)} Mbit/s`} />} />
                    <Line dataKey="netInMbps" type="monotone" stroke="var(--color-netInMbps)" dot={false} strokeWidth={2} isAnimationActive={false} />
                    <Line dataKey="netOutMbps" type="monotone" stroke="var(--color-netOutMbps)" dot={false} strokeWidth={2} isAnimationActive={false} />
                  </LineChart>
                </ChartContainer>
              </ChartSection>
            </div>
          </>
        )}

        <Section title={t('met.how')} bodyClassName="space-y-1.5 p-3 text-xs text-muted-foreground">
          <p>{t('met.how1', { min: Math.round(d.intervalSeconds / 60) })}</p>
          <p>{t('met.how2', { days: d.retentionDays, reset: d.resetDays })}</p>
          {d.enabled && <p>{t('met.how3', { since: fmtDate(d.lastResetAt ?? d.enabledAt), next: fmtDate(d.nextResetAt) })}</p>}
          <p>{t('met.how4')}</p>
        </Section>
      </div>

      {confirmOff && (
        <ActionDialog title={t('met.turnOffTitle', { name: vm.name })} confirmLabel={t('met.turnOff')} onClose={() => setConfirmOff(false)}
          onConfirm={() => requestMetering(false)}>
          <p className="text-sm text-muted-foreground">{t('met.turnOffImpact', { days: d.retentionDays })}</p>
        </ActionDialog>
      )}
    </div>
  );
}

/** A chart with what it shows and what to look for: every chart must help to decide something. */
function ChartSection({ title, what, look, children }: { title: string; what: string; look: string; children: ReactNode }) {
  const { t } = useI18n();
  return (
    <Section title={title} bodyClassName="space-y-3 p-3">
      {children}
      <div className="space-y-1 text-xs">
        <p className="text-muted-foreground"><span className="font-medium text-foreground">{t('met.what')}</span> {what}</p>
        <p className="text-muted-foreground"><span className="font-medium text-foreground">{t('met.look')}</span> {look}</p>
      </div>
    </Section>
  );
}

type T = ReturnType<typeof useI18n>['t'];
const cpuConfig = (t: T): ChartConfig => ({
  cpuAvg: { label: t('met.s.cpuAvg'), color: 'var(--chart-1)', unit: ' %' },
  cpuMax: { label: t('met.s.cpuMax'), color: 'var(--chart-4)', unit: ' %' },
});
const memConfig = (t: T): ChartConfig => ({
  memAssigned: { label: t('met.s.memAssigned'), color: 'var(--chart-1)' },
  memDemand: { label: t('met.s.memDemand'), color: 'var(--chart-3)' },
  memMaximum: { label: t('met.s.memMaximum'), color: 'var(--muted-foreground)' },
});
const pressureConfig = (t: T): ChartConfig => ({ memPressure: { label: t('met.s.memPressure'), color: 'var(--chart-3)', unit: ' %' } });
const iopsConfig = (t: T): ChartConfig => ({ iops: { label: t('met.s.iops'), color: 'var(--chart-5)' } });
const diskConfig = (t: T): ChartConfig => ({
  readMBps: { label: t('met.s.read'), color: 'var(--chart-2)' },
  writeMBps: { label: t('met.s.write'), color: 'var(--chart-4)' },
});
const netConfig = (t: T): ChartConfig => ({
  netInMbps: { label: t('met.s.netIn'), color: 'var(--chart-1)' },
  netOutMbps: { label: t('met.s.netOut'), color: 'var(--chart-2)' },
});
