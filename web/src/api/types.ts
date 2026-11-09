// Types mirror the gateway API (see /openapi/v1.json). Keep in sync with src/HvPortal.Gateway/Api.

export type PowerState =
  | 'Unknown' | 'Running' | 'Off' | 'Saved' | 'Paused' | 'Starting' | 'Stopping'
  | 'Saving' | 'Pausing' | 'Resuming' | 'Resetting' | 'Other';

export type PowerOperation = 'Start' | 'Shutdown' | 'Restart' | 'TurnOff' | 'Reset' | 'Pause' | 'Resume' | 'Save';

export type JobStatus = 'Queued' | 'Running' | 'Reconciling' | 'Succeeded' | 'Unverified' | 'Failed' | 'Cancelled' | 'Unknown';

export interface AuthState {
  setupRequired: boolean;
  authenticated: boolean;
  providers: string[];
  /** "allowed" or "break-glass-only" (local sign-in reserved to emergency accounts). */
  localSignIn?: string;
  version: string;
}

export interface Me {
  id: string;
  userName: string;
  displayName: string | null;
  /** "local" or "entra" (Microsoft account: no portal password, MFA by Entra ID). */
  authSource?: string;
  restriction: 'password_change' | 'mfa_enrollment' | null;
  mfaEnabled: boolean;
  mfaRequired: boolean;
  recoveryCodesRemaining: number;
  isBreakGlass: boolean;
  authMethods: string;
  sessionExpiresAt: string;
  idleTimeoutMinutes: number;
  passwordMinLength: number;
  globalPermissions: string[];
}

export interface Capability { name: string; status: string; note?: string | null }

export interface NetworkAdapterInfo {
  name: string;
  macAddress: string | null;
  ipAddresses: string[];
  subnets: string[];
  gateways: string[];
  dnsServers: string[];
  dhcpEnabled: boolean | null;
  switchName?: string | null;
}

export interface HostVolume { name: string; label: string | null; fileSystem: string | null; sizeBytes: number | null; freeBytes: number | null }

export interface VmDisk {
  kind: 'VirtualHardDisk' | 'DvdIso' | 'Other';
  path: string | null;
  controller: string | null;
  location: string | null;
  format: string | null;
  diskType: string | null;
  maxSizeBytes: number | null;
  fileSizeBytes: number | null;
  parentPath: string | null;
}

export interface VmDetails { vmId: string; collectedAtUtc: string; disks: VmDisk[]; networkAdapters: NetworkAdapterInfo[]; warnings: string[] }

