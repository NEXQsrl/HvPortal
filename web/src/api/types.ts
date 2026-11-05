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
  version: string;
}

export interface Me {
  id: string;
  userName: string;
  displayName: string | null;
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
  isoLibrary: string[];
}

export interface InventoryEvent { id: number; hostId: string; vmId: string | null; kind: string; details: string | null; at: string }

export interface Vm {
  id: string;
  hyperVId: string;
  name: string;
  description: string | null;
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
  canApplyCheckpoint: boolean;
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
}

export interface PackageInfo {
  fileName: string;
  sizeBytes: number;
  sha256: string;
  version: string | null;
  builtAtUtc: string | null;
  signed: boolean;
  signer: string | null;
}
