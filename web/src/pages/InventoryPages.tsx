import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { get, newIdempotencyKey, post } from '../api/client';
import { realtime } from '../api/realtime';
import type { EnrollmentInfo, Host, InventoryEvent, Job, PowerOperation, Vm } from '../api/types';
import { AgentSetupGuide } from '../components/AgentSetupGuide';
import { Dialog, ErrorAlert, formatBytes, formatDuration, JobBadge, Loading, PowerBadge, Progress, useLoad } from '../components/ui';
import { useI18n, type TKey } from '../i18n';
import { useSession } from '../session';

function useLive(reload: () => void, event: 'inventoryChanged' | 'jobChanged' = 'inventoryChanged') {
  useEffect(() => realtime.on(event, () => reload()), [reload, event]);
}

export function DashboardPage() {
  const { t, fmtDate } = useI18n();
  const { can } = useSession();
  const hosts = useLoad(() => get<Host[]>('/api/v1/hosts'));
  const vms = useLoad(() => get<Vm[]>('/api/v1/vms'));
  const jobs = useLoad(() => get<{ job: Job; requestedBy: string | null }[]>('/api/v1/jobs?take=8'));
  useLive(hosts.reload);
  useLive(vms.reload);
  useLive(jobs.reload, 'jobChanged');

  if (hosts.loading && !hosts.data) return <Loading />;
  const h = hosts.data ?? [];
  const v = vms.data ?? [];
  const active = (jobs.data ?? []).filter((j) => ['Queued', 'Running', 'Reconciling'].includes(j.job.status)).length;
  const attention = h.filter((x) => x.status === 'Active' && (!x.connected || x.access !== 'Ok' || x.stale)).length + v.filter((x) => x.isCritical || x.isMissing).length;

  return (
    <>
      <h1>{t('nav.dashboard')}</h1>
      <ErrorAlert error={hosts.error ?? vms.error} />
      <div className="grid cols-4">
        <div className="card stat"><div className="value">{h.filter((x) => x.connected).length}/{h.length}</div><div className="label">{t('dash.hosts')} {t('dash.connected')}</div></div>
        <div className="card stat"><div className="value">{v.filter((x) => x.powerState === 'Running').length}/{v.length}</div><div className="label">{t('dash.vms')} {t('dash.running')}</div></div>
        <div className="card stat"><div className="value">{active}</div><div className="label">{t('dash.jobs')}</div></div>
        <div className="card stat"><div className="value">{attention}</div><div className="label">{t('dash.attention')}</div></div>
      </div>
      {h.length === 0 && (
        <div className="card" style={{ marginTop: 16 }}>
          <p>{t('dash.noHosts')}</p>
          {can('hosts.manage') && <Link className="button primary" to="/hosts">{t('dash.addHost')}</Link>}
        </div>
      )}
      <h2>{t('dash.recentJobs')}</h2>
      <JobTable rows={jobs.data ?? []} compact fmtDate={fmtDate} />
    </>
  );
}

// ---------------------------------------------------------------- hosts