export interface Host {
  id: string;
  /** Folder of the host where its templates are kept (null = not chosen yet). */
  templateLibraryPath: string | null;
  /** The host can work with templates (connected, Hyper-V access, agent 0.6.0+). */
  templates: boolean;
  displayName: string;
  description: string | null;
  machineName: string | null;
  dnsHostName: string | null;
  status: 'Pending' | 'Active' | 'Revoked';
  connected: boolean;
  stale: boolean;
  lastSeenAt: string | null;
  lastInventoryAt: string | null;
  access: 'Ok' | 'NotInstalled' | 'ServiceStopped' | 'AccessDenied' | 'Error' | null;
  accessMessage: string | null;
  partial: boolean;
  warnings: string[];
  agentVersion: string | null;
  enrolledAt: string | null;
  vmCount: number;
  os: { osCaption: string | null; osVersion: string | null; osBuildNumber: string | null; domain: string | null; partOfDomain: boolean } | null;
  hardware: {
    logicalProcessorCount: number | null; totalPhysicalMemoryBytes: number | null; freePhysicalMemoryBytes: number | null;
    cpuName: string | null; cpuSockets: number | null; cpuCores: number | null; cpuLoadPercent: number | null; cpuLoadSource: 'hypervisor' | 'root-partition' | null;
  } | null;
  lastBootUtc: string | null;
  volumes: HostVolume[];
  networkAdapters: NetworkAdapterInfo[];
  virtualSwitches: string[];
  edition: string;
  platform: string;
  support: string;
  capabilities: Capability[];
  canCreateVm: boolean;
  /** Agent 0.5.0+ and network.manage on the host: create and remove virtual switches. */
  canManageNetworks?: boolean;
  isoLibrary: string[];
  /** Agent 0.3.1+: folder browser for the VM location and ISO files. */
  canBrowseFolders?: boolean;
  /** Agent 0.3.2+: portal groups mirrored as native Hyper-V VM groups. */
  nativeVmGroups?: boolean;
  /** Agent 0.3.3+: file explorer (browse, download, upload). */
  canBrowseFiles?: boolean;
  vmGroupsSyncedAt?: string | null;
  vmGroupsSyncError?: string | null;
  /** Agent: an agent runs on the host. Remote: reached by a management agent with stored credentials. */
  connectionMode?: 'Agent' | 'Remote';
  managementAgentId?: string | null;
  managementAgentName?: string | null;
  /** Only for global host administrators; the password is never returned. */
  remote?: { computerName: string; userName: string; useHttps: boolean; credentialUpdatedAt: string | null } | null;
  /** Agent 0.7.0+ connected with Hyper-V access: host health, VM move, backups, new folders. */
  agent7?: boolean;
  /** Folders the portal may use on the host (empty = not restricted; null = unknown, agent older than 0.7.0). */
  storageFolders?: string[] | null;
  /** Remote hosts: the folders are set in the portal; hosts with an agent decide them at install time. */
  storageFoldersEditable?: boolean;
  /** Local folder for temporary copies of backups (shares with credentials, restore tests, file restore). */
  backupStagingPath?: string | null;
  /** The updater task is registered on the host (updates from the portal possible for signed builds). */
  updaterInstalled?: boolean | null;
  lastUpdateResult?: string | null;
  /** Failover cluster of the portal the host is a node of (null = standalone). */
  clusterId?: string | null;
  clusterName?: string | null;
  /** What the agent (0.9.0+) reads on the host about its Windows cluster. */
  clusterFacts?: import('./cluster-types').ClusterFacts | null;
  /** other_cluster | not_clustered | unassigned: the Windows cluster of the host and the portal disagree. */
  clusterMismatch?: string | null;
  /** hosts.maintenance on this node of a cluster. */
  canMaintain?: boolean;
}

export interface ManagementAgent {
  id: string;
  displayName: string;
  description: string | null;
  machineName: string | null;
  agentVersion: string | null;
  status: 'Pending' | 'Active' | 'Revoked';
  connected: boolean;
  createdAt: string;
  enrolledAt: string | null;
  lastSeenAt: string | null;
  hostCount: number;
}

export interface InventoryEvent { id: number; hostId: string; vmId: string | null; kind: string; details: string | null; at: string }

export interface Vm {
  id: string;
  hyperVId: string;
  name: string;
  checkpointCount: number | null;
  hostId: string;
  hostName: string | null;
  hostConnected: boolean;
  stale: boolean;
  powerState: PowerState;
  isCritical: boolean;
  generation: number | null;
  configurationVersion: string | null;
  processorCount: number | null;
  memoryStartupMb: number | null;
  memoryAssignedMb: number | null;
  dynamicMemoryEnabled: boolean | null;
  uptimeMs: number | null;
  heartbeat: number | null;
  healthState: number | null;
  processorLoadPercent: number | null;
  guestOperatingSystem: string | null;
  shutdownServiceAvailable: boolean | null;
  /** Agent 0.3.4+ (null with older agents). */
  hyperVCreatedAt?: string | null;
  /** Hyper-V notes of the VM, read from the host (the VM's only description; edited through a configuration job). */
  notes?: string | null;
  configurationPath?: string | null;
  memoryAvailablePercent?: number | null;
  memoryBufferPercent?: number | null;
  /** Dynamic memory, running VM: memory the guest needs now. */
  memoryDemandMb?: number | null;
  replicationMode?: number | null;
  replicationState?: number | null;
  replicationHealth?: number | null;
  lastSeenAt: string;
  stateChangedAt: string | null;
  isMissing: boolean;
  missingSince: string | null;
  allowedOperations: PowerOperation[];
  destructiveOperations: PowerOperation[];
  canAnnotate: boolean;
  canDelete: boolean;
  canForget: boolean;
  canConsole: boolean;
  canConfigure: boolean;
  canCheckpoint: boolean;
  /** May create a template from this VM (templates.manage on its host, agent 0.6.0+). */
  canTemplate: boolean;
  canApplyCheckpoint: boolean;
  /** vm.migrate on the VM and agent 0.7.0+ on its host. */
  canMigrate?: boolean;
  /** Role of the failover cluster of its host (highly available); null = not known. */
  isClustered?: boolean | null;
  /** backup.run on the VM and agent 0.7.0+ on its host. */
  canBackup?: boolean;
  /** backup.restore on the VM. */
  canRestore?: boolean;
}

