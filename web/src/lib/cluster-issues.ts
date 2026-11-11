import type { ClusterIssue, ClusterSummary, IssueLevel } from '@/api/cluster-types';

export type ClusterTab = 'summary' | 'nodes' | 'storage' | 'quorum' | 'network' | 'roles';

/** The tab of the cluster page where a problem is explained (cluster.issue code of ClusterRules). */
export function issueTab(code: string): ClusterTab {
  if (/^(csv_|pool_|virtual_disk_|physical_disk_|storage_|health_fault|replication_)/.test(code)) return 'storage';
  if (/^(witness_|no_witness_|nodes_majority_down)/.test(code)) return 'quorum';
  if (code.startsWith('network_')) return 'network';
  if (/^(role_|resource_failed|core_)/.test(code)) return code.startsWith('core_') ? 'summary' : 'roles';
  if (/^(node_|drain_|agent_|host_not_a_node|capacity_)/.test(code)) return code.startsWith('capacity_') ? 'summary' : 'nodes';
  return 'summary';
}

export const issueLink = (clusterId: string, code: string) => {
  const tab = issueTab(code);
  return `/clusters/${clusterId}${tab === 'summary' ? '' : `?tab=${tab}`}`;
};

const RANK: Record<string, number> = { Critical: 0, Warning: 1, Unknown: 2, Info: 3, Ok: 4 };
export const levelRank = (level: IssueLevel | string) => RANK[level] ?? 5;

export interface ClusterAttention { key: string; clusterId: string; clusterName: string; issue: ClusterIssue; to: string }

/**
 * Problems of the clusters for the dashboard: only clusters whose status the user may read, each problem once
 * (same code and subject), without the "agent not connected" of a node that the dashboard already lists as an
 * offline host. Notes (Info) are not problems.
 */
export function clusterAttention(clusters: ClusterSummary[], offlineHostIds: ReadonlySet<string>): ClusterAttention[] {
  const out: ClusterAttention[] = [];
  for (const c of clusters) {
    if (!c.can.readStatus) continue;
    const alreadyListed = new Set<string>();
    for (const np of c.nodeProblems ?? []) {
      if (!offlineHostIds.has(np.hostId)) continue;
      for (const i of np.issues) if (i.code === 'agent_disconnected') alreadyListed.add(`${i.code}|${(i.subject ?? '').toLowerCase()}`);
    }
    const seen = new Set<string>();
    for (const issue of c.problems ?? []) {
      if (issue.level === 'Info' || issue.level === 'Ok') continue;
      const id = `${issue.code}|${(issue.subject ?? '').toLowerCase()}`;
      if (seen.has(id) || alreadyListed.has(id)) continue;
      seen.add(id);
      out.push({ key: `cl-${c.id}-${id}`, clusterId: c.id, clusterName: c.name, issue, to: issueLink(c.id, issue.code) });
    }
  }
  return out;
}
