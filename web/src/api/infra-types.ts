// Types of the protocol 9 infrastructure endpoints: host health, portal networks (VLAN), agent updates.

export type HealthLevel = 'Ok' | 'Warning' | 'Critical' | 'Unknown';

export interface HostHealthReport {
  collectedUtc: string;
  status: HealthLevel;
  hardware: {
    manufacturer: string | null; model: string | null; serialNumber: string | null; biosVersion: string | null; biosDate: string | null;
    memoryModules: number | null; memoryInstalledBytes: number | null; processors: string[]; status: string | null;
  } | null;
  physicalDisks: {
    name: string; serialNumber: string | null; mediaType: string | null; busType: string | null; sizeBytes: number | null;
    healthStatus: string | null; operationalStatus: string[]; temperatureCelsius: number | null; wearPercent: number | null;
    readErrorsUncorrected: number | null; writeErrorsUncorrected: number | null; powerOnHours: number | null; status: HealthLevel;
  }[];
  volumes: { name: string; label: string | null; fileSystem: string | null; sizeBytes: number | null; freeBytes: number | null; healthStatus: string | null; operationalStatus: string | null; status: HealthLevel }[];
  storagePools: { name: string; healthStatus: string | null; operationalStatus: string | null; sizeBytes: number | null; allocatedBytes: number | null; virtualDisks: string[]; status: HealthLevel }[];
  networkAdapters: { name: string; description: string | null; status: string | null; linkSpeed: string | null; health: HealthLevel }[];
  events: { timeUtc: string; provider: string; id: number; level: string; message: string | null }[];
  services: { name: string; displayName: string | null; state: string | null; startMode: string | null }[];
  errors: string[];
}

/** A network of the portal: a name for "virtual switch + access VLAN" on one host (null VLAN = untagged). */
export interface PortalNetwork {
  id: string;
  hostId: string;
  hostName: string | null;
  name: string;
  switchName: string;
  vlanId: number | null;
  description: string | null;
  createdAt: string;
  canManage: boolean;
}

export interface NetworkUpdateResult {
  network: PortalNetwork;
  jobs: { vmId: string; name: string; jobId: string }[];
  skipped: string[];
}

export type AgentUpdateReason = 'not_connected' | 'agent_too_old' | 'updater_missing' | 'package_unsigned';

export interface AgentUpdateState {
  id: string;
  kind: 'host' | 'management';
  name: string;
  version: string | null;
  connected: boolean;
  protocol: number;
  outdated: boolean;
  updaterInstalled: boolean | null;
  /** Outcome of the last update on that machine ("<time> <status> <version>: <message>", from the updater's last-result.json). */
  lastUpdateResult: string | null;
  canUpdate: boolean;
  reason: AgentUpdateReason | null;
  runningJobId: string | null;
}

export interface AgentUpdatesResponse {
  gatewayVersion: string;
  package: { version: string | null; sha256: string; sizeBytes: number; signed: boolean; selfSigned: boolean; catalogSigned: boolean; signer: string | null } | null;
  outdated: number;
  agents: AgentUpdateState[];
}