export interface Job {
  id: string;
  type: string;
  operation: string;
  targetType: string;
  targetId: string;
  targetName: string | null;
  hostId: string | null;
  requestedByUserId: string;
  status: JobStatus;
  progress: number;
  progressMessage: string | null;
  createdAt: string;
  startedAt: string | null;
  dispatchedAt: string | null;
  completedAt: string | null;
  attempt: number;
  resultMessage: string | null;
  errorCode: string | null;
  hyperVCode: number | null;
  correlationId: string;
  resultJson: string | null;
  cancellable: boolean;
}

export interface JobEvent { id: number; jobId: string; at: string; kind: string; message: string | null }

export interface AuditEntry {
  id: number;
  timestampUtc: string;
  actorUserId: string | null;
  actorName: string | null;
  authMethod: string;
  sourceIp: string | null;
  action: string;
  resourceType: string | null;
  resourceId: string | null;
  outcome: 'Success' | 'Failure' | 'Denied';
  correlationId: string;
  detailsJson: string | null;
  /** Name of the resource: its current one, or the one recorded in the entry when the resource is gone. */
  resourceName: string | null;
  resourceExists: boolean;
}

export interface UserRow {
  id: string;
  userName: string;
  displayName: string | null;
  authSource: string;
  isEnabled: boolean;
  mfaEnabled: boolean;
  mustChangePassword: boolean;
  isBreakGlass: boolean;
  createdAt: string;
  lastLoginAt: string | null;
  entraBound?: boolean;
  mfaEnrollmentRequired?: boolean;
}

export interface Role { id: string; name: string; description: string | null; isBuiltIn: boolean; builtInKey: string | null; permissions: string[] }

export interface Assignment {
  id: string;
  userId: string;
  userName: string | null;
  roleId: string;
  roleName: string | null;
  scopeType: string;
  scopeId: string;
  scopeName: string | null;
  effect: 'Allow' | 'Deny';
  createdAt: string;
}

export interface PermissionDef { name: string; description: string; scopes: string[]; implemented: boolean; privileged: boolean }

export interface SecuritySettings {
  mfaRequiredFor: 'None' | 'Privileged' | 'All';
  sessionIdleMinutes: number;
  sessionAbsoluteHours: number;
  passwordMinLength: number;
  localSignIn: 'allowed' | 'break-glass-only';
}

export interface GatewayConnection {
  gatewayNames: string[];
  preferredName: string;
  agentPort: number;
  gatewayAgentUrl: string;
  caFingerprintSha256: string;
  listensOnNetwork: boolean;
  bindAddress: string;
}

export interface EnrollmentInfo {
  token: string;
  expiresAt: string;
  caFingerprintSha256: string;
  gatewayAgentUrl: string;
  gatewayNames: string[];
  agentPort: number;
  listensOnNetwork: boolean;
  bindAddress: string;
  command: string;
  note: string;
  /** Token of a management agent (install with -Management). */
  management?: boolean;
  /** Token of a node of a failover cluster (install with -ClusterAccess Full). */
  clusterNode?: boolean;
}

/** Graphical setup of the agent served by the gateway (present only when the release provides it). */
export interface AgentSetupInfo { fileName: string; sizeBytes: number; sha256: string }

export interface PackageInfo {
  fileName: string;
  sizeBytes: number;
  sha256: string;
  version: string | null;
  builtAtUtc: string | null;
  signed: boolean;
  signer: string | null;
  /** Signed with a self-signed certificate: trusted only where HvPortal-CodeSigning.cer is imported. */
  selfSigned: boolean;
}
