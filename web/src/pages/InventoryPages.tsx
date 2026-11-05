import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { get, newIdempotencyKey, post, api } from '../api/client';
import { realtime } from '../api/realtime';
import type { EnrollmentInfo, Host, InventoryEvent, Job, PowerOperation, Vm, VmDetails } from '../api/types';
import { AgentSetupGuide } from '../components/AgentSetupGuide';
import { ConfirmDeleteDialog, EditDialog } from '../components/dialogs';
import { Dialog, ErrorAlert, formatBytes, formatDuration, JobBadge, Loading, PowerBadge, Progress, useLoad } from '../components/ui';
import { CheckpointIcon, KebabButton, rowProps, StateIcon, useContextMenu, type MenuItem } from '../components/widgets';
import { useI18n, type TKey } from '../i18n';
import { useSession } from '../session';

function useLive(reload: () => void, event: 'inventoryChanged' | 'jobChanged' = 'inventoryChanged') {
  useEffect(() => realtime.on(event, () => reload()), [reload, event]);
}

const patch = (path: string, body: unknown) => api('PATCH', path, body);

function Meter({ used, total }: { used: number | null | undefined; total: number | null | undefined }) {
  if (used == null || !total) return null;
  const pct = Math.min(100, Math.round((used / total) * 100));
  return <div className={'meter' + (pct >= 90 ? ' danger' : pct >= 75 ? ' warn' : '')} role="meter" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}><span style={{ width: `${pct}%` }} /></div>;
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

/** Actions shared by the hosts list (row menu) and the host page. */
function useHostActions(reload: () => void) {
  const { t } = useI18n();
  const { can } = useSession();
  const navigate = useNavigate();
  const [editing, setEditing] = useState<Host | null>(null);
  const [deleting, setDeleting] = useState<Host | null>(null);
  const [revoking, setRevoking] = useState<Host | null>(null);
  const [enrollment, setEnrollment] = useState<{ info: EnrollmentInfo; hostId: string } | null>(null);
  const [error, setError] = useState<unknown>(null);
  const manage = can('hosts.manage');

  const items = (h: Host, opts: { open?: boolean } = {}): MenuItem[] => [
    ...(opts.open === false ? [] : [{ label: t('menu.open'), onSelect: () => navigate(`/hosts/${h.id}`) }]),
    ...(manage ? [
      { label: t('menu.editDescription'), onSelect: () => setEditing(h) },
      { label: t('host.newToken'), onSelect: async () => {
          setError(null);
          try { setEnrollment({ info: await post<EnrollmentInfo>(`/api/v1/hosts/${h.id}/enrollment-token`), hostId: h.id }); } catch (e) { setError(e); }
        } },
      { label: t('common.refresh'), disabled: !h.connected, onSelect: async () => {
          setError(null);
          try { await post(`/api/v1/hosts/${h.id}/refresh`); reload(); } catch (e) { setError(e); }
        } },
      { label: t('host.revoke'), danger: true, separatorBefore: true, disabled: h.status === 'Revoked', onSelect: () => setRevoking(h) },
      { label: t('host.delete'), danger: true, onSelect: () => setDeleting(h) },
    ] : []),
  ];

  const dialogs = (hosts: Host[]) => (
    <>
      <ErrorAlert error={error} />
      {editing && (
        <EditDialog title={t('host.editTitle', { name: editing.displayName })} withName initialName={editing.displayName} initialDescription={editing.description}
          onSave={async (name, description) => { await patch(`/api/v1/hosts/${editing.id}`, { displayName: name, description }); reload(); }}
          onClose={() => setEditing(null)} />
      )}
      {deleting && (
        <ConfirmDeleteDialog title={t('host.deleteTitle', { name: deleting.displayName })} name={deleting.displayName}
          impact={t('host.deleteImpact', { n: deleting.vmCount })} confirmLabel={t('host.delete')}
          onConfirm={async (typed) => { await post(`/api/v1/hosts/${deleting.id}/delete`, { confirmName: typed }); reload(); navigate('/hosts'); }}
          onClose={() => setDeleting(null)} />
      )}
      {revoking && (
        <Dialog open title={t('host.revokeTitle', { name: revoking.displayName })} onClose={() => setRevoking(null)}
          actions={<><button type="button" onClick={() => setRevoking(null)}>{t('common.cancel')}</button>
            <button type="button" className="danger" onClick={async () => {
              setError(null);
              try { await post(`/api/v1/hosts/${revoking.id}/revoke`); setRevoking(null); reload(); } catch (e) { setError(e); setRevoking(null); }
            }}>{t('host.revoke')}</button></>}>
          <p>{t('host.revokeImpact')}</p>
        </Dialog>
      )}
      <EnrollmentDialog info={enrollment?.info ?? null} host={hosts.find((x) => x.id === enrollment?.hostId) ?? null}
        onClose={() => { setEnrollment(null); reload(); }} />
    </>
  );
  return { items, dialogs, setEnrollment, manage };
}

