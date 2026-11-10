// Failover clusters (ADR-041): mirrors src/HvPortal.Gateway/Api/ClusterEndpoints.cs and Contracts/HyperV/Cluster.cs.
import type { HostVolume } from './types';

export type ClusterLevel = 'Ok' | 'Warning' | 'Critical' | 'Unknown';
export type IssueLevel = ClusterLevel | 'Info';

export interface ClusterSummary {
  id: string;
  name: string;
  description: string | null;
  windowsName: string | null;
  /** Nodes the user can see. */
  nodeIds: string[];
  nodesConnected: number;
  nodesTotal: number;
  /** Last status read (null when the user may not read the cluster status, or never read). */
  status: ClusterLevel | null;
  statusAt: string | null;
  can: { manage: boolean; readStatus: boolean };
  /** Problems of the last read (notes left out); empty for who may not read the cluster status. */
  problems?: ClusterIssue[];
  /** Problems about each node (issue subject = node or host name). */
  nodeProblems?: { hostId: string; issues: ClusterIssue[] }[];
}

/** What the agent of a node reads about its cluster with every inventory (protocol 11). */
export interface ClusterFacts {
  name: string | null;
  nodeName: string | null;
  nodeState: string | null;
  drainStatus: string | null;
  error: string | null;
  sharedVolumes: HostVolume[];
}

export interface ClusterIssue {
  level: IssueLevel;
  code: string;
  message: string;
  subject: string | null;
  args: Record<string, string> | null;
}

export interface ClusterNodeStatus {
  name: string;
  state: string | null;
  drainStatus: string | null;
  nodeWeight: number | null;
  dynamicWeight: number | null;
  manufacturer: string | null;
  model: string | null;
  storageMaintenance: boolean | null;
}

export interface ClusterQuorumStatus {
  quorumType: string | null;
  witnessType: string | null;
  witnessName: string | null;
  witnessState: string | null;
  witnessTarget: string | null;
  dynamicQuorum: boolean | null;
  witnessDynamicWeight: number | null;
}

export interface ClusterNetworkStatus { name: string; state: string | null; role: string | null; address: string | null }

export interface ClusterSharedVolumeStatus {
  name: string;
  path: string | null;
  ownerNode: string | null;
  state: string | null;
  sizeBytes: number | null;
  freeBytes: number | null;
  access: string | null;
  maintenanceMode: boolean | null;
}

export interface ClusterStorageStatus {
  s2dEnabled: boolean;
  s2dState: string | null;
  cacheState: string | null;
  pools: { name: string; healthStatus: string | null; operationalStatus: string | null; sizeBytes: number | null; allocatedBytes: number | null; isReadOnly: boolean | null }[];
  virtualDisks: { name: string; healthStatus: string | null; operationalStatus: string | null; resiliency: string | null; dataCopies: number | null; sizeBytes: number | null; footprintBytes: number | null }[];
  physicalDisks: { name: string; serialNumber: string | null; mediaType: string | null; usage: string | null; healthStatus: string | null; operationalStatus: string | null; sizeBytes: number | null }[];
  jobs: { name: string; state: string | null; percentComplete: number | null; bytesTotal: number | null; bytesProcessed: number | null; elapsedSeconds?: number | null }[];
}

export interface ClusterRoleStatus { name: string; groupType: string | null; state: string | null; ownerNode: string | null; vmId: string | null }

export interface ClusterStatusReport {
  collectedUtc: string;
  name: string | null;
  fqdn: string | null;
  administrativeAccessPoint: string | null;
  domain: string | null;
  readByNode: string | null;
  nodes: ClusterNodeStatus[];
  quorum: ClusterQuorumStatus | null;
  networks: ClusterNetworkStatus[];
  sharedVolumes: ClusterSharedVolumeStatus[];
  storage: ClusterStorageStatus | null;
  roles: ClusterRoleStatus[];
  failedResources: { name: string; resourceType: string | null; state: string | null; ownerGroup: string | null; ownerNode: string | null }[];
  errors: string[];
  /** Agent 0.9.1+ (absent with older agents). */
  coreGroupState?: string | null;
  coreResources?: { name: string; resourceType: string | null; state: string | null }[];
  healthFaults?: { severity: string | null; faultType: string | null; reason: string | null; recommendedAction: string | null; faultingObject: string | null }[];
  replication?: { name: string; replicationMode: string | null; status: string | null; bytesRemaining: number | null; isPrimary: boolean | null; partner: string | null }[];
}

export interface CapacityReport {
  nodes: number;
  nodesUp: number;
  totalMemoryBytes: number;
  freeMemoryBytes: number;
  vmMemoryBytes: number;
  logicalProcessors: number;
  runningVcpus: number;
  vcpuRatio: number | null;
  vcpuRatioN1: number | null;
  worstNode: string | null;
  failoverNeededBytes: number | null;
  failoverAvailableBytes: number | null;
  toleratesNodeLoss: boolean | null;
  sharedVolumeBytes: number | null;
  sharedVolumeFreeBytes: number | null;
  poolFreeBytes: number | null;
  poolReserveBytes: number | null;
}

export interface ClusterNodeView {
  name: string;
  hostId: string | null;
  hostName: string | null;
  state: string | null;
  drainStatus: string | null;
  storageMaintenance: boolean | null;
  nodeWeight: number | null;
  dynamicWeight: number | null;
  model: string | null;
  agentConnected: boolean;
  agentVersion: string | null;
  agentReady: boolean;
  totalMemoryBytes: number | null;
  freeMemoryBytes: number | null;
  logicalProcessors: number | null;
  cpuLoadPercent: number | null;
  runningVms: number;
  runningVcpus: number;
  runningVmMemoryBytes: number;
  clusteredVmMemoryBytes: number;
  notClusteredRunningVms: string[];
  clusterError: string | null;
  clusterMismatch: string | null;
  backupStagingPath: string | null;
  canMaintain: boolean;
}

export interface ClusterDetail {
  id: string;
  name: string;
  description: string | null;
  windowsName: string | null;
  createdAt: string;
  can: { manage: boolean; createVm: boolean; setBackupStaging: boolean };
  /** Number of backup repositories with credentials (they need a staging folder on every node). */
  backup: { credentialRepositories: number };
  nodes: ClusterNodeView[];
  suggestedHosts: { id: string; displayName: string }[];
  status: {
    level: ClusterLevel | null;
    live: boolean;
    storedAt: string | null;
    collectedUtc: string | null;
    readByNode: string | null;
    error: string | null;
    attempts: string[];
    report: ClusterStatusReport | null;
    issues: ClusterIssue[];
    capacity: CapacityReport;
  };
}

export interface MaintenanceCheck {
  action: 'Enter' | 'Exit';
  nodeName: string | null;
  allowed: boolean;
  blocking: ClusterIssue[];
  warnings: ClusterIssue[];
  s2d: boolean;
  readError: string | null;
}

export interface ClusterNodeEnrollment { hostId: string; displayName: string; enrollment: import('./types').EnrollmentInfo }
