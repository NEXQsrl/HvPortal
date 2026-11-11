import { describe, expect, it } from 'vitest';
import { advance, isSlow, loadRun, nextDelay, ROLLBACK_CONFIRMATIONS, saveRun, SLOW_AFTER_MS, type UpdateRun } from './portal-update';
import { clusterAttention, issueTab } from './cluster-issues';
import type { ClusterIssue, ClusterSummary } from '@/api/cluster-types';

const run = (p: Partial<UpdateRun> = {}): UpdateRun => ({ target: '0.9.5', from: '0.9.4', startedAt: 1000, phase: 'installing', sawDown: false, backOnOtherVersion: 0, ...p });

describe('portal update waiting screen', () => {
  it('keeps waiting while the old gateway still answers', () => {
    const r = advance(run(), { reachable: true, version: '0.9.4', authenticated: true, outcome: 'pending' });
    expect(r.phase).toBe('installing');
    expect(r.sawDown).toBe(false);
  });

  it('follows the restart and calls it a success only on the requested version', () => {
    let r = advance(run(), { reachable: false });
    expect(r.phase).toBe('restarting');
    expect(r.sawDown).toBe(true);
    r = advance(r, { reachable: true, version: '0.9.5+abc', authenticated: false });
    expect(r.phase).toBe('success');
    expect(r.signedOut).toBe(true);
    // Final: later observations change nothing.
    expect(advance(r, { reachable: false }).phase).toBe('success');
  });

  it('an "ok" outcome without the new version running is not a success', () => {
    const r = advance(run(), { reachable: true, version: '0.9.4', authenticated: true, outcome: 'installed' });
    expect(r.phase).toBe('installing');
  });

  it('reports the failure written by the updater with its message', () => {
    const r = advance(run(), { reachable: true, version: '0.9.4', authenticated: true, outcome: 'failed', outcomeMessage: 'not signed by the publisher' });
    expect(r.phase).toBe('failed');
    expect(r.failure).toBe('updater');
    expect(r.message).toBe('not signed by the publisher');
  });

  it('a gateway back on the old version is a rollback only after several answers in a row', () => {
    let r = advance(run(), { reachable: false });
    for (let i = 1; i < ROLLBACK_CONFIRMATIONS; i++) {
      r = advance(r, { reachable: true, version: '0.9.4', authenticated: false });
      expect(r.phase).toBe('installing');
    }
    // Down again in between: the count starts again.
    const again = advance(r, { reachable: false });
    expect(again.backOnOtherVersion).toBe(0);
    r = advance(r, { reachable: true, version: '0.9.4', authenticated: false });
    expect(r.phase).toBe('failed');
    expect(r.failure).toBe('rolled_back');
  });

  it('does not poll while the setup is still being downloaded', () => {
    const r = run({ phase: 'preparing' });
    expect(advance(r, { reachable: false })).toBe(r);
  });

  it('backs off while the gateway is down and says when it is slow', () => {
    const down = run({ phase: 'restarting' });
    expect(nextDelay(down, 0)).toBe(2000);
    expect(nextDelay(down, 3)).toBeGreaterThan(nextDelay(down, 1));
    expect(nextDelay(down, 50)).toBe(10000);
    expect(nextDelay(run(), 0)).toBe(3000);
    expect(isSlow(run(), 1000 + SLOW_AFTER_MS + 1)).toBe(true);
    expect(isSlow(run({ phase: 'success' }), 1000 + SLOW_AFTER_MS + 1)).toBe(false);
  });

  it('stores the run across reloads and drops what cannot be resumed', () => {
    const data = new Map<string, string>();
    const storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); }, removeItem: (k: string) => { data.delete(k); } };
    saveRun(run({ phase: 'preparing' }), storage);
    expect(loadRun(2000, storage)).toBeNull();
    saveRun(run({ phase: 'restarting', sawDown: true }), storage);
    expect(loadRun(2000, storage)).toMatchObject({ target: '0.9.5', phase: 'restarting', sawDown: true });
    expect(loadRun(1000 + 7 * 60 * 60 * 1000, storage)).toBeNull();
    data.set('hvp.portalUpdate', '{"target":5}');
    expect(loadRun(2000, storage)).toBeNull();
    data.set('hvp.portalUpdate', 'not json');
    expect(loadRun(2000, storage)).toBeNull();
    saveRun(null, storage);
    expect(data.size).toBe(0);
  });
});

const issue = (code: string, level: ClusterIssue['level'] = 'Warning', subject: string | null = null): ClusterIssue => ({ level, code, message: code, subject, args: null });
const cluster = (p: Partial<ClusterSummary>): ClusterSummary => ({
  id: 'c1', name: 'Cluster 1', description: null, windowsName: null, nodeIds: [], nodesConnected: 2, nodesTotal: 2, status: 'Warning', statusAt: null,
  can: { manage: false, readStatus: true }, ...p,
});

describe('cluster problems on the dashboard', () => {
  it('opens the tab that explains the problem', () => {
    expect(issueTab('node_down')).toBe('nodes');
    expect(issueTab('csv_not_online')).toBe('storage');
    expect(issueTab('storage_job_failed')).toBe('storage');
    expect(issueTab('health_fault')).toBe('storage');
    expect(issueTab('witness_not_online')).toBe('quorum');
    expect(issueTab('network_down')).toBe('network');
    expect(issueTab('resource_failed')).toBe('roles');
    expect(issueTab('status_unreadable')).toBe('summary');
  });

  it('lists each problem once, without notes and without what the user may not read', () => {
    const list = clusterAttention([
      cluster({ problems: [issue('node_down', 'Critical', 'N1'), issue('node_down', 'Critical', 'n1'), issue('witness_recommended', 'Info'), issue('status_stale', 'Unknown')] }),
      cluster({ id: 'c2', name: 'Hidden', can: { manage: false, readStatus: false }, problems: [issue('node_down', 'Critical', 'X')] }),
    ], new Set());
    expect(list.map((x) => x.issue.code)).toEqual(['node_down', 'status_stale']);
    expect(list[0]?.to).toBe('/clusters/c1?tab=nodes');
    expect(list[1]?.to).toBe('/clusters/c1');
  });

  it('does not repeat the disconnected agent of a host already listed as offline', () => {
    const disconnected = issue('agent_disconnected', 'Warning', 'N2');
    const c = cluster({ problems: [disconnected, issue('csv_low_space', 'Warning', 'C:\\ClusterStorage\\Volume1')], nodeProblems: [{ hostId: 'h2', issues: [disconnected] }] });
    expect(clusterAttention([c], new Set(['h2'])).map((x) => x.issue.code)).toEqual(['csv_low_space']);
    expect(clusterAttention([c], new Set()).map((x) => x.issue.code)).toEqual(['agent_disconnected', 'csv_low_space']);
  });
});