export function HostsPage() {
  const { t, fmtDate } = useI18n();
  const { can } = useSession();
  const hosts = useLoad(() => get<Host[]>('/api/v1/hosts'));
  useLive(hosts.reload);
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState('');
  const [enrollment, setEnrollment] = useState<EnrollmentInfo | null>(null);
  const [enrollHostId, setEnrollHostId] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const r = await post<{ hostId: string; enrollment: EnrollmentInfo }>('/api/v1/hosts', { displayName: name });
      setEnrollment(r.enrollment);
      setEnrollHostId(r.hostId);
      setAdding(false);
      setName('');
      hosts.reload();
    } catch (err) { setError(err); }
  };

  return (
    <>
      <div className="row"><h1>{t('nav.hosts')}</h1><span className="spacer" />
        {can('hosts.manage') && <button className="primary" type="button" onClick={() => setAdding(true)}>{t('host.add')}</button>}
      </div>
      <ErrorAlert error={hosts.error ?? error} />
      {hosts.loading && !hosts.data ? <Loading /> : (
        <div className="card table-wrap">
          <table>
            <thead><tr><th>{t('host.name')}</th><th>{t('host.status')}</th><th>{t('host.connection')}</th><th>{t('host.access')}</th><th>{t('host.platform')}</th><th>VM</th><th>{t('host.lastInventory')}</th></tr></thead>
            <tbody>
              {(hosts.data ?? []).map((h) => (
                <tr key={h.id}>
                  <td><Link to={`/hosts/${h.id}`}>{h.displayName}</Link><div className="muted small">{h.machineName}</div></td>
                  <td><span className={'badge ' + (h.status === 'Active' ? 'ok' : h.status === 'Revoked' ? 'danger' : 'warn')}>{h.status}</span></td>
                  <td><span className={'badge ' + (h.connected ? 'ok' : 'danger')}>{h.connected ? t('host.online') : t('host.offline')}</span>{h.stale && h.status === 'Active' && <span className="badge warn" title={t('stale.banner')}> stale</span>}</td>
                  <td>{h.access ? <span className={'badge ' + (h.access === 'Ok' ? 'ok' : 'danger')} title={h.accessMessage ?? ''}>{h.access}</span> : '—'}</td>
                  <td className="small">{h.platform}<div className="muted">{h.edition}</div></td>
                  <td>{h.vmCount}</td>
                  <td className="small">{fmtDate(h.lastInventoryAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <Dialog open={adding} title={t('host.add')} onClose={() => setAdding(false)}
        actions={<><button type="button" onClick={() => setAdding(false)}>{t('common.cancel')}</button>
          <button type="submit" form="add-host" className="primary" disabled={!name}>{t('host.create')}</button></>}>
        <form id="add-host" onSubmit={create}>
          <div className="field"><label htmlFor="host-name">{t('host.name')}</label>
            <input id="host-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} /></div>
        </form>
      </Dialog>
      <EnrollmentDialog info={enrollment} host={(hosts.data ?? []).find((h) => h.id === enrollHostId) ?? null} onClose={() => setEnrollment(null)} />
    </>
  );
}

function EnrollmentDialog({ info, host, onClose }: { info: EnrollmentInfo | null; host: Host | null; onClose: () => void }) {
  const { t } = useI18n();
  if (!info) return null;
  const connection = {
    gatewayNames: info.gatewayNames,
    preferredName: new URL(info.gatewayAgentUrl).hostname,
    agentPort: info.agentPort,
    gatewayAgentUrl: info.gatewayAgentUrl,
    caFingerprintSha256: info.caFingerprintSha256,
    listensOnNetwork: info.listensOnNetwork,
    bindAddress: info.bindAddress,
  };
  return (
    <Dialog open wide title={t('host.enrollTitle') + (host ? ` — ${host.displayName}` : '')} onClose={onClose}
      actions={<button type="button" className="primary" onClick={onClose}>{t('common.close')}</button>}>
      <AgentSetupGuide connection={connection} token={info.token} expiresAt={info.expiresAt} host={host} />
    </Dialog>
  );
}
export function HostDetailPage() {
  const { id } = useParams();
  const { t, fmtDate } = useI18n();
  const { can } = useSession();
  const data = useLoad(() => get<{ host: Host; events: InventoryEvent[] }>(`/api/v1/hosts/${id}`), [id]);
  useLive(data.reload);
  const [confirmRevoke, setConfirmRevoke] = useState(false);
  const [enrollment, setEnrollment] = useState<EnrollmentInfo | null>(null);
  const [error, setError] = useState<unknown>(null);

  if (data.loading && !data.data) return <Loading />;
  if (!data.data) return <ErrorAlert error={data.error} />;
  const h = data.data.host;

  const revoke = async () => {
    setError(null);
    try { await post(`/api/v1/hosts/${h.id}/revoke`); setConfirmRevoke(false); data.reload(); } catch (e) { setError(e); }
  };
  const newToken = async () => {
    setError(null);
    try { setEnrollment(await post<EnrollmentInfo>(`/api/v1/hosts/${h.id}/enrollment-token`)); } catch (e) { setError(e); }
  };
  const refresh = async () => {
    setError(null);
    try { await post(`/api/v1/hosts/${h.id}/refresh`); data.reload(); } catch (e) { setError(e); }
  };

  return (
    <>
      <div className="row"><h1>{h.displayName}</h1><span className="spacer" />
        <button type="button" onClick={() => void refresh()} disabled={!h.connected}>{t('common.refresh')}</button>
        {can('hosts.manage') && <button type="button" onClick={() => void newToken()}>{t('host.newToken')}</button>}
        {can('hosts.manage') && h.status !== 'Revoked' && <button type="button" className="danger-outline" onClick={() => setConfirmRevoke(true)}>{t('host.revoke')}</button>}
      </div>
      <ErrorAlert error={error} />
      {h.stale && h.status === 'Active' && <div className="alert warn">{t('stale.banner')}</div>}
      {h.access && h.access !== 'Ok' && <div className="alert danger">{h.access}: {h.accessMessage}</div>}
      {h.partial && <div className="alert warn">{t('partial.banner')} {h.warnings.join(' · ')}</div>}
      <div className="grid cols-2">
        <div className="card">
          <dl className="kv">
            <dt>{t('host.status')}</dt><dd>{h.status} · {h.connected ? t('host.online') : t('host.offline')}</dd>
            <dt>{t('host.platform')}</dt><dd>{h.platform} <span className="muted">({h.os?.osCaption})</span></dd>
            <dt>{t('host.edition')}</dt><dd>{h.edition}</dd>
            <dt>{t('host.support')}</dt><dd>{h.support}</dd>
            <dt>{t('host.domain')}</dt><dd>{h.os?.domain ?? '—'} {h.os && (h.os.partOfDomain ? '(AD)' : '(workgroup)')}</dd>
            <dt>{t('host.cpuMem')}</dt><dd>{h.hardware?.logicalProcessorCount ?? '—'} LP · {formatBytes(h.hardware?.totalPhysicalMemoryBytes)}</dd>
            <dt>Agent</dt><dd>{h.agentVersion ?? '—'}</dd>
            <dt>{t('host.lastInventory')}</dt><dd>{fmtDate(h.lastInventoryAt)}</dd>
          </dl>
        </div>
        <div className="card">
          <h3 style={{ marginTop: 0 }}>{t('host.capabilities')}</h3>
          <table><tbody>{h.capabilities.map((c) => <tr key={c.name}><td className="mono">{c.name}</td><td>{c.status}{c.note && <div className="muted small">{c.note}</div>}</td></tr>)}</tbody></table>
        </div>
      </div>
      <h2>{t('host.events')}</h2>
      <EventsTable events={data.data.events} />
      <Dialog open={confirmRevoke} title={t('host.revokeTitle', { name: h.displayName })} onClose={() => setConfirmRevoke(false)}
        actions={<><button type="button" onClick={() => setConfirmRevoke(false)}>{t('common.cancel')}</button>
          <button type="button" className="danger" onClick={() => void revoke()}>{t('host.revoke')}</button></>}>
        <p>{t('host.revokeImpact')}</p>
      </Dialog>
      <EnrollmentDialog info={enrollment} host={h} onClose={() => { setEnrollment(null); data.reload(); }} />
    </>
  );
}

function EventsTable({ events }: { events: InventoryEvent[] }) {
  const { fmtDate } = useI18n();
  if (events.length === 0) return <p className="muted">—</p>;
  return (
    <div className="card table-wrap">
      <table><tbody>
        {events.map((e) => (
          <tr key={e.id}>
            <td className="small" style={{ whiteSpace: 'nowrap' }}>{fmtDate(e.at)}</td>
            <td><span className={'badge ' + (e.kind.includes('externally') ? 'warn' : e.kind.includes('missing') ? 'danger' : 'info')}>{e.kind}</span></td>
            <td>{e.details}</td>
          </tr>
        ))}
      </tbody></table>
    </div>
  );
}

// ---------------------------------------------------------------- VMs

export function VmListPage() {
  const { t } = useI18n();
  const [search, setSearch] = useState('');
  const [state, setState] = useState('');
  const vms = useLoad(() => get<Vm[]>('/api/v1/vms'));
  useLive(vms.reload);
  const rows = useMemo(() => (vms.data ?? []).filter((v) =>
    (!search || v.name.toLowerCase().includes(search.toLowerCase()) || (v.hostName ?? '').toLowerCase().includes(search.toLowerCase()))
    && (!state || v.powerState === state)), [vms.data, search, state]);

  return (
    <>
      <h1>{t('nav.vms')}</h1>
      <ErrorAlert error={vms.error} />
      <div className="row" style={{ marginBottom: 12 }}>
        <input aria-label={t('common.search')} placeholder={t('common.search')} value={search} onChange={(e) => setSearch(e.target.value)} style={{ maxWidth: 280 }} />
        <select aria-label={t('vm.state')} value={state} onChange={(e) => setState(e.target.value)} style={{ maxWidth: 200 }}>
          <option value="">{t('vm.filterState')}</option>
          {['Running', 'Off', 'Saved', 'Paused'].map((s) => <option key={s} value={s}>{t(('state.' + s) as TKey)}</option>)}
        </select>
      </div>
      {vms.loading && !vms.data ? <Loading /> : rows.length === 0 ? <p className="muted">{t('vm.empty')}</p> : (
        <div className="card table-wrap">
          <table>
            <thead><tr><th>{t('vm.name')}</th><th>{t('vm.state')}</th><th>{t('vm.host')}</th><th>{t('vm.cpu')}</th><th>{t('vm.memory')}</th><th>{t('vm.uptime')}</th><th>Gen</th></tr></thead>
            <tbody>
              {rows.map((v) => (
                <tr key={v.id}>
                  <td><Link to={`/vms/${v.id}`}>{v.name}</Link>{v.isMissing && <span className="badge danger"> {t('vm.missing')}</span>}</td>
                  <td><PowerBadge state={v.powerState} critical={v.isCritical} />{v.stale && <span className="badge warn" title={t('stale.banner')}> stale</span>}</td>
                  <td>{v.hostName}</td>
                  <td>{v.processorCount ?? '—'}</td>
                  <td>{v.memoryAssignedMb != null ? formatBytes(v.memoryAssignedMb * 1024 * 1024) : v.memoryStartupMb != null ? formatBytes(v.memoryStartupMb * 1024 * 1024) : '—'}</td>
                  <td>{v.powerState === 'Running' ? formatDuration(v.uptimeMs) : '—'}</td>
                  <td>{v.generation ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

export function VmDetailPage() {
  const { id } = useParams();
  const { t, fmtDate } = useI18n();
  const data = useLoad(() => get<{ vm: Vm; recentJobs: Job[] }>(`/api/v1/vms/${id}`), [id]);
  const events = useLoad(() => get<InventoryEvent[]>(`/api/v1/vms/${id}/events`), [id]);
  useLive(data.reload);
  useLive(data.reload, 'jobChanged');
  useLive(events.reload);
  const [op, setOp] = useState<PowerOperation | null>(null);

  if (data.loading && !data.data) return <Loading />;
  if (!data.data) return <ErrorAlert error={data.error} />;
  const vm = data.data.vm;

  return (
    <>
      <div className="row"><h1>{vm.name}</h1><PowerBadge state={vm.powerState} critical={vm.isCritical} /></div>
      {vm.stale && <div className="alert warn">{t('stale.banner')}</div>}
      {vm.isMissing && <div className="alert danger">{t('vm.missing')} ({fmtDate(vm.missingSince)})</div>}
      <div className="card" style={{ marginBottom: 16 }}>
        <h3 style={{ marginTop: 0 }}>{t('vm.actions')}</h3>
        {vm.allowedOperations.length === 0 ? <p className="muted">{t('vm.noActions')}</p> : (
          <div className="row">
            {vm.allowedOperations.map((o) => (
              <button key={o} type="button" className={vm.destructiveOperations.includes(o) ? 'danger-outline' : ''} onClick={() => setOp(o)}>{t(('op.' + o) as TKey)}</button>
            ))}
          </div>
        )}
      </div>
      <div className="grid cols-2">
        <div className="card">
          <dl className="kv">
            <dt>{t('vm.host')}</dt><dd><Link to={`/hosts/${vm.hostId}`}>{vm.hostName}</Link></dd>
            <dt>{t('vm.generation')}</dt><dd>{vm.generation ?? '—'}</dd>
            <dt>{t('vm.version')}</dt><dd>{vm.configurationVersion ?? '—'}</dd>
            <dt>{t('vm.cpu')}</dt><dd>{vm.processorCount ?? '—'} {vm.processorLoadPercent != null && <span className="muted">({vm.processorLoadPercent}%)</span>}</dd>
            <dt>{t('vm.memory')}</dt><dd>{vm.memoryStartupMb != null ? `${vm.memoryStartupMb} MB` : '—'} {vm.memoryAssignedMb != null && <span className="muted">(assigned {vm.memoryAssignedMb} MB)</span>}</dd>
            <dt>{t('vm.dynamicMemory')}</dt><dd>{vm.dynamicMemoryEnabled == null ? '—' : vm.dynamicMemoryEnabled ? t('common.yes') : t('common.no')}</dd>
            <dt>{t('vm.uptime')}</dt><dd>{vm.powerState === 'Running' ? formatDuration(vm.uptimeMs) : '—'}</dd>
            <dt>{t('vm.guestOs')}</dt><dd>{vm.guestOperatingSystem ?? '—'}</dd>
            <dt>{t('vm.shutdownService')}</dt><dd>{vm.shutdownServiceAvailable == null ? '—' : vm.shutdownServiceAvailable ? 'OK' : t('common.no')}</dd>
            <dt>Hyper-V ID</dt><dd className="mono small">{vm.hyperVId}</dd>
          </dl>
        </div>
        <div className="card">
          <h3 style={{ marginTop: 0 }}>{t('vm.recentJobs')}</h3>
          <JobTable rows={data.data.recentJobs.map((job) => ({ job, requestedBy: null }))} compact fmtDate={fmtDate} />
        </div>
      </div>
      <h2>{t('vm.history')}</h2>
      <EventsTable events={events.data ?? []} />
      {op && <PowerDialog vm={vm} op={op} onClose={() => { setOp(null); data.reload(); }} />}
    </>
  );
}

function PowerDialog({ vm, op, onClose }: { vm: Vm; op: PowerOperation; onClose: () => void }) {
  const { t } = useI18n();
  const destructive = vm.destructiveOperations.includes(op);
  const [typed, setTyped] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState<string | null>(null);
  const [key] = useState(newIdempotencyKey);

  const submit = async () => {
    setError(null);
    try {
      const r = await post<{ jobId: string }>(`/api/v1/vms/${vm.id}/power`, { operation: op, confirmName: destructive ? typed : undefined, idempotencyKey: key });
      setDone(r.jobId);
    } catch (e) { setError(e); }
  };

  return (
    <Dialog open title={t('confirm.title', { op: t(('op.' + op) as TKey), name: vm.name })} onClose={onClose}
      actions={done ? <button type="button" className="primary" onClick={onClose}>{t('common.close')}</button> : <>
        <button type="button" onClick={onClose}>{t('common.cancel')}</button>
        <button type="button" className={destructive ? 'danger' : 'primary'} disabled={destructive && typed !== vm.name} onClick={() => void submit()}>{t(('op.' + op) as TKey)}</button>
      </>}>
      <div className={'alert ' + (destructive ? 'danger' : 'info')}>{t(('impact.' + op) as TKey)}</div>
      <dl className="kv small"><dt>VM</dt><dd>{vm.name}</dd><dt>{t('vm.host')}</dt><dd>{vm.hostName}</dd><dt>{t('vm.state')}</dt><dd>{t(('state.' + vm.powerState) as TKey)}</dd></dl>
      <ErrorAlert error={error} />
      {done ? <div className="alert ok">{t('job.queued')} <Link to={`/jobs/${done}`}>{done}</Link></div> : destructive && (
        <div className="field" style={{ marginTop: 12 }}>
          <label htmlFor="confirm-name">{t('confirm.typeName', { name: vm.name })}</label>
          <input id="confirm-name" autoComplete="off" value={typed} onChange={(e) => setTyped(e.target.value)} />
        </div>
      )}
    </Dialog>
  );
}

// ---------------------------------------------------------------- jobs

function JobTable({ rows, compact, fmtDate }: { rows: { job: Job; requestedBy: string | null }[]; compact?: boolean; fmtDate: (s: string | null) => string }) {
  const { t } = useI18n();
  if (rows.length === 0) return <p className="muted">{t('job.empty')}</p>;
  return (
    <div className="card table-wrap">
      <table>
        <thead><tr><th>{t('job.created')}</th><th>{t('job.operation')}</th><th>{t('job.target')}</th><th>{t('job.status')}</th>{!compact && <th>{t('job.by')}</th>}<th>{t('job.result')}</th></tr></thead>
        <tbody>
          {rows.map(({ job, requestedBy }) => (
            <tr key={job.id}>
              <td className="small" style={{ whiteSpace: 'nowrap' }}><Link to={`/jobs/${job.id}`}>{fmtDate(job.createdAt)}</Link></td>
              <td>{t(('op.' + job.operation) as TKey)}</td>
              <td>{job.targetName}</td>
              <td><JobBadge status={job.status} />{(job.status === 'Running' || job.status === 'Queued') && <Progress value={job.progress} />}</td>
              {!compact && <td>{requestedBy}</td>}
              <td className="small">{job.resultMessage ?? job.progressMessage}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function JobsPage() {
  const { t, fmtDate } = useI18n();
  const jobs = useLoad(() => get<{ job: Job; requestedBy: string | null }[]>('/api/v1/jobs?take=200'));
  useLive(jobs.reload, 'jobChanged');
  return (
    <>
      <h1>{t('nav.jobs')}</h1>
      <ErrorAlert error={jobs.error} />
      {jobs.loading && !jobs.data ? <Loading /> : <JobTable rows={jobs.data ?? []} fmtDate={fmtDate} />}
    </>
  );
}

export function JobDetailPage() {
  const { id } = useParams();
  const { t, fmtDate } = useI18n();
  const data = useLoad(() => get<{ job: Job; events: { id: number; at: string; kind: string; message: string | null }[] }>(`/api/v1/jobs/${id}`), [id]);
  useLive(data.reload, 'jobChanged');
  const [error, setError] = useState<unknown>(null);
  if (data.loading && !data.data) return <Loading />;
  if (!data.data) return <ErrorAlert error={data.error} />;
  const j = data.data.job;
  const cancel = async () => {
    setError(null);
    try { await post(`/api/v1/jobs/${j.id}/cancel`); data.reload(); } catch (e) { setError(e); }
  };
  return (
    <>
      <div className="row"><h1>{t(('op.' + j.operation) as TKey)} — {j.targetName}</h1><JobBadge status={j.status} /><span className="spacer" />
        {j.cancellable && <button type="button" onClick={() => void cancel()}>{t('job.cancel')}</button>}</div>
      <ErrorAlert error={error} />
      <div className="card">
        <dl className="kv">
          <dt>{t('job.status')}</dt><dd>{j.status} {j.errorCode && <span className="mono">({j.errorCode}{j.hyperVCode != null ? ` / Hyper-V ${j.hyperVCode}` : ''})</span>}</dd>
          <dt>{t('job.result')}</dt><dd>{j.resultMessage ?? j.progressMessage ?? '—'}</dd>
          <dt>{t('job.created')}</dt><dd>{fmtDate(j.createdAt)}</dd>
          <dt>{t('job.completed')}</dt><dd>{fmtDate(j.completedAt)}</dd>
          <dt>Correlation</dt><dd className="mono small">{j.correlationId}</dd>
        </dl>
        {(j.status === 'Running' || j.status === 'Queued') && <Progress value={j.progress} />}
      </div>
      <h2>{t('job.events')}</h2>
      <div className="card table-wrap"><table><tbody>
        {data.data.events.map((e) => <tr key={e.id}><td className="small" style={{ whiteSpace: 'nowrap' }}>{fmtDate(e.at)}</td><td><span className="badge info">{e.kind}</span></td><td>{e.message}</td></tr>)}
      </tbody></table></div>
    </>
  );
}