export function HostsPage() {
  const { t, fmtDate } = useI18n();
  const navigate = useNavigate();
  const hosts = useLoad(() => get<Host[]>('/api/v1/hosts'));
  useLive(hosts.reload);
  const menu = useContextMenu();
  const actions = useHostActions(hosts.reload);
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<unknown>(null);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const r = await post<{ hostId: string; enrollment: EnrollmentInfo }>('/api/v1/hosts', { displayName: name });
      if (description.trim()) await patch(`/api/v1/hosts/${r.hostId}`, { description });
      actions.setEnrollment({ info: r.enrollment, hostId: r.hostId });
      setAdding(false);
      setName('');
      setDescription('');
      hosts.reload();
    } catch (err) { setError(err); }
  };

  return (
    <div className="page-fill">
      <div className="row"><h1>{t('nav.hosts')}</h1><span className="spacer" />
        {actions.manage && <button className="primary" type="button" onClick={() => setAdding(true)}>{t('host.add')}</button>}
      </div>
      <ErrorAlert error={hosts.error ?? error} />
      {hosts.loading && !hosts.data ? <Loading /> : (
        <div className="table-card fill">
          <table>
            <thead><tr><th>{t('host.name')}</th><th>{t('host.status')}</th><th>{t('host.connection')}</th><th>{t('host.access')}</th><th>{t('host.platform')}</th><th>VM</th><th>{t('host.lastInventory')}</th><th className="actions-col"><span className="sr-only">{t('vm.actions')}</span></th></tr></thead>
            <tbody>
              {(hosts.data ?? []).map((h) => {
                const items = actions.items(h);
                return (
                  <tr key={h.id} {...rowProps(() => navigate(`/hosts/${h.id}`), (e) => menu.onContextMenu(e, items))}>
                    <td><div className="cell-name">{h.displayName}</div><div className="cell-sub">{h.description ?? h.machineName}</div></td>
                    <td><span className={'badge ' + (h.status === 'Active' ? 'ok' : h.status === 'Revoked' ? 'danger' : 'warn')}>{h.status}</span></td>
                    <td><span className={'badge ' + (h.connected ? 'ok' : 'danger')}>{h.connected ? t('host.online') : t('host.offline')}</span>{h.stale && h.status === 'Active' && <span className="badge warn" title={t('stale.banner')}> stale</span>}</td>
                    <td>{h.access ? <span className={'badge ' + (h.access === 'Ok' ? 'ok' : 'danger')} title={h.accessMessage ?? ''}>{h.access}</span> : '—'}</td>
                    <td className="small">{h.platform}<div className="muted">{h.edition}</div></td>
                    <td>{h.vmCount}</td>
                    <td className="small">{fmtDate(h.lastInventoryAt)}</td>
                    <td className="actions-col">{items.length > 0 && <KebabButton label={t('menu.more')} onClick={(e) => menu.onButton(e, items)} />}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {menu.element}
      {actions.dialogs(hosts.data ?? [])}
      <Dialog open={adding} title={t('host.add')} onClose={() => setAdding(false)}
        actions={<><button type="button" onClick={() => setAdding(false)}>{t('common.cancel')}</button>
          <button type="submit" form="add-host" className="primary" disabled={!name}>{t('host.create')}</button></>}>
        <form id="add-host" onSubmit={create}>
          <div className="field"><label htmlFor="host-name">{t('host.name')}</label>
            <input id="host-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} /></div>
          <div className="field"><label htmlFor="host-desc">{t('common.description')}</label>
            <textarea id="host-desc" rows={3} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} /></div>
        </form>
      </Dialog>
    </div>
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
  const data = useLoad(() => get<{ host: Host; events: InventoryEvent[] }>(`/api/v1/hosts/${id}`), [id]);
  const vms = useLoad(() => get<Vm[]>(`/api/v1/vms?hostId=${id}`), [id]);
  useLive(data.reload);
  useLive(vms.reload);
  const actions = useHostActions(data.reload);
  const menu = useContextMenu();
  const vmActions = useVmActions(vms.reload);

  if (data.loading && !data.data) return <Loading />;
  if (!data.data) return <ErrorAlert error={data.error} />;
  const h = data.data.host;
  const hw = h.hardware;
  const usedMem = hw?.totalPhysicalMemoryBytes != null && hw.freePhysicalMemoryBytes != null ? hw.totalPhysicalMemoryBytes - hw.freePhysicalMemoryBytes : null;
  const hostItems = actions.items(h, { open: false });

  return (
    <>
      <div className="row"><h1>{h.displayName}</h1>
        <span className={'badge ' + (h.connected ? 'ok' : 'danger')}>{h.connected ? t('host.online') : t('host.offline')}</span>
        <span className="spacer" />
        {hostItems.length > 0 && <button type="button" onClick={(e) => menu.onButton(e, hostItems)} aria-haspopup="menu">{t('vm.actions')} ▾</button>}
      </div>
      {h.description && <p className="description muted">{h.description}</p>}
      {h.stale && h.status === 'Active' && <div className="alert warn">{t('stale.banner')}</div>}
      {h.access && h.access !== 'Ok' && <div className="alert danger">{h.access}: {h.accessMessage}</div>}
      {h.partial && <div className="alert warn">{t('partial.banner')} {h.warnings.join(' · ')}</div>}

      <div className="resource-grid">
        <div className="card resource">
          <div className="label">CPU</div>
          <div className="value">{hw?.cpuLoadPercent != null ? `${hw.cpuLoadPercent}%` : '—'}</div>
          <Meter used={hw?.cpuLoadPercent} total={100} />
          <div className="small muted" style={{ marginTop: 6 }}>{hw?.cpuName ?? '—'}</div>
          <div className="small muted">{t('host.cpuTopology', { s: hw?.cpuSockets ?? '?', c: hw?.cpuCores ?? '?', lp: hw?.logicalProcessorCount ?? '?' })}</div>
        </div>
        <div className="card resource">
          <div className="label">{t('vm.memory')}</div>
          <div className="value">{formatBytes(usedMem)} / {formatBytes(hw?.totalPhysicalMemoryBytes)}</div>
          <Meter used={usedMem} total={hw?.totalPhysicalMemoryBytes} />
          <div className="small muted" style={{ marginTop: 6 }}>{t('host.free', { v: formatBytes(hw?.freePhysicalMemoryBytes) })}</div>
        </div>
        <div className="card resource">
          <div className="label">{t('host.uptime')}</div>
          <div className="value">{h.lastBootUtc ? formatDuration(Date.now() - new Date(h.lastBootUtc).getTime()) : '—'}</div>
          <div className="small muted">{t('host.lastBoot')}: {fmtDate(h.lastBootUtc)}</div>
          <div className="small muted">{h.platform} · {h.edition}</div>
        </div>
        <div className="card resource">
          <div className="label">{t('host.vms')}</div>
          <div className="value">{(vms.data ?? []).filter((v) => v.powerState === 'Running').length} / {h.vmCount}</div>
          <div className="small muted">{t('dash.running')}</div>
          <div className="small muted">{t('host.switches')}: {h.virtualSwitches.join(', ') || '—'}</div>
        </div>
      </div>

      <div className="grid cols-2">
        <div>
          <h2>{t('host.volumes')}</h2>
          <div className="table-card capped"><table>
            <thead><tr><th>{t('host.volume')}</th><th>{t('host.fileSystem')}</th><th>{t('host.size')}</th><th>{t('host.freeSpace')}</th><th style={{ width: 120 }}>{t('host.usage')}</th></tr></thead>
            <tbody>{h.volumes.length === 0 ? <tr><td colSpan={5} className="muted">—</td></tr> : h.volumes.map((v) => (
              <tr key={v.name}><td><strong>{v.name}</strong> <span className="muted">{v.label}</span></td><td>{v.fileSystem}</td><td>{formatBytes(v.sizeBytes)}</td><td>{formatBytes(v.freeBytes)}</td>
                <td><Meter used={v.sizeBytes != null && v.freeBytes != null ? v.sizeBytes - v.freeBytes : null} total={v.sizeBytes} /></td></tr>
            ))}</tbody>
          </table></div>
        </div>
        <div>
          <h2>{t('host.network')}</h2>
          <div className="table-card capped"><table>
            <thead><tr><th>{t('host.adapter')}</th><th>{t('vm.ip')}</th><th>{t('vm.gateway')}</th><th>MAC</th></tr></thead>
            <tbody>{h.networkAdapters.length === 0 ? <tr><td colSpan={4} className="muted">—</td></tr> : h.networkAdapters.map((n, i) => (
              <tr key={i}><td>{n.name}<div className="cell-sub">{n.dhcpEnabled ? 'DHCP' : t('vm.static')}</div></td>
                <td className="ip-list">{n.ipAddresses.map((ip) => <div key={ip}>{ip}</div>)}</td>
                <td className="ip-list">{n.gateways.join(', ')}</td><td className="mono small">{n.macAddress}</td></tr>
            ))}</tbody>
          </table></div>
        </div>
      </div>

      <h2>{t('nav.vms')}</h2>
      <VmTable vms={vms.data ?? []} showHost={false} actions={vmActions} capped />
      <h2>{t('host.events')}</h2>
      <EventsTable events={data.data.events} />
      {menu.element}
      {actions.dialogs([h])}
      {vmActions.dialogs}
    </>
  );
}

function EventsTable({ events }: { events: InventoryEvent[] }) {
  const { fmtDate } = useI18n();
  if (events.length === 0) return <p className="muted">—</p>;
  return (
    <div className="table-card capped">
      <table><tbody>
        {events.map((e) => (
          <tr key={e.id}>
            <td className="small" style={{ whiteSpace: 'nowrap' }}>{fmtDate(e.at)}</td>
            <td><span className={'badge ' + (e.kind.includes('externally') ? 'warn' : e.kind.includes('missing') || e.kind.includes('deleted') ? 'danger' : 'info')}>{e.kind}</span></td>
            <td>{e.details}</td>
          </tr>
        ))}
      </tbody></table>
    </div>
  );
}

// ---------------------------------------------------------------- VMs

function useVmActions(reload: () => void) {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [editing, setEditing] = useState<Vm | null>(null);
  const [deleting, setDeleting] = useState<Vm | null>(null);
  const [power, setPower] = useState<{ vm: Vm; op: PowerOperation } | null>(null);
  const [error, setError] = useState<unknown>(null);

  const items = (v: Vm, opts: { open?: boolean } = {}): MenuItem[] => [
    ...(opts.open === false ? [] : [{ label: t('menu.open'), onSelect: () => navigate(`/vms/${v.id}`) }]),
    ...(v.canAnnotate ? [{ label: t('menu.editDescription'), onSelect: () => setEditing(v) }] : []),
    ...v.allowedOperations.map((op, i) => ({
      label: t(('op.' + op) as TKey), danger: v.destructiveOperations.includes(op), separatorBefore: i === 0, onSelect: () => setPower({ vm: v, op }),
    })),
    ...(v.canDelete ? [{ label: t('vm.delete'), danger: true, separatorBefore: true, onSelect: () => setDeleting(v) }] : []),
    ...(v.canForget ? [{ label: t('vm.forget'), danger: true, separatorBefore: true, onSelect: async () => {
        setError(null);
        try { await post(`/api/v1/vms/${v.id}/forget`); reload(); navigate('/vms'); } catch (e) { setError(e); }
      } }] : []),
  ];

  const dialogs = (
    <>
      <ErrorAlert error={error} />
      {editing && (
        <EditDialog title={t('vm.editTitle', { name: editing.name })} initialDescription={editing.description}
          onSave={async (_, description) => { await patch(`/api/v1/vms/${editing.id}`, { description }); reload(); }}
          onClose={() => setEditing(null)} />
      )}
      {deleting && (
        <ConfirmDeleteDialog title={t('vm.deleteTitle', { name: deleting.name })} name={deleting.name}
          impact={<>{t('vm.deleteImpact')}<br /><strong>{t('vm.deleteDisksKept')}</strong></>} confirmLabel={t('vm.delete')}
          onConfirm={async (typed) => {
            const r = await post<{ jobId: string }>(`/api/v1/vms/${deleting.id}/delete`, { confirmName: typed, idempotencyKey: newIdempotencyKey() });
            navigate(`/jobs/${r.jobId}`);
          }}
          onClose={() => setDeleting(null)} />
      )}
      {power && <PowerDialog vm={power.vm} op={power.op} onClose={() => { setPower(null); reload(); }} />}
    </>
  );
  return { items, dialogs };
}

function VmTable({ vms, showHost = true, actions, capped }: { vms: Vm[]; showHost?: boolean; actions: ReturnType<typeof useVmActions>; capped?: boolean }) {
  const { t } = useI18n();
  const navigate = useNavigate();
  const menu = useContextMenu();
  if (vms.length === 0) return <p className="muted">{t('vm.empty')}</p>;
  return (
    <div className={'table-card' + (capped ? ' capped' : ' fill')}>
      <table>
        <thead><tr>
          <th className="icon-col"><span className="sr-only">{t('vm.state')}</span></th>
          <th className="icon-col" title={t('vm.checkpoints')}><span aria-hidden="true">⏱</span><span className="sr-only">{t('vm.checkpoints')}</span></th>
          <th>{t('vm.name')}</th><th>{t('vm.state')}</th>{showHost && <th>{t('vm.host')}</th>}<th>{t('vm.cpu')}</th><th>{t('vm.memory')}</th><th>{t('vm.uptime')}</th><th>Gen</th>
          <th className="actions-col"><span className="sr-only">{t('vm.actions')}</span></th>
        </tr></thead>
        <tbody>
          {vms.map((v) => {
            const items = actions.items(v);
            return (
              <tr key={v.id} {...rowProps(() => navigate(`/vms/${v.id}`), (e) => menu.onContextMenu(e, items))}>
                <td className="icon-col"><StateIcon state={v.powerState} critical={v.isCritical} /></td>
                <td className="icon-col"><CheckpointIcon count={v.checkpointCount} /></td>
                <td><div className="cell-name">{v.name}{v.isMissing && <span className="badge danger"> {t('vm.missing')}</span>}</div>{v.description && <div className="cell-sub">{v.description}</div>}</td>
                <td><PowerBadge state={v.powerState} critical={v.isCritical} />{v.stale && <span className="badge warn" title={t('stale.banner')}> stale</span>}</td>
                {showHost && <td>{v.hostName}</td>}
                <td>{v.processorCount ?? '—'}</td>
                <td>{v.memoryAssignedMb != null && v.powerState === 'Running' ? formatBytes(v.memoryAssignedMb * 1024 * 1024) : v.memoryStartupMb != null ? formatBytes(v.memoryStartupMb * 1024 * 1024) : '—'}</td>
                <td>{v.powerState === 'Running' ? formatDuration(v.uptimeMs) : '—'}</td>
                <td>{v.generation ?? '—'}</td>
                <td className="actions-col"><KebabButton label={t('menu.more')} onClick={(e) => menu.onButton(e, items)} /></td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {menu.element}
    </div>
  );
}

export function VmListPage() {
  const { t } = useI18n();
  const [search, setSearch] = useState('');
  const [state, setState] = useState('');
  const vms = useLoad(() => get<Vm[]>('/api/v1/vms'));
  useLive(vms.reload);
  useLive(vms.reload, 'jobChanged');
  const actions = useVmActions(vms.reload);
  const rows = useMemo(() => (vms.data ?? []).filter((v) =>
    (!search || [v.name, v.hostName ?? '', v.description ?? ''].some((s) => s.toLowerCase().includes(search.toLowerCase())))
    && (!state || v.powerState === state)), [vms.data, search, state]);

  return (
    <div className="page-fill">
      <h1>{t('nav.vms')}</h1>
      <ErrorAlert error={vms.error} />
      <div className="row" style={{ marginBottom: 12 }}>
        <input aria-label={t('common.search')} placeholder={t('common.search')} value={search} onChange={(e) => setSearch(e.target.value)} style={{ maxWidth: 280 }} />
        <select aria-label={t('vm.state')} value={state} onChange={(e) => setState(e.target.value)} style={{ maxWidth: 200 }}>
          <option value="">{t('vm.filterState')}</option>
          {['Running', 'Off', 'Saved', 'Paused'].map((s) => <option key={s} value={s}>{t(('state.' + s) as TKey)}</option>)}
        </select>
        <span className="spacer" />
        <span className="muted small">{rows.length} / {(vms.data ?? []).length}</span>
      </div>
      {vms.loading && !vms.data ? <Loading /> : <VmTable vms={rows} actions={actions} />}
      {actions.dialogs}
    </div>
  );
}

export function VmDetailPage() {
  const { id } = useParams();
  const { t, fmtDate } = useI18n();
  const data = useLoad(() => get<{ vm: Vm; recentJobs: Job[] }>(`/api/v1/vms/${id}`), [id]);
  const details = useLoad(() => get<{ details: VmDetails | null; stale: boolean; collectedAt: string | null; error?: string }>(`/api/v1/vms/${id}/details`), [id]);
  const events = useLoad(() => get<InventoryEvent[]>(`/api/v1/vms/${id}/events`), [id]);
  useLive(data.reload);
  useLive(data.reload, 'jobChanged');
  useLive(events.reload);
  const actions = useVmActions(data.reload);
  const menu = useContextMenu();

  if (data.loading && !data.data) return <Loading />;
  if (!data.data) return <ErrorAlert error={data.error} />;
  const vm = data.data.vm;
  const d = details.data?.details;
  const items = actions.items(vm, { open: false });

  return (
    <>
      <div className="row"><StateIcon state={vm.powerState} critical={vm.isCritical} /><h1 style={{ margin: 0 }}>{vm.name}</h1><PowerBadge state={vm.powerState} critical={vm.isCritical} />
        <span className="spacer" />
        {items.length > 0 && <button type="button" aria-haspopup="menu" onClick={(e) => menu.onButton(e, items)}>{t('vm.actions')} ▾</button>}
      </div>
      {vm.description && <p className="description muted">{vm.description}</p>}
      {vm.stale && <div className="alert warn">{t('stale.banner')}</div>}
      {vm.isMissing && <div className="alert danger">{t('vm.missing')} ({fmtDate(vm.missingSince)})</div>}

      <div className="resource-grid" style={{ marginTop: 16 }}>
        <div className="card resource">
          <div className="label">CPU</div>
          <div className="value">{vm.processorCount ?? '—'} vCPU</div>
          <Meter used={vm.processorLoadPercent} total={100} />
          <div className="small muted" style={{ marginTop: 6 }}>{vm.processorLoadPercent != null ? t('vm.load', { p: vm.processorLoadPercent }) : '—'}</div>
        </div>
        <div className="card resource">
          <div className="label">{t('vm.memory')}</div>
          <div className="value">{vm.memoryAssignedMb != null && vm.powerState === 'Running' ? formatBytes(vm.memoryAssignedMb * 1048576) : formatBytes(vm.memoryStartupMb != null ? vm.memoryStartupMb * 1048576 : null)}</div>
          <div className="small muted">{t('vm.startup')}: {vm.memoryStartupMb != null ? formatBytes(vm.memoryStartupMb * 1048576) : '—'} · {t('vm.dynamicMemory')}: {vm.dynamicMemoryEnabled == null ? '—' : vm.dynamicMemoryEnabled ? t('common.yes') : t('common.no')}</div>
        </div>
        <div className="card resource">
          <div className="label">{t('vm.uptime')}</div>
          <div className="value">{vm.powerState === 'Running' ? formatDuration(vm.uptimeMs) : '—'}</div>
          <div className="small muted">{t('vm.guestOs')}: {vm.guestOperatingSystem ?? '—'}</div>
          <div className="small muted">{t('vm.shutdownService')}: {vm.shutdownServiceAvailable == null ? '—' : vm.shutdownServiceAvailable ? 'OK' : t('common.no')}</div>
        </div>
        <div className="card resource">
          <div className="label">{t('vm.configuration')}</div>
          <div className="value">Gen {vm.generation ?? '—'}</div>
          <div className="small muted">{t('vm.version')}: {vm.configurationVersion ?? '—'}</div>
          <div className="small muted">{t('vm.checkpoints')}: <CheckpointIcon count={vm.checkpointCount} /></div>
          <div className="small muted">{t('vm.host')}: <Link to={`/hosts/${vm.hostId}`}>{vm.hostName}</Link></div>
        </div>
      </div>

      <div className="row"><h2>{t('vm.disks')}</h2><span className="spacer" />
        {details.data?.stale && <span className="badge warn" title={details.data.error ?? ''}>{t('vm.detailsStale', { at: fmtDate(details.data.collectedAt) })}</span>}
        <button type="button" onClick={details.reload} disabled={details.loading}>{t('common.refresh')}</button>
      </div>
      {details.loading && !details.data ? <Loading /> : (
        <div className="table-card capped"><table>
          <thead><tr><th>{t('vm.diskType')}</th><th>{t('vm.path')}</th><th>{t('vm.controller')}</th><th>{t('vm.format')}</th><th>{t('vm.capacity')}</th><th>{t('vm.fileSize')}</th></tr></thead>
          <tbody>{!d || d.disks.length === 0 ? <tr><td colSpan={6} className="muted">{details.data?.error ?? '—'}</td></tr> : d.disks.map((k, i) => (
            <tr key={i}>
              <td>{k.kind === 'VirtualHardDisk' ? t('vm.vhd') : k.kind === 'DvdIso' ? 'DVD / ISO' : k.kind}</td>
              <td className="mono small" style={{ wordBreak: 'break-all' }}>{k.path ?? '—'}{k.parentPath && <div className="cell-sub">↳ {k.parentPath}</div>}</td>
              <td className="small">{k.controller ?? '—'}{k.location != null && ` : ${k.location}`}</td>
              <td className="small">{[k.format, k.diskType].filter(Boolean).join(' · ') || '—'}</td>
              <td>{formatBytes(k.maxSizeBytes)}</td>
              <td>{formatBytes(k.fileSizeBytes)}<Meter used={k.fileSizeBytes} total={k.maxSizeBytes} /></td>
            </tr>
          ))}</tbody>
        </table></div>
      )}

      <h2>{t('vm.networkAdapters')}</h2>
      <div className="table-card capped"><table>
        <thead><tr><th>{t('host.adapter')}</th><th>{t('vm.switch')}</th><th>{t('vm.ip')}</th><th>{t('vm.gateway')}</th><th>DNS</th><th>MAC</th></tr></thead>
        <tbody>{!d || d.networkAdapters.length === 0 ? <tr><td colSpan={6} className="muted">—</td></tr> : d.networkAdapters.map((n, i) => (
          <tr key={i}>
            <td>{n.name}<div className="cell-sub">{n.dhcpEnabled == null ? '' : n.dhcpEnabled ? 'DHCP' : t('vm.static')}</div></td>
            <td>{n.switchName ?? <span className="muted">{t('vm.notConnected')}</span>}</td>
            <td className="ip-list">{n.ipAddresses.length === 0 ? <span className="muted">{t('vm.noIp')}</span> : n.ipAddresses.map((ip) => <div key={ip}>{ip}</div>)}</td>
            <td className="ip-list">{n.gateways.join(', ')}</td>
            <td className="ip-list">{n.dnsServers.join(', ')}</td>
            <td className="mono small">{n.macAddress}</td>
          </tr>
        ))}</tbody>
      </table></div>
      {d && d.warnings.length > 0 && <div className="alert warn small" style={{ marginTop: 8 }}>{d.warnings.join(' · ')}</div>}

      <div className="grid cols-2" style={{ marginTop: 8 }}>
        <div>
          <h2>{t('vm.recentJobs')}</h2>
          <JobTable rows={data.data.recentJobs.map((job) => ({ job, requestedBy: null }))} compact fmtDate={fmtDate} capped />
        </div>
        <div>
          <h2>{t('vm.history')}</h2>
          <EventsTable events={events.data ?? []} />
        </div>
      </div>
      {menu.element}
      {actions.dialogs}
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

function JobTable({ rows, compact, fmtDate, capped, fill }: { rows: { job: Job; requestedBy: string | null }[]; compact?: boolean; fmtDate: (s: string | null) => string; capped?: boolean; fill?: boolean }) {
  const { t } = useI18n();
  const navigate = useNavigate();
  if (rows.length === 0) return <p className="muted">{t('job.empty')}</p>;
  return (
    <div className={'table-card' + (fill ? ' fill' : capped || compact ? ' capped' : '')}>
      <table>
        <thead><tr><th>{t('job.created')}</th><th>{t('job.operation')}</th><th>{t('job.target')}</th><th>{t('job.status')}</th>{!compact && <th>{t('job.by')}</th>}<th>{t('job.result')}</th></tr></thead>
        <tbody>
          {rows.map(({ job, requestedBy }) => (
            <tr key={job.id} {...rowProps(() => navigate(`/jobs/${job.id}`), () => {})}>
              <td className="small" style={{ whiteSpace: 'nowrap' }}>{fmtDate(job.createdAt)}</td>
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
  const jobs = useLoad(() => get<{ job: Job; requestedBy: string | null }[]>('/api/v1/jobs?take=500'));
  useLive(jobs.reload, 'jobChanged');
  return (
    <div className="page-fill">
      <h1>{t('nav.jobs')}</h1>
      <ErrorAlert error={jobs.error} />
      {jobs.loading && !jobs.data ? <Loading /> : <JobTable rows={jobs.data ?? []} fmtDate={fmtDate} fill />}
    </div>
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
      <div className="table-card capped"><table><tbody>
        {data.data.events.map((e) => <tr key={e.id}><td className="small" style={{ whiteSpace: 'nowrap' }}>{fmtDate(e.at)}</td><td><span className="badge info">{e.kind}</span></td><td>{e.message}</td></tr>)}
      </tbody></table></div>
    </>
  );
}
