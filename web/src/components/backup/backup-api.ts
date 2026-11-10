// Types of the backup API (src/HvPortal.Gateway/VmBackup/*Endpoints.cs). Keep in sync with the DTOs there.

export type PointStatus = 'Creating' | 'Ok' | 'Failed' | 'Unverified' | 'Deleting' | 'Deleted';
export type RunStatus = 'Running' | 'Success' | 'Warning' | 'Failed';
export type CheckStatus = 'Ok' | 'Warning' | 'Critical';
export type CheckpointType = 'Production' | 'ProductionOnly' | 'Standard';
export type ScheduleKind = 'daily' | 'hourly' | 'manual';
export type NotifyOn = 'never' | 'failure' | 'warning' | 'always';
export type RestoreMode = 'NewVm' | 'Replace' | 'Test';
export type RestoreNetwork = 'Same' | 'Disconnected' | 'Switch';

export const POINT_STATUSES: PointStatus[] = ['Ok', 'Unverified', 'Failed', 'Creating', 'Deleting'];
export const RUN_STATUSES: RunStatus[] = ['Running', 'Success', 'Warning', 'Failed'];
export const CHECKPOINT_TYPES: CheckpointType[] = ['Production', 'ProductionOnly', 'Standard'];
export const NOTIFY_VALUES: NotifyOn[] = ['never', 'failure', 'warning', 'always'];

/** One readiness check of a host (GetBackupReadiness): Fix is what to do, often a PowerShell command. */
export interface BackupCheck { key: string; status: CheckStatus | string; message: string; fix: string | null }

export interface Repository {
  id: string;
  name: string;
  description: string | null;
  path: string;
  /** Local folder repositories: the host that has the folder. Null for shares. */
  hostId: string | null;
  hostName: string | null;
  isShare: boolean;
  hasCredentials: boolean;
  /** Returned only to backup.manage holders. */
  userName: string | null;
  credentialUpdatedAt: string | null;
  createdAt: string;
  lastCheckedAt: string | null;
  lastCheckHostId: string | null;
  lastCheck: { host: string; checks: BackupCheck[] } | null;
  points: number;
  bytes: number;
  /** Backup administrators: hosts (and nodes of their clusters) that back up here without the staging folder a share with credentials needs. */
  hostsWithoutStaging: { id: string; name: string; clusterId: string | null }[];
}

export interface BackupRun {
  id: string;
  backupJobId: string | null;
  jobName: string | null;
  /** schedule | manual | adhoc */
  trigger: string;
  startedAt: string;
  completedAt: string | null;
  status: RunStatus;
  total: number;
  succeeded: number;
  warnings: number;
  failed: number;
  message: string | null;
}

export interface BackupJobDef {
  id: string;
  name: string;
  description: string | null;
  enabled: boolean;
  repositoryId: string;
  scheduleKind: ScheduleKind;
  /** Bit 0 = Sunday ... bit 6 = Saturday. */
  daysMask: number;
  times: string[];
  intervalHours: number;
  timeZoneId: string;
  retentionPoints: number;
  retentionDays: number | null;
  checkpointType: CheckpointType;
  verifyAfterCopy: boolean;
  restoreTestDays: number;
  notifyOn: NotifyOn;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastRestoreTestAt: string | null;
  vmIds: string[];
  groupIds: string[];
  /** User the scheduled runs execute as (the one who saved the job). */
  runsAs: string | null;
  lastRun: BackupRun | null;
}

export interface BackupPoint {
  id: string;
  runId: string | null;
  backupJobId: string | null;
  jobName: string | null;
  repositoryId: string;
  repositoryName: string | null;
  vmId: string;
  vmHyperVId: string;
  vmName: string;
  vmExists: boolean;
  hostId: string;
  folder: string;
  status: PointStatus;
  createdAt: string;
  completedAt: string | null;
  sizeBytes: number;
  checkpointType: string | null;
  checkpointTypeUsed: string | null;
  error: string | null;
  /** The task (job of the portal) that creates or deletes the point. */
  jobId: string | null;
  verifiedAt: string | null;
  verifyOk: boolean | null;
  verifyDetail: string | null;
  testedAt: string | null;
  testPassed: boolean | null;
  testDetail: string | null;
  keep: boolean;
  /** Relative paths of the virtual disks in the manifest. */
  disks: string[];
}

export interface BackupManifest {
  format: number;
  vmName: string | null;
  hostName: string | null;
  createdUtc: string;
  completedUtc: string;
  checkpointTypeRequested: string | null;
  checkpointTypeUsed: string | null;
  generation: number;
  configurationVersion: string | null;
  processorCount: number;
  memoryStartupMb: number;
  networkAdapters: { name: string | null; switchName: string | null; vlanId: number }[];
  configFile: string | null;
  disks: string[];
  files: { path: string; sizeBytes: number; sha256: string | null }[];
  totalBytes: number;
  agentVersion: string | null;
  warnings: string[];
}

export interface Overview {
  last24h: { runs: number; success: number; warning: number; failed: number; running: number };
  repositories: number;
  jobs: number;
  enabledJobs: number;
  points: number;
  bytes: number;
  vms: number;
  protectedVms: number;
  unprotected: { id: string; name: string; hostId: string }[];
  problems: BackupPoint[];
  recentRuns: BackupRun[];
}

export interface VmBackupsResponse {
  allowed: boolean;
  canRun: boolean;
  canRestore: boolean;
  canManage: boolean;
  points: BackupPoint[];
  /** Repositories usable for "back up now" from the VM's host. */
  repositories: { id: string; name: string; path: string }[];
}

export interface ReadinessResponse { host: string; checks: BackupCheck[] }
export interface CheckResponse { host: string; hostId: string; checks: BackupCheck[] }
export interface RescanResponse { found: number; added: number; at: string }

export interface MountedVolume { root: string; label: string | null; fileSystem: string | null; sizeBytes: number | null }
export interface MountResponse { mountId: string; hostId: string; volumes: MountedVolume[]; expiresUtc: string; staged: boolean }
export interface MountInfo { mountId: string; hostId: string; pointId: string; vmName: string; disk: string; volumes: MountedVolume[]; createdUtc: string }

export interface NotificationSettings {
  email: { enabled: boolean; host: string | null; port: number; security: string; userName: string | null; hasPassword: boolean; from: string | null; to: string[] };
  /** Addresses are masked when read back (only the host and the start of the path). */
  webhooks: { name: string; url: string; format: string }[];
  restoreTestFailed: boolean;
  agentsOutdated: boolean;
  /** A failover cluster gets worse or is back to normal (ADR-041). */
  clusterProblems?: boolean;
}

export interface ApiTokenRow {
  id: string;
  name: string;
  prefix: string;
  scopes: string[];
  createdBy: string | null;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  lastUsedIp: string | null;
  revokedAt: string | null;
}

export interface CreatedToken { id: string; name: string; token: string; expiresAt: string | null; scopes: string[] }

/** Idle time after which the agent detaches a disk opened for file restore (BackupMounts.IdleLimit). */
export const MOUNT_IDLE_HOURS = 2;
