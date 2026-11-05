// Types of the operations API (groups, VM configuration, creation, checkpoints, host resources, console).
// Values of `config` come from the agent's Hyper-V module (GetVmConfig): memory in bytes.
import type { PowerState } from './types';

export interface Group {
  id: string;
  parentId: string | null;
  name: string;
  description: string | null;
  createdAt: string;
  vmIds: string[];
}

export interface GroupsResponse { canManage: boolean; groups: Group[] }

export interface VmConfigAdapter { id: string; name: string; switchName: string | null; macAddress?: string | null; dynamicMac?: boolean; vlanId: number; vlanMode?: string }
export interface VmConfigDrive { controllerType: string; controllerNumber: number; controllerLocation: number; path: string | null }

export interface VmConfig {
  name: string;
  notes: string | null;
  generation: number;
  state: string;
  version: string;
  configurationLocation?: string | null;
  processorCount: number;
  processorReserve?: number;
  processorLimit?: number;
  processorWeight?: number;
  memoryStartupBytes: number;
  dynamicMemoryEnabled: boolean;
  memoryMinimumBytes?: number;
  memoryMaximumBytes?: number;
  memoryBuffer?: number;
  memoryPriority?: number;
  automaticStartAction?: string;
  automaticStartDelay?: number;
  automaticStopAction?: string;
  checkpointType?: string;
  secureBootEnabled: boolean | null;
  secureBootTemplate: string | null;
  tpmEnabled: boolean | null;
  dvdDrives: VmConfigDrive[];
  networkAdapters: VmConfigAdapter[];
  hardDisks: VmConfigDrive[];
}

export interface ConfigOptions { startActions: string[]; stopActions: string[]; checkpointTypes: string[]; secureBootTemplates: string[] }

export interface ConfigResponse {
  config: VmConfig;
  powerState: PowerState;
  can: { compute: boolean; storage: boolean; network: boolean };
  requiresOff: string[];
  switches: string[];
  isoLibrary: string[];
  options: ConfigOptions;
}

/** PATCH /vms/{id}/config body: only changed fields are sent. */
export interface ConfigChange {
  newName?: string;
  notes?: string;
  processorCount?: number;
  processorReserve?: number;
  processorLimit?: number;
  processorWeight?: number;
  memoryStartupMb?: number;
  dynamicMemoryEnabled?: boolean;
  memoryMinimumMb?: number;
  memoryMaximumMb?: number;
  memoryBuffer?: number;
  memoryPriority?: number;
  automaticStartAction?: string;
  automaticStartDelaySeconds?: number;
  automaticStopAction?: string;
  checkpointType?: string;
  secureBootEnabled?: boolean;
  secureBootTemplate?: string;
  dvdIsoPath?: string;
  networkAdapterChanges?: { adapterId: string; switchName?: string | null; vlanId?: number | null }[];
  addNetworkAdapterSwitch?: string;
  newDisk?: { sizeGb: number; fixed: boolean };
  expandDisk?: { path: string; newSizeGb: number };
}

export interface CheckpointInfo { id: string; name: string; parentId: string | null; createdUtc: string; type: string | null; isCurrent: boolean }
export interface CheckpointsResponse { checkpoints: CheckpointInfo[]; powerState: PowerState; canCreate: boolean; canApply: boolean; note: string }

export interface SwitchInfo { id: string; name: string; type: string; netAdapter: string | null; allowManagementOs: boolean; embeddedTeaming: boolean; connectedVms: string[] }

export interface HostDiskInfo { path: string; vmId: string | null; vmName: string | null; controller: string | null; format: string | null; diskType: string | null; sizeBytes: number | null; fileSizeBytes: number | null; parentPath: string | null }
export interface HostDiskRow { disk: HostDiskInfo; portalVmId: string }

export interface IsoFile { path: string; sizeBytes: number; modifiedUtc: string }
export interface IsosResponse { roots: string[]; files: IsoFile[] }

export interface HostDefaultsResponse {
  defaults: { virtualHardDiskPath: string | null; virtualMachinePath: string | null; logicalProcessorCount: number; memoryCapacityBytes: number; supportedVersions: string[]; defaultVersion: string | null };
  switches: string[];
  isoLibrary: string[];
  existingNames: string[];
  options: { startActions: string[]; checkpointTypes: string[]; secureBootTemplates: string[] };
}

export interface NewVmSpec {
  name: string;
  generation: 1 | 2;
  processorCount: number;
  memoryStartupMb: number;
  dynamicMemory: boolean;
  memoryMinimumMb?: number;
  memoryMaximumMb?: number;
  diskSizeGb: number;
  diskFixed: boolean;
  switchName?: string;
  isoPath?: string;
  secureBootTemplate?: string;
  enableTpm: boolean;
  checkpointType?: string;
  automaticStartAction?: string;
  startAfterCreate: boolean;
  notes?: string;
}

export interface Accepted { jobId: string; created: boolean }
